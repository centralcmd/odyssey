using Microsoft.EntityFrameworkCore;
using Odyssey.Context;

namespace Odyssey.Core.Internet.News;

/// <summary>
/// The <c>NewsArticles.FeedKind</c> copy (issue #556 §4.8) and the feeds whose copy is still being rewritten after a
/// type change. The join to the feed row stays the authoritative filter; <c>FeedKind</c> is what lets a reader use the
/// kind-aware sort index, and a feed is "pending" from its <see cref="NewsFeed.KindChangedAtUtc"/> until the purge loop
/// records, in the worker-owned <see cref="NewsFeedFetchState.FeedKindRewrittenAtUtc"/>, that it rewrote every article.
/// A reader adds the pending feeds to its kind predicate, so the copy's lag never hides or shows an article wrongly.
/// </summary>
public static class NewsFeedKindRewrite
{
    /// <summary>Live feeds whose articles may still carry the old kind (a short list: only feeds that just changed type).</summary>
    public static IQueryable<Guid> PendingFeeds(OdysseyContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return from feed in context.NewsFeeds.AsNoTracking()
               join state in context.NewsFeedFetchStates.AsNoTracking() on feed.NewsFeedId equals state.NewsFeedId into states
               from state in states.DefaultIfEmpty()
               where feed.DeletionRequestedAtUtc == null && feed.KindChangedAtUtc != null
                   && (state == null || state.FeedKindRewrittenAtUtc == null || state.FeedKindRewrittenAtUtc < feed.KindChangedAtUtc)
               select feed.NewsFeedId;
    }

    /// <summary>
    /// Articles of <paramref name="kind"/>, plus every article of <paramref name="openFeeds"/> (the pending feeds and any
    /// feed a request names). With no open feed the predicate is the bare indexed equality.
    /// </summary>
    public static IQueryable<NewsArticle> OfKind(IQueryable<NewsArticle> articles, Dtos.Internet.NewsFeedKind kind, IReadOnlyCollection<Guid> openFeeds)
    {
        ArgumentNullException.ThrowIfNull(articles);
        ArgumentNullException.ThrowIfNull(openFeeds);
        return openFeeds.Count == 0
            ? articles.Where(a => a.FeedKind == kind)
            : articles.Where(a => a.FeedKind == kind || openFeeds.Contains(a.NewsFeedId));
    }
}
