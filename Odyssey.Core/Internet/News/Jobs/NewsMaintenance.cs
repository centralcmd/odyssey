using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Odyssey.Context;
using Odyssey.Core.FileStorage;
using Odyssey.Core.Internet.News.Files;

namespace Odyssey.Core.Internet.News.Jobs;

/// <summary>What one maintenance pass did, for the log and the tests.</summary>
public sealed record NewsMaintenanceSummary(int CounterDrifts, bool TotalsDrifted, int QuotaReasonsChanged, int ImagesRequeued, int ImagesVerified);

/// <summary>
/// The hourly maintenance (issue #470 §3.4 step 9), per replica and idempotent: recount the feed counters and the
/// storage totals (logging any drift), recompute the quota reason of feeds not fetched since under the §6.3 conditional
/// write, repair missing images from the store's integrity findings and a bounded probe, and prune the host throttle
/// and the crash events. Every write is a tracked one, so the fast tiers run it (AC 44).
/// </summary>
public sealed class NewsMaintenance(IServiceScopeFactory scopes, TimeProvider time, ILogger<NewsMaintenance> logger)
{
    private readonly Queue<DateTime> requeues = new();

    private DateTime Now => time.GetUtcNow().UtcDateTime;

    public async Task<NewsMaintenanceSummary> RunAsync(NewsSettings settings, bool storageActive, CancellationToken cancellationToken)
    {
        var drifts = await RecountFeedsAsync(cancellationToken);
        var totalsDrifted = await RecountTotalsAsync(cancellationToken);
        var reasons = await RecomputeQuotaReasonsAsync(settings, cancellationToken);
        var (requeued, verified) = storageActive ? await RepairImagesAsync(cancellationToken) : (0, 0);
        await PruneAsync(cancellationToken);
        return new NewsMaintenanceSummary(drifts, totalsDrifted, reasons, requeued, verified);
    }

    public async Task<int> RecountFeedsAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        var counts = await context.NewsArticles.AsNoTracking()
            .GroupBy(a => a.NewsFeedId)
            .Select(g => new { FeedId = g.Key, Articles = g.Count(), Unread = g.Count(a => !a.IsRead) })
            .ToDictionaryAsync(r => r.FeedId, cancellationToken);
        var drifts = 0;
        foreach (var row in await context.NewsFeedCounters.OrderBy(c => c.NewsFeedId).ToListAsync(cancellationToken))
        {
            var actual = counts.GetValueOrDefault(row.NewsFeedId);
            var (articles, unread) = actual is null ? (0, 0) : (actual.Articles, actual.Unread);
            if (row.ArticleCount != articles || row.UnreadCount != unread)
            {
                drifts++;
                row.ArticleCount = articles;
                row.UnreadCount = unread;
            }
        }

        if (drifts > 0)
        {
            await context.SaveChangesAsync(cancellationToken);
            logger.LogWarning("The news feed counters had drifted for {Count} feed(s); they were recounted.", drifts);
        }

        return drifts;
    }

    public async Task<bool> RecountTotalsAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        var image = await context.NewsArticleFiles.AsNoTracking().Where(f => f.Kind == NewsArticleFileKind.Image).SumAsync(f => (long?)f.SizeBytes, cancellationToken) ?? 0;
        var text = await context.NewsArticleFiles.AsNoTracking().Where(f => f.Kind != NewsArticleFileKind.Image).SumAsync(f => (long?)f.SizeBytes, cancellationToken) ?? 0;
        var totals = await context.NewsStorageTotals.FirstOrDefaultAsync(t => t.Id == NewsStorageTotal.SingletonId, cancellationToken);
        if (totals is null)
        {
            totals = new NewsStorageTotal { Id = NewsStorageTotal.SingletonId };
            context.NewsStorageTotals.Add(totals);
        }
        else if (totals.ImageBytes == image && totals.TextAndSourceBytes == text)
        {
            return false;
        }

        totals.ImageBytes = image;
        totals.TextAndSourceBytes = text;
        await context.SaveChangesAsync(cancellationToken);
        logger.LogWarning("The news storage totals had drifted; they were recounted.");
        return true;
    }

    /// <summary>
    /// §6.3: the same rule as a completion, for feeds not fetched since — written only WHERE the feed holds no live lease
    /// and its <c>LastFetchAttemptAtUtc</c> is still the value read, so a pass cannot overwrite a completion's reason.
    /// The fence stays in the <c>WHERE</c> (it is not rotated), which is what makes the condition hold at write time.
    /// </summary>
    public async Task<int> RecomputeQuotaReasonsAsync(NewsSettings settings, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        var now = Now;
        var today = DateOnly.FromDateTime(now);
        var textBytes = await context.NewsStorageTotals.AsNoTracking()
            .Where(t => t.Id == NewsStorageTotal.SingletonId).Select(t => t.TextAndSourceBytes).FirstOrDefaultAsync(cancellationToken);
        var textReached = NewsTextQuota.IsReached(textBytes, settings.TextQuotaBytes);
        var changed = 0;
        foreach (var state in await context.NewsFeedFetchStates.Where(s => s.LeaseOwner == null || s.LeaseExpiresAtUtc < now).ToListAsync(cancellationToken))
        {
            var seenAttempt = state.LastFetchAttemptAtUtc;
            if (!NewsFetchRules.ApplyQuotaReason(state, textReached, NewsDailyCap.IsReached(state, today, settings.FeedMaxArticlesPerDay), now))
            {
                continue;
            }

            try
            {
                var fresh = await context.NewsFeedFetchStates.AsNoTracking()
                    .Where(s => s.NewsFeedId == state.NewsFeedId)
                    .Select(s => new { s.LeaseOwner, s.LeaseExpiresAtUtc, s.LastFetchAttemptAtUtc })
                    .FirstAsync(cancellationToken);
                if ((fresh.LeaseOwner is not null && fresh.LeaseExpiresAtUtc >= now) || fresh.LastFetchAttemptAtUtc != seenAttempt)
                {
                    context.Entry(state).State = EntityState.Detached;
                    continue;
                }

                await context.SaveChangesAsync(cancellationToken);
                changed++;
            }
            catch (DbUpdateConcurrencyException)
            {
                // A fetch claimed or completed it in between: its own recompute stands.
                context.Entry(state).State = EntityState.Detached;
            }
        }

        return changed;
    }

    /// <summary>
    /// Image repair (§3.4 step 9): candidates only from the store's integrity findings and a probe of at most 200
    /// stored images, oldest verification first. A missing file's stale object is released and its file row removed in
    /// one save, and the image goes back to <c>Pending</c> — at most 100 times an hour per process and once per image a
    /// day, so no reader can make the worker fetch from a publisher again and again (AC 54).
    /// </summary>
    public async Task<(int Requeued, int Verified)> RepairImagesAsync(CancellationToken cancellationToken)
    {
        var now = Now;
        while (requeues.Count > 0 && now - requeues.Peek() >= TimeSpan.FromHours(1))
        {
            requeues.Dequeue();
        }

        List<Guid> faulted;
        List<Guid> probe;
        await using (var scope = scopes.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
            faulted = await (from image in context.NewsArticleImages.AsNoTracking()
                             join file in context.NewsArticleFiles.AsNoTracking() on image.NewsArticleFileId equals file.NewsArticleFileId
                             join stored in context.StoredObjects.AsNoTracking() on file.StoredObjectId equals stored.Id
                             where image.State == NewsArticleImageState.Stored && stored.FeatureKey == NewsArticleStorage.FeatureKey
                                 && stored.IntegrityFaultAtUtc != null
                             select image.NewsArticleImageId).Take(NewsJobTiming.RepairRequeuesPerHour).ToListAsync(cancellationToken);
            probe = await context.NewsArticleImages.AsNoTracking()
                .Where(i => i.State == NewsArticleImageState.Stored && i.NewsArticleFileId != null)
                .OrderBy(i => i.VerifiedAtUtc).ThenBy(i => i.NewsArticleImageId)
                .Select(i => i.NewsArticleImageId)
                .Take(NewsJobTiming.RepairProbePerPass)
                .ToListAsync(cancellationToken);
        }

        var requeued = 0;
        foreach (var imageId in faulted)
        {
            requeued += await RequeueAsync(imageId, cancellationToken) ? 1 : 0;
        }

        var verified = 0;
        foreach (var imageId in probe.Except(faulted))
        {
            await using var scope = scopes.CreateAsyncScope();
            var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
            var store = scope.ServiceProvider.GetRequiredService<IObjectStore>();
            var image = await context.NewsArticleImages.FirstOrDefaultAsync(i => i.NewsArticleImageId == imageId, cancellationToken);
            var storedObjectId = image?.NewsArticleFileId is { } fileId
                ? await context.NewsArticleFiles.AsNoTracking().Where(f => f.NewsArticleFileId == fileId).Select(f => (Guid?)f.StoredObjectId).FirstOrDefaultAsync(cancellationToken)
                : null;
            if (image is null || storedObjectId is null)
            {
                continue;
            }

            bool missing;
            try
            {
                var content = await store.OpenReadAsync(NewsArticleStorage.FeatureKey, storedObjectId.Value, cancellationToken);
                await content.Content.DisposeAsync();
                missing = false;
            }
            catch (Exception ex) when (ex is FileStorageIntegrityException or DomainNotFoundException)
            {
                missing = true;
            }
            catch (FileStorageUnavailableException)
            {
                // The volume, not the file: nothing is repaired while it is down.
                break;
            }

            if (missing)
            {
                requeued += await RequeueAsync(imageId, cancellationToken) ? 1 : 0;
                continue;
            }

            image.VerifiedAtUtc = Now;
            await context.SaveChangesAsync(cancellationToken);
            verified++;
        }

        if (requeued > 0)
        {
            logger.LogWarning("{Count} news image(s) had lost their stored file and were queued to download again.", requeued);
        }

        return (requeued, verified);
    }

    private async Task<bool> RequeueAsync(Guid imageId, CancellationToken cancellationToken)
    {
        var now = Now;
        if (requeues.Count >= NewsJobTiming.RepairRequeuesPerHour)
        {
            return false;
        }

        await using var scope = scopes.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        var store = scope.ServiceProvider.GetRequiredService<IObjectStore>();
        var counters = scope.ServiceProvider.GetRequiredService<INewsCounterWriter>();
        var image = await context.NewsArticleImages.FirstOrDefaultAsync(i => i.NewsArticleImageId == imageId, cancellationToken);
        if (image is null || image.LastRequeuedAtUtc is { } last && now - last < NewsJobTiming.RepairInterval)
        {
            return false;
        }

        var file = image.NewsArticleFileId is { } fileId
            ? await context.NewsArticleFiles.FirstOrDefaultAsync(f => f.NewsArticleFileId == fileId, cancellationToken)
            : null;
        await NewsTransactions.RunAsync(context, async () =>
        {
            long released = 0;
            if (file is not null)
            {
                released = await NewsIngestor.ReleaseAsync(context, store, [file], cancellationToken);
            }

            image.NewsArticleFileId = null;
            image.State = NewsArticleImageState.Pending;
            image.Attempts = 0;
            image.NextAttemptAtUtc = null;
            image.DeferredReason = null;
            image.LastRequeuedAtUtc = now;
            await context.SaveChangesAsync(cancellationToken);
            await counters.AddToStorageAsync(context, -released, 0, cancellationToken);
        }, cancellationToken);
        requeues.Enqueue(now);
        logger.LogError("News image {ImageId} of article {ArticleId} had lost its stored file; it is queued to download again.", imageId, image.NewsArticleId);
        return true;
    }

    public async Task PruneAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        var now = Now;
        var throttleCutoff = now - NewsJobTiming.ThrottleRetention;
        var crashCutoff = now - NewsJobTiming.CrashEventRetention;
        context.NewsHostThrottle.RemoveRange(await context.NewsHostThrottle.Where(t => t.NextAllowedAtUtc < throttleCutoff).Take(1000).ToListAsync(cancellationToken));
        context.NewsFeedCrashEvents.RemoveRange(await context.NewsFeedCrashEvents.Where(e => e.DetectedAtUtc < crashCutoff).Take(1000).ToListAsync(cancellationToken));
        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Another replica pruned the same rows (or a host was taken again): the next pass tidies up.
        }
    }
}
