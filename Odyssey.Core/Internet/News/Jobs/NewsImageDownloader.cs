using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Odyssey.Context;
using Odyssey.Core.Internet.News.Fetching;
using Odyssey.Core.Internet.News.Files;
using Odyssey.Core.Internet.News.Images;
using Odyssey.Dtos;

namespace Odyssey.Core.Internet.News.Jobs;

/// <summary>What one image phase did.</summary>
public sealed record NewsImagePhaseSummary(int Stored, bool StorageRefused);

/// <summary>
/// The image phase (issue #470 §3.4 step 8, §6.2, §6.3): up to 50 images per tick per process in a 20 s budget,
/// claimed by row lease. <c>Pending</c> images are taken before <c>Deferred</c> ones, so a backlog of deferrals never
/// starves new work (AC 30). Each request first takes its host's throttle slot — a busy host defers the image to the
/// next tick — and an allow-list grant applies only to an image on its feed's own host. What is stored is the
/// validator's stripped output, written into the image's own article folder.
/// </summary>
public sealed class NewsImageDownloader(
    IServiceScopeFactory scopes,
    IHttpClientFactory httpClients,
    NewsHostThrottleGate throttle,
    INewsCounterWriter counters,
    TimeProvider time,
    ILogger<NewsImageDownloader> logger)
{
    private DateTime Now => time.GetUtcNow().UtcDateTime;

    public async Task<NewsImagePhaseSummary> RunAsync(Guid owner, NewsSettings settings, CancellationToken cancellationToken)
    {
        var started = Now;
        var stored = 0;
        var storageRefused = false;
        var claimed = await ClaimAsync(owner, settings, cancellationToken);
        foreach (var imageId in claimed)
        {
            if (Now - started >= NewsJobTiming.ImagePhaseBudget)
            {
                await ReleaseAsync(imageId, owner, nextAttempt: null, cancellationToken);
                continue;
            }

            var outcome = await DownloadAsync(imageId, owner, settings, cancellationToken);
            stored += outcome == ImageOutcome.Stored ? 1 : 0;
            storageRefused |= outcome == ImageOutcome.StorageRefused;
        }

        return new NewsImagePhaseSummary(stored, storageRefused);
    }

    /// <summary>The images due now, Pending first; each claimed by its lease (another replica's live lease is skipped).</summary>
    public async Task<IReadOnlyList<Guid>> ClaimAsync(Guid owner, NewsSettings settings, CancellationToken cancellationToken)
    {
        var now = Now;
        await using var scope = scopes.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        var imageBytes = await context.NewsStorageTotals.AsNoTracking()
            .Where(t => t.Id == NewsStorageTotal.SingletonId).Select(t => t.ImageBytes + t.ArtworkBytes).FirstOrDefaultAsync(cancellationToken);
        var quotaHolds = imageBytes >= settings.ImageStoreQuotaBytes;

        var eligible = from image in context.NewsArticleImages.AsNoTracking()
                       join article in context.NewsArticles on image.NewsArticleId equals article.NewsArticleId
                       join feed in context.NewsFeeds on article.NewsFeedId equals feed.NewsFeedId
                       where feed.DownloadImages && feed.IsActive && feed.DeletionRequestedAtUtc == null
                           && (image.LeaseOwner == null || image.LeaseExpiresAtUtc < now)
                       select image;
        var pending = await eligible
            .Where(i => i.State == NewsArticleImageState.Pending && (i.NextAttemptAtUtc == null || i.NextAttemptAtUtc <= now))
            .OrderBy(i => i.NextAttemptAtUtc).ThenBy(i => i.NewsArticleImageId)
            .Select(i => i.NewsArticleImageId)
            .Take(NewsJobTiming.ImagesPerTick)
            .ToListAsync(cancellationToken);
        if (pending.Count < NewsJobTiming.ImagesPerTick)
        {
            // A store-quota deferral is due as soon as the quota no longer holds: raising the setting makes it due.
            pending.AddRange(await eligible
                .Where(i => i.State == NewsArticleImageState.Deferred
                    && (i.NextAttemptAtUtc <= now || (!quotaHolds && i.DeferredReason == NewsImageDeferredReason.StoreQuota)))
                .OrderBy(i => i.NextAttemptAtUtc).ThenBy(i => i.NewsArticleImageId)
                .Select(i => i.NewsArticleImageId)
                .Take(NewsJobTiming.ImagesPerTick - pending.Count)
                .ToListAsync(cancellationToken));
        }

        var claimed = new List<Guid>(pending.Count);
        foreach (var id in pending)
        {
            if (await TryLeaseAsync(context, id, owner, now, cancellationToken))
            {
                claimed.Add(id);
            }
        }

        return claimed;
    }

    /// <summary>
    /// A conditional lease: relational, an <c>UPDATE … WHERE</c> the lease is free (exactly one replica's affects the
    /// row); EF InMemory, a tracked write behind the same predicate. Never <c>ExecuteUpdate</c> (AC 44).
    /// </summary>
    private static async Task<bool> TryLeaseAsync(OdysseyContext context, Guid imageId, Guid owner, DateTime now, CancellationToken cancellationToken)
    {
        var expires = now + NewsJobTiming.ImageLease;
        if (context.Database.IsRelational())
        {
            return await context.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE `NewsArticleImages` SET `LeaseOwner` = {owner}, `LeaseExpiresAtUtc` = {expires} WHERE `NewsArticleImageId` = {imageId} AND (`LeaseOwner` IS NULL OR `LeaseExpiresAtUtc` < {now})",
                cancellationToken) == 1;
        }

        var image = await context.NewsArticleImages.FirstOrDefaultAsync(i => i.NewsArticleImageId == imageId, cancellationToken);
        if (image is null || (image.LeaseOwner is not null && image.LeaseExpiresAtUtc >= now))
        {
            return false;
        }

        image.LeaseOwner = owner;
        image.LeaseExpiresAtUtc = expires;
        await context.SaveChangesAsync(cancellationToken);
        return true;
    }

    private enum ImageOutcome
    {
        Stored,
        Deferred,
        Retry,
        Rejected,
        StorageRefused,
        Skipped,
    }

    private async Task<ImageOutcome> DownloadAsync(Guid imageId, Guid owner, NewsSettings settings, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var context = services.GetRequiredService<OdysseyContext>();
        var now = Now;
        var image = await context.NewsArticleImages.FirstOrDefaultAsync(i => i.NewsArticleImageId == imageId && i.LeaseOwner == owner, cancellationToken);
        if (image is null)
        {
            return ImageOutcome.Skipped;
        }

        var target = await (from article in context.NewsArticles.AsNoTracking()
                            join feed in context.NewsFeeds.AsNoTracking() on article.NewsFeedId equals feed.NewsFeedId
                            where article.NewsArticleId == image.NewsArticleId
                            select new { article.NewsArticleId, article.NewsFeedId, article.Bucket, feed.Url }).FirstOrDefaultAsync(cancellationToken);
        var state = target is null ? null : await context.NewsFeedFetchStates.FirstOrDefaultAsync(s => s.NewsFeedId == target.NewsFeedId, cancellationToken);
        if (target is null || state is null || !Uri.TryCreate(target.Url, UriKind.Absolute, out var feedUrl) || NewsUrlKey.CanonicalHost(feedUrl) is not { } feedHost)
        {
            return await FinishAsync(context, image, ImageOutcome.Skipped, cancellationToken);
        }

        // The caps (§6.3): the per-feed daily bytes until the next UTC midnight, the image store quota hourly.
        var today = DateOnly.FromDateTime(now);
        var todayBytes = state.DailyQuotaDate == today ? state.ImageBytesToday : 0;
        if (todayBytes >= settings.FeedMaxImageBytesPerDay)
        {
            return await DeferAsync(context, image, NewsImageDeferredReason.DailyCap, NewsFetchRules.NextUtcMidnight(now), cancellationToken);
        }

        var imageBytes = await context.NewsStorageTotals.AsNoTracking()
            .Where(t => t.Id == NewsStorageTotal.SingletonId).Select(t => t.ImageBytes + t.ArtworkBytes).FirstOrDefaultAsync(cancellationToken);
        if (imageBytes >= settings.ImageStoreQuotaBytes)
        {
            return await DeferAsync(context, image, NewsImageDeferredReason.StoreQuota, now + NewsJobTiming.StoreQuotaRetry, cancellationToken);
        }

        var writer = services.GetRequiredService<NewsArticleWriter>();
        if (!writer.HasHeadroom())
        {
            await ReleaseAsync(context, image, now + NewsJobTiming.ShortRetry, cancellationToken);
            return ImageOutcome.StorageRefused;
        }

        if (!Uri.TryCreate(image.SourceUrl, UriKind.Absolute, out var source))
        {
            return await FinishAsync(context, image, ImageOutcome.Rejected, cancellationToken);
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
                // A busy host defers the image to the next tick.
                await ReleaseAsync(context, image, image.NextAttemptAtUtc, cancellationToken);
                return ImageOutcome.Deferred;
            case NewsHttpOutcome.Failed when http.ErrorKind is Dtos.Internet.NewsFeedErrorKind.TooLarge or Dtos.Internet.NewsFeedErrorKind.BlockedAddress:
                logger.LogInformation("Image {ImageId} of article {ArticleId} is refused ({Kind}).", image.NewsArticleImageId, image.NewsArticleId, http.ErrorKind);
                return await FinishAsync(context, image, ImageOutcome.Rejected, cancellationToken);
            case NewsHttpOutcome.Failed or NewsHttpOutcome.NotModified:
                return await FinishAsync(context, image, ImageOutcome.Retry, cancellationToken);
        }

        var validated = NewsImageValidation.Validate(http.Body, settings.ImageMaxBytes);
        if (validated.Image is not { } accepted)
        {
            // The reason names the rule, never the URL (§7.3).
            logger.LogInformation("Image {ImageId} of article {ArticleId} is rejected: {Reason}.", image.NewsArticleImageId, image.NewsArticleId, validated.RejectionReason);
            return await FinishAsync(context, image, ImageOutcome.Rejected, cancellationToken);
        }

        var layout = new NewsArticleLayout(target.NewsFeedId, target.Bucket, target.NewsArticleId);
        Context.StoredObject storedObject;
        try
        {
            storedObject = await writer.PutAsync(layout, accepted.ContentType, accepted.Bytes, cancellationToken);
        }
        catch (Exception ex) when (NewsIngestor.IsTransient(ex) && !cancellationToken.IsCancellationRequested)
        {
            await ReleaseAsync(context, image, now + NewsJobTiming.ShortRetry, cancellationToken);
            return ImageOutcome.StorageRefused;
        }

        var file = new NewsArticleFile
        {
            NewsArticleFileId = Guid.NewGuid(),
            NewsArticleId = target.NewsArticleId,
            Kind = NewsArticleFileKind.Image,
            StoredObjectId = storedObject.Id,
            ContentType = accepted.ContentType,
            SizeBytes = storedObject.SizeBytes,
            Sha256 = Convert.FromHexString(storedObject.Sha256Hash!),
            CreatedAtUtc = now,
        };
        try
        {
            await NewsTransactions.RunAsync(context, async () =>
            {
                context.NewsArticleFiles.Add(file);
                image.NewsArticleFileId = file.NewsArticleFileId;
                image.State = NewsArticleImageState.Stored;
                image.DeferredReason = null;
                image.NextAttemptAtUtc = null;
                image.Attempts = (byte)Math.Min(byte.MaxValue, image.Attempts + 1);
                image.Width = accepted.Width;
                image.Height = accepted.Height;
                image.VerifiedAtUtc = now;
                image.LeaseOwner = null;
                image.LeaseExpiresAtUtc = null;

                // The day's bytes on the fetch-state row, checked against its fence WITHOUT rotating it: a running fetch
                // keeps its lease, and a fetch completing first makes this save fail and the image retry.
                NewsFetchRules.RollDay(state, today);
                state.ImageBytesToday += file.SizeBytes;
                await context.SaveChangesAsync(cancellationToken);
                await counters.AddToStorageAsync(context, file.SizeBytes, 0, cancellationToken);
            }, cancellationToken);
        }
        catch (Exception ex) when (ex is DbUpdateException or NewsTransactionRetryException && !cancellationToken.IsCancellationRequested)
        {
            // Nothing committed; the reservation is swept with its file and the image is retried on a later tick.
            logger.LogInformation("Storing image {ImageId} was interrupted ({Failure}); it is retried.", imageId, ex.GetType().Name);
            return ImageOutcome.Retry;
        }

        return ImageOutcome.Stored;
    }

    private async Task<ImageOutcome> FinishAsync(OdysseyContext context, NewsArticleImage image, ImageOutcome outcome, CancellationToken cancellationToken)
    {
        image.LeaseOwner = null;
        image.LeaseExpiresAtUtc = null;
        switch (outcome)
        {
            case ImageOutcome.Rejected:
                image.State = NewsArticleImageState.Rejected;
                image.NextAttemptAtUtc = null;
                image.DeferredReason = null;
                break;
            case ImageOutcome.Retry:
                image.Attempts = (byte)Math.Min(byte.MaxValue, image.Attempts + 1);
                image.DeferredReason = null;
                if (NewsFetchRules.ImageRetry(image.Attempts) is { } wait)
                {
                    image.State = NewsArticleImageState.Pending;
                    image.NextAttemptAtUtc = Now + wait;
                }
                else
                {
                    image.State = NewsArticleImageState.Failed;
                    image.NextAttemptAtUtc = null;
                }

                break;
            case ImageOutcome.Skipped:
                image.State = NewsArticleImageState.Skipped;
                break;
        }

        await context.SaveChangesAsync(cancellationToken);
        return outcome;
    }

    private static async Task<ImageOutcome> DeferAsync(
        OdysseyContext context, NewsArticleImage image, NewsImageDeferredReason reason, DateTime nextAttempt, CancellationToken cancellationToken)
    {
        image.State = NewsArticleImageState.Deferred;
        image.DeferredReason = reason;
        image.NextAttemptAtUtc = nextAttempt;
        image.LeaseOwner = null;
        image.LeaseExpiresAtUtc = null;
        await context.SaveChangesAsync(cancellationToken);
        return ImageOutcome.Deferred;
    }

    private static async Task ReleaseAsync(OdysseyContext context, NewsArticleImage image, DateTime? nextAttempt, CancellationToken cancellationToken)
    {
        image.LeaseOwner = null;
        image.LeaseExpiresAtUtc = null;
        image.NextAttemptAtUtc = nextAttempt;
        await context.SaveChangesAsync(cancellationToken);
    }

    private async Task ReleaseAsync(Guid imageId, Guid owner, DateTime? nextAttempt, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        var image = await context.NewsArticleImages.FirstOrDefaultAsync(i => i.NewsArticleImageId == imageId && i.LeaseOwner == owner, cancellationToken);
        if (image is not null)
        {
            await ReleaseAsync(context, image, nextAttempt ?? image.NextAttemptAtUtc, cancellationToken);
        }
    }
}
