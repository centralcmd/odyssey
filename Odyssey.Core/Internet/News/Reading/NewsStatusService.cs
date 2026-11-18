using Microsoft.EntityFrameworkCore;
using Odyssey.Context;
using Odyssey.Core.FileStorage;
using Odyssey.Core.Internet.News.Files;
using Odyssey.Dtos.Internet;

namespace Odyssey.Core.Internet.News.Reading;

/// <summary>
/// <c>GET /api/news-status</c> (issue #470 §5.5 item 26). The storage state comes from this process's volume registry;
/// the byte totals from <c>NewsStorageTotals</c>; both quotas are the RESOLVED settings, read through the lookup the
/// worker uses, and the text quota's reached flag is <see cref="NewsTextQuota.IsReached"/> — the one comparison.
/// </summary>
public sealed class NewsStatusService(
    OdysseyContext context,
    INewsSettingsLookup settings,
    FileStorageVolumeRegistry volumes,
    IFileStorageSpaceProbe space)
{
    /// <summary>How many feeds the image-usage list names.</summary>
    public const int TopFeedCount = 10;

    public async Task<NewsStatus> GetAsync(CancellationToken cancellationToken)
    {
        var resolved = await settings.GetAsync(cancellationToken);
        var totals = await context.NewsStorageTotals.AsNoTracking().News()
            .FirstOrDefaultAsync(t => t.Id == NewsStorageTotal.SingletonId, cancellationToken);
        var live = context.NewsFeeds.AsNoTracking().Where(f => f.DeletionRequestedAtUtc == null);
        var feedCount = await live.News().CountAsync(cancellationToken);
        var quarantined = await (from feed in live
                                 join state in context.NewsFeedFetchStates.AsNoTracking() on feed.NewsFeedId equals state.NewsFeedId
                                 where state.QuarantinedAtUtc != null
                                 select feed.NewsFeedId).News().CountAsync(cancellationToken);
        var articleCount = await (from feed in live
                                  join counter in context.NewsFeedCounters.AsNoTracking() on feed.NewsFeedId equals counter.NewsFeedId
                                  select counter.ArticleCount).News().SumAsync(cancellationToken);

        var top = await (from file in context.NewsArticleFiles.AsNoTracking()
                         join article in context.NewsArticles.AsNoTracking() on file.NewsArticleId equals article.NewsArticleId
                         join feed in live on article.NewsFeedId equals feed.NewsFeedId
                         where file.Kind == NewsArticleFileKind.Image
                         group file by article.NewsFeedId into g
                         select new { FeedId = g.Key, Bytes = g.Sum(f => f.SizeBytes) })
            .OrderByDescending(g => g.Bytes).ThenBy(g => g.FeedId).Take(TopFeedCount)
            .News().ToListAsync(cancellationToken);
        var topIds = top.Select(t => t.FeedId).ToList();
        var titles = await (from feed in context.NewsFeeds.AsNoTracking()
                            join state in context.NewsFeedFetchStates.AsNoTracking() on feed.NewsFeedId equals state.NewsFeedId into states
                            from state in states.DefaultIfEmpty()
                            where topIds.Contains(feed.NewsFeedId)
                            select new { feed.NewsFeedId, feed.CustomTitle, Title = state == null ? null : state.Title, SiteUrl = state == null ? null : state.SiteUrl, Language = state == null ? null : state.Language })
            .News().ToDictionaryAsync(f => f.NewsFeedId, cancellationToken);

        var (storage, volumeName, free) = Storage();
        var imageBytes = totals?.ImageBytes ?? 0;
        var textBytes = totals?.TextAndSourceBytes ?? 0;
        return new NewsStatus
        {
            Storage = storage,
            Volume = volumeName,
            ArticleCount = articleCount,
            FeedCount = feedCount,
            QuarantinedFeedCount = quarantined,
            ImageBytes = imageBytes,
            ImageQuotaBytes = resolved.ImageStoreQuotaBytes,
            ImageQuotaReached = imageBytes >= resolved.ImageStoreQuotaBytes,
            TextAndSourceBytes = textBytes,
            TextAndSourceQuotaBytes = resolved.TextQuotaBytes,
            TextAndSourceQuotaReached = NewsTextQuota.IsReached(textBytes, resolved.TextQuotaBytes),
            VolumeFreeBytes = free,
            TopFeedsByImageBytes = [.. top.Select(t =>
            {
                var feed = titles.GetValueOrDefault(t.FeedId);
                return new NewsFeedImageUsage
                {
                    FeedId = t.FeedId,
                    DisplayTitle = NewsFeedProjection.DisplayTitle(feed?.CustomTitle, feed?.Title, feed?.SiteUrl),
                    Language = feed?.Language,
                    ImageBytes = t.Bytes,
                };
            })],
        };
    }

    private (NewsStorageState State, string? Volume, long FreeBytes) Storage()
    {
        if (!volumes.IsActive || !volumes.Bindings.TryGetValue(NewsArticleStorage.FeatureKey, out var name) || volumes.Find(name) is not { } volume)
        {
            return (NewsStorageState.Unconfigured, null, 0);
        }

        var free = space.Measure(volume.RootPath)?.Free ?? 0;
        return (volumes.RecheckIfDue(volume) ? NewsStorageState.Degraded : NewsStorageState.Ready, volume.Name, free);
    }
}
