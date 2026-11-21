using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MySqlConnector;
using Odyssey.Context;
using Odyssey.Core.Pagination;
using Odyssey.Dtos;
using Odyssey.Dtos.Internet;

namespace Odyssey.Core.Internet.News.Reading;

/// <summary>
/// What <see cref="NewsTagService.ResolveOrCreateAsync"/> made of a list of names (issue #508 §8.3.5). Every input name
/// is in exactly one of <see cref="Resolved"/> (keyed by the name as given, ordinally) or <see cref="Skipped"/>, or in
/// neither when a concurrent delete removed the tag a collision mapped onto.
/// </summary>
public sealed record NewsTagResolution(
    IReadOnlyDictionary<string, Guid> Resolved,
    IReadOnlyList<string> Created,
    IReadOnlyList<(string Name, NewsOpmlTagSkipReason Reason)> Skipped);

/// <summary>
/// The news tags (issue #470 §4.12, §5.4): the journal and photo tags' contract — a paged list with name/description
/// search, archival-status filter and allowlisted sort; a full-replacement update that also archives or restores; and a
/// delete that refuses a tag in use (<c>409</c>, archive it instead). A name is unique across every tag, archived or not
/// (the unique <c>NameKey</c>); there are at most <see cref="NewsLimits.MaxTags"/>.
/// </summary>
public sealed class NewsTagService(OdysseyContext context, TimeProvider time, ILogger<NewsTagService> logger)
{
    private DateTime Now => time.GetUtcNow().UtcDateTime;

    /// <summary>Server-side paged list (issue #277): name/description search + status filter + allowlisted sort.</summary>
    public Task<PagedResult<ExistingNewsTag>> ListAsync(NewsTagsQueryParams query, CancellationToken cancellationToken) =>
        NewsQueryGuard.RunAsync(async () =>
        {
            ArgumentNullException.ThrowIfNull(query);
            var q = context.NewsTags.AsNoTracking().News();

            var term = ListQuery.NormalizeSearch(query.Search);
            if (term is not null)
            {
                var pattern = ListQuery.ContainsPattern(term);
                q = q.Where(t => EF.Functions.Like(t.Name, pattern) ||
                                 (t.Description != null && EF.Functions.Like(t.Description, pattern)));
            }

            q = query.Status switch
            {
                ArchivalStatus.Archived => q.Where(t => t.ArchivedAtUtc != null),
                ArchivalStatus.Active => q.Where(t => t.ArchivedAtUtc == null),
                _ => q,
            };

            var ascending = ListQuery.Ascending(query.SortDir, naturalDefaultAscending: true);
            IOrderedQueryable<NewsTag> sorted = query.SortBy switch
            {
                NewsTagSortBy.Status => ascending ? q.OrderBy(t => t.ArchivedAtUtc != null) : q.OrderByDescending(t => t.ArchivedAtUtc != null),
                _ => ascending ? q.OrderBy(t => t.Name) : q.OrderByDescending(t => t.Name),
            };

            return await sorted.ThenBy(t => t.NewsTagId).ToPagedResultAsync(query.Offset, query.Limit, ToDto, cancellationToken);
        });

    public Task<ExistingNewsTag?> GetAsync(Guid id, CancellationToken cancellationToken) =>
        NewsQueryGuard.RunAsync(async () =>
        {
            var tag = await context.NewsTags.AsNoTracking().News().FirstOrDefaultAsync(t => t.NewsTagId == id, cancellationToken);
            return tag is null ? null : ToDto(tag);
        });

    public Task<ExistingNewsTag> CreateAsync(NewNewsTag request, CancellationToken cancellationToken) =>
        NewsQueryGuard.RunAsync(async () =>
        {
            ArgumentNullException.ThrowIfNull(request);
            TagIconGuard.EnsureKnown(request.Icon);
            TagColorGuard.EnsureValid(request.Color);
            var name = request.Name!.Trim();
            var key = NameKey(name);
            await EnsureNameAvailableAsync(key, null, cancellationToken);
            if (await context.NewsTags.News().CountAsync(cancellationToken) >= NewsLimits.MaxTags)
            {
                throw NewsErrors.TagLimit();
            }

            var now = Now;
            var tag = new NewsTag
            {
                NewsTagId = Guid.NewGuid(),
                Name = name,
                NameKey = key,
                Description = Clean(request.Description),
                Icon = request.Icon,
                Color = HexColor.Normalize(request.Color),
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
            };
            context.NewsTags.Add(tag);
            await SaveAsync(cancellationToken);
            return ToDto(tag);
        });

    /// <summary>
    /// Full replacement: an omitted icon, colour or description clears it, and <see cref="UpdateNewsTag.Archived"/>
    /// archives or restores — links stay either way, so an archived tag still filters. <c>null</c> for an unknown tag.
    /// </summary>
    public Task<ExistingNewsTag?> UpdateAsync(Guid id, UpdateNewsTag request, CancellationToken cancellationToken) =>
        NewsQueryGuard.RunAsync(async () =>
        {
            ArgumentNullException.ThrowIfNull(request);
            TagIconGuard.EnsureKnown(request.Icon);
            TagColorGuard.EnsureValid(request.Color);
            var tag = await context.NewsTags.News().FirstOrDefaultAsync(t => t.NewsTagId == id, cancellationToken);
            if (tag is null)
            {
                return null;
            }

            var name = request.Name!.Trim();
            var key = NameKey(name);
            await EnsureNameAvailableAsync(key, id, cancellationToken);
            tag.Name = name;
            tag.NameKey = key;
            tag.Description = Clean(request.Description);
            tag.Icon = request.Icon;
            tag.Color = HexColor.Normalize(request.Color);
            var now = Now;
            if (request.Archived != tag.ArchivedAtUtc is not null)
            {
                tag.ArchivedAtUtc = request.Archived ? now : null;
            }

            tag.UpdatedAtUtc = now;
            await SaveAsync(cancellationToken);
            return (ExistingNewsTag?)ToDto(tag);
        });

    /// <summary>
    /// <c>DELETE</c>: a tag any feed or article links is <c>409 news-tag-in-use</c> with its counts — archive it instead,
    /// as on every tag resource. The check and the delete share one transaction, so a link added between them is never
    /// destroyed.
    /// </summary>
    public Task<bool> DeleteAsync(Guid id, string userId, CancellationToken cancellationToken) =>
        NewsQueryGuard.RunAsync(async () =>
        {
            var deleted = await NewsTransactions.RunAsync(context, async () =>
            {
                var tag = await context.NewsTags.News().FirstOrDefaultAsync(t => t.NewsTagId == id, cancellationToken);
                if (tag is null)
                {
                    return false;
                }

                var feeds = await context.NewsFeedTags.News().CountAsync(l => l.NewsTagId == id, cancellationToken);
                var articles = await context.NewsArticleTags.News().CountAsync(l => l.NewsTagId == id, cancellationToken);
                if (feeds + articles > 0)
                {
                    throw NewsErrors.TagInUse(feeds, articles);
                }

                context.NewsTags.Remove(tag);
                await context.SaveChangesAsync(cancellationToken);
                return true;
            }, cancellationToken);

            if (deleted)
            {
                logger.LogInformation("News tag {TagId} deleted by {ActorUserId}.", id, userId);
            }

            return deleted;
        });

    /// <summary>Distinct keys per batched lookup (§8.3.5.1): up to 32,000 names is at most 64 queries.</summary>
    internal const int ResolveLookupBatch = 500;

    /// <summary>Names per committed insert batch (§8.3.5.3).</summary>
    internal const int ResolveInsertBatch = 50;

    /// <summary>
    /// Maps tag names to tags for a bulk caller — the OPML import (issue #508 §8.3.5, B6) — never a loop over
    /// <see cref="CreateAsync"/>, which saves per call and answers a collision with <c>409</c>. It must run OUTSIDE any
    /// explicit transaction: each lookup and each insert batch commits on its own, so a re-read after a collision sees
    /// the row the other spelling or the other request committed (B1, R2-2).
    /// <list type="number">
    /// <item>One <c>WHERE NameKey IN (…)</c> per <see cref="ResolveLookupBatch"/> distinct keys, so the database's
    /// comparison — accent- and case-insensitive, which <see cref="NameKey"/> alone is not — decides equality. A row
    /// maps every input spelling it equals.</item>
    /// <item>A matched archived tag is skipped as <c>Archived</c>: a new link to it is refused, as on a feed write.</item>
    /// <item>Unmatched names are inserted in first-seen order, <see cref="ResolveInsertBatch"/> per committed save, while
    /// <paramref name="canCreate"/> holds and the tag count stays below <see cref="NewsLimits.MaxTags"/>; otherwise they
    /// are <c>NoPermission</c> or <c>TagLimit</c>. A created tag has no description, icon or colour.</item>
    /// <item>On a <c>NameKey</c> duplicate-key error the batch's entities are detached and it is retried one name at a
    /// time; a colliding name is re-read by a fresh query and maps onto the committed row, reported in neither list.</item>
    /// </list>
    /// A name longer than <see cref="NewsLimits.MaxTagNameLength"/> is <c>NameTooLong</c>, never truncated.
    /// </summary>
    public async Task<NewsTagResolution> ResolveOrCreateAsync(IReadOnlyList<string> names, bool canCreate, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(names);
        var resolved = new Dictionary<string, Guid>(StringComparer.Ordinal);
        var created = new List<string>();
        var skipped = new List<(string Name, NewsOpmlTagSkipReason Reason)>();

        // Distinct by key, first spelling first; the other spellings of a key follow its fate.
        var spellings = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var keys = new List<string>();
        foreach (var raw in names)
        {
            var name = raw.Trim();
            if (name.Length == 0 || spellings.TryGetValue(NameKey(name), out var seen) && seen.Contains(name))
            {
                continue;
            }

            if (name.Length > NewsLimits.MaxTagNameLength)
            {
                skipped.Add((name, NewsOpmlTagSkipReason.NameTooLong));
                continue;
            }

            var key = NameKey(name);
            if (!spellings.TryGetValue(key, out var list))
            {
                spellings[key] = list = [];
                keys.Add(key);
            }

            list.Add(name);
        }

        if (keys.Count == 0)
        {
            return new NewsTagResolution(resolved, created, skipped);
        }

        void Map(string key, Guid tagId, bool archived)
        {
            foreach (var name in spellings[key])
            {
                if (archived)
                {
                    skipped.Add((name, NewsOpmlTagSkipReason.Archived));
                }
                else
                {
                    resolved[name] = tagId;
                }
            }
        }

        // 1–2. Batched lookups. The database decided which rows equal which keys; an exact key match is taken first, then
        // the accent- and case-insensitive comparison that approximates the column's collation. A key the database
        // matched but this comparison misses falls through to the insert, whose collision re-read maps it exactly.
        var unmatched = new List<string>();
        foreach (var batch in keys.Chunk(ResolveLookupBatch))
        {
            var rows = await NewsQueryGuard.RunAsync(() => context.NewsTags.AsNoTracking().News()
                .Where(t => batch.Contains(t.NameKey))
                .Select(t => new { t.NewsTagId, t.NameKey, t.ArchivedAtUtc })
                .ToListAsync(cancellationToken));
            foreach (var key in batch)
            {
                var row = rows.FirstOrDefault(r => string.Equals(r.NameKey, key, StringComparison.Ordinal))
                    ?? rows.FirstOrDefault(r => CollationEquals(r.NameKey, key));
                if (row is null)
                {
                    unmatched.Add(key);
                }
                else
                {
                    Map(key, row.NewsTagId, row.ArchivedAtUtc is not null);
                }
            }
        }

        if (unmatched.Count == 0)
        {
            return new NewsTagResolution(resolved, created, skipped);
        }

        if (!canCreate)
        {
            foreach (var key in unmatched)
            {
                skipped.AddRange(spellings[key].Select(name => (name, NewsOpmlTagSkipReason.NoPermission)));
            }

            return new NewsTagResolution(resolved, created, skipped);
        }

        // 3–4. Inserts in committed batches, under the cap.
        var count = await NewsQueryGuard.RunAsync(() => context.NewsTags.AsNoTracking().News().CountAsync(cancellationToken));
        var queue = new Queue<string>(unmatched);
        while (queue.Count > 0)
        {
            var room = NewsLimits.MaxTags - count;
            if (room <= 0)
            {
                foreach (var key in queue)
                {
                    skipped.AddRange(spellings[key].Select(name => (name, NewsOpmlTagSkipReason.TagLimit)));
                }

                break;
            }

            var batch = new List<string>();
            while (queue.Count > 0 && batch.Count < Math.Min(ResolveInsertBatch, room))
            {
                batch.Add(queue.Dequeue());
            }

            var entities = batch.Select(NewTag).ToList();
            context.NewsTags.AddRange(entities);
            if (await TrySaveInsertsAsync(entities, cancellationToken))
            {
                foreach (var (key, tag) in batch.Zip(entities))
                {
                    Map(key, tag.NewsTagId, archived: false);
                    created.Add(tag.Name);
                }

                count += entities.Count;
                continue;
            }

            // One at a time: the batch failed as a whole, so each name either inserts or collides on its own.
            foreach (var key in batch)
            {
                if (count >= NewsLimits.MaxTags)
                {
                    skipped.AddRange(spellings[key].Select(name => (name, NewsOpmlTagSkipReason.TagLimit)));
                    continue;
                }

                var tag = NewTag(key);
                context.NewsTags.Add(tag);
                if (await TrySaveInsertsAsync([tag], cancellationToken))
                {
                    Map(key, tag.NewsTagId, archived: false);
                    created.Add(tag.Name);
                    count++;
                    continue;
                }

                // A fresh, autocommitted query: it sees the row the other spelling or the other request committed.
                var existing = await NewsQueryGuard.RunAsync(() => context.NewsTags.AsNoTracking().News()
                    .Where(t => t.NameKey == key)
                    .Select(t => new { t.NewsTagId, t.ArchivedAtUtc })
                    .FirstOrDefaultAsync(cancellationToken));
                if (existing is not null)
                {
                    Map(key, existing.NewsTagId, existing.ArchivedAtUtc is not null);
                }
            }
        }

        return new NewsTagResolution(resolved, created, skipped);

        NewsTag NewTag(string key)
        {
            var now = Now;
            var name = spellings[key][0];
            return new NewsTag
            {
                NewsTagId = Guid.NewGuid(),
                Name = name,
                NameKey = key,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
            };
        }
    }

    /// <summary>The application-side stand-in for the column's accent- and case-insensitive collation.</summary>
    internal static bool CollationEquals(string left, string right) =>
        string.Compare(left, right, CultureInfo.InvariantCulture, CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) == 0;

    /// <summary>Saves pending inserts; on a duplicate key detaches them and returns <c>false</c> (§8.3.5.4).</summary>
    private async Task<bool> TrySaveInsertsAsync(IReadOnlyList<NewsTag> entities, CancellationToken cancellationToken)
    {
        try
        {
            await NewsQueryGuard.RunAsync(() => context.SaveChangesAsync(cancellationToken));
            foreach (var entity in entities)
            {
                context.Entry(entity).State = EntityState.Detached;
            }

            return true;
        }
        catch (DbUpdateException ex) when (ex.GetBaseException() is MySqlException { ErrorCode: MySqlErrorCode.DuplicateKeyEntry })
        {
            foreach (var entity in entities)
            {
                context.Entry(entity).State = EntityState.Detached;
            }

            return false;
        }
    }

    /// <summary>The name's comparison key: trimmed and lower-cased, so names differing only in case collide.</summary>
    public static string NameKey(string name) => name.Trim().ToLowerInvariant();

    private async Task EnsureNameAvailableAsync(string key, Guid? excludeId, CancellationToken cancellationToken)
    {
        if (await context.NewsTags.AsNoTracking().News().AnyAsync(t => t.NameKey == key && (excludeId == null || t.NewsTagId != excludeId), cancellationToken))
        {
            throw NewsErrors.TagDuplicate();
        }
    }

    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.GetBaseException() is MySqlException { ErrorCode: MySqlErrorCode.DuplicateKeyEntry })
        {
            // The race the pre-check cannot see: the unique NameKey index answers the same 409.
            throw NewsErrors.TagDuplicate();
        }
    }

    private static ExistingNewsTag ToDto(NewsTag tag) => new()
    {
        NewsTagId = tag.NewsTagId,
        Name = tag.Name,
        Description = tag.Description,
        Archived = tag.ArchivedAtUtc,
        Icon = tag.Icon is not null && TagIcons.IsKnown(tag.Icon) ? tag.Icon : null,
        Color = HexColor.Normalize(tag.Color),
    };

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
