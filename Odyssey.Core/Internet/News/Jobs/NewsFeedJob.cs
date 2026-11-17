using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Odyssey.Context;
using Odyssey.Core.FileStorage;
using Odyssey.Core.Internet.News.Fetching;
using Odyssey.Core.Jobs;
using Odyssey.Dtos.Application;
using Odyssey.Dtos.Internet;

namespace Odyssey.Core.Internet.News.Jobs;

/// <summary>The counts of one tick, shared by the concurrent fetches of that tick.</summary>
public sealed class NewsJobTally
{
    private int feedsFetched;
    private int itemsStored;
    private int itemsRevised;
    private int imagesStored;
    private int feedsQuarantined;

    public int FeedsFetched => feedsFetched;

    public int ItemsStored => itemsStored;

    public int ItemsRevised => itemsRevised;

    public int ImagesStored => imagesStored;

    public int FeedsQuarantined => feedsQuarantined;

    public bool HasWork => feedsFetched + itemsStored + itemsRevised + imagesStored + feedsQuarantined > 0;

    public void Fetched(int stored, int revised)
    {
        Interlocked.Increment(ref feedsFetched);
        Interlocked.Add(ref itemsStored, stored);
        Interlocked.Add(ref itemsRevised, revised);
    }

    public void Images(int stored) => Interlocked.Add(ref imagesStored, stored);

    public void Quarantined() => Interlocked.Increment(ref feedsQuarantined);

    public void AddTo(NewsFeedJobRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        run.FeedsFetched += FeedsFetched;
        run.ItemsStored += ItemsStored;
        run.ItemsRevised += ItemsRevised;
        run.ImagesStored += ImagesStored;
        run.FeedsQuarantined += FeedsQuarantined;
    }
}

/// <summary>
/// The news reader's job (issue #470 §3.4), catalogued as <see cref="JobType.NewsFeedFetch"/>: every 30 s on every
/// worker replica — purge, crash accounting, claim due feeds, fetch (at most 4 at once and one per host per process),
/// images, and the hourly maintenance. It holds no schedule in memory: every claim is a tracked, fenced update of the
/// feed's fetch-state row, so replicas coordinate through the rows alone. A full text quota is a per-feed transient
/// skip reported as <c>QuotaReason = TextQuota</c>, never a held reason (§5.5 item 27).
/// </summary>
public sealed class NewsFeedJob(
    IServiceScopeFactory scopes,
    NewsFeedFetcher fetcher,
    NewsImageDownloader images,
    NewsFeedPurger purger,
    NewsMaintenance maintenance,
    NewsHostThrottleGate throttle,
    INewsFetchStateFlags flags,
    FileStorageVolumeRegistry volumes,
    FileStorageWorkerVerification verification,
    TimeProvider time,
    ILogger<NewsFeedJob> logger) : IPeriodicJob
{
    /// <summary>This process's lease owner: every fetch, purge and image lease it takes carries it.</summary>
    private readonly Guid owner = Guid.NewGuid();

    /// <summary>Per feed, the full parses so far that had a deterministic item failure (§3.4 step 6).</summary>
    private readonly ConcurrentDictionary<Guid, int> itemFailureParses = new();

    private DateTime? lastMaintenance;
    private bool spreadDone;

    public string Name => "news-feeds";

    public TimeSpan Interval => NewsJobTiming.Tick;

    public Guid Owner => owner;

    private DateTime Now => time.GetUtcNow().UtcDateTime;

    public async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        JobRunRecorder recorder;
        await using (var scope = scopes.CreateAsyncScope())
        {
            recorder = scope.ServiceProvider.GetRequiredService<JobRunRecorder>();
        }

        // The heartbeat first, before any early return (issue #409 §3.3 step 1).
        await recorder.HeartbeatAsync(JobType.NewsFeedFetch, cancellationToken);
        var tally = new NewsJobTally();
        Guid? manualRunId = null;
        var manualMarked = 0;
        JobHeldReason? held = null;
        JobFailureCategory? failure = null;
        try
        {
            (held, manualRunId, manualMarked) = await RunPhasesAsync(recorder, tally, cancellationToken);
        }
        catch (Exception ex)
        {
            failure = ex is OperationCanceledException ? JobFailureCategory.Cancelled : JobFailureCategory.Internal;
            throw;
        }
        finally
        {
            await RecordTickAsync(recorder, tally, held, manualRunId, manualMarked, failure);
        }
    }

    private async Task<(JobHeldReason? Held, Guid? ManualRunId, int Marked)> RunPhasesAsync(
        JobRunRecorder recorder, NewsJobTally tally, CancellationToken cancellationToken)
    {
        NewsSettings settings;
        await using (var scope = scopes.CreateAsyncScope())
        {
            settings = await scope.ServiceProvider.GetRequiredService<INewsSettingsLookup>().GetAsync(cancellationToken);
        }

        // 1. Purge, and the FeedVisible rewrite. Database work only: it needs no healthy volume.
        await purger.RunAsync(owner, cancellationToken);

        // The manual run (§5.5 item 27) marks eligible feeds due; the ordinary ticks then fetch them under their leases.
        var manualRunId = await recorder.ClaimManualAsync(JobType.NewsFeedFetch, cancellationToken);
        var marked = manualRunId is null ? 0 : await MarkDueAsync(cancellationToken);

        // 7. Crash accounting: an expired lease is a crash event; at three a feed is quarantined.
        await DetectCrashesAsync(tally, cancellationToken);

        var now = Now;
        if (Now - lastMaintenance is not { } sinceMaintenance || sinceMaintenance >= NewsJobTiming.Maintenance)
        {
            // 9. Hourly, per replica, idempotent.
            lastMaintenance = now;
            await maintenance.RunAsync(settings, volumes.IsActive, cancellationToken);
        }

        if (!await HasFetchWorkAsync(cancellationToken))
        {
            return (null, manualRunId, marked);
        }

        if (await BreakerTrippedAsync(cancellationToken))
        {
            return (JobHeldReason.BreakerTripped, manualRunId, marked);
        }

        if (!volumes.IsActive)
        {
            // Nothing could be stored: this worker's storage checks fail, or no volume is set up here.
            return (await VerdictFailingAsync(cancellationToken) ? JobHeldReason.StorageCheckFailed : JobHeldReason.StorageUnavailable, manualRunId, marked);
        }

        if (!spreadDone)
        {
            spreadDone = true;
            await SpreadOverdueAsync(cancellationToken);
        }

        // 2–6. Claim and fetch.
        var storageRefused = await FetchDueAsync(settings, tally, cancellationToken);

        // 8. Images.
        var imagePhase = await images.RunAsync(owner, settings, cancellationToken);
        tally.Images(imagePhase.Stored);

        return (storageRefused || imagePhase.StorageRefused ? JobHeldReason.StorageUnavailable : null, manualRunId, marked);
    }

    // ── Claim and fetch ──────────────────────────────────────────────────────────────────────────────

    /// <summary>Claims up to <see cref="NewsJobTiming.FetchSlots"/> due feeds, one per host, and fetches them concurrently.</summary>
    private async Task<bool> FetchDueAsync(NewsSettings settings, NewsJobTally tally, CancellationToken cancellationToken)
    {
        var claims = await ClaimDueAsync(settings, NewsJobTiming.FetchSlots, cancellationToken);
        if (claims.Count == 0)
        {
            return false;
        }

        IReadOnlyList<Dtos.NewsPrivateHostGrant> grants;
        await using (var scope = scopes.CreateAsyncScope())
        {
            // Read uncached for this tick's fetches (§7.9): a revoked grant binds on the next fetch.
            grants = await scope.ServiceProvider.GetRequiredService<INewsSettingsLookup>().GetAllowListAsync(cancellationToken);
        }

        var results = await Task.WhenAll(claims.Select(claim =>
            fetcher.RunAsync(claim, settings, grants, itemFailureParses.GetValueOrDefault(claim.FeedId), cancellationToken)));
        var storageRefused = false;
        for (var k = 0; k < claims.Count; k++)
        {
            if (results[k] is not { } result)
            {
                continue;
            }

            if (result.Kind is NewsFetchResultKind.Success or NewsFetchResultKind.NotModified or NewsFetchResultKind.Failure)
            {
                tally.Fetched(result.ItemsStored, result.ItemsRevised);
            }

            if (result.Kind == NewsFetchResultKind.Success && result.ItemFailures > 0)
            {
                itemFailureParses.AddOrUpdate(claims[k].FeedId, 1, (_, n) => n + 1);
            }
            else if (result.Kind == NewsFetchResultKind.Success)
            {
                itemFailureParses.TryRemove(claims[k].FeedId, out _);
            }

            storageRefused |= result.StorageRefused;
        }

        return storageRefused;
    }

    /// <summary>The due feeds of §3.4 step 2, oldest first.</summary>
    public static IQueryable<NewsFeedFetchState> Due(OdysseyContext context, DateTime now)
    {
        var refreshCutoff = now - NewsJobTiming.RefreshFloor;
        return from state in context.NewsFeedFetchStates
               join feed in context.NewsFeeds on state.NewsFeedId equals feed.NewsFeedId
               where feed.IsActive && feed.ArchivedAtUtc == null && feed.DeletionRequestedAtUtc == null
                   && state.QuarantinedAtUtc == null && state.LeaseOwner == null
                   && ((state.NextFetchAtUtc <= now
                           // A 410 deactivates the feed (§9.3) until a URL change made after it is pending.
                           && (state.LastErrorKind != NewsFeedErrorKind.Gone
                               || (state.ValidatorsResetRequestedAtUtc != null && state.ValidatorsResetRequestedAtUtc > state.LastFetchAttemptAtUtc)))
                       || (state.RefreshRequestedAtUtc != null && state.RefreshRequestedAtUtc <= now
                           && (state.LastFetchAttemptAtUtc == null || state.LastFetchAttemptAtUtc <= refreshCutoff)))
               select state;
    }

    /// <summary>
    /// Claims (§3.4 step 2): a tracked per-feed update of the fetch-state row — never <c>ExecuteUpdate</c> — taking only
    /// a feed whose host's throttle slot is free, and only one feed per host per process. A replica that loses the
    /// token race skips that feed.
    /// </summary>
    public async Task<IReadOnlyList<NewsClaim>> ClaimDueAsync(NewsSettings settings, int slots, CancellationToken cancellationToken)
    {
        var now = Now;
        List<Guid> candidates;
        await using (var scope = scopes.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
            candidates = await Due(context, now).AsNoTracking()
                .OrderBy(s => s.NextFetchAtUtc).ThenBy(s => s.NewsFeedId)
                .Select(s => s.NewsFeedId)
                .Take(50)
                .ToListAsync(cancellationToken);
        }

        var claims = new List<NewsClaim>();
        var hosts = new HashSet<string>(StringComparer.Ordinal);
        foreach (var feedId in candidates)
        {
            if (claims.Count >= slots)
            {
                break;
            }

            await using var scope = scopes.CreateAsyncScope();
            var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
            var feed = await context.NewsFeeds.AsNoTracking().FirstOrDefaultAsync(f => f.NewsFeedId == feedId, cancellationToken);
            var host = feed is not null && Uri.TryCreate(feed.Url, UriKind.Absolute, out var url) ? NewsUrlKey.CanonicalHost(url) : null;
            if (feed is null || (host is not null && !hosts.Add(host)))
            {
                continue;
            }

            if (host is not null && await throttle.TryTakeAsync(host, cancellationToken) is not null)
            {
                hosts.Remove(host);
                continue;
            }

            var state = await Due(context, now).FirstOrDefaultAsync(s => s.NewsFeedId == feedId, cancellationToken);
            if (state is null)
            {
                continue;
            }

            var interval = NewsFetchRules.EffectiveInterval(feed.FetchIntervalMinutes, settings.DefaultFetchIntervalMinutes, state.FeedTtlMinutes);
            var fence = Guid.NewGuid();
            var validatorsReset = state.ValidatorsResetRequestedAtUtc is not null;
            NewsFetchRules.RollDay(state, DateOnly.FromDateTime(now));
            var claim = new NewsClaim
            {
                FeedId = feedId,
                Url = feed.Url,
                Owner = owner,
                Fence = fence,
                ClaimTime = now,
                Interval = interval,
                DownloadImages = feed.DownloadImages,
                FeedVisible = feed.ArchivedAtUtc is null,
                ETag = state.ETag,
                LastModified = state.LastModified,
                LastBodySha256 = state.LastBodySha256,
                ValidatorsResetPending = validatorsReset,
                ConsecutiveFailures = state.ConsecutiveFailures,
                Language = state.Language,
                SuggestedUrl = state.SuggestedUrl,
                ArticlesToday = state.ArticlesToday,
            };

            state.LeaseOwner = owner;
            state.LeaseExpiresAtUtc = now + NewsJobTiming.Lease;
            state.NextFetchAtUtc = now + (interval > NewsJobTiming.ProvisionalMinimum ? interval : NewsJobTiming.ProvisionalMinimum);
            state.CrashSuspectCount = (byte)Math.Min(byte.MaxValue, state.CrashSuspectCount + 1);
            state.LastFetchAttemptAtUtc = now;
            state.FetchFence = fence;
            try
            {
                await context.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateConcurrencyException)
            {
                continue;
            }

            claims.Add(claim);
        }

        return claims;
    }

    // ── Crash accounting and the breaker (§3.4 step 7) ───────────────────────────────────────────────

    /// <summary>
    /// Each lease that expired without completing is one crash event (recorded with no feed id). The claim's increment
    /// of <c>CrashSuspectCount</c> then stands: only a completion resets it and only a graceful stop puts it back, so
    /// the count is the feed's run of consecutive crash events. At <see cref="NewsJobTiming.QuarantineAt"/> the feed is
    /// quarantined. A purge's expired lease clears without an event.
    /// </summary>
    public async Task DetectCrashesAsync(NewsJobTally tally, CancellationToken cancellationToken)
    {
        var now = Now;
        List<(Guid FeedId, Guid Owner)> expired;
        await using (var scope = scopes.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
            expired = (await context.NewsFeedFetchStates.AsNoTracking()
                    .Where(s => s.LeaseOwner != null && s.LeaseExpiresAtUtc < now)
                    .Select(s => new { s.NewsFeedId, Owner = s.LeaseOwner!.Value })
                    .ToListAsync(cancellationToken))
                .Select(s => (s.NewsFeedId, s.Owner))
                .ToList();
        }

        var inFlight = expired.GroupBy(e => e.Owner).ToDictionary(g => g.Key, g => g.Count());
        foreach (var (feedId, leaseOwner) in expired)
        {
            await using var scope = scopes.CreateAsyncScope();
            var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
            var state = await context.NewsFeedFetchStates.FirstOrDefaultAsync(
                s => s.NewsFeedId == feedId && s.LeaseOwner == leaseOwner && s.LeaseExpiresAtUtc < now, cancellationToken);
            if (state is null)
            {
                continue;
            }

            var pendingDeletion = await context.NewsFeeds.AsNoTracking()
                .AnyAsync(f => f.NewsFeedId == feedId && f.DeletionRequestedAtUtc != null, cancellationToken);
            state.LeaseOwner = null;
            state.LeaseExpiresAtUtc = null;
            state.FetchFence = Guid.NewGuid();
            var quarantined = false;
            if (!pendingDeletion)
            {
                context.NewsFeedCrashEvents.Add(new NewsFeedCrashEvent
                {
                    NewsFeedCrashEventId = Guid.NewGuid(), LeaseOwner = leaseOwner, DetectedAtUtc = now, FeedsInFlight = inFlight[leaseOwner],
                });
                if (state.CrashSuspectCount >= NewsJobTiming.QuarantineAt && state.QuarantinedAtUtc is null)
                {
                    state.QuarantinedAtUtc = now;
                    state.LastErrorKind = NewsFeedErrorKind.Quarantined;
                    state.LastErrorAtUtc = now;
                    state.LastErrorMessage = "The feed was set aside after repeated crashes while it was being read.";
                    quarantined = true;
                }
            }

            try
            {
                await context.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateConcurrencyException)
            {
                // Another replica recorded this expiry first.
                continue;
            }

            if (quarantined)
            {
                tally.Quarantined();
                logger.LogWarning("News feed {FeedId} is quarantined after repeated crashes while it was being read.", feedId);
            }
            else if (!pendingDeletion)
            {
                logger.LogWarning("The fetch of news feed {FeedId} did not complete before its lease expired; it counts as a crash.", feedId);
            }
        }
    }

    /// <summary>Three crash events within an hour trip the breaker for an hour (the thumbnail precedent).</summary>
    public async Task<bool> BreakerTrippedAsync(CancellationToken cancellationToken)
    {
        var since = Now - NewsJobTiming.BreakerWindow;
        await using var scope = scopes.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        return await context.NewsFeedCrashEvents.CountAsync(e => e.DetectedAtUtc >= since, cancellationToken) >= NewsJobTiming.BreakerEvents;
    }

    // ── The manual run, the startup spread and the work checks ──────────────────────────────────────

    /// <summary>
    /// The manual run (§5.5 item 27): every eligible feed whose last attempt is at least 15 minutes old is marked due by
    /// the conditional refresh write (§3.3), in chunks. Returns how many were marked (<c>FeedsMarked</c>).
    /// </summary>
    public async Task<int> MarkDueAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        return await NewsManualRun.MarkDueAsync(context, flags, Now, cancellationToken);
    }

    /// <summary>At startup, overdue feeds (fetched before) are spread over the next 10 minutes (§9.3).</summary>
    private async Task SpreadOverdueAsync(CancellationToken cancellationToken)
    {
        var now = Now;
        await using var scope = scopes.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        var overdue = await context.NewsFeedFetchStates
            .Where(s => s.NextFetchAtUtc < now && s.LeaseOwner == null && s.LastFetchAttemptAtUtc != null && s.RefreshRequestedAtUtc == null)
            .Take(1000)
            .ToListAsync(cancellationToken);
        if (overdue.Count < 2)
        {
            return;
        }

        foreach (var state in overdue)
        {
            state.NextFetchAtUtc = now + TimeSpan.FromSeconds(Random.Shared.Next((int)NewsJobTiming.StartupSpread.TotalSeconds));
            state.FetchFence = Guid.NewGuid();
            try
            {
                await context.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateConcurrencyException)
            {
                context.Entry(state).State = EntityState.Detached;
            }
        }
    }

    private async Task<bool> HasFetchWorkAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        var now = Now;
        return await Due(context, now).AnyAsync(cancellationToken)
            || await context.NewsArticleImages.AnyAsync(i => (i.State == NewsArticleImageState.Pending || i.State == NewsArticleImageState.Deferred)
                && (i.NextAttemptAtUtc == null || i.NextAttemptAtUtc <= now), cancellationToken);
    }

    /// <summary>
    /// What the manual-run endpoint asks (§11.1 item 4): any feed due, any image <c>Pending</c> and due, or any feed
    /// pending deletion. A request with none of them is finished <c>Skipped</c> at once.
    /// </summary>
    public static async Task<bool> HasWorkAsync(OdysseyContext context, DateTime now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        return await Due(context, now).AnyAsync(cancellationToken)
            || await context.NewsArticleImages.AnyAsync(i => i.State == NewsArticleImageState.Pending
                && (i.NextAttemptAtUtc == null || i.NextAttemptAtUtc <= now), cancellationToken)
            || await context.NewsFeeds.AnyAsync(f => f.DeletionRequestedAtUtc != null, cancellationToken);
    }

    private async Task<bool> VerdictFailingAsync(CancellationToken cancellationToken)
    {
        var cutoff = Now - FileStorageWorkerVerification.VerdictTtl;
        await using var scope = scopes.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        return await context.FileStorageWorkerVerdicts.AsNoTracking()
            .AnyAsync(v => v.WorkerId == verification.WorkerId && v.LastReportedAtUtc >= cutoff, cancellationToken);
    }

    // ── The run log ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The tick into the run log (issue #409 §3.2–§3.3): a manual run is finished with <c>FeedsMarked</c>, a tick that
    /// did work extends this process's burst, an idle tick closes it, and a held tick opens (or keeps) its held period.
    /// </summary>
    private async Task RecordTickAsync(
        JobRunRecorder recorder, NewsJobTally tally, JobHeldReason? held, Guid? manualRunId, int marked, JobFailureCategory? failure)
    {
        if (manualRunId is { } runId)
        {
            await recorder.FinishAsync<NewsFeedJobRun>(JobType.NewsFeedFetch, runId, run => run.FeedsMarked = marked,
                failure is { } category ? (JobRunStatus.Failed, category) : (JobRunStatus.Succeeded, null), CancellationToken.None);
        }

        if (tally.HasWork)
        {
            await recorder.ExtendBurstAsync<NewsFeedJobRun>(JobType.NewsFeedFetch, tally.AddTo, CancellationToken.None);
        }
        else
        {
            await recorder.CloseBurstAsync(JobType.NewsFeedFetch, CancellationToken.None);
        }

        if (held is { } reason)
        {
            await recorder.HoldAsync<NewsFeedJobRun>(JobType.NewsFeedFetch, reason, null, CancellationToken.None);
        }
        else
        {
            await recorder.ReleaseHoldAsync(JobType.NewsFeedFetch, CancellationToken.None);
        }
    }
}
