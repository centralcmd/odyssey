using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Odyssey.Context;
using Odyssey.Core.FileStorage;

namespace Odyssey.Core.Internet.News.Jobs;

/// <summary>
/// The purge (issue #470 §3.4 step 1): a feed with <c>DeletionRequestedAtUtc</c> set is claimed with the same lease and
/// fence as a fetch — only when no live lease holds it, so purge and ingest never run on one feed at once — and its
/// articles are deleted in time-boxed chunks (≤ 250 ms a transaction, ≤ 5 s a tick). Each chunk stages the release of
/// its files in the same save; when none remain, the feed row goes. The same loop also rewrites <c>FeedVisible</c>
/// after an archive or unarchive (eventually consistent, an index aid only). Every removal is a tracked
/// <c>RemoveRange</c>, so the fast tiers run it too (AC 44).
/// </summary>
public sealed class NewsFeedPurger(
    IServiceScopeFactory scopes,
    INewsCounterWriter counters,
    TimeProvider time,
    ILogger<NewsFeedPurger> logger)
{
    /// <summary>Articles per chunk; the chunk also stops at its 250 ms budget.</summary>
    public const int ChunkSize = 50;

    /// <summary>Articles whose <c>FeedVisible</c> is rewritten per save.</summary>
    public const int VisibilityChunk = 500;

    private DateTime Now => time.GetUtcNow().UtcDateTime;

    /// <summary>Runs the purge for every feed pending deletion, within the tick's budget. Returns the feeds removed.</summary>
    public async Task<int> RunAsync(Guid owner, CancellationToken cancellationToken)
    {
        var started = Now;
        var removed = 0;
        List<Guid> pending;
        await using (var scope = scopes.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
            var now = Now;
            pending = await (from feed in context.NewsFeeds.AsNoTracking()
                             join state in context.NewsFeedFetchStates.AsNoTracking() on feed.NewsFeedId equals state.NewsFeedId into states
                             from state in states.DefaultIfEmpty()
                             where feed.DeletionRequestedAtUtc != null
                                 && (state == null || state.LeaseOwner == null || state.LeaseExpiresAtUtc < now)
                             orderby feed.DeletionRequestedAtUtc
                             select feed.NewsFeedId).Take(10).ToListAsync(cancellationToken);
        }

        foreach (var feedId in pending)
        {
            if (Now - started >= NewsJobTiming.PurgeTickBudget)
            {
                break;
            }

            if (await PurgeFeedAsync(feedId, owner, started, cancellationToken))
            {
                removed++;
            }
        }

        if (Now - started < NewsJobTiming.PurgeTickBudget)
        {
            await RewriteVisibilityAsync(cancellationToken);
        }

        return removed;
    }

    private async Task<bool> PurgeFeedAsync(Guid feedId, Guid owner, DateTime tickStarted, CancellationToken cancellationToken)
    {
        var claim = await ClaimAsync(feedId, owner, cancellationToken);
        if (claim is null)
        {
            return false;
        }

        using var session = new NewsFetchSession(claim);
        while (Now - tickStarted < NewsJobTiming.PurgeTickBudget)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var scope = scopes.CreateAsyncScope();
            var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
            var store = scope.ServiceProvider.GetRequiredService<IObjectStore>();
            var articleIds = await context.NewsArticles.AsNoTracking()
                .Where(a => a.NewsFeedId == feedId)
                .OrderBy(a => a.NewsArticleId)
                .Select(a => a.NewsArticleId)
                .Take(ChunkSize)
                .ToListAsync(cancellationToken);

            if (articleIds.Count == 0)
            {
                return await RemoveFeedAsync(context, session, cancellationToken);
            }

            var chunkStarted = Now;
            var outcome = await NewsTransactions.RunAsync(context, () => session.WithFenceAsync(context, async _ =>
            {
                long imageBytes = 0;
                long textBytes = 0;
                foreach (var articleId in articleIds)
                {
                    if (Now - chunkStarted >= NewsJobTiming.PurgeChunkBudget && imageBytes + textBytes > 0)
                    {
                        break;
                    }

                    var files = await context.NewsArticleFiles.Where(f => f.NewsArticleId == articleId).ToListAsync(cancellationToken);
                    imageBytes += files.Where(f => f.Kind == NewsArticleFileKind.Image).Sum(f => f.SizeBytes);
                    textBytes += files.Where(f => f.Kind != NewsArticleFileKind.Image).Sum(f => f.SizeBytes);
                    await NewsIngestor.ReleaseAsync(context, store, files, cancellationToken);
                    context.NewsArticleImages.RemoveRange(await context.NewsArticleImages.Where(i => i.NewsArticleId == articleId).ToListAsync(cancellationToken));
                    context.NewsArticleIdentities.RemoveRange(await context.NewsArticleIdentities.Where(i => i.NewsArticleId == articleId).ToListAsync(cancellationToken));
                    context.NewsArticleRevisions.RemoveRange(await context.NewsArticleRevisions.Where(r => r.NewsArticleId == articleId).ToListAsync(cancellationToken));
                    context.NewsArticleTags.RemoveRange(await context.NewsArticleTags.Where(t => t.NewsArticleId == articleId).ToListAsync(cancellationToken));
                    var article = await context.NewsArticles.FirstAsync(a => a.NewsArticleId == articleId, cancellationToken);
                    context.NewsArticles.Remove(article);
                }

                await context.SaveChangesAsync(cancellationToken);
                await counters.AddToStorageAsync(context, -imageBytes, -textBytes, cancellationToken);
            }, cancellationToken, allowPendingDeletion: true), cancellationToken);

            if (outcome != NewsFenceOutcome.Ok)
            {
                return false;
            }
        }

        // Out of budget: the lease is cleared so the next tick (any replica) resumes.
        await using (var scope = scopes.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
            await session.WithFenceAsync(context, async state =>
            {
                state.LeaseOwner = null;
                state.LeaseExpiresAtUtc = null;
                await context.SaveChangesAsync(CancellationToken.None);
            }, CancellationToken.None, allowPendingDeletion: true);
        }

        return false;
    }

    private async Task<NewsClaim?> ClaimAsync(Guid feedId, Guid owner, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        var now = Now;
        var feed = await context.NewsFeeds.AsNoTracking().FirstOrDefaultAsync(f => f.NewsFeedId == feedId && f.DeletionRequestedAtUtc != null, cancellationToken);
        if (feed is null)
        {
            return null;
        }

        var state = await context.NewsFeedFetchStates.FirstOrDefaultAsync(s => s.NewsFeedId == feedId, cancellationToken);
        if (state is null)
        {
            // No fetch state to fence on (a row inserted outside the API's create path): the feed is removed as is.
            state = new NewsFeedFetchState { NewsFeedId = feedId, FetchFence = Guid.NewGuid(), NextFetchAtUtc = now };
            context.NewsFeedFetchStates.Add(state);
        }
        else if (state.LeaseOwner is not null && state.LeaseExpiresAtUtc >= now)
        {
            return null;
        }

        var fence = Guid.NewGuid();
        state.FetchFence = fence;
        state.LeaseOwner = owner;
        state.LeaseExpiresAtUtc = now + NewsJobTiming.Lease;
        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            return null;
        }

        return new NewsClaim { FeedId = feedId, Url = feed.Url, Owner = owner, Fence = fence, ClaimTime = now, Interval = NewsJobTiming.Maintenance };
    }

    private async Task<bool> RemoveFeedAsync(OdysseyContext context, NewsFetchSession session, CancellationToken cancellationToken)
    {
        var feedId = session.FeedId;
        var outcome = await NewsTransactions.RunAsync(context, () => session.WithFenceAsync(context, async state =>
        {
            context.NewsFeedTags.RemoveRange(await context.NewsFeedTags.Where(t => t.NewsFeedId == feedId).ToListAsync(cancellationToken));
            context.NewsFeedBuckets.RemoveRange(await context.NewsFeedBuckets.Where(b => b.NewsFeedId == feedId).ToListAsync(cancellationToken));
            context.NewsFeedCounters.RemoveRange(await context.NewsFeedCounters.Where(c => c.NewsFeedId == feedId).ToListAsync(cancellationToken));
            context.NewsFeedFetchStates.Remove(state);
            context.NewsFeeds.Remove(await context.NewsFeeds.FirstAsync(f => f.NewsFeedId == feedId, cancellationToken));
            await context.SaveChangesAsync(cancellationToken);
        }, cancellationToken, allowPendingDeletion: true), cancellationToken);

        if (outcome == NewsFenceOutcome.Ok)
        {
            // Audit (§7.7): ids only.
            logger.LogInformation("News feed {FeedId} was purged; its files are released after the deletion grace.", feedId);
            return true;
        }

        return false;
    }

    /// <summary>Rewrites <c>FeedVisible</c> to match each live feed's archive state, a chunk at a time.</summary>
    private async Task RewriteVisibilityAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        var stale = await (from article in context.NewsArticles
                           join feed in context.NewsFeeds on article.NewsFeedId equals feed.NewsFeedId
                           where feed.DeletionRequestedAtUtc == null && article.FeedVisible != (feed.ArchivedAtUtc == null)
                           select new { Article = article, Visible = feed.ArchivedAtUtc == null })
            .Take(VisibilityChunk)
            .ToListAsync(cancellationToken);
        if (stale.Count == 0)
        {
            return;
        }

        foreach (var row in stale)
        {
            row.Article.FeedVisible = row.Visible;
        }

        await context.SaveChangesAsync(cancellationToken);
    }
}
