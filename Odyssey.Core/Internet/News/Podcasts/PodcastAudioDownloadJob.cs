using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MySqlConnector;
using Odyssey.Context;
using Odyssey.Core.FileStorage;
using Odyssey.Core.Finance;
using Odyssey.Core.Internet.News.Fetching;
using Odyssey.Core.Internet.News.Files;
using Odyssey.Core.Internet.News.Podcasts.Audio;
using Odyssey.Core.Internet.News.Podcasts.Files;
using Odyssey.Core.Jobs;
using Odyssey.Dtos;
using Odyssey.Dtos.Application;
using Odyssey.Dtos.Internet;

namespace Odyssey.Core.Internet.News.Podcasts;

/// <summary>
/// The podcast audio download job (issue #556 §3.6), catalogued as <see cref="JobType.PodcastAudioDownload"/>: every
/// 30 s on every replica, recover expired leases, then claim up to <see cref="PodcastLimits.DownloadsPerRun"/> due rows
/// and download them concurrently. At most one download per feed and per host runs across all replicas: the read-side
/// exclusion passes busy rows over, and on MariaDB the two generated guard columns make it exact. Every byte reserved
/// against the quota is returned on every exit path, and the hourly recount rebuilds the total from live leases.
/// </summary>
public sealed class PodcastAudioDownloadJob(
    IServiceScopeFactory scopes,
    IHttpClientFactory httpClients,
    NewsHostThrottleGate throttle,
    INewsCounterWriter counters,
    FileStorageVolumeRegistry volumes,
    IOptions<FileStorageOptions> fileStorage,
    TimeProvider time,
    ILogger<PodcastAudioDownloadJob> logger) : IPeriodicJob
{
    private const int LockWaitSeconds = 5;
    private const int LockWaitTimeout = 1205;
    private const int DuplicateKey = 1062;
    private const int CandidateWindow = 50;

    private readonly Guid owner = Guid.NewGuid();

    public string Name => "podcast-audio-downloads";

    public TimeSpan Interval => PodcastTiming.DownloadTick;

    public Guid Owner => owner;

    private DateTime Now => time.GetUtcNow().UtcDateTime;

    public async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        JobRunRecorder recorder;
        await using (var scope = scopes.CreateAsyncScope())
        {
            recorder = scope.ServiceProvider.GetRequiredService<JobRunRecorder>();
        }

        await recorder.HeartbeatAsync(JobType.PodcastAudioDownload, cancellationToken);
        var tally = new Tally();
        var storageRefused = false;
        try
        {
            tally.Recovered = await RecoverExpiredAsync(cancellationToken);
            if (!volumes.IsActive)
            {
                storageRefused = await HasWorkAsync(cancellationToken);
                return;
            }

            NewsSettings settings;
            await using (var scope = scopes.CreateAsyncScope())
            {
                settings = await scope.ServiceProvider.GetRequiredService<INewsSettingsLookup>().GetAsync(cancellationToken);
            }

            var claims = await ClaimAsync(cancellationToken);
            var outcomes = await Task.WhenAll(claims.Select(claim => DownloadAsync(claim, settings, recorder, cancellationToken)));
            foreach (var outcome in outcomes)
            {
                tally.Add(outcome);
                storageRefused |= outcome.Kind == OutcomeKind.StorageUnavailable;
            }
        }
        finally
        {
            await RecordAsync(recorder, tally, storageRefused);
        }
    }

    /// <summary>Any due <c>Queued</c> or <c>Deferred</c> row (the catalogue's <c>HasWork</c>, arch N2).</summary>
    public async Task<bool> HasWorkAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        var now = Now;
        return await Due(context, now).AnyAsync(cancellationToken);
    }

    private static IQueryable<PodcastEpisodeAudio> Due(OdysseyContext context, DateTime now) =>
        from audio in context.PodcastEpisodeAudio
        join feed in context.NewsFeeds on audio.NewsFeedId equals feed.NewsFeedId
        where (audio.State == PodcastAudioState.Queued || audio.State == PodcastAudioState.Deferred)
            && (audio.NextAttemptAtUtc == null || audio.NextAttemptAtUtc <= now)
            && feed.Kind == NewsFeedKind.Podcast && feed.ArchivedAtUtc == null && feed.DeletionRequestedAtUtc == null
        select audio;

    // ── 1. Expired-lease recovery ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Every <c>Downloading</c> row whose lease expired goes back to <c>Queued</c> without spending an attempt, its byte
    /// reservation returned; one fenced save per row (§3.6 step 1). The store reservation it left goes to the sweep.
    /// </summary>
    public async Task<int> RecoverExpiredAsync(CancellationToken cancellationToken)
    {
        var now = Now;
        List<Guid> expired;
        await using (var scope = scopes.CreateAsyncScope())
        {
            expired = await scope.ServiceProvider.GetRequiredService<OdysseyContext>().PodcastEpisodeAudio.AsNoTracking()
                .Where(a => a.State == PodcastAudioState.Downloading && (a.LeaseExpiresAtUtc == null || a.LeaseExpiresAtUtc < now))
                .Select(a => a.NewsArticleId)
                .Take(CandidateWindow)
                .ToListAsync(cancellationToken);
        }

        var recovered = 0;
        foreach (var id in expired)
        {
            await using var scope = scopes.CreateAsyncScope();
            var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
            var row = await context.PodcastEpisodeAudio.FirstOrDefaultAsync(
                a => a.NewsArticleId == id && a.State == PodcastAudioState.Downloading && (a.LeaseExpiresAtUtc == null || a.LeaseExpiresAtUtc < now),
                cancellationToken);
            if (row is null)
            {
                continue;
            }

            var reserved = row.ReservedBytes;
            Requeue(row, now);
            if (await SaveWithTotalsAsync(context, new PodcastStorageDelta(AudioReservedBytes: -reserved), cancellationToken))
            {
                recovered++;
                logger.LogInformation("Podcast audio {EpisodeId}: an expired download lease was returned to the queue.", id);
            }
        }

        return recovered;
    }

    // ── 2. Claim ─────────────────────────────────────────────────────────────────────────────────────

    private sealed record Claim(Guid EpisodeId, Guid FeedId, Guid Fence);

    private async Task<IReadOnlyList<Claim>> ClaimAsync(CancellationToken cancellationToken)
    {
        var now = Now;
        List<(Guid Id, Guid FeedId, byte[] Host)> candidates;
        List<(Guid FeedId, byte[] Host)> busy;
        await using (var scope = scopes.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
            candidates = [.. (await Due(context, now).AsNoTracking()
                    .OrderBy(a => a.RequestedAtUtc).ThenBy(a => a.NewsArticleId)
                    .Select(a => new { a.NewsArticleId, a.NewsFeedId, a.EnclosureHostKeyHash })
                    .Take(CandidateWindow)
                    .ToListAsync(cancellationToken))
                .Select(a => (a.NewsArticleId, a.NewsFeedId, a.EnclosureHostKeyHash))];
            busy = [.. (await context.PodcastEpisodeAudio.AsNoTracking()
                    .Where(a => a.State == PodcastAudioState.Downloading)
                    .Select(a => new { a.NewsFeedId, a.EnclosureHostKeyHash })
                    .ToListAsync(cancellationToken))
                .Select(a => (a.NewsFeedId, a.EnclosureHostKeyHash))];
        }

        // The read-side exclusion (§3.6 step 2, arch S4): a busy feed's or host's rows are passed over, not each tried.
        var claims = new List<Claim>();
        foreach (var candidate in candidates)
        {
            if (claims.Count >= PodcastLimits.DownloadsPerRun)
            {
                break;
            }

            if (busy.Any(b => b.FeedId == candidate.FeedId || b.Host.AsSpan().SequenceEqual(candidate.Host)))
            {
                continue;
            }

            await using var scope = scopes.CreateAsyncScope();
            var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
            var row = await Due(context, now).FirstOrDefaultAsync(a => a.NewsArticleId == candidate.Id, cancellationToken);
            if (row is null)
            {
                continue;
            }

            var fence = Guid.NewGuid();
            row.State = PodcastAudioState.Downloading;
            row.LeaseOwner = owner;
            row.LeaseExpiresAtUtc = now + PodcastTiming.AudioLease;
            row.ReservedBytes = 0;
            row.AudioFence = fence;
            try
            {
                await context.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateConcurrencyException)
            {
                continue;
            }
            catch (DbUpdateException ex) when (IsMySql(ex, DuplicateKey))
            {
                // The guard: another replica is downloading this feed or host.
                busy.Add((candidate.FeedId, candidate.Host));
                continue;
            }

            busy.Add((candidate.FeedId, candidate.Host));
            claims.Add(new Claim(candidate.Id, candidate.FeedId, fence));
        }

        return claims;
    }

    // ── 3–11. One download ───────────────────────────────────────────────────────────────────────────

    private enum OutcomeKind
    {
        Stored,
        Failed,
        Retrying,
        Deferred,
        Requeued,
        StorageUnavailable,
        Stopped,
    }

    private sealed record Outcome(OutcomeKind Kind, long Bytes = 0, bool ReservationRefused = false);

    /// <summary>Why the download stopped from inside (a renewal saw the feed change, or lost the fence).</summary>
    private sealed class DownloadStoppedException(bool requeue) : Exception("The download was stopped.")
    {
        public bool Requeue { get; } = requeue;
    }

    private async Task<Outcome> DownloadAsync(Claim claim, NewsSettings settings, JobRunRecorder recorder, CancellationToken stoppingToken)
    {
        var cap = PodcastAudioSizeCeiling.Bytes(settings.PodcastAudioMaxEpisodeMb, fileStorage.Value.MaxFileSizeBytes);
        string enclosure;
        string feedUrl;
        IReadOnlyList<NewsPrivateHostGrant> grants;
        await using (var scope = scopes.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
            var row = await (from episode in context.PodcastEpisodes.AsNoTracking()
                             join feed in context.NewsFeeds.AsNoTracking() on episode.NewsFeedId equals feed.NewsFeedId
                             where episode.NewsArticleId == claim.EpisodeId
                             select new { episode.EnclosureUrl, feed.Url }).FirstOrDefaultAsync(stoppingToken);
            if (row is null)
            {
                return new Outcome(OutcomeKind.Stopped);
            }

            enclosure = row.EnclosureUrl;
            feedUrl = row.Url;
            grants = await scope.ServiceProvider.GetRequiredService<INewsSettingsLookup>().GetAllowListAsync(stoppingToken);
        }

        if (!Uri.TryCreate(enclosure, UriKind.Absolute, out var source) || !Uri.TryCreate(feedUrl, UriKind.Absolute, out var feedUri)
            || NewsUrlKey.CanonicalHost(feedUri) is not { } feedHost)
        {
            return await FailAsync(claim, PodcastAudioFailureKind.Refused, permanent: true, reserved: 0, stoppingToken);
        }

        var opened = await NewsStreamFetch.OpenAsync(
            httpClients.CreateClient(NewsEgress.AudioHttpClientName),
            source,
            new NewsEgressContext(feedHost, NewsEgressPurpose.Image, grants),
            PodcastLimits.AudioMaxRedirects,
            cap,
            TakeHostAsync,
            takeFirstHost: true,
            time,
            stoppingToken);
        if (opened.Outcome == NewsHttpOutcome.Throttled)
        {
            return await FailAsync(claim, PodcastAudioFailureKind.HttpError, permanent: false, reserved: 0, stoppingToken);
        }

        if (opened.Stream is not { } stream)
        {
            var (kind, permanent) = Classify(opened);
            return await FailAsync(claim, kind, permanent, reserved: 0, stoppingToken);
        }

        await using (stream)
        {
            if (stream.ContentLength is { } declared && declared > cap)
            {
                return await FailAsync(claim, PodcastAudioFailureKind.TooLarge, permanent: true, reserved: 0, stoppingToken);
            }

            var expected = stream.ContentLength ?? cap;
            var reservation = await ReserveAsync(claim, expected, settings, stoppingToken);
            if (reservation != Reservation.Taken)
            {
                return reservation switch
                {
                    Reservation.Quota => await DeferAsync(claim, PodcastAudioDeferredReason.AudioQuota, stoppingToken),
                    Reservation.Headroom => await DeferAsync(claim, PodcastAudioDeferredReason.DiskHeadroom, stoppingToken),
                    Reservation.Busy => await RequeueAsync(claim, reserved: 0, PodcastAudioState.Queued, stoppingToken),
                    _ => new Outcome(OutcomeKind.Stopped),
                };
            }

            StoredObjectReservation? written = null;
            try
            {
                var head = new byte[PodcastAudioFormat.HeadBytes];
                var headLength = await stream.Body.ReadAtLeastAsync(head, head.Length, throwOnEndOfStream: false, stoppingToken);
                if (PodcastAudioFormat.Detect(head.AsSpan(0, headLength)) is not { } type)
                {
                    return await FailAsync(claim, PodcastAudioFailureKind.NotAudio, permanent: true, expected, stoppingToken);
                }

                using var renewing = new RenewingStream(
                    new HeadThenRest(head, headLength, stream.Body), () => RenewAsync(claim, recorder, stoppingToken), time);
                await using var feedStore = scopes.CreateAsyncScope();
                var store = feedStore.ServiceProvider.GetRequiredService<IObjectStore>();
                written = await store.ReserveAndWriteAsync(
                    PodcastAudioStorage.FeatureKey, new PodcastAudioLayout(claim.FeedId, claim.EpisodeId), originalFileName: null, suffix: null,
                    type.ContentType, renewing, cap, stoppingToken);
                var committed = await CommitAsync(claim, written, type, expected, stoppingToken);
                if (!committed)
                {
                    await store.AbandonReservationAsync(written, CancellationToken.None);
                    await ReturnReservationAsync(expected);
                    return new Outcome(OutcomeKind.Stopped);
                }

                logger.LogInformation(
                    "Podcast audio {EpisodeId}: stored {Bytes} bytes from {Host}.", claim.EpisodeId, written.SizeBytes, stream.FinalHost);
                await ReadChaptersAsync(claim.EpisodeId, type.ContentType, stoppingToken);
                return new Outcome(OutcomeKind.Stored, written.SizeBytes);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
            {
                await AbandonAsync(written);
                return ex switch
                {
                    DownloadStoppedException stopped => await StopAsync(claim, expected, stopped.Requeue),
                    StoredObjectTooLargeException => await FailAsync(claim, PodcastAudioFailureKind.TooLarge, permanent: true, expected, CancellationToken.None),
                    NewsStreamAbortedException => await FailAsync(claim, PodcastAudioFailureKind.Timeout, permanent: false, expected, CancellationToken.None),
                    FileStorageFullException => await DeferAsync(claim, PodcastAudioDeferredReason.DiskHeadroom, CancellationToken.None, expected),
                    FileStorageUnavailableException => await StorageUnavailableAsync(claim, expected),
                    HttpRequestException or IOException => await FailAsync(claim, PodcastAudioFailureKind.HttpError, permanent: false, expected, CancellationToken.None),
                    _ => await UnexpectedAsync(claim, expected, ex),
                };
            }
            catch (OperationCanceledException)
            {
                // 11. Graceful stop: the row goes back to the queue without spending an attempt.
                await AbandonAsync(written);
                await RequeueAsync(claim, expected, PodcastAudioState.Queued, CancellationToken.None);
                throw;
            }
        }
    }

    private async Task<bool> TakeHostAsync(string host, CancellationToken cancellationToken)
    {
        // A busy slot frees within the 2 s spacing: wait it out a few times rather than spend an attempt on it.
        for (var attempt = 0; attempt < 5; attempt++)
        {
            if (await throttle.TryTakeAsync(host, cancellationToken) is not { } next)
            {
                return true;
            }

            var wait = next - Now;
            if (wait > TimeSpan.FromSeconds(5))
            {
                return false;
            }

            await Task.Delay(wait > TimeSpan.Zero ? wait : TimeSpan.FromMilliseconds(100), time, cancellationToken);
        }

        return false;
    }

    private static (PodcastAudioFailureKind Kind, bool Permanent) Classify(NewsStreamResult result) => result.ErrorKind switch
    {
        _ when result.EncodingRefused => (PodcastAudioFailureKind.Refused, true),
        NewsFeedErrorKind.Gone => (PodcastAudioFailureKind.Gone, true),
        NewsFeedErrorKind.BlockedAddress => (PodcastAudioFailureKind.Refused, true),
        NewsFeedErrorKind.TooManyRedirects => (PodcastAudioFailureKind.HttpError, true),
        NewsFeedErrorKind.Timeout => (PodcastAudioFailureKind.Timeout, false),
        NewsFeedErrorKind.HttpStatus when result.HttpStatus is >= 500 or 429 or 408 => (PodcastAudioFailureKind.HttpError, false),
        NewsFeedErrorKind.HttpStatus => (PodcastAudioFailureKind.HttpError, true),
        _ => (PodcastAudioFailureKind.HttpError, false),
    };

    // ── 4. Reserve ───────────────────────────────────────────────────────────────────────────────────

    private enum Reservation
    {
        Taken,
        Quota,
        Headroom,
        Busy,
        Lost,
    }

    /// <summary>
    /// §3.6 step 4: free space probed first, outside any lock; then one short transaction — the audio row's fenced
    /// update first, the totals row locked <c>FOR UPDATE</c> last — admits the download only while the quota and the
    /// headroom both hold with every other in-flight reservation counted.
    /// </summary>
    private async Task<Reservation> ReserveAsync(Claim claim, long expected, NewsSettings settings, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        var free = scope.ServiceProvider.GetRequiredService<PodcastFileWriter>().FreeBytes(PodcastAudioStorage.FeatureKey);
        if (free is < 0)
        {
            return Reservation.Headroom;
        }

        try
        {
            return await NewsTransactions.RunAsync(context, async () =>
            {
                var row = await context.PodcastEpisodeAudio.FirstOrDefaultAsync(
                    a => a.NewsArticleId == claim.EpisodeId && a.LeaseOwner == owner && a.AudioFence == claim.Fence, cancellationToken);
                if (row is null)
                {
                    return Reservation.Lost;
                }

                row.ReservedBytes = expected;
                await context.SaveChangesAsync(cancellationToken);

                var totals = context.Database.IsRelational()
                    ? (await context.NewsStorageTotals
                        .FromSqlInterpolated(
                            $"SET STATEMENT innodb_lock_wait_timeout = {LockWaitSeconds} FOR SELECT * FROM `NewsStorageTotals` WHERE `Id` = {NewsStorageTotal.SingletonId} FOR UPDATE")
                        .AsTracking()
                        .ToListAsync(cancellationToken)).SingleOrDefault()
                    : await context.NewsStorageTotals.FirstOrDefaultAsync(t => t.Id == NewsStorageTotal.SingletonId, cancellationToken);
                if (totals is null)
                {
                    throw new RefusedException(Reservation.Quota);
                }

                if (totals.AudioBytes + totals.AudioPendingReleaseBytes + totals.AudioReservedBytes + expected >= settings.PodcastAudioQuotaBytes)
                {
                    throw new RefusedException(Reservation.Quota);
                }

                if (free is { } measured && measured - totals.AudioReservedBytes - expected < NewsArticleWriter.HeadroomThresholdBytes)
                {
                    throw new RefusedException(Reservation.Headroom);
                }

                totals.AudioReservedBytes += expected;
                await context.SaveChangesAsync(cancellationToken);
                return Reservation.Taken;
            }, cancellationToken);
        }
        catch (RefusedException refused)
        {
            // The transaction rolled back the row's ReservedBytes with it; on InMemory it never left the tracker.
            await UndoInMemoryReservationAsync(claim, cancellationToken);
            return refused.Reason;
        }
        catch (DbUpdateConcurrencyException)
        {
            return Reservation.Lost;
        }
        catch (Exception ex) when (IsMySql(ex, LockWaitTimeout))
        {
            return Reservation.Busy;
        }
    }

    private sealed class RefusedException(Reservation reason) : Exception("The reservation was refused.")
    {
        public Reservation Reason { get; } = reason;
    }

    /// <summary>EF InMemory honours no transaction: a refused reservation's row write is undone by hand.</summary>
    private async Task UndoInMemoryReservationAsync(Claim claim, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        if (context.Database.IsRelational())
        {
            return;
        }

        var row = await context.PodcastEpisodeAudio.FirstOrDefaultAsync(
            a => a.NewsArticleId == claim.EpisodeId && a.LeaseOwner == owner && a.AudioFence == claim.Fence, cancellationToken);
        if (row is not null)
        {
            row.ReservedBytes = 0;
            await context.SaveChangesAsync(cancellationToken);
        }
    }

    /// <summary>A reservation whose row is gone (a lost fence): only the total is returned.</summary>
    private async Task ReturnReservationAsync(long bytes)
    {
        await using var scope = scopes.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        await counters.AddToPodcastStorageAsync(context, new PodcastStorageDelta(AudioReservedBytes: -bytes), CancellationToken.None);
    }

    // ── 7. Lease renewal ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A fenced save that re-reads the feed (§3.6 step 7): archived → stop and requeue; pending deletion or no longer a
    /// podcast → stop, back to <c>None</c>; a lost fence (a user remove or cancel) → stop.
    /// </summary>
    private async Task RenewAsync(Claim claim, JobRunRecorder recorder, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        var row = await context.PodcastEpisodeAudio.FirstOrDefaultAsync(
            a => a.NewsArticleId == claim.EpisodeId && a.LeaseOwner == owner && a.AudioFence == claim.Fence, cancellationToken)
            ?? throw new DownloadStoppedException(requeue: false);
        var feed = await context.NewsFeeds.AsNoTracking().FirstOrDefaultAsync(f => f.NewsFeedId == claim.FeedId, cancellationToken);
        if (feed is null || feed.Kind != NewsFeedKind.Podcast || feed.DeletionRequestedAtUtc is not null)
        {
            throw new DownloadStoppedException(requeue: false);
        }

        if (feed.ArchivedAtUtc is not null)
        {
            throw new DownloadStoppedException(requeue: true);
        }

        row.LeaseExpiresAtUtc = Now + PodcastTiming.AudioLease;
        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new DownloadStoppedException(requeue: false);
        }

        await recorder.HeartbeatAsync(JobType.PodcastAudioDownload, cancellationToken);
    }

    // ── 8. Commit ────────────────────────────────────────────────────────────────────────────────────

    private async Task<bool> CommitAsync(Claim claim, StoredObjectReservation written, PodcastAudioType type, long expected, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        var store = scope.ServiceProvider.GetRequiredService<IObjectStore>();
        try
        {
            return await NewsTransactions.RunAsync(context, async () =>
            {
                context.ChangeTracker.Clear();
                var row = await context.PodcastEpisodeAudio.FirstOrDefaultAsync(
                    a => a.NewsArticleId == claim.EpisodeId && a.LeaseOwner == owner && a.AudioFence == claim.Fence, cancellationToken);
                if (row is null)
                {
                    return false;
                }

                var stored = store.AttachReserved(context, written);
                var reserved = row.ReservedBytes;
                row.State = PodcastAudioState.Stored;
                row.StoredObjectId = stored.Id;
                row.ContentType = type.ContentType;
                row.SizeBytes = written.SizeBytes;
                row.Sha256 = Convert.FromHexString(written.Sha256Hash);
                row.StoredAtUtc = Now;
                row.ReservedBytes = 0;
                row.Attempts = 0;
                row.NextAttemptAtUtc = null;
                row.FailureKind = null;
                row.DeferredReason = null;
                row.LeaseOwner = null;
                row.LeaseExpiresAtUtc = null;
                await context.SaveChangesAsync(cancellationToken);
                await counters.AddToPodcastStorageAsync(
                    context, new PodcastStorageDelta(AudioBytes: written.SizeBytes, AudioReservedBytes: -(reserved == 0 ? expected : reserved)), cancellationToken);
                return true;
            }, cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return false;
        }
    }

    // ── 9. Chapter markers ───────────────────────────────────────────────────────────────────────────

    private async Task ReadChaptersAsync(Guid episodeId, string contentType, CancellationToken stoppingToken)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
            var storedObjectId = await context.PodcastEpisodeAudio.AsNoTracking()
                .Where(a => a.NewsArticleId == episodeId && a.State == PodcastAudioState.Stored)
                .Select(a => a.StoredObjectId).FirstOrDefaultAsync(stoppingToken);
            if (storedObjectId is not { } id)
            {
                return;
            }

            IReadOnlyList<PodcastRawChapter> raw;
            var content = await scope.ServiceProvider.GetRequiredService<IObjectStore>().OpenReadAsync(PodcastAudioStorage.FeatureKey, id, stoppingToken);
            await using (content.Content)
            {
                using var deadline = new CancellationTokenSource(PodcastAudioChapters.Deadline, time);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, deadline.Token);
                try
                {
                    raw = await PodcastAudioChapters.ReadAsync(content.Content, contentType, linked.Token);
                }
                catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
                {
                    raw = [];
                }
            }

            var chapters = PodcastFields.NormalizeChapters(raw);
            await PodcastChapterRows.ReplaceInPlaceAsync(context, episodeId, PodcastChapterSource.AudioFile, chapters, stoppingToken);
            await context.SaveChangesAsync(stoppingToken);
            if (chapters.Count > 0)
            {
                logger.LogInformation("Podcast audio {EpisodeId}: {Count} chapters read from the file.", episodeId, chapters.Count);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("Podcast audio {EpisodeId}: reading its chapter markers failed ({Failure}).", episodeId, ex.GetType().Name);
        }
    }

    // ── 10. Failure, deferral, stop ──────────────────────────────────────────────────────────────────

    private async Task<Outcome> FailAsync(Claim claim, PodcastAudioFailureKind kind, bool permanent, long reserved, CancellationToken cancellationToken)
    {
        var final = false;
        var saved = await EndAsync(claim, row =>
        {
            row.FailureKind = kind;
            row.DeferredReason = null;
            row.Attempts = (byte)Math.Min(byte.MaxValue, row.Attempts + 1);
            if (!permanent && row.Attempts <= PodcastTiming.Backoff.Length)
            {
                row.State = PodcastAudioState.Queued;
                row.NextAttemptAtUtc = Now + PodcastTiming.Backoff[row.Attempts - 1];
            }
            else
            {
                row.State = PodcastAudioState.Failed;
                row.NextAttemptAtUtc = null;
                final = true;
            }
        }, reserved, cancellationToken);
        if (saved)
        {
            logger.LogInformation("Podcast audio {EpisodeId}: download failed ({Kind}{Final}).", claim.EpisodeId, kind, final ? ", final" : ", will retry");
        }

        return new Outcome(final ? OutcomeKind.Failed : OutcomeKind.Retrying);
    }

    private async Task<Outcome> DeferAsync(Claim claim, PodcastAudioDeferredReason reason, CancellationToken cancellationToken, long reserved = 0)
    {
        await EndAsync(claim, row =>
        {
            row.State = PodcastAudioState.Deferred;
            row.DeferredReason = reason;
            row.FailureKind = null;
            row.NextAttemptAtUtc = Now + PodcastTiming.DeferredRetry;
        }, reserved, cancellationToken);
        logger.LogInformation("Podcast audio {EpisodeId}: deferred ({Reason}).", claim.EpisodeId, reason);
        return new Outcome(OutcomeKind.Deferred, ReservationRefused: true);
    }

    private async Task<Outcome> RequeueAsync(Claim claim, long reserved, PodcastAudioState state, CancellationToken cancellationToken)
    {
        await EndAsync(claim, row =>
        {
            if (state == PodcastAudioState.Queued)
            {
                Requeue(row, Now);
            }
            else
            {
                row.State = PodcastAudioState.None;
                row.RequestedBy = null;
                row.RequestedAtUtc = null;
                row.NextAttemptAtUtc = null;
            }
        }, reserved, cancellationToken);
        return new Outcome(OutcomeKind.Requeued);
    }

    private async Task<Outcome> StopAsync(Claim claim, long reserved, bool requeue)
    {
        // A lost fence leaves the row to whoever rotated it; only the total is ours to return.
        if (!await EndAsync(claim, row =>
            {
                if (requeue)
                {
                    Requeue(row, Now);
                }
                else
                {
                    row.State = PodcastAudioState.None;
                    row.RequestedBy = null;
                    row.RequestedAtUtc = null;
                    row.NextAttemptAtUtc = null;
                }
            }, reserved, CancellationToken.None))
        {
            await ReturnReservationAsync(reserved);
        }

        logger.LogInformation("Podcast audio {EpisodeId}: download stopped ({Next}).", claim.EpisodeId, requeue ? "queued for later" : "cancelled");
        return new Outcome(OutcomeKind.Stopped);
    }

    private async Task<Outcome> StorageUnavailableAsync(Claim claim, long reserved)
    {
        await RequeueAsync(claim, reserved, PodcastAudioState.Queued, CancellationToken.None);
        return new Outcome(OutcomeKind.StorageUnavailable);
    }

    private async Task<Outcome> UnexpectedAsync(Claim claim, long reserved, Exception exception)
    {
        logger.LogError("Podcast audio {EpisodeId}: the download failed unexpectedly ({Failure}).", claim.EpisodeId, exception.GetType().Name);
        return await FailAsync(claim, PodcastAudioFailureKind.HttpError, permanent: false, reserved, CancellationToken.None);
    }

    /// <summary>
    /// The save that ends a download: the row's change, the lease cleared, its byte reservation returned — fenced, with
    /// the totals row last. <c>false</c> when the fence was lost.
    /// </summary>
    private async Task<bool> EndAsync(Claim claim, Action<PodcastEpisodeAudio> change, long reserved, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        var row = await context.PodcastEpisodeAudio.FirstOrDefaultAsync(
            a => a.NewsArticleId == claim.EpisodeId && a.LeaseOwner == owner && a.AudioFence == claim.Fence, cancellationToken);
        if (row is null)
        {
            return false;
        }

        var held = row.ReservedBytes != 0 ? row.ReservedBytes : reserved;
        change(row);
        row.ReservedBytes = 0;
        row.LeaseOwner = null;
        row.LeaseExpiresAtUtc = null;
        return await SaveWithTotalsAsync(context, new PodcastStorageDelta(AudioReservedBytes: -held), cancellationToken);
    }

    private async Task<bool> SaveWithTotalsAsync(OdysseyContext context, PodcastStorageDelta delta, CancellationToken cancellationToken)
    {
        try
        {
            await NewsTransactions.RunAsync(context, async () =>
            {
                await context.SaveChangesAsync(cancellationToken);
                await counters.AddToPodcastStorageAsync(context, delta, cancellationToken);
            }, cancellationToken);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            return false;
        }
    }

    private static void Requeue(PodcastEpisodeAudio row, DateTime due)
    {
        row.State = PodcastAudioState.Queued;
        row.NextAttemptAtUtc = due;
        row.ReservedBytes = 0;
        row.LeaseOwner = null;
        row.LeaseExpiresAtUtc = null;
    }

    private async Task AbandonAsync(StoredObjectReservation? written)
    {
        if (written is null)
        {
            return;
        }

        try
        {
            await using var scope = scopes.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<IObjectStore>().AbandonReservationAsync(written, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogWarning("Abandoning a podcast audio reservation failed ({Failure}); the sweep removes it.", ex.GetType().Name);
        }
    }

    private static bool IsMySql(Exception exception, int number)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is MySqlException mysql && mysql.Number == number)
            {
                return true;
            }
        }

        return false;
    }

    // ── Run log ──────────────────────────────────────────────────────────────────────────────────────

    private sealed class Tally
    {
        public int Stored { get; private set; }

        public int Failed { get; private set; }

        public int Deferred { get; private set; }

        public int Recovered { get; set; }

        public int Refused { get; private set; }

        public long Bytes { get; private set; }

        public bool HasWork => Stored + Failed + Deferred + Recovered + Refused > 0;

        public void Add(Outcome outcome)
        {
            switch (outcome.Kind)
            {
                case OutcomeKind.Stored:
                    Stored++;
                    Bytes += outcome.Bytes;
                    break;
                case OutcomeKind.Failed:
                case OutcomeKind.Retrying:
                    Failed++;
                    break;
                case OutcomeKind.Deferred:
                    Deferred++;
                    break;
            }

            Refused += outcome.ReservationRefused ? 1 : 0;
        }

        public void AddTo(PodcastAudioDownloadJobRun run)
        {
            run.DownloadsStored += Stored;
            run.DownloadsFailed += Failed;
            run.DownloadsDeferred += Deferred;
            run.DownloadsRecovered += Recovered;
            run.ReservationsRefused += Refused;
            run.AudioBytesStored += Bytes;
        }
    }

    private static async Task RecordAsync(JobRunRecorder recorder, Tally tally, bool storageRefused)
    {
        if (tally.HasWork)
        {
            await recorder.ExtendBurstAsync<PodcastAudioDownloadJobRun>(JobType.PodcastAudioDownload, tally.AddTo, CancellationToken.None);
        }
        else
        {
            await recorder.CloseBurstAsync(JobType.PodcastAudioDownload, CancellationToken.None);
        }

        if (storageRefused)
        {
            await recorder.HoldAsync<PodcastAudioDownloadJobRun>(JobType.PodcastAudioDownload, JobHeldReason.StorageUnavailable, null, CancellationToken.None);
        }
        else
        {
            await recorder.ReleaseHoldAsync(JobType.PodcastAudioDownload, CancellationToken.None);
        }
    }

    // ── Streams ──────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The detection head already read, then the rest of the body.</summary>
    private sealed class HeadThenRest(byte[] head, int headLength, Stream rest) : Stream
    {
        private int position;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (position < headLength)
            {
                var count = Math.Min(buffer.Length, headLength - position);
                head.AsMemory(position, count).CopyTo(buffer);
                position += count;
                return count;
            }

            return await rest.ReadAsync(buffer, cancellationToken);
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>Renews the lease every <see cref="PodcastTiming.AudioLeaseRenewal"/> while bytes flow.</summary>
    private sealed class RenewingStream(Stream inner, Func<Task> renew, TimeProvider time) : Stream
    {
        private long lastRenewal = time.GetTimestamp();

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (time.GetElapsedTime(lastRenewal) >= PodcastTiming.AudioLeaseRenewal)
            {
                await renew();
                lastRenewal = time.GetTimestamp();
            }

            return await inner.ReadAsync(buffer, cancellationToken);
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
