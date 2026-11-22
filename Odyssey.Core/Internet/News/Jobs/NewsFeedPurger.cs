using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Odyssey.Context;
using Odyssey.Core.FileStorage;
using Odyssey.Core.Internet.News.Podcasts;
using Odyssey.Dtos.Internet;

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

        // Podcasts (issue #556 §3.8): the type change reaches the articles and the audio within a tick or two.
        if (Now - started < NewsJobTiming.PurgeTickBudget)
        {
            await RewriteFeedKindAsync(cancellationToken);
        }

        if (Now - started < NewsJobTiming.PurgeTickBudget)
        {
            await CleanUpAudioAsync(cancellationToken);
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
            var leaseCutoff = Now;
            // An episode whose audio is downloading under a live lease waits: the download stops at its next renewal
            // (issue #556 §3.8), and a later tick takes the article.
            var articleIds = await context.NewsArticles.AsNoTracking()
                .Where(a => a.NewsFeedId == feedId)
                .Where(a => !context.PodcastEpisodeAudio.Any(x => x.NewsArticleId == a.NewsArticleId
                    && x.State == PodcastAudioState.Downloading && x.LeaseExpiresAtUtc >= leaseCutoff))
                .OrderBy(a => a.NewsArticleId)
                .Select(a => a.NewsArticleId)
                .Take(ChunkSize)
                .ToListAsync(cancellationToken);

            if (articleIds.Count == 0)
            {
                if (await context.NewsArticles.AnyAsync(a => a.NewsFeedId == feedId, cancellationToken))
                {
                    break;
                }

                return await RemoveFeedAsync(context, session, store, cancellationToken);
            }

            var chunkStarted = Now;
            var outcome = await NewsTransactions.RunAsync(context, () => session.WithFenceAsync(context, async _ =>
            {
                long imageBytes = 0;
                long textBytes = 0;
                var podcast = default(PodcastStorageDelta);
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
                    podcast = Add(podcast, await RemoveEpisodeAsync(context, store, articleId, cancellationToken));
                    var article = await context.NewsArticles.FirstAsync(a => a.NewsArticleId == articleId, cancellationToken);
                    context.NewsArticles.Remove(article);
                }

                await context.SaveChangesAsync(cancellationToken);
                await counters.AddToStorageAsync(context, -imageBytes, -textBytes, cancellationToken);
                await counters.AddToPodcastStorageAsync(context, podcast, cancellationToken);
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

    /// <summary>
    /// An article's podcast rows (issue #556 §3.8), removed explicitly in the chunk's save: its stored audio released
    /// with the grace, any byte reservation an expired download left returned, then chapters, audio and episode.
    /// </summary>
    private static async Task<PodcastStorageDelta> RemoveEpisodeAsync(OdysseyContext context, IObjectStore store, Guid articleId, CancellationToken cancellationToken)
    {
        var delta = default(PodcastStorageDelta);
        var audio = await context.PodcastEpisodeAudio.FirstOrDefaultAsync(a => a.NewsArticleId == articleId, cancellationToken);
        if (audio is not null)
        {
            var reserved = audio.ReservedBytes;
            var released = await PodcastAudioRelease.StageAsync(store, audio, cancellationToken);
            delta = Add(PodcastStorageDelta.AudioReleased(released), new PodcastStorageDelta(AudioReservedBytes: -reserved));
            context.PodcastEpisodeAudio.Remove(audio);
        }

        context.PodcastChapters.RemoveRange(await context.PodcastChapters.Where(c => c.NewsArticleId == articleId).ToListAsync(cancellationToken));
        if (await context.PodcastEpisodes.FirstOrDefaultAsync(e => e.NewsArticleId == articleId, cancellationToken) is { } episode)
        {
            context.PodcastEpisodes.Remove(episode);
        }

        return delta;
    }

    private static PodcastStorageDelta Add(PodcastStorageDelta a, PodcastStorageDelta b) => new(
        a.AudioBytes + b.AudioBytes, a.AudioPendingReleaseBytes + b.AudioPendingReleaseBytes,
        a.AudioReservedBytes + b.AudioReservedBytes, a.ArtworkBytes + b.ArtworkBytes);

    private async Task<bool> RemoveFeedAsync(OdysseyContext context, NewsFetchSession session, IObjectStore store, CancellationToken cancellationToken)
    {
        var feedId = session.FeedId;
        var outcome = await NewsTransactions.RunAsync(context, () => session.WithFenceAsync(context, async state =>
        {
            // The feed's artwork (≤ 1,000 rows × 3 files) and its podcast details, in the feed's own save.
            long artworkBytes = 0;
            var artwork = await context.PodcastArtwork.Where(a => a.NewsFeedId == feedId).ToListAsync(cancellationToken);
            var artworkIds = artwork.Select(a => a.PodcastArtworkId).ToList();
            var files = await context.PodcastArtworkFiles.Where(f => artworkIds.Contains(f.PodcastArtworkId)).ToListAsync(cancellationToken);
            foreach (var file in files)
            {
                await store.StageReleaseAsync(file.StoredObjectId, immediate: false, cancellationToken);
                artworkBytes += file.SizeBytes;
            }

            context.PodcastArtworkFiles.RemoveRange(files);
            context.PodcastFeedDetails.RemoveRange(await context.PodcastFeedDetails.Where(d => d.NewsFeedId == feedId).ToListAsync(cancellationToken));
            context.PodcastArtwork.RemoveRange(artwork);
            context.NewsFeedTags.RemoveRange(await context.NewsFeedTags.Where(t => t.NewsFeedId == feedId).ToListAsync(cancellationToken));
            context.NewsFeedBuckets.RemoveRange(await context.NewsFeedBuckets.Where(b => b.NewsFeedId == feedId).ToListAsync(cancellationToken));
            context.NewsFeedCounters.RemoveRange(await context.NewsFeedCounters.Where(c => c.NewsFeedId == feedId).ToListAsync(cancellationToken));
            context.NewsFeedFetchStates.Remove(state);
            context.NewsFeeds.Remove(await context.NewsFeeds.FirstAsync(f => f.NewsFeedId == feedId, cancellationToken));
            await context.SaveChangesAsync(cancellationToken);
            await counters.AddToPodcastStorageAsync(context, new PodcastStorageDelta(ArtworkBytes: -artworkBytes), cancellationToken);
        }, cancellationToken, allowPendingDeletion: true), cancellationToken);

        if (outcome == NewsFenceOutcome.Ok)
        {
            // Audit (§7.7): ids only.
            logger.LogInformation("News feed {FeedId} was purged; its files are released after the deletion grace.", feedId);
            return true;
        }

        return false;
    }

    /// <summary>Audio rows cleaned up per tick (§3.8).</summary>
    public const int AudioCleanupChunk = 100;

    /// <summary>
    /// Rewrites <c>FeedKind</c> for the feeds whose type changed (§4.8), a chunk at a time and only for those feeds — never
    /// a scan of every article. A switch to <c>Podcast</c> also marks the feed's unread articles read, with
    /// <c>ReadAtUtc</c> so the read-state CHECK holds, and takes them off the feed's unread count in the same
    /// transaction; a switch back leaves them read. When a feed has no article left with the old kind, the change it
    /// caught up with is recorded in <see cref="NewsFeedFetchState.FeedKindRewrittenAtUtc"/>, and readers stop treating it
    /// as pending. A second type change made meanwhile carries a later <c>KindChangedAtUtc</c>, so it stays pending.
    /// </summary>
    public async Task RewriteFeedKindAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        var pending = await (from feed in context.NewsFeeds.AsNoTracking()
                             where NewsFeedKindRewrite.PendingFeeds(context).Contains(feed.NewsFeedId)
                             select new { feed.NewsFeedId, feed.Kind, feed.KindChangedAtUtc })
            .Take(VisibilityChunk).ToListAsync(cancellationToken);
        var budget = VisibilityChunk;
        foreach (var feed in pending)
        {
            if (budget <= 0)
            {
                return;
            }

            var stale = await context.NewsArticles
                .Where(a => a.NewsFeedId == feed.NewsFeedId && a.FeedKind != feed.Kind)
                .Take(budget)
                .ToListAsync(cancellationToken);
            if (stale.Count == 0)
            {
                await MarkRewrittenAsync(context, feed.NewsFeedId, feed.KindChangedAtUtc!.Value, cancellationToken);
                continue;
            }

            budget -= stale.Count;
            var now = Now;
            var markedRead = 0;
            foreach (var article in stale)
            {
                article.FeedKind = feed.Kind;
                if (feed.Kind == NewsFeedKind.Podcast && !article.IsRead)
                {
                    article.IsRead = true;
                    article.ReadAtUtc = now;
                    markedRead++;
                }
            }

            await NewsTransactions.RunAsync(context, async () =>
            {
                await context.SaveChangesAsync(cancellationToken);
                if (markedRead > 0)
                {
                    await counters.AddToFeedAsync(context, feed.NewsFeedId, 0, -markedRead, cancellationToken);
                }
            }, cancellationToken);
            context.ChangeTracker.Clear();
        }
    }

    /// <summary>
    /// The worker-owned completion mark, one column, never touching the fetch fence — and only while the feed holds no
    /// live fetch lease, tested in the same statement. A fetch claimed before the type change carries the old kind and
    /// would insert old-kind articles after this pass looked; its live lease keeps the feed pending until it ends, and the
    /// next pass rewrites what it inserted. A fetch claimed later reads the new kind.
    /// </summary>
    private async Task MarkRewrittenAsync(OdysseyContext context, Guid feedId, DateTime changedAtUtc, CancellationToken cancellationToken)
    {
        var now = Now;
        if (context.Database.IsRelational())
        {
            await context.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE `NewsFeedFetchStates` SET `FeedKindRewrittenAtUtc` = {changedAtUtc} WHERE `NewsFeedId` = {feedId} AND (`LeaseOwner` IS NULL OR `LeaseExpiresAtUtc` < {now})",
                cancellationToken);
            return;
        }

        if (await context.NewsFeedFetchStates.FirstOrDefaultAsync(
                s => s.NewsFeedId == feedId && (s.LeaseOwner == null || s.LeaseExpiresAtUtc < now), cancellationToken) is { } state)
        {
            state.FeedKindRewrittenAtUtc = changedAtUtc;
            await context.SaveChangesAsync(cancellationToken);
        }
    }

    /// <summary>
    /// The audio cleanup (§3.8, arch N1, R2): on a feed that is no longer a podcast or is pending deletion, a queued,
    /// deferred or failed row goes back to <c>None</c> and stored audio is released with the grace. An archived feed is
    /// left alone — archiving is reversible and destroys nothing. Each row is its own fenced save; a row a download or a
    /// user is touching loses the fence and is taken next tick.
    /// </summary>
    public async Task<int> CleanUpAudioAsync(CancellationToken cancellationToken)
    {
        List<Guid> due;
        await using (var scope = scopes.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
            due = await (from audio in context.PodcastEpisodeAudio.AsNoTracking()
                         join feed in context.NewsFeeds.AsNoTracking() on audio.NewsFeedId equals feed.NewsFeedId
                         where (feed.Kind != NewsFeedKind.Podcast || feed.DeletionRequestedAtUtc != null)
                             && (audio.State == PodcastAudioState.Queued || audio.State == PodcastAudioState.Deferred
                                 || audio.State == PodcastAudioState.Failed || audio.State == PodcastAudioState.Stored)
                         orderby audio.NewsArticleId
                         select audio.NewsArticleId)
                .Take(AudioCleanupChunk)
                .ToListAsync(cancellationToken);
        }

        var cleaned = 0;
        foreach (var id in due)
        {
            await using var scope = scopes.CreateAsyncScope();
            var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
            var store = scope.ServiceProvider.GetRequiredService<IObjectStore>();
            var row = await context.PodcastEpisodeAudio.FirstOrDefaultAsync(
                a => a.NewsArticleId == id && a.State != PodcastAudioState.Downloading && a.State != PodcastAudioState.None, cancellationToken);
            if (row is null)
            {
                continue;
            }

            try
            {
                await NewsTransactions.RunAsync(context, async () =>
                {
                    var released = await PodcastAudioRelease.StageAsync(store, row, cancellationToken);
                    await context.SaveChangesAsync(cancellationToken);
                    await counters.AddToPodcastStorageAsync(context, PodcastStorageDelta.AudioReleased(released), cancellationToken);
                }, cancellationToken);
                cleaned++;
            }
            catch (DbUpdateConcurrencyException)
            {
                // Lost the fence: taken next tick.
            }
        }

        if (cleaned > 0)
        {
            logger.LogInformation("Podcast audio of {Count} episode(s) on feeds no longer podcasts was cleaned up.", cleaned);
        }

        return cleaned;
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
