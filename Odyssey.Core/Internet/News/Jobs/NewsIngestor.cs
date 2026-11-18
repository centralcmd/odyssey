using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Odyssey.Context;
using Odyssey.Core.FileStorage;
using Odyssey.Core.Internet.News.Content;
using Odyssey.Core.Internet.News.Files;
using Odyssey.Core.Internet.News.Identity;
using Odyssey.Core.Internet.News.Parsing;
using Odyssey.Dtos;

namespace Odyssey.Core.Internet.News.Jobs;

/// <summary>What ingesting one fetch's items did.</summary>
public sealed record NewsIngestSummary
{
    public int Stored { get; init; }

    public int Revised { get; init; }

    public int Failures { get; init; }

    public bool TransientSkip { get; init; }

    public bool StorageRefused { get; init; }

    /// <summary>A fenced write found the lease lost or the deletion requested.</summary>
    public NewsFenceOutcome? Aborted { get; init; }
}

/// <summary>Why an item was not written this time, and whether that is the item's fault.</summary>
internal enum NewsItemSkip
{
    None,
    DailyCap,
    TextQuota,
    Storage,
    Transient,
    Aborted,
}

/// <summary>
/// The ingestor (issue #470 §3.4 step 5, §3.5, §8.8): per item — normalise under the 2 s deadline, resolve identity,
/// check the headroom and the text quota before any file write, then insert, touch or edit; image references are
/// queued with the article. Each item is its own fenced transaction, so a failure skips that item only and a lease
/// overrun can never double-ingest.
/// </summary>
public sealed class NewsIngestor(
    IServiceScopeFactory scopes,
    INewsCounterWriter counters,
    TimeProvider time,
    ILogger<NewsIngestor> logger)
{
    /// <summary>The "PlainText" content type (§4.10): bare, UTF-8 by this spec.</summary>
    public const string PlainTextContentType = "text/plain";

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private DateTime Now => time.GetUtcNow().UtcDateTime;

    public async Task<NewsIngestSummary> IngestAsync(
        NewsFetchSession session, NewsParsedFeed feed, NewsSettings settings, DateTime budgetEndsAtUtc, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(feed);
        var claim = session.Claim;
        var language = feed.Language ?? claim.Language;

        // 5.1: sanitise, clamp and normalise. A failure here is the item's own (deterministic).
        var failures = 0;
        var items = new List<(NewsParsedItem Parsed, NewsNormalizedItem Item)>(feed.Items.Count);
        foreach (var parsed in feed.Items)
        {
            try
            {
                var item = NewsItemNormalizer.Normalize(parsed, NewsJobTiming.ItemDeadline);
                if (item.SourceTooLarge)
                {
                    logger.LogInformation("A news item of feed {FeedId} is over the source cap; it keeps no source file.", claim.FeedId);
                }

                items.Add((parsed, item));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failures++;
                logger.LogWarning("A news item of feed {FeedId} failed to normalise ({Failure}); it is skipped.", claim.FeedId, ex.GetType().Name);
            }
        }

        // 5.2: identity, against a snapshot of the feed's known keys.
        IReadOnlyList<NewsIdentityResolution> resolutions;
        await using (var scope = scopes.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
            var snapshot = await SnapshotAsync(context, claim.FeedId, items.Select(i => i.Item.Keys).ToList(), cancellationToken);
            resolutions = NewsIdentityResolver.Resolve(items.Select(i => i.Item.Keys).ToList(), snapshot);
        }

        var stored = 0;
        var revised = 0;
        var transient = false;
        var storageRefused = false;
        var articlesToday = claim.ArticlesToday;
        var dailyCapHit = false;
        for (var k = 0; k < items.Count; k++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Now >= budgetEndsAtUtc)
            {
                // The 60 s ingest budget: what is left is a transient skip, re-parsed on the next poll.
                transient = true;
                logger.LogInformation("Feed {FeedId} ran out of its ingest budget; the rest of its items wait for the next fetch.", claim.FeedId);
                break;
            }

            var resolution = resolutions[k];
            if (resolution.DuplicateOfEarlierItem)
            {
                continue;
            }

            var item = items[k].Item;
            NewsItemSkip skip;
            try
            {
                if (resolution.IsNew)
                {
                    if (articlesToday >= settings.FeedMaxArticlesPerDay)
                    {
                        dailyCapHit = true;
                        transient = true;
                        continue;
                    }

                    skip = await InsertAsync(session, item, resolution, settings, language, cancellationToken);
                    if (skip == NewsItemSkip.None)
                    {
                        stored++;
                        articlesToday++;
                    }
                }
                else
                {
                    var (edited, editSkip) = await UpdateAsync(session, item, resolution, settings, cancellationToken);
                    skip = editSkip;
                    if (edited)
                    {
                        revised++;
                    }
                }
            }
            catch (Exception ex) when (IsTransient(ex) && !cancellationToken.IsCancellationRequested)
            {
                skip = ex is NewsHeadroomException or FileStorageFullException ? NewsItemSkip.Storage : NewsItemSkip.Transient;
                logger.LogInformation("A news item of feed {FeedId} was not stored this time ({Failure}); it is retried on the next fetch.", claim.FeedId, ex.GetType().Name);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failures++;
                skip = NewsItemSkip.None;
                logger.LogWarning("A news item of feed {FeedId} failed ({Failure}); it is skipped.", claim.FeedId, ex.GetType().Name);
            }

            switch (skip)
            {
                case NewsItemSkip.Aborted:
                    return new NewsIngestSummary { Stored = stored, Revised = revised, Failures = failures, TransientSkip = true, Aborted = session.Aborted };
                case NewsItemSkip.Storage:
                    // The volume is below the news headroom or refused the write: nothing more fits this fetch.
                    return new NewsIngestSummary { Stored = stored, Revised = revised, Failures = failures, TransientSkip = true, StorageRefused = true };
                case NewsItemSkip.TextQuota:
                    return new NewsIngestSummary { Stored = stored, Revised = revised, Failures = failures, TransientSkip = true, StorageRefused = storageRefused };
                case NewsItemSkip.DailyCap:
                    dailyCapHit = true;
                    transient = true;
                    break;
                case NewsItemSkip.Transient:
                    transient = true;
                    break;
            }
        }

        if (dailyCapHit)
        {
            logger.LogInformation("Feed {FeedId} reached its daily article cap; new items wait for the next UTC day.", claim.FeedId);
        }

        return new NewsIngestSummary { Stored = stored, Revised = revised, Failures = failures, TransientSkip = transient, StorageRefused = storageRefused };
    }

    /// <summary>The known identity rows for the candidate keys, plus the GUID keys of every article a link matched (§8.8 rule 2).</summary>
    private static async Task<NewsIdentitySnapshot> SnapshotAsync(
        OdysseyContext context, Guid feedId, IReadOnlyList<NewsItemKeys> keys, CancellationToken cancellationToken)
    {
        var hashes = keys.SelectMany(k => new[] { k.Guid, k.Link, k.Hash }).OfType<NewsIdentityKey>().Select(k => k.Hash).ToList();
        List<(Guid ArticleId, NewsArticleIdentityKind Kind, byte[] KeyHash)> rows;
        if (context.Database.IsRelational())
        {
            var matched = new List<(Guid, NewsArticleIdentityKind, byte[])>();
            foreach (var chunk in hashes.Chunk(500))
            {
                var list = chunk.ToList();
                matched.AddRange((await context.NewsArticleIdentities.AsNoTracking()
                        .Where(i => i.NewsFeedId == feedId && list.Contains(i.IdentityKeyHash))
                        .Select(i => new { i.NewsArticleId, i.Kind, i.IdentityKeyHash })
                        .ToListAsync(cancellationToken))
                    .Select(i => (i.NewsArticleId, i.Kind, i.IdentityKeyHash)));
            }

            var linked = matched.Where(r => r.Item2 == NewsArticleIdentityKind.Link).Select(r => r.Item1).Distinct().ToList();
            var guids = linked.Count == 0
                ? []
                : (await context.NewsArticleIdentities.AsNoTracking()
                    .Where(i => i.NewsFeedId == feedId && linked.Contains(i.NewsArticleId) && i.Kind == NewsArticleIdentityKind.Guid)
                    .Select(i => new { i.NewsArticleId, i.Kind, i.IdentityKeyHash })
                    .ToListAsync(cancellationToken))
                    .Select(i => (i.NewsArticleId, i.Kind, i.IdentityKeyHash));
            rows = [.. matched, .. guids];
        }
        else
        {
            // EF InMemory compares a byte array in Contains by reference, so the fast tiers read the feed's rows and
            // filter here. Same result, and only the tests take this branch.
            var wanted = hashes.Select(NewsHashing.ToHex).ToHashSet(StringComparer.Ordinal);
            var all = await context.NewsArticleIdentities.AsNoTracking()
                .Where(i => i.NewsFeedId == feedId)
                .Select(i => new { i.NewsArticleId, i.Kind, i.IdentityKeyHash })
                .ToListAsync(cancellationToken);
            var matched = all.Where(i => wanted.Contains(NewsHashing.ToHex(i.IdentityKeyHash))).ToList();
            var linked = matched.Where(i => i.Kind == NewsArticleIdentityKind.Link).Select(i => i.NewsArticleId).ToHashSet();
            rows = matched.Concat(all.Where(i => i.Kind == NewsArticleIdentityKind.Guid && linked.Contains(i.NewsArticleId)))
                .Select(i => (i.NewsArticleId, i.Kind, i.IdentityKeyHash))
                .ToList();
        }

        return new NewsIdentitySnapshot(rows);
    }

    // ── New ──────────────────────────────────────────────────────────────────────────────────────────

    private async Task<NewsItemSkip> InsertAsync(
        NewsFetchSession session, NewsNormalizedItem item, NewsIdentityResolution resolution, NewsSettings settings, string? language,
        CancellationToken cancellationToken)
    {
        var claim = session.Claim;
        await using var scope = scopes.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var context = services.GetRequiredService<OdysseyContext>();
        var writer = services.GetRequiredService<NewsArticleWriter>();

        // 5.3: the headroom and the text quota before any file write.
        if (!writer.HasHeadroom())
        {
            return NewsItemSkip.Storage;
        }

        if (await TextQuotaReachedAsync(context, settings, cancellationToken))
        {
            return NewsItemSkip.TextQuota;
        }

        var bucket = await services.GetRequiredService<NewsBucketAllocator>().AllocateAsync(claim.FeedId, cancellationToken);
        var now = Now;
        var articleId = resolution.ArticleId;
        var layout = new NewsArticleLayout(claim.FeedId, bucket, articleId);
        var files = await WriteFilesAsync(writer, layout, articleId, null, item, now, cancellationToken);

        var article = new NewsArticle
        {
            NewsArticleId = articleId,
            NewsFeedId = claim.FeedId,
            Bucket = bucket,
            FirstSeenAtUtc = now,
            LastSeenAtUtc = now,
            SortAtUtc = NewsFetchRules.SortAt(item.PublishedAtUtc, now),
            FeedVisible = claim.FeedVisible,
            Language = language is null ? null : NewsText.Clamp(language, NewsLimits.MaxLanguageLength),
        };
        Apply(article, item);

        var outcome = await NewsTransactions.RunAsync(context, () => session.WithFenceAsync(context, async state =>
        {
            NewsFetchRules.RollDay(state, DateOnly.FromDateTime(now));
            state.ArticlesToday++;
            context.NewsArticles.Add(article);
            context.NewsArticleIdentities.AddRange(resolution.KeysToAttach.Select(key => Identity(article, key, now)));
            context.NewsArticleFiles.AddRange(files);
            if (claim.DownloadImages)
            {
                context.NewsArticleImages.AddRange(NewImages(articleId, item.Images, 0, 0, settings.ImageMaxPerArticle));
            }

            await context.SaveChangesAsync(cancellationToken);
            await counters.AddToFeedAsync(context, claim.FeedId, articles: 1, unread: 1, cancellationToken);
            // The storage totals last, as the final statement of the transaction (§3.5).
            await counters.AddToStorageAsync(context, 0, files.Sum(f => f.SizeBytes), cancellationToken);
        }, cancellationToken), cancellationToken);
        return outcome == NewsFenceOutcome.Ok ? NewsItemSkip.None : NewsItemSkip.Aborted;
    }

    // ── Existing: unchanged or changed ───────────────────────────────────────────────────────────────

    private async Task<(bool Edited, NewsItemSkip Skip)> UpdateAsync(
        NewsFetchSession session, NewsNormalizedItem item, NewsIdentityResolution resolution, NewsSettings settings, CancellationToken cancellationToken)
    {
        var claim = session.Claim;
        await using var scope = scopes.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var context = services.GetRequiredService<OdysseyContext>();
        var article = await context.NewsArticles.AsNoTracking()
            .FirstOrDefaultAsync(a => a.NewsArticleId == resolution.ArticleId && a.NewsFeedId == claim.FeedId, cancellationToken);
        if (article is null)
        {
            return (false, NewsItemSkip.None);
        }

        var now = Now;
        if (article.ContentHash.AsSpan().SequenceEqual(item.ContentHash))
        {
            // Unchanged: touch LastSeenAtUtc at most once an hour, and attach any key the item newly carries.
            var touch = now - article.LastSeenAtUtc >= NewsJobTiming.TouchInterval;
            if (!touch && resolution.KeysToAttach.Count == 0)
            {
                return (false, NewsItemSkip.None);
            }

            var touched = await NewsTransactions.RunAsync(context, () => session.WithFenceAsync(context, async _ =>
            {
                if (touch)
                {
                    var tracked = await context.NewsArticles.FirstAsync(a => a.NewsArticleId == article.NewsArticleId, cancellationToken);
                    tracked.LastSeenAtUtc = now;
                }

                context.NewsArticleIdentities.AddRange(resolution.KeysToAttach.Select(key => Identity(article, key, now)));
                await context.SaveChangesAsync(cancellationToken);
            }, cancellationToken), cancellationToken);
            return (false, touched == NewsFenceOutcome.Ok ? NewsItemSkip.None : NewsItemSkip.Aborted);
        }

        // Changed: the edit sequence (§3.5). The new files first — no row lock is held during file I/O.
        var writer = services.GetRequiredService<NewsArticleWriter>();
        if (!writer.HasHeadroom())
        {
            return (false, NewsItemSkip.Storage);
        }

        if (await TextQuotaReachedAsync(context, settings, cancellationToken))
        {
            return (false, NewsItemSkip.TextQuota);
        }

        var store = services.GetRequiredService<IObjectStore>();
        var layout = new NewsArticleLayout(claim.FeedId, article.Bucket, article.NewsArticleId);
        var files = await WriteFilesAsync(writer, layout, article.NewsArticleId, null, item, now, cancellationToken);

        var outcome = await NewsTransactions.RunAsync(context, () => session.WithFenceAsync(context, async _ =>
        {
            var tracked = await context.NewsArticles.FirstAsync(a => a.NewsArticleId == article.NewsArticleId, cancellationToken);
            var current = await context.NewsArticleFiles
                .Where(f => f.NewsArticleId == tracked.NewsArticleId && f.NewsArticleRevisionId == null
                    && (f.Kind == NewsArticleFileKind.Source || f.Kind == NewsArticleFileKind.PlainText))
                .ToListAsync(cancellationToken);
            long released = 0;

            // Save 1: a new revision that the current files are re-labelled to — or, within an hour of the last
            // change, the overwrite: the current files are released and their rows removed in this same save.
            if (tracked.ContentChangedAtUtc is { } changed && now - changed < NewsJobTiming.RevisionWindow)
            {
                released += await ReleaseAsync(context, store, current, cancellationToken);
            }
            else
            {
                var revisions = await context.NewsArticleRevisions
                    .Where(r => r.NewsArticleId == tracked.NewsArticleId)
                    .Select(r => new { r.NewsArticleRevisionId, r.RevisionNumber })
                    .ToListAsync(cancellationToken);
                var revision = new NewsArticleRevision
                {
                    NewsArticleRevisionId = Guid.NewGuid(),
                    NewsArticleId = tracked.NewsArticleId,
                    RevisionNumber = revisions.Count == 0 ? 1 : revisions.Max(r => r.RevisionNumber) + 1,
                    Title = tracked.Title,
                    Author = tracked.Author,
                    Link = tracked.Link,
                    ContentHash = tracked.ContentHash,
                    IdentityHash = tracked.IdentityHash,
                    BodyLength = tracked.BodyLength,
                    BodyOmittedReason = tracked.BodyOmittedReason,
                    ValidFromUtc = tracked.ContentChangedAtUtc ?? tracked.FirstSeenAtUtc,
                    ReplacedAtUtc = now,
                };
                context.NewsArticleRevisions.Add(revision);
                foreach (var file in current)
                {
                    file.NewsArticleRevisionId = revision.NewsArticleRevisionId;
                }

                // At most MaxRevisions; the original (1) is always kept, so the oldest LATER revision goes.
                var held = revisions.Count + 1;
                foreach (var oldest in revisions.Where(r => r.RevisionNumber > 1).OrderBy(r => r.RevisionNumber))
                {
                    if (held <= NewsLimits.MaxRevisions)
                    {
                        break;
                    }

                    var revisionFiles = await context.NewsArticleFiles
                        .Where(f => f.NewsArticleRevisionId == oldest.NewsArticleRevisionId)
                        .ToListAsync(cancellationToken);
                    released += await ReleaseAsync(context, store, revisionFiles, cancellationToken);
                    context.NewsArticleRevisions.Remove(await context.NewsArticleRevisions.FirstAsync(
                        r => r.NewsArticleRevisionId == oldest.NewsArticleRevisionId, cancellationToken));
                    held--;
                }

                tracked.RevisionCount = held;
            }

            await context.SaveChangesAsync(cancellationToken);

            // Save 2: the new current files, the article, and the image list.
            context.NewsArticleFiles.AddRange(files);
            Apply(tracked, item);
            tracked.LastSeenAtUtc = now;
            tracked.ContentChangedAtUtc = now;
            context.NewsArticleIdentities.AddRange(resolution.KeysToAttach.Select(key => Identity(tracked, key, now)));
            if (claim.DownloadImages)
            {
                await ReconcileImagesAsync(context, tracked.NewsArticleId, item.Images, settings.ImageMaxPerArticle, cancellationToken);
            }

            await context.SaveChangesAsync(cancellationToken);
            await counters.AddToStorageAsync(context, 0, files.Sum(f => f.SizeBytes) - released, cancellationToken);
        }, cancellationToken), cancellationToken);
        return outcome == NewsFenceOutcome.Ok ? (true, NewsItemSkip.None) : (false, NewsItemSkip.Aborted);
    }

    /// <summary>Stages the release of each file's object and removes its row, in the caller's save. Returns the bytes released.</summary>
    internal static async Task<long> ReleaseAsync(
        OdysseyContext context, IObjectStore store, IReadOnlyList<NewsArticleFile> files, CancellationToken cancellationToken)
    {
        long released = 0;
        foreach (var file in files)
        {
            var stored = await context.StoredObjects.FirstOrDefaultAsync(o => o.Id == file.StoredObjectId, cancellationToken);
            if (stored is { State: StoredObjectState.Committed or StoredObjectState.PendingDelete })
            {
                store.StageRelease(stored);
            }

            released += file.SizeBytes;
            context.NewsArticleFiles.Remove(file);
        }

        return released;
    }

    private static async Task ReconcileImagesAsync(
        OdysseyContext context, Guid articleId, IReadOnlyList<NewsNormalizedImage> images, int perArticle, CancellationToken cancellationToken)
    {
        var existing = await context.NewsArticleImages.Where(i => i.NewsArticleId == articleId).ToListAsync(cancellationToken);
        var wanted = images.Select((image, index) => (image, index)).ToDictionary(p => p.image.SourceUrl, p => p.index, StringComparer.Ordinal);

        foreach (var row in existing)
        {
            if (wanted.TryGetValue(row.SourceUrl, out var order))
            {
                row.DisplayOrder = order;
            }
            else if (row.State != NewsArticleImageState.Superseded)
            {
                // Dropped by the edit: its file stays in the folder (§6.2 item 6).
                row.State = NewsArticleImageState.Superseded;
                row.LeaseOwner = null;
                row.LeaseExpiresAtUtc = null;
            }
        }

        var known = existing.Select(r => r.SourceUrl).ToHashSet(StringComparer.Ordinal);
        var active = existing.Count(r => r.State is NewsArticleImageState.Pending or NewsArticleImageState.Stored
            or NewsArticleImageState.Deferred or NewsArticleImageState.Failed && wanted.ContainsKey(r.SourceUrl));
        var nextPosition = existing.Count == 0 ? 0 : existing.Max(r => r.Position);
        context.NewsArticleImages.AddRange(NewImages(
            articleId, images.Where(i => !known.Contains(i.SourceUrl)).ToList(), nextPosition, active, perArticle,
            image => wanted[image.SourceUrl]));
    }

    /// <summary>
    /// Image rows appended after <paramref name="lastPosition"/> (append-only, so repositioning never collides, AC 25):
    /// the first ones up to the per-article cap are <c>Pending</c>, the rest <c>Skipped</c>, and a position past
    /// <see cref="NewsLimits.MaxImagePosition"/> is not recorded.
    /// </summary>
    private static IEnumerable<NewsArticleImage> NewImages(
        Guid articleId, IReadOnlyList<NewsNormalizedImage> images, int lastPosition, int active, int perArticle,
        Func<NewsNormalizedImage, int>? displayOrder = null)
    {
        var position = lastPosition;
        for (var k = 0; k < images.Count; k++)
        {
            if (++position > NewsLimits.MaxImagePosition)
            {
                yield break;
            }

            var image = images[k];
            var admitted = active < perArticle;
            if (admitted)
            {
                active++;
            }

            yield return new NewsArticleImage
            {
                NewsArticleImageId = Guid.NewGuid(),
                NewsArticleId = articleId,
                Position = position,
                DisplayOrder = displayOrder?.Invoke(image) ?? k,
                Origin = image.Origin,
                SourceUrl = image.SourceUrl,
                AltText = image.AltText is null ? null : NewsText.Clamp(image.AltText, NewsLimits.MaxImageAltTextLength),
                Width = image.Width,
                Height = image.Height,
                State = admitted ? NewsArticleImageState.Pending : NewsArticleImageState.Skipped,
            };
        }
    }

    // ── Shared ───────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The source and plain-text files of a version, written before the transaction (§3.5 step 1).</summary>
    private static async Task<List<NewsArticleFile>> WriteFilesAsync(
        NewsArticleWriter writer, NewsArticleLayout layout, Guid articleId, Guid? revisionId, NewsNormalizedItem item, DateTime now,
        CancellationToken cancellationToken)
    {
        var files = new List<NewsArticleFile>(2);
        if (item.Source is { } source)
        {
            files.Add(await WriteAsync(writer, layout, articleId, revisionId, NewsArticleFileKind.Source, item.SourceContentType, source, now, cancellationToken));
        }

        // No plain-text file when the body was refused (§7.10).
        if (item.BodyOmittedReason is null && item.Body is { } body)
        {
            files.Add(await WriteAsync(writer, layout, articleId, revisionId, NewsArticleFileKind.PlainText, PlainTextContentType,
                Utf8.GetBytes(body), now, cancellationToken));
        }

        return files;
    }

    private static async Task<NewsArticleFile> WriteAsync(
        NewsArticleWriter writer, NewsArticleLayout layout, Guid articleId, Guid? revisionId, NewsArticleFileKind kind, string contentType,
        byte[] bytes, DateTime now, CancellationToken cancellationToken)
    {
        var stored = await writer.PutAsync(layout, contentType, bytes, cancellationToken);
        return new NewsArticleFile
        {
            NewsArticleFileId = Guid.NewGuid(),
            NewsArticleId = articleId,
            NewsArticleRevisionId = revisionId,
            Kind = kind,
            StoredObjectId = stored.Id,
            ContentType = contentType,
            SizeBytes = stored.SizeBytes,
            Sha256 = Convert.FromHexString(stored.Sha256Hash!),
            CreatedAtUtc = now,
        };
    }

    private static void Apply(NewsArticle article, NewsNormalizedItem item)
    {
        article.Title = item.Title;
        article.Author = item.Author;
        article.Excerpt = NewsText.Excerpt(item.Excerpt, NewsLimits.MaxExcerptLength);
        article.BodyOmittedReason = item.BodyOmittedReason;
        article.Link = item.Link;
        article.LinkKeyHash = item.LinkKeyHash;
        article.ContentHash = item.ContentHash;
        article.IdentityHash = item.IdentityHash;
        article.BodyLength = item.BodyLength;
        article.PublishedAtUtc = item.PublishedAtUtc;
        article.FeedUpdatedAtUtc = item.UpdatedAtUtc;
    }

    private static NewsArticleIdentity Identity(NewsArticle article, NewsIdentityKey key, DateTime now) => new()
    {
        NewsArticleIdentityId = Guid.NewGuid(),
        NewsArticleId = article.NewsArticleId,
        NewsFeedId = article.NewsFeedId,
        Kind = key.Kind,
        IdentityKey = key.Key,
        IdentityKeyHash = key.Hash,
        FirstSeenAtUtc = now,
        LastSeenAtUtc = now,
    };

    private static async Task<bool> TextQuotaReachedAsync(OdysseyContext context, NewsSettings settings, CancellationToken cancellationToken)
    {
        var bytes = await context.NewsStorageTotals.AsNoTracking()
            .Where(t => t.Id == NewsStorageTotal.SingletonId)
            .Select(t => t.TextAndSourceBytes)
            .FirstOrDefaultAsync(cancellationToken);
        return NewsTextQuota.IsReached(bytes, settings.TextQuotaBytes);
    }

    /// <summary>
    /// The transient classes (§3.4 step 6, §3.5 step 4): the headroom or a store refusal, a reservation swept before its
    /// save (the <c>StoredObject</c>'s <c>State</c> token), a bucket lock timeout, an interrupted transaction.
    /// </summary>
    internal static bool IsTransient(Exception exception) => exception switch
    {
        NewsHeadroomException or FileStorageFullException or FileStorageUnavailableException or NewsTransactionRetryException => true,
        DbUpdateConcurrencyException concurrency => concurrency.Entries.Any(e => e.Entity is StoredObject),
        System.Data.Common.DbException => true,
        _ => exception.InnerException is { } inner && IsTransient(inner),
    };
}
