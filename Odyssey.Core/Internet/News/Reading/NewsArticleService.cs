using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Odyssey.Context;
using Odyssey.Core.FileStorage;
using Odyssey.Core.Internet.News.Files;
using Odyssey.Core.Pagination;
using Odyssey.Dtos;
using Odyssey.Dtos.Internet;

namespace Odyssey.Core.Internet.News.Reading;

/// <summary>One list row's sort key: what the union branches return and the cursor continues from.</summary>
public sealed class NewsArticleKey
{
    public Guid Id { get; set; }

    public DateTime SortAtUtc { get; set; }
}

/// <summary>A stored image ready to serve: never its source URL, path or stored-object id outside this process.</summary>
public sealed record NewsImageDescriptor(Guid StoredObjectId, string ContentType, string Sha256Hex);

/// <summary>
/// The article endpoints (issue #470 §5.2, §5.3): the list with its signed keyset cursor, the detail and revisions read
/// from their files, the image descriptor, and the read-state and tag writes. A feed pending deletion is a 404 for
/// every article of it. Mark-read writes in tracked chunks, never <c>ExecuteUpdate</c> (AC 44), and every unread-count
/// change goes through <see cref="INewsCounterWriter"/>.
/// </summary>
public sealed class NewsArticleService(
    OdysseyContext context,
    INewsArticleSearch search,
    NewsListCursor cursors,
    INewsCounterWriter counters,
    IObjectStore store,
    FileStorageVolumeRegistry volumes,
    TimeProvider time,
    ILogger<NewsArticleService> logger)
{
    /// <summary>A mark-read request's chunk: each is loaded tracked, flipped and saved with its counter updates.</summary>
    public const int MarkReadChunk = 1_000;

    /// <summary>How far ahead of the server clock a mark-read <c>asOfUtc</c> may be.</summary>
    public static readonly TimeSpan AsOfTolerance = TimeSpan.FromMinutes(5);

    private DateTime Now => time.GetUtcNow().UtcDateTime;

    // ── The list ──────────────────────────────────────────────────────────────────────────────────────

    public Task<NewsArticlePage> ListAsync(NewsArticlesQueryParams query, CancellationToken cancellationToken) =>
        NewsQueryGuard.RunAsync(async () =>
        {
            ArgumentNullException.ThrowIfNull(query);
            var filter = Normalize(query);
            var terms = Terms(filter.Search);
            var limit = Math.Clamp(query.Limit, 1, NewsLimits.MaxPageSize);

            NewsCursorPosition? position = query.Cursor is { } raw ? cursors.Read(raw, filter) : null;
            var asOf = position?.AsOfUtc ?? Now;
            var scope = await ResolveScopeAsync(filter, (field, _) => NewsErrors.FilterUnknown(field), cancellationToken);

            var page = new NewsArticlePage { AsOfUtc = asOf };
            List<NewsArticleKey> keys;
            int? nextOffset = null;
            if (filter.SortBy == NewsArticleSortBy.Relevance)
            {
                // Offset paging (§5.2): the decoded offset is re-checked against the cap.
                var offset = position?.Offset ?? query.Offset;
                if (offset >= NewsLimits.MaxCountedArticles)
                {
                    keys = [];
                }
                else
                {
                    var take = Math.Min(limit + 1, NewsLimits.MaxCountedArticles - offset + 1);
                    keys = await search.OrderByRelevance(Matching(scope, terms, asOf, filter), terms)
                        .Skip(offset).Take(take)
                        .Select(a => new NewsArticleKey { Id = a.NewsArticleId, SortAtUtc = a.SortAtUtc })
                        .News().ToListAsync(cancellationToken);
                    if (keys.Count > limit && offset + limit < NewsLimits.MaxCountedArticles)
                    {
                        nextOffset = offset + limit;
                    }
                }
            }
            else
            {
                var after = position is { } p ? (p.SortAtUtc, p.Id) : ((DateTime, Guid)?)null;
                var newest = filter.SortBy == NewsArticleSortBy.Newest;
                var offset = position is null ? query.Offset : 0;
                keys = await KeysAsync(scope, terms, asOf, filter, after, newest, offset, limit + 1, cancellationToken);
            }

            var hasMore = keys.Count > limit;
            if (hasMore)
            {
                keys = keys[..limit];
            }

            if (position is null)
            {
                var count = await Matching(scope, terms, asOf, filter).Take(NewsLimits.MaxCountedArticles + 1).News().CountAsync(cancellationToken);
                page.TotalCount = Math.Min(count, NewsLimits.MaxCountedArticles);
                page.TotalCountCapped = count > NewsLimits.MaxCountedArticles;
            }

            if (hasMore && keys.Count > 0)
            {
                var last = keys[^1];
                page.NextCursor = filter.SortBy == NewsArticleSortBy.Relevance
                    ? nextOffset is { } next ? cursors.Issue(filter, new NewsCursorPosition(default, Guid.Empty, next, asOf)) : null
                    : cursors.Issue(filter, new NewsCursorPosition(last.SortAtUtc, last.Id, 0, asOf));
            }

            page.Items = await SummariesAsync(keys.Select(k => k.Id).ToList(), cancellationToken);
            return page;
        });

    /// <summary>
    /// The keyset page (§5.3). A single-id filter reads its sort index in order and stops after the limit, so each feed
    /// and each own tag gets its own branch, ordered and limited, and an outer sort merges them; <c>UNION</c> removes an
    /// article two branches both returned. Every branch carries every filter and the cursor predicate. With no id list
    /// (or a search) the plain filtered scan of the global sort index serves, as it does when an inherited-tag
    /// expansion passes <see cref="NewsLimits.MaxFilterIds"/> feeds.
    /// </summary>
    private async Task<List<NewsArticleKey>> KeysAsync(
        NewsScope scope, IReadOnlyList<string> terms, DateTime asOf, NewsArticleFilter filter,
        (DateTime SortAtUtc, Guid Id)? after, bool newest, int offset, int take, CancellationToken cancellationToken)
    {
        var branches = new List<IQueryable<NewsArticleKey>>();
        if (terms.Count == 0 && (filter.FeedIds.Count > 0 || filter.TagIds.Count > 0))
        {
            if (filter.FeedIds.Count > 0)
            {
                // Both lists set: every result is in a named feed, so the feed branches alone cover it, each also
                // checking the tag list.
                foreach (var feedId in filter.FeedIds)
                {
                    var articles = Filtered(scope, asOf, filter).Where(a => a.NewsFeedId == feedId);
                    branches.Add(Branch(TagMember(articles, filter.TagIds), after, newest, take + offset));
                }
            }
            else if (scope.ExpandedFeeds.Count <= NewsLimits.MaxFilterIds)
            {
                foreach (var feedId in scope.ExpandedFeeds)
                {
                    branches.Add(Branch(Filtered(scope, asOf, filter).Where(a => a.NewsFeedId == feedId), after, newest, take + offset));
                }

                foreach (var tagId in filter.TagIds)
                {
                    branches.Add(OwnTagBranch(tagId, scope, asOf, filter, after, newest, take + offset));
                }
            }
        }

        if (branches.Count == 0)
        {
            return await Ordered(After(Matching(scope, terms, asOf, filter), after, newest), newest)
                .Skip(offset).Take(take)
                .Select(a => new NewsArticleKey { Id = a.NewsArticleId, SortAtUtc = a.SortAtUtc })
                .News().ToListAsync(cancellationToken);
        }

        var union = branches[0];
        foreach (var branch in branches.Skip(1))
        {
            union = union.Union(branch);
        }

        var merged = newest
            ? union.OrderByDescending(k => k.SortAtUtc).ThenByDescending(k => k.Id)
            : union.OrderBy(k => k.SortAtUtc).ThenBy(k => k.Id);
        return await merged.Skip(offset).Take(take).News().ToListAsync(cancellationToken);
    }

    private static IQueryable<NewsArticleKey> Branch(IQueryable<NewsArticle> articles, (DateTime SortAtUtc, Guid Id)? after, bool newest, int take) =>
        Ordered(After(articles, after, newest), newest)
            .Take(take)
            .Select(a => new NewsArticleKey { Id = a.NewsArticleId, SortAtUtc = a.SortAtUtc });

    /// <summary>An own-tag branch, driven by <c>IX_NewsArticleTags_Tag_Sort</c> through the link's copied sort key.</summary>
    private IQueryable<NewsArticleKey> OwnTagBranch(
        Guid tagId, NewsScope scope, DateTime asOf, NewsArticleFilter filter, (DateTime SortAtUtc, Guid Id)? after, bool newest, int take)
    {
        var links = context.NewsArticleTags.AsNoTracking().Where(l => l.NewsTagId == tagId);
        if (after is { } key)
        {
            var (s, id) = key;
            links = newest
                ? links.Where(l => l.SortAtUtc < s || (l.SortAtUtc == s && l.NewsArticleId.CompareTo(id) < 0))
                : links.Where(l => l.SortAtUtc > s || (l.SortAtUtc == s && l.NewsArticleId.CompareTo(id) > 0));
        }

        var joined = from link in links
                     join article in Filtered(scope, asOf, filter) on link.NewsArticleId equals article.NewsArticleId
                     select link;
        var ordered = newest
            ? joined.OrderByDescending(l => l.SortAtUtc).ThenByDescending(l => l.NewsArticleId)
            : joined.OrderBy(l => l.SortAtUtc).ThenBy(l => l.NewsArticleId);
        return ordered.Take(take).Select(l => new NewsArticleKey { Id = l.NewsArticleId, SortAtUtc = l.SortAtUtc });
    }

    // ── Detail, revisions, images ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The detail (§5.2 item 13): the summary plus the body, read from the article's plain-text file as a bounded stream.
    /// A file the store reports missing is <c>bodyUnavailable</c> (and logged); one it cannot read is
    /// <c>503 news-storage-unavailable</c>. An omitted body opens no file.
    /// </summary>
    public async Task<NewsArticleDetail?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var article = await NewsQueryGuard.RunAsync(() => VisibleArticles().News()
            .Where(a => a.NewsArticleId == id).FirstOrDefaultAsync(cancellationToken));
        if (article is null)
        {
            return null;
        }

        var summary = (await SummariesAsync([id], cancellationToken)).Single();
        var images = await NewsQueryGuard.RunAsync(() => context.NewsArticleImages.AsNoTracking().News()
            .Where(i => i.NewsArticleId == id && i.State == NewsArticleImageState.Stored && i.NewsArticleFileId != null)
            .OrderBy(i => i.DisplayOrder).ThenBy(i => i.Position)
            .Select(i => new NewsArticleImageItem { Id = i.NewsArticleImageId, DisplayOrder = i.DisplayOrder, AltText = i.AltText, Width = i.Width, Height = i.Height })
            .ToListAsync(cancellationToken));

        var detail = Detail(summary);
        detail.Images = images;
        detail.BodyOmittedReason = article.BodyOmittedReason;
        if (article.BodyOmittedReason is null)
        {
            EnsureStorage();
            var file = await NewsQueryGuard.RunAsync(() => context.NewsArticleFiles.AsNoTracking().News()
                .Where(f => f.NewsArticleId == id && f.NewsArticleRevisionId == null && f.Kind == NewsArticleFileKind.PlainText)
                .Select(f => (Guid?)f.StoredObjectId).FirstOrDefaultAsync(cancellationToken));
            detail.Body = await ReadTextAsync(file, $"article {id}", cancellationToken);
            detail.BodyUnavailable = detail.Body is null;
        }

        return detail;
    }

    /// <summary>Revisions newest first, metadata only (§5.2 item 15). <c>null</c> for an unknown article.</summary>
    public async Task<List<NewsArticleRevisionSummary>?> RevisionsAsync(Guid id, CancellationToken cancellationToken)
    {
        if (!await ExistsAsync(id, cancellationToken))
        {
            return null;
        }

        EnsureStorage();
        return await NewsQueryGuard.RunAsync(() => context.NewsArticleRevisions.AsNoTracking().News()
            .Where(r => r.NewsArticleId == id)
            .OrderByDescending(r => r.RevisionNumber)
            .Select(r => new NewsArticleRevisionSummary
            {
                RevisionNumber = r.RevisionNumber,
                Title = r.Title,
                Author = r.Author,
                Link = r.Link,
                ValidFromUtc = r.ValidFromUtc,
                ReplacedAtUtc = r.ReplacedAtUtc,
                BodyLength = r.BodyLength,
            })
            .ToListAsync(cancellationToken));
    }

    /// <summary>One revision with its body (§5.2 item 15a), read from its revision text file. <c>null</c> when unknown.</summary>
    public async Task<NewsArticleRevisionDetail?> RevisionAsync(Guid id, int number, CancellationToken cancellationToken)
    {
        if (!await ExistsAsync(id, cancellationToken))
        {
            return null;
        }

        var revision = await NewsQueryGuard.RunAsync(() => context.NewsArticleRevisions.AsNoTracking().News()
            .FirstOrDefaultAsync(r => r.NewsArticleId == id && r.RevisionNumber == number, cancellationToken));
        if (revision is null)
        {
            return null;
        }

        var detail = new NewsArticleRevisionDetail
        {
            RevisionNumber = revision.RevisionNumber,
            Title = revision.Title,
            Author = revision.Author,
            Link = revision.Link,
            ValidFromUtc = revision.ValidFromUtc,
            ReplacedAtUtc = revision.ReplacedAtUtc,
            BodyLength = revision.BodyLength,
            BodyOmittedReason = revision.BodyOmittedReason,
        };
        if (revision.BodyOmittedReason is null)
        {
            EnsureStorage();
            var file = await NewsQueryGuard.RunAsync(() => context.NewsArticleFiles.AsNoTracking().News()
                .Where(f => f.NewsArticleRevisionId == revision.NewsArticleRevisionId && f.Kind == NewsArticleFileKind.PlainText)
                .Select(f => (Guid?)f.StoredObjectId).FirstOrDefaultAsync(cancellationToken));
            detail.Body = await ReadTextAsync(file, $"revision {number} of article {id}", cancellationToken);
        }

        return detail;
    }

    /// <summary>
    /// The image to serve (§5.2 item 14, steps 2–3): <c>null</c> unless the row is <c>Stored</c>, belongs to this
    /// article, the article exists and its feed is not pending deletion. Reads the database only; never writes.
    /// </summary>
    public Task<NewsImageDescriptor?> ImageAsync(Guid articleId, Guid imageId, CancellationToken cancellationToken) =>
        NewsQueryGuard.RunAsync(async () =>
        {
            var row = await (from image in context.NewsArticleImages.AsNoTracking()
                             join file in context.NewsArticleFiles.AsNoTracking() on image.NewsArticleFileId equals file.NewsArticleFileId
                             join article in VisibleArticles() on image.NewsArticleId equals article.NewsArticleId
                             where image.NewsArticleImageId == imageId && image.NewsArticleId == articleId
                                 && image.State == NewsArticleImageState.Stored && file.Kind == NewsArticleFileKind.Image
                                 && file.NewsArticleId == articleId
                             select new { file.StoredObjectId, file.ContentType, file.Sha256 })
                .News().FirstOrDefaultAsync(cancellationToken);
            return row is null ? null : new NewsImageDescriptor(row.StoredObjectId, row.ContentType, Convert.ToHexStringLower(row.Sha256));
        });

    /// <summary>
    /// Opens a stored image (step 4) once every 404 has been decided. A missing file is a 404 and an error log, and
    /// nothing is repaired here (§3.4 step 9); an unreadable volume is <c>503 news-storage-unavailable</c>.
    /// </summary>
    public async Task<Stream?> OpenImageAsync(Guid articleId, Guid imageId, NewsImageDescriptor descriptor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        EnsureStorage();
        try
        {
            return (await store.OpenReadAsync(NewsArticleStorage.FeatureKey, descriptor.StoredObjectId, cancellationToken)).Content;
        }
        catch (FileStorageUnavailableException)
        {
            throw NewsErrors.StorageUnavailable();
        }
        catch (Exception ex) when (ex is FileStorageIntegrityException or DomainNotFoundException)
        {
            logger.LogError("News image {ImageId} of article {ArticleId} could not be served: its file is missing or failed its check.", imageId, articleId);
            return null;
        }
    }

    // ── Read state ────────────────────────────────────────────────────────────────────────────────────

    /// <summary><c>PUT …/read-state</c> (§5.2 item 16). Idempotent. False for an unknown article.</summary>
    public Task<bool> SetReadAsync(Guid id, bool isRead, CancellationToken cancellationToken) =>
        NewsQueryGuard.RunAsync(async () =>
        {
            var article = await VisibleArticles(tracked: true).News().FirstOrDefaultAsync(a => a.NewsArticleId == id, cancellationToken);
            if (article is null)
            {
                return false;
            }

            await ApplyReadAsync([article], isRead, cancellationToken);
            return true;
        });

    /// <summary><c>POST /api/news-articles/read-state</c> (§5.2 item 17): 1–500 ids; unknown ids change nothing.</summary>
    public Task<MarkNewsReadResult> SetReadAsync(NewsArticlesReadStateBulk request, CancellationToken cancellationToken) =>
        NewsQueryGuard.RunAsync(async () =>
        {
            ArgumentNullException.ThrowIfNull(request);
            var ids = NewsFilterIds.Normalize(request.ArticleIds);
            var articles = await VisibleArticles(tracked: true).News().Where(a => ids.Contains(a.NewsArticleId)).ToListAsync(cancellationToken);
            return new MarkNewsReadResult { MarkedCount = await ApplyReadAsync(articles, request.IsRead!.Value, cancellationToken) };
        });

    /// <summary>
    /// <c>POST …/mark-read</c> (§5.2 item 18): the list's filters up to the echoed <c>asOfUtc</c>, at most
    /// <see cref="NewsLimits.MaxMarkReadPerRequest"/> articles per request in tracked chunks of <see cref="MarkReadChunk"/>.
    /// Repeating the request while <c>hasMore</c> marks exactly the matching set.
    /// </summary>
    public Task<MarkNewsReadResult> MarkReadAsync(MarkNewsRead request, CancellationToken cancellationToken) =>
        NewsQueryGuard.RunAsync(async () =>
        {
            ArgumentNullException.ThrowIfNull(request);
            var asOf = request.AsOfUtc!.Value.UtcDateTime;
            if (asOf > Now + AsOfTolerance)
            {
                throw NewsErrors.MarkReadScope("asOfUtc", "The list's time cannot be in the future.");
            }

            var filter = new NewsArticleFilter(
                Sorted(request.FeedIds), Sorted(request.TagIds), [NewsArticleReadStatus.Unread], ListQuery.NormalizeSearch(request.Search),
                request.From?.UtcDateTime, request.To?.UtcDateTime, request.IncludeArchivedFeeds, NewsArticleSortBy.Newest);
            var terms = Terms(filter.Search);
            var scope = await ResolveScopeAsync(filter, (field, message) => NewsErrors.MarkReadScope(field, message), cancellationToken);

            var ids = await Ordered(Matching(scope, terms, asOf, filter), newest: true)
                .Take(NewsLimits.MaxMarkReadPerRequest + 1)
                .Select(a => a.NewsArticleId)
                .News().ToListAsync(cancellationToken);
            var hasMore = ids.Count > NewsLimits.MaxMarkReadPerRequest;
            var marked = 0;
            foreach (var chunk in ids.Take(NewsLimits.MaxMarkReadPerRequest).Chunk(MarkReadChunk))
            {
                var articles = await context.NewsArticles.News().Where(a => chunk.Contains(a.NewsArticleId) && !a.IsRead).ToListAsync(cancellationToken);
                marked += await ApplyReadAsync(articles, isRead: true, cancellationToken);
                context.ChangeTracker.Clear();
            }

            return new MarkNewsReadResult { MarkedCount = marked, HasMore = hasMore };
        });

    // ── Article tags ──────────────────────────────────────────────────────────────────────────────────

    /// <summary><c>PUT …/tags</c> (§5.2 item 19): replaces the article's own tags; an archived tag already on it may stay.</summary>
    public Task<bool> SetTagsAsync(Guid id, NewsArticleTagsUpdate request, CancellationToken cancellationToken) =>
        NewsQueryGuard.RunAsync(async () =>
        {
            ArgumentNullException.ThrowIfNull(request);
            var article = await VisibleArticles().News().Where(a => a.NewsArticleId == id).Select(a => new { a.SortAtUtc }).FirstOrDefaultAsync(cancellationToken);
            if (article is null)
            {
                return false;
            }

            var wanted = NewsFilterIds.Normalize(request.TagIds);
            var links = await context.NewsArticleTags.News().Where(l => l.NewsArticleId == id).ToListAsync(cancellationToken);
            var existing = links.Select(l => l.NewsTagId).ToHashSet();
            if (wanted.Count > 0)
            {
                var tags = await context.NewsTags.AsNoTracking().News().Where(t => wanted.Contains(t.NewsTagId))
                    .Select(t => new { t.NewsTagId, t.ArchivedAtUtc }).ToListAsync(cancellationToken);
                if (tags.Count != wanted.Count || tags.Any(t => t.ArchivedAtUtc is not null && !existing.Contains(t.NewsTagId)))
                {
                    throw NewsErrors.TagNotUsable();
                }
            }

            context.NewsArticleTags.RemoveRange(links.Where(l => !wanted.Contains(l.NewsTagId)));
            context.NewsArticleTags.AddRange(wanted.Where(t => !existing.Contains(t)).Select(t => NewLink(id, t, article.SortAtUtc)));
            await context.SaveChangesAsync(cancellationToken);
            return true;
        });

    /// <summary>
    /// <c>PUT …/tags/{tagId}</c> (§5.2 item 20): adds one tag, idempotently. An archived tag is <c>422</c>. Returns
    /// <c>false</c> when the article or the tag is unknown.
    /// </summary>
    public Task<bool> AddTagAsync(Guid id, Guid tagId, CancellationToken cancellationToken) =>
        NewsQueryGuard.RunAsync(async () =>
        {
            var article = await VisibleArticles().News().Where(a => a.NewsArticleId == id).Select(a => new { a.SortAtUtc }).FirstOrDefaultAsync(cancellationToken);
            var tag = await context.NewsTags.AsNoTracking().News().Where(t => t.NewsTagId == tagId).Select(t => new { t.ArchivedAtUtc }).FirstOrDefaultAsync(cancellationToken);
            if (article is null || tag is null)
            {
                return false;
            }

            if (await context.NewsArticleTags.News().AnyAsync(l => l.NewsArticleId == id && l.NewsTagId == tagId, cancellationToken))
            {
                return true;
            }

            if (tag.ArchivedAtUtc is not null)
            {
                throw NewsErrors.TagArchived(tagId);
            }

            context.NewsArticleTags.Add(NewLink(id, tagId, article.SortAtUtc));
            try
            {
                await context.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException ex) when (ex.GetBaseException() is MySqlConnector.MySqlException { ErrorCode: MySqlConnector.MySqlErrorCode.DuplicateKeyEntry })
            {
                // A concurrent add of the same link: idempotent, so the outcome is the same.
            }

            return true;
        });

    /// <summary>
    /// <c>DELETE …/tags/{tagId}</c> (§5.2 item 20): removes one tag, idempotently — an archived one included, which the
    /// replace endpoint can remove too. <c>false</c> when the article or the tag is unknown.
    /// </summary>
    public Task<bool> RemoveTagAsync(Guid id, Guid tagId, CancellationToken cancellationToken) =>
        NewsQueryGuard.RunAsync(async () =>
        {
            if (!await ExistsAsync(id, cancellationToken) || !await context.NewsTags.AsNoTracking().News().AnyAsync(t => t.NewsTagId == tagId, cancellationToken))
            {
                return false;
            }

            var links = await context.NewsArticleTags.News().Where(l => l.NewsArticleId == id && l.NewsTagId == tagId).ToListAsync(cancellationToken);
            context.NewsArticleTags.RemoveRange(links);
            await context.SaveChangesAsync(cancellationToken);
            return true;
        });

    // ── Shared rules ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>The resolved scope of one list or mark-read request: the feeds to exclude and an inherited-tag expansion.</summary>
    private sealed record NewsScope(IReadOnlyList<Guid> ExcludedFeeds, IReadOnlyList<Guid> ExpandedFeeds);

    /// <summary>
    /// The id rules (§5.2): an unknown id — a feed pending deletion included — is a <c>422</c> keyed by its list. A named
    /// archived feed is included whatever <c>includeArchivedFeeds</c> says; an archived tag filters normally. A feed
    /// pending deletion is always excluded.
    /// </summary>
    private async Task<NewsScope> ResolveScopeAsync(NewsArticleFilter filter, Func<string, string, Exception> unknown, CancellationToken cancellationToken)
    {
        if (filter.FeedIds.Count > 0)
        {
            var known = await context.NewsFeeds.AsNoTracking().News()
                .CountAsync(f => filter.FeedIds.Contains(f.NewsFeedId) && f.DeletionRequestedAtUtc == null, cancellationToken);
            if (known != filter.FeedIds.Count)
            {
                throw unknown("feedIds", "Every feed id must name an existing feed.");
            }
        }

        if (filter.TagIds.Count > 0)
        {
            var known = await context.NewsTags.AsNoTracking().News().CountAsync(t => filter.TagIds.Contains(t.NewsTagId), cancellationToken);
            if (known != filter.TagIds.Count)
            {
                throw unknown("tagIds", "Every tag id must name an existing tag.");
            }
        }

        var excluded = await context.NewsFeeds.AsNoTracking().News()
            .Where(f => f.DeletionRequestedAtUtc != null
                || (!filter.IncludeArchivedFeeds && f.ArchivedAtUtc != null && !filter.FeedIds.Contains(f.NewsFeedId)))
            .Select(f => f.NewsFeedId)
            .ToListAsync(cancellationToken);

        IReadOnlyList<Guid> expanded = [];
        if (filter.TagIds.Count > 0 && filter.FeedIds.Count == 0)
        {
            expanded = await context.NewsFeedTags.AsNoTracking()
                .Where(l => filter.TagIds.Contains(l.NewsTagId) && !excluded.Contains(l.NewsFeedId))
                .Select(l => l.NewsFeedId)
                .Distinct()
                .OrderBy(id => id)
                .Take(NewsLimits.MaxFilterIds + 1)
                .News().ToListAsync(cancellationToken);
        }

        return new NewsScope(excluded, expanded);
    }

    /// <summary>Every filter but the id lists and the search: visibility, read state, dates and the <c>asOfUtc</c> edge.</summary>
    private IQueryable<NewsArticle> Filtered(NewsScope scope, DateTime asOf, NewsArticleFilter filter)
    {
        var articles = context.NewsArticles.AsNoTracking().Where(a => a.FirstSeenAtUtc <= asOf);
        if (scope.ExcludedFeeds.Count > 0)
        {
            var excluded = scope.ExcludedFeeds;
            articles = articles.Where(a => !excluded.Contains(a.NewsFeedId));
        }

        articles = NewsReadStatusFilter.Apply(articles, filter.Statuses);

        if (filter.FromUtc is { } from)
        {
            articles = articles.Where(a => a.SortAtUtc >= from);
        }

        if (filter.ToUtc is { } to)
        {
            articles = articles.Where(a => a.SortAtUtc < to);
        }

        return articles;
    }

    /// <summary>The plain filter form: every filter, the id lists as membership tests, and the search.</summary>
    private IQueryable<NewsArticle> Matching(NewsScope scope, IReadOnlyList<string> terms, DateTime asOf, NewsArticleFilter filter)
    {
        var articles = Filtered(scope, asOf, filter);
        if (filter.FeedIds.Count > 0)
        {
            var feeds = filter.FeedIds;
            articles = articles.Where(a => feeds.Contains(a.NewsFeedId));
        }

        articles = TagMember(articles, filter.TagIds);
        return terms.Count > 0 ? search.Apply(articles, terms) : articles;
    }

    /// <summary>Any of the tags, own or inherited from the article's feed.</summary>
    private IQueryable<NewsArticle> TagMember(IQueryable<NewsArticle> articles, IReadOnlyList<Guid> tagIds)
    {
        if (tagIds.Count == 0)
        {
            return articles;
        }

        return articles.Where(a => context.NewsArticleTags.Any(l => l.NewsArticleId == a.NewsArticleId && tagIds.Contains(l.NewsTagId))
            || context.NewsFeedTags.Any(l => l.NewsFeedId == a.NewsFeedId && tagIds.Contains(l.NewsTagId)));
    }

    /// <summary>The keyset predicate (§5.2), written expanded rather than as a row constructor.</summary>
    private static IQueryable<NewsArticle> After(IQueryable<NewsArticle> articles, (DateTime SortAtUtc, Guid Id)? after, bool newest)
    {
        if (after is not { } key)
        {
            return articles;
        }

        var (s, id) = key;
        return newest
            ? articles.Where(a => a.SortAtUtc < s || (a.SortAtUtc == s && a.NewsArticleId.CompareTo(id) < 0))
            : articles.Where(a => a.SortAtUtc > s || (a.SortAtUtc == s && a.NewsArticleId.CompareTo(id) > 0));
    }

    private static IOrderedQueryable<NewsArticle> Ordered(IQueryable<NewsArticle> articles, bool newest) =>
        newest
            ? articles.OrderByDescending(a => a.SortAtUtc).ThenByDescending(a => a.NewsArticleId)
            : articles.OrderBy(a => a.SortAtUtc).ThenBy(a => a.NewsArticleId);

    /// <summary>Articles whose feed is not pending deletion: the only articles any endpoint reaches.</summary>
    private IQueryable<NewsArticle> VisibleArticles(bool tracked = false)
    {
        var articles = tracked ? context.NewsArticles : context.NewsArticles.AsNoTracking();
        return articles.Where(a => context.NewsFeeds.Any(f => f.NewsFeedId == a.NewsFeedId && f.DeletionRequestedAtUtc == null));
    }

    private Task<bool> ExistsAsync(Guid id, CancellationToken cancellationToken) =>
        NewsQueryGuard.RunAsync(() => VisibleArticles().News().AnyAsync(a => a.NewsArticleId == id, cancellationToken));

    /// <summary>
    /// Flips the read state of tracked articles and moves their feeds' unread counts, in one transaction. Marking read
    /// an article that is already read but updated since it was read is a RE-READ: <c>ReadAtUtc</c> moves to now, so it
    /// leaves the Updated set, while the state, the unread counts and the returned count stay as they were.
    /// </summary>
    private async Task<int> ApplyReadAsync(IReadOnlyList<NewsArticle> articles, bool isRead, CancellationToken cancellationToken)
    {
        var changed = articles.Where(a => a.IsRead != isRead).ToList();
        var reread = isRead ? articles.Where(NewsReadStatusFilter.UpdatedSinceRead).ToList() : [];
        if (changed.Count == 0 && reread.Count == 0)
        {
            return 0;
        }

        var now = Now;
        foreach (var article in changed)
        {
            article.IsRead = isRead;
            article.ReadAtUtc = isRead ? now : null;
        }

        foreach (var article in reread)
        {
            article.ReadAtUtc = now;
        }

        await NewsTransactions.RunAsync(context, async () =>
        {
            await context.SaveChangesAsync(cancellationToken);
            // Ascending feed id: the counter writer's lock order.
            foreach (var feed in changed.GroupBy(a => a.NewsFeedId).OrderBy(g => g.Key))
            {
                await counters.AddToFeedAsync(context, feed.Key, 0, isRead ? -feed.Count() : feed.Count(), cancellationToken);
            }
        }, cancellationToken);
        return changed.Count;
    }

    private async Task<List<NewsArticleSummary>> SummariesAsync(IReadOnlyList<Guid> ids, CancellationToken cancellationToken)
    {
        if (ids.Count == 0)
        {
            return [];
        }

        var articles = await context.NewsArticles.AsNoTracking().News().Where(a => ids.Contains(a.NewsArticleId)).ToDictionaryAsync(a => a.NewsArticleId, cancellationToken);
        var feedIds = articles.Values.Select(a => a.NewsFeedId).Distinct().ToList();
        var feeds = await (from feed in context.NewsFeeds.AsNoTracking()
                           join state in context.NewsFeedFetchStates.AsNoTracking() on feed.NewsFeedId equals state.NewsFeedId into states
                           from state in states.DefaultIfEmpty()
                           where feedIds.Contains(feed.NewsFeedId)
                           select new { feed.NewsFeedId, feed.CustomTitle, Title = state == null ? null : state.Title, SiteUrl = state == null ? null : state.SiteUrl })
            .News().ToDictionaryAsync(f => f.NewsFeedId, cancellationToken);
        var ownTags = (await context.NewsArticleTags.AsNoTracking().News().Where(l => ids.Contains(l.NewsArticleId))
                .Select(l => new { l.NewsArticleId, l.NewsTagId }).ToListAsync(cancellationToken))
            .ToLookup(l => l.NewsArticleId, l => l.NewsTagId);
        var feedTags = (await context.NewsFeedTags.AsNoTracking().News().Where(l => feedIds.Contains(l.NewsFeedId))
                .Select(l => new { l.NewsFeedId, l.NewsTagId }).ToListAsync(cancellationToken))
            .ToLookup(l => l.NewsFeedId, l => l.NewsTagId);
        var imageCounts = await context.NewsArticleImages.AsNoTracking().News()
            .Where(i => ids.Contains(i.NewsArticleId) && i.State == NewsArticleImageState.Stored && i.NewsArticleFileId != null)
            .GroupBy(i => i.NewsArticleId)
            .Select(g => new { ArticleId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.ArticleId, g => g.Count, cancellationToken);

        var result = new List<NewsArticleSummary>(ids.Count);
        foreach (var id in ids)
        {
            if (!articles.TryGetValue(id, out var a))
            {
                continue;
            }

            var feed = feeds.GetValueOrDefault(a.NewsFeedId);
            result.Add(new NewsArticleSummary
            {
                Id = a.NewsArticleId,
                FeedId = a.NewsFeedId,
                FeedTitle = NewsFeedProjection.DisplayTitle(feed?.CustomTitle, feed?.Title, feed?.SiteUrl),
                Title = a.Title,
                Author = a.Author,
                Excerpt = a.Excerpt,
                Link = a.Link,
                PublishedAtUtc = a.PublishedAtUtc,
                SortAtUtc = a.SortAtUtc,
                FirstSeenAtUtc = a.FirstSeenAtUtc,
                ContentChangedAtUtc = a.ContentChangedAtUtc,
                RevisionCount = a.RevisionCount,
                IsRead = a.IsRead,
                ReadAtUtc = a.ReadAtUtc,
                TagIds = [.. ownTags[id].Order()],
                FeedTagIds = [.. feedTags[a.NewsFeedId].Order()],
                ImageCount = imageCounts.GetValueOrDefault(id),
                Language = a.Language,
            });
        }

        return result;
    }

    private static NewsArticleDetail Detail(NewsArticleSummary s) => new()
    {
        Id = s.Id,
        FeedId = s.FeedId,
        FeedTitle = s.FeedTitle,
        Title = s.Title,
        Author = s.Author,
        Excerpt = s.Excerpt,
        Link = s.Link,
        PublishedAtUtc = s.PublishedAtUtc,
        SortAtUtc = s.SortAtUtc,
        FirstSeenAtUtc = s.FirstSeenAtUtc,
        ContentChangedAtUtc = s.ContentChangedAtUtc,
        RevisionCount = s.RevisionCount,
        IsRead = s.IsRead,
        ReadAtUtc = s.ReadAtUtc,
        TagIds = s.TagIds,
        FeedTagIds = s.FeedTagIds,
        ImageCount = s.ImageCount,
        Language = s.Language,
    };

    /// <summary>
    /// The bounded read (§5.2 item 13): at most <see cref="NewsLimits.MaxBodyLength"/> characters through a fixed buffer,
    /// under the request token. A file the store reports missing (or flagged) is <c>null</c> and logged; an unreadable
    /// volume or an I/O error is <c>503 news-storage-unavailable</c>.
    /// </summary>
    private async Task<string?> ReadTextAsync(Guid? storedObjectId, string what, CancellationToken cancellationToken)
    {
        if (storedObjectId is not { } objectId)
        {
            logger.LogError("News text of {What} has no plain-text file row.", what);
            return null;
        }

        StoredObjectContent content;
        try
        {
            content = await store.OpenReadAsync(NewsArticleStorage.FeatureKey, objectId, cancellationToken);
        }
        catch (FileStorageUnavailableException)
        {
            throw NewsErrors.StorageUnavailable();
        }
        catch (Exception ex) when (ex is FileStorageIntegrityException or DomainNotFoundException)
        {
            logger.LogError("News text of {What} is unavailable: its file is missing or failed its check.", what);
            return null;
        }

        try
        {
            await using var stream = content.Content;
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 16 * 1024);
            var buffer = new char[8 * 1024];
            var text = new StringBuilder();
            while (text.Length < NewsLimits.MaxBodyLength)
            {
                var read = await reader.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, NewsLimits.MaxBodyLength - text.Length)), cancellationToken);
                if (read == 0)
                {
                    break;
                }

                text.Append(buffer, 0, read);
            }

            return text.ToString();
        }
        catch (IOException)
        {
            throw NewsErrors.StorageUnavailable();
        }
    }

    /// <summary>No binding, an inactive store or a degraded bound volume: the file endpoints are <c>503</c> (§5).</summary>
    private void EnsureStorage()
    {
        if (!volumes.IsActive
            || !volumes.Bindings.TryGetValue(NewsArticleStorage.FeatureKey, out var name)
            || volumes.Find(name) is not { } volume
            || volumes.RecheckIfDue(volume))
        {
            throw NewsErrors.StorageUnavailable();
        }
    }

    private static NewsArticleTag NewLink(Guid articleId, Guid tagId, DateTime sortAtUtc) =>
        new() { NewsArticleTagId = Guid.NewGuid(), NewsArticleId = articleId, NewsTagId = tagId, SortAtUtc = sortAtUtc };

    private static NewsArticleFilter Normalize(NewsArticlesQueryParams query) => new(
        Sorted(query.FeedIds), Sorted(query.TagIds), NewsReadStatusFilter.Normalize(query.IsRead, query.Statuses), ListQuery.NormalizeSearch(query.Search),
        query.From?.UtcDateTime, query.To?.UtcDateTime, query.IncludeArchivedFeeds, query.SortBy ?? NewsArticleSortBy.Newest);

    private static List<Guid> Sorted(IEnumerable<Guid>? ids) => [.. NewsFilterIds.Normalize(ids).Order()];

    private static IReadOnlyList<string> Terms(string? normalizedSearch)
    {
        if (normalizedSearch is null)
        {
            return [];
        }

        var terms = NewsSearchTerms.Parse(normalizedSearch);
        return terms.Count == 0 ? throw NewsErrors.SearchUnusable() : terms;
    }
}
