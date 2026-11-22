using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Odyssey.Context;
using Odyssey.Core.Imaging;
using Odyssey.Core.Internet.News.Fetching;
using Odyssey.Core.Internet.News.Files;
using Odyssey.Core.Internet.News.Images;
using Odyssey.Core.Internet.News.Jobs;
using Odyssey.Core.Internet.News.Podcasts.Files;
using Odyssey.Core.Journal.Thumbnails;
using Odyssey.Dtos.Internet;

namespace Odyssey.Core.Internet.News.Podcasts;

/// <summary>What one artwork phase did.</summary>
public sealed record PodcastArtworkPhaseSummary(int Stored, int Failed);

/// <summary>
/// The artwork phase of the news job (issue #556 §3.4): up to <see cref="PodcastTiming.ArtworkPerTick"/> show and episode
/// images per tick per process, claimed by row lease, fetched through the news egress, validated with the news image
/// policy, and stored with Small and Medium WebP renditions rendered under the shared watchdog.
/// </summary>
/// <remarks>
/// <para>
/// <b>Its own incident scope.</b> A render crash or hang is a <see cref="PodcastArtworkIncident"/>, counted by this
/// phase's breaker alone, so a publisher's image never stops photo thumbnails. The artwork is rejected and the feed's
/// remaining artwork with it (<see cref="PodcastArtworkRules.BlockFeedAsync"/>), so one hostile feed costs at most one
/// worker exit. A crash cannot record itself: <see cref="PodcastArtwork.RenderStartedAtUtc"/> is saved before the
/// renderer runs, and an expired lease that still carries it is a crash.
/// </para>
/// <para>
/// A hung native decode cannot be cancelled. While its thread lives the phase claims nothing; past
/// <see cref="PhotoThumbnailTiming.BoundedPause"/> the worker is stopped (exit code 70) for its supervisor to restart.
/// Downloads in flight on that process are lost and re-queued by their lease expiry — the accepted residual.
/// </para>
/// </remarks>
public sealed class PodcastArtworkPhase(
    IServiceScopeFactory scopes,
    IHttpClientFactory httpClients,
    NewsHostThrottleGate throttle,
    INewsCounterWriter counters,
    PhotoThumbnailProcessHooks hooks,
    TimeProvider time,
    ILogger<PodcastArtworkPhase> logger)
{
    private const string ThreadName = "odyssey-podcast-artwork";

    private readonly object gate = new();
    private Task? abandonedRender;
    private DateTime? abandonedAtUtc;
    private bool? decoderReady;
    private bool stopRequested;

    private DateTime Now => time.GetUtcNow().UtcDateTime;

    public async Task<PodcastArtworkPhaseSummary> RunAsync(Guid owner, NewsSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        await using var scope = scopes.CreateAsyncScope();
        var services = scope.ServiceProvider;
        if (services.GetService<IPhotoThumbnailRenderer>() is not { } renderer || !DecoderReady(services))
        {
            // Only the worker registers the renderer; nothing decodes anywhere else.
            return new PodcastArtworkPhaseSummary(0, 0);
        }

        await DetectCrashesAsync(cancellationToken);
        if (await PausedAsync(services, cancellationToken))
        {
            return new PodcastArtworkPhaseSummary(0, 0);
        }

        var stored = 0;
        var failed = 0;
        foreach (var id in await ClaimAsync(owner, settings, cancellationToken))
        {
            switch (await ProcessAsync(id, owner, settings, renderer, cancellationToken))
            {
                case PodcastArtworkState.Stored:
                    stored++;
                    break;
                case PodcastArtworkState.Failed or PodcastArtworkState.Rejected:
                    failed++;
                    break;
            }

            if (abandonedRender is not null)
            {
                break;
            }
        }

        return new PodcastArtworkPhaseSummary(stored, failed);
    }

    /// <summary>Whether any artwork is due: the news job's work check.</summary>
    public static Task<bool> HasWorkAsync(OdysseyContext context, DateTime now, CancellationToken cancellationToken) =>
        context.PodcastArtwork.AnyAsync(
            a => (a.State == PodcastArtworkState.Pending || a.State == PodcastArtworkState.Deferred)
                && (a.NextAttemptAtUtc == null || a.NextAttemptAtUtc <= now),
            cancellationToken);

    /// <summary>The decoder self-test, once per process, before the first render: it is what makes the coder policy live.</summary>
    private bool DecoderReady(IServiceProvider services)
    {
        if (decoderReady is { } ready)
        {
            return ready;
        }

        try
        {
            var result = services.GetService<IThumbnailDecoderSelfTest>()?.Run() ?? ThumbnailSelfTestResult.Passed;
            decoderReady = result.Ok;
            if (!result.Ok)
            {
                logger.LogCritical("The image decoder self-test failed ({Probe}); podcast artwork is not rendered by this worker.", result.FailedProbe);
            }
        }
        catch (Exception ex)
        {
            decoderReady = false;
            logger.LogCritical("The image decoder self-test raised {Failure}; podcast artwork is not rendered by this worker.", ex.GetType().Name);
        }

        return decoderReady.Value;
    }

    /// <summary>
    /// The breaker (3 incidents an hour pause this phase for an hour) and the hung-render pause. Past the bounded pause
    /// the worker stops for its supervisor to restart it.
    /// </summary>
    private async Task<bool> PausedAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (abandonedRender is { IsCompleted: true })
            {
                abandonedRender = null;
                abandonedAtUtc = null;
            }
        }

        if (abandonedRender is not null)
        {
            if (!stopRequested && abandonedAtUtc is { } since && Now - since >= PhotoThumbnailTiming.BoundedPause)
            {
                stopRequested = true;
                logger.LogCritical("A podcast artwork decode is still hung after {Pause}; restarting the worker to free it.", PhotoThumbnailTiming.BoundedPause);
                services.GetService<IHostApplicationLifetime>()?.StopApplication();
                _ = Task.Delay(PhotoThumbnailTiming.ExitGrace, time).ContinueWith(_ => hooks.Exit(70), TaskScheduler.Default);
            }

            return true;
        }

        var context = services.GetRequiredService<OdysseyContext>();
        var window = Now - PodcastTiming.ArtworkBreakerWindow;
        return await context.PodcastArtworkIncidents.CountAsync(i => i.DetectedAtUtc > window, cancellationToken) >= PodcastTiming.ArtworkBreakerThreshold;
    }

    /// <summary>
    /// An expired lease whose row still carries <see cref="PodcastArtwork.RenderStartedAtUtc"/> lost its process during a
    /// render — a crash (#556 sec N4, the photo job's expired-lease-means-crash attribution). An expired lease without it
    /// is an ordinary retry, released by the next claim.
    /// </summary>
    private async Task DetectCrashesAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        var now = Now;
        var crashed = await context.PodcastArtwork
            .Where(a => a.RenderStartedAtUtc != null && a.LeaseExpiresAtUtc != null && a.LeaseExpiresAtUtc < now)
            .ToListAsync(cancellationToken);
        foreach (var artwork in crashed)
        {
            try
            {
                await RecordIncidentAsync(context, artwork, PodcastArtworkIncidentKind.Crash, now, cancellationToken);
                logger.LogError("Podcast artwork {ArtworkId} crashed its render; it and the rest of its feed's artwork are rejected.", artwork.PodcastArtworkId);
            }
            catch (DbUpdateConcurrencyException)
            {
                // Another replica recorded it first.
            }
        }
    }

    private async Task<IReadOnlyList<Guid>> ClaimAsync(Guid owner, NewsSettings settings, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        var now = Now;
        var quotaHolds = await ImageQuotaHoldsAsync(context, settings, cancellationToken);
        var due = await (from artwork in context.PodcastArtwork.AsNoTracking()
                         join feed in context.NewsFeeds.AsNoTracking() on artwork.NewsFeedId equals feed.NewsFeedId
                         where feed.Kind == NewsFeedKind.Podcast && feed.DeletionRequestedAtUtc == null
                             && (artwork.LeaseOwner == null || artwork.LeaseExpiresAtUtc < now)
                             && artwork.RenderStartedAtUtc == null
                             && ((artwork.State == PodcastArtworkState.Pending && (artwork.NextAttemptAtUtc == null || artwork.NextAttemptAtUtc <= now))
                                 || (artwork.State == PodcastArtworkState.Deferred && (artwork.NextAttemptAtUtc <= now || !quotaHolds)))
                         orderby artwork.State, artwork.NextAttemptAtUtc, artwork.PodcastArtworkId
                         select artwork.PodcastArtworkId)
            .Take(PodcastTiming.ArtworkPerTick)
            .ToListAsync(cancellationToken);

        var claimed = new List<Guid>(due.Count);
        foreach (var id in due)
        {
            if (await TryLeaseAsync(context, id, owner, now, cancellationToken))
            {
                claimed.Add(id);
            }
        }

        return claimed;
    }

    private static async Task<bool> TryLeaseAsync(OdysseyContext context, Guid id, Guid owner, DateTime now, CancellationToken cancellationToken)
    {
        var expires = now + PodcastTiming.PhaseLease;
        if (context.Database.IsRelational())
        {
            return await context.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE `PodcastArtwork` SET `LeaseOwner` = {owner}, `LeaseExpiresAtUtc` = {expires} WHERE `PodcastArtworkId` = {id} AND `RenderStartedAtUtc` IS NULL AND (`LeaseOwner` IS NULL OR `LeaseExpiresAtUtc` < {now})",
                cancellationToken) == 1;
        }

        var row = await context.PodcastArtwork.FirstOrDefaultAsync(a => a.PodcastArtworkId == id, cancellationToken);
        if (row is null || row.RenderStartedAtUtc is not null || (row.LeaseOwner is not null && row.LeaseExpiresAtUtc >= now))
        {
            return false;
        }

        row.LeaseOwner = owner;
        row.LeaseExpiresAtUtc = expires;
        await context.SaveChangesAsync(cancellationToken);
        return true;
    }

    private async Task<PodcastArtworkState?> ProcessAsync(
        Guid id, Guid owner, NewsSettings settings, IPhotoThumbnailRenderer renderer, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var context = services.GetRequiredService<OdysseyContext>();
        var now = Now;
        var artwork = await context.PodcastArtwork.FirstOrDefaultAsync(a => a.PodcastArtworkId == id && a.LeaseOwner == owner, cancellationToken);
        var feedUrl = artwork is null ? null : await context.NewsFeeds.AsNoTracking()
            .Where(f => f.NewsFeedId == artwork.NewsFeedId).Select(f => f.Url).FirstOrDefaultAsync(cancellationToken);
        if (artwork is null || feedUrl is null || !Uri.TryCreate(feedUrl, UriKind.Absolute, out var feedUri)
            || NewsUrlKey.CanonicalHost(feedUri) is not { } feedHost)
        {
            return null;
        }

        if (await ImageQuotaHoldsAsync(context, settings, cancellationToken))
        {
            return await DeferAsync(context, artwork, now + PodcastTiming.DeferredRetry, cancellationToken);
        }

        var writer = services.GetRequiredService<PodcastFileWriter>();
        if (!writer.HasHeadroom(PodcastArtworkStorage.FeatureKey) || !Uri.TryCreate(artwork.SourceUrl, UriKind.Absolute, out var source))
        {
            return await RetryAsync(context, artwork, now, cancellationToken);
        }

        var grants = await services.GetRequiredService<INewsSettingsLookup>().GetAllowListAsync(cancellationToken);
        var http = await NewsHttpFetch.GetAsync(
            httpClients.CreateClient(NewsEgress.HttpClientName),
            source,
            new NewsEgressContext(feedHost, NewsEgressPurpose.Image, grants),
            etag: null,
            lastModified: null,
            settings.ImageMaxBytes,
            async (host, ct) => await throttle.TryTakeAsync(host, ct) is null,
            takeFirstHost: true,
            NewsEgress.ImageTimeout,
            cancellationToken);
        switch (http.Outcome)
        {
            case NewsHttpOutcome.Throttled:
                return await ReleaseAsync(context, artwork, artwork.NextAttemptAtUtc, cancellationToken);
            case NewsHttpOutcome.Failed when http.ErrorKind is NewsFeedErrorKind.TooLarge or NewsFeedErrorKind.BlockedAddress:
                return await RejectAsync(context, artwork, PodcastArtworkRejectedReason.Invalid, cancellationToken);
            case NewsHttpOutcome.Failed or NewsHttpOutcome.NotModified:
                return await RetryAsync(context, artwork, now, cancellationToken);
        }

        var validated = NewsImageValidation.Validate(http.Body, settings.ImageMaxBytes);
        if (validated.Image is not { } image || FormatOf(image.ContentType) is not { } format)
        {
            logger.LogInformation("Podcast artwork {ArtworkId} is rejected: {Reason}.", artwork.PodcastArtworkId, validated.RejectionReason);
            return await RejectAsync(context, artwork, PodcastArtworkRejectedReason.Invalid, cancellationToken);
        }

        // Saved BEFORE the native code runs: a crash cannot record itself, so the next claim reads this marker (sec N4).
        artwork.RenderStartedAtUtc = now;
        await context.SaveChangesAsync(cancellationToken);

        var run = await ThumbnailRenderGuard.RunAsync(
            () => SafeRender(renderer, new ThumbnailRenderRequest(image.Bytes, format, Orientation: 1)),
            PhotoThumbnailTiming.Watchdog, time, ThreadName);
        if (!run.Finished)
        {
            lock (gate)
            {
                abandonedRender = run.Abandoned;
                abandonedAtUtc = Now;
            }

            await RecordIncidentAsync(context, artwork, PodcastArtworkIncidentKind.Hang, Now, cancellationToken);
            logger.LogError("Podcast artwork {ArtworkId} hung its render; it and the rest of its feed's artwork are rejected.", artwork.PodcastArtworkId);
            return PodcastArtworkState.Rejected;
        }

        if (run.Value is not { Small: { } small, Medium: { } medium })
        {
            artwork.RenderStartedAtUtc = null;
            return await RejectAsync(context, artwork, PodcastArtworkRejectedReason.Invalid, cancellationToken);
        }

        return await StoreAsync(context, writer, artwork, image, small, medium, cancellationToken);
    }

    private async Task<PodcastArtworkState?> StoreAsync(
        OdysseyContext context, PodcastFileWriter writer, PodcastArtwork artwork, ValidatedImage image, ThumbnailRendition small,
        ThumbnailRendition medium, CancellationToken cancellationToken)
    {
        var now = Now;
        List<(PodcastArtworkFileKind Kind, StoredObject Object, string ContentType)> files;
        try
        {
            files =
            [
                (PodcastArtworkFileKind.Original, await writer.PutArtworkAsync(artwork.NewsFeedId, null, image.ContentType, image.Bytes, cancellationToken), image.ContentType),
                (PodcastArtworkFileKind.Small, await writer.PutArtworkAsync(artwork.NewsFeedId, "-s", "image/webp", small.WebP, cancellationToken), "image/webp"),
                (PodcastArtworkFileKind.Medium, await writer.PutArtworkAsync(artwork.NewsFeedId, "-m", "image/webp", medium.WebP, cancellationToken), "image/webp"),
            ];
        }
        catch (Exception ex) when (NewsIngestor.IsTransient(ex) && !cancellationToken.IsCancellationRequested)
        {
            artwork.RenderStartedAtUtc = null;
            return await RetryAsync(context, artwork, now, cancellationToken);
        }

        try
        {
            await NewsTransactions.RunAsync(context, async () =>
            {
                context.PodcastArtworkFiles.AddRange(files.Select(f => new PodcastArtworkFile
                {
                    PodcastArtworkFileId = Guid.NewGuid(),
                    PodcastArtworkId = artwork.PodcastArtworkId,
                    Kind = f.Kind,
                    StoredObjectId = f.Object.Id,
                    ContentType = f.ContentType,
                    SizeBytes = f.Object.SizeBytes,
                    Sha256 = Convert.FromHexString(f.Object.Sha256Hash!),
                }));
                artwork.State = PodcastArtworkState.Stored;
                artwork.Width = image.Width;
                artwork.Height = image.Height;
                artwork.RenderStartedAtUtc = null;
                artwork.DeferredReason = null;
                artwork.NextAttemptAtUtc = null;
                artwork.LeaseOwner = null;
                artwork.LeaseExpiresAtUtc = null;
                await context.SaveChangesAsync(cancellationToken);
                await counters.AddToPodcastStorageAsync(context, new PodcastStorageDelta(ArtworkBytes: files.Sum(f => f.Object.SizeBytes)), cancellationToken);
            }, cancellationToken);
        }
        catch (Exception ex) when (ex is DbUpdateException or NewsTransactionRetryException && !cancellationToken.IsCancellationRequested)
        {
            // Nothing committed: the reservations go to the abandoned-write sweep and the artwork is retried.
            logger.LogInformation("Storing podcast artwork {ArtworkId} was interrupted ({Failure}); it is retried.", artwork.PodcastArtworkId, ex.GetType().Name);
            return null;
        }

        return PodcastArtworkState.Stored;
    }

    private ThumbnailRenderResult SafeRender(IPhotoThumbnailRenderer renderer, ThumbnailRenderRequest request)
    {
        try
        {
            return renderer.Render(request);
        }
        catch (Exception ex)
        {
            logger.LogWarning("The artwork decoder raised {Failure}.", ex.GetType().Name);
            return ThumbnailRenderResult.Failed(PhotoThumbnailFailure.Decode);
        }
    }

    /// <summary>An incident in the artwork scope, the artwork rejected for it, and the feed blocked — one save.</summary>
    private static async Task RecordIncidentAsync(
        OdysseyContext context, PodcastArtwork artwork, PodcastArtworkIncidentKind kind, DateTime now, CancellationToken cancellationToken)
    {
        context.PodcastArtworkIncidents.Add(new PodcastArtworkIncident { PodcastArtworkIncidentId = Guid.NewGuid(), Kind = kind, DetectedAtUtc = now });

        // The feed first: its query returns this same tracked row while it is still Pending in the database, and would
        // relabel it a feed incident. This artwork's own reason is set after.
        await PodcastArtworkRules.BlockFeedAsync(context, artwork.NewsFeedId, now, cancellationToken);
        artwork.State = PodcastArtworkState.Rejected;
        artwork.RejectedReason = PodcastArtworkRejectedReason.RenderIncident;
        artwork.RenderStartedAtUtc = null;
        artwork.NextAttemptAtUtc = null;
        artwork.LeaseOwner = null;
        artwork.LeaseExpiresAtUtc = null;
        await context.SaveChangesAsync(cancellationToken);
    }

    private static async Task<bool> ImageQuotaHoldsAsync(OdysseyContext context, NewsSettings settings, CancellationToken cancellationToken)
    {
        var totals = await context.NewsStorageTotals.AsNoTracking().FirstOrDefaultAsync(t => t.Id == NewsStorageTotal.SingletonId, cancellationToken);
        return totals is not null && totals.ImageBytes + totals.ArtworkBytes >= settings.ImageStoreQuotaBytes;
    }

    private static ThumbnailSourceFormat? FormatOf(string contentType) => contentType switch
    {
        "image/jpeg" => ThumbnailSourceFormat.Jpeg,
        "image/png" => ThumbnailSourceFormat.Png,
        "image/webp" => ThumbnailSourceFormat.WebP,
        _ => null,
    };

    private static async Task<PodcastArtworkState?> DeferAsync(OdysseyContext context, PodcastArtwork artwork, DateTime next, CancellationToken cancellationToken)
    {
        artwork.State = PodcastArtworkState.Deferred;
        artwork.DeferredReason = 1;
        artwork.NextAttemptAtUtc = next;
        artwork.LeaseOwner = null;
        artwork.LeaseExpiresAtUtc = null;
        await context.SaveChangesAsync(cancellationToken);
        return PodcastArtworkState.Deferred;
    }

    private static async Task<PodcastArtworkState?> RejectAsync(
        OdysseyContext context, PodcastArtwork artwork, PodcastArtworkRejectedReason reason, CancellationToken cancellationToken)
    {
        artwork.State = PodcastArtworkState.Rejected;
        artwork.RejectedReason = reason;
        artwork.DeferredReason = null;
        artwork.NextAttemptAtUtc = null;
        artwork.LeaseOwner = null;
        artwork.LeaseExpiresAtUtc = null;
        await context.SaveChangesAsync(cancellationToken);
        return PodcastArtworkState.Rejected;
    }

    private static async Task<PodcastArtworkState?> RetryAsync(OdysseyContext context, PodcastArtwork artwork, DateTime now, CancellationToken cancellationToken)
    {
        artwork.Attempts = (byte)Math.Min(byte.MaxValue, artwork.Attempts + 1);
        artwork.DeferredReason = null;
        artwork.LeaseOwner = null;
        artwork.LeaseExpiresAtUtc = null;
        if (artwork.Attempts <= PodcastTiming.ArtworkBackoff.Length)
        {
            artwork.State = PodcastArtworkState.Pending;
            artwork.NextAttemptAtUtc = now + PodcastTiming.ArtworkBackoff[artwork.Attempts - 1];
        }
        else
        {
            artwork.State = PodcastArtworkState.Failed;
            artwork.NextAttemptAtUtc = null;
        }

        await context.SaveChangesAsync(cancellationToken);
        return artwork.State;
    }

    private static async Task<PodcastArtworkState?> ReleaseAsync(OdysseyContext context, PodcastArtwork artwork, DateTime? next, CancellationToken cancellationToken)
    {
        artwork.LeaseOwner = null;
        artwork.LeaseExpiresAtUtc = null;
        artwork.NextAttemptAtUtc = next;
        await context.SaveChangesAsync(cancellationToken);
        return null;
    }
}
