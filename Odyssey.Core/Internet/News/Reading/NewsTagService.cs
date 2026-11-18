using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MySqlConnector;
using Odyssey.Context;
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

/// <summary>Who may detach which link class on a tag delete (§5.4 item 25): decided by the caller's claims.</summary>
public readonly record struct NewsTagDetachRights(bool FeedLinks, bool ArticleLinks);

/// <summary>
/// The news tags (issue #470 §4.12, §5.4): the journal tags' shape and guards. A name is unique across every tag,
/// archived or not (the unique <c>NameKey</c>); there are at most <see cref="NewsLimits.MaxTags"/>. A tag in use is a
/// <c>409</c> with its direct-link counts unless the delete detaches its links, which needs a claim per link class
/// actually present, decided against the delete's own snapshot.
/// </summary>
public sealed class NewsTagService(OdysseyContext context, TimeProvider time, ILogger<NewsTagService> logger)
{
    private DateTime Now => time.GetUtcNow().UtcDateTime;

    /// <summary>Every tag, unpaged (≤ 500), with its direct-link counts (one <c>GROUP BY</c> per link table).</summary>
    public Task<List<ExistingNewsTag>> ListAsync(CancellationToken cancellationToken) =>
        NewsQueryGuard.RunAsync(async () =>
        {
            var tags = await context.NewsTags.AsNoTracking().News().ToListAsync(cancellationToken);
            var feedCounts = await context.NewsFeedTags.AsNoTracking().News().GroupBy(l => l.NewsTagId)
                .Select(g => new { TagId = g.Key, Count = g.Count() }).ToDictionaryAsync(g => g.TagId, g => g.Count, cancellationToken);
            var articleCounts = await context.NewsArticleTags.AsNoTracking().News().GroupBy(l => l.NewsTagId)
                .Select(g => new { TagId = g.Key, Count = g.Count() }).ToDictionaryAsync(g => g.TagId, g => g.Count, cancellationToken);
            return tags
                .OrderBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(t => t.NewsTagId)
                .Select(t => ToDto(t, feedCounts.GetValueOrDefault(t.NewsTagId), articleCounts.GetValueOrDefault(t.NewsTagId)))
                .ToList();
        });

    public Task<ExistingNewsTag?> GetAsync(Guid id, CancellationToken cancellationToken) =>
        NewsQueryGuard.RunAsync(async () =>
        {
            var tag = await context.NewsTags.AsNoTracking().News().FirstOrDefaultAsync(t => t.NewsTagId == id, cancellationToken);
            if (tag is null)
            {
                return null;
            }

            var (feeds, articles) = await CountsAsync(id, cancellationToken);
            return (ExistingNewsTag?)ToDto(tag, feeds, articles);
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
            return ToDto(tag, 0, 0);
        });

    /// <summary>Full replacement: an omitted icon, colour or description clears it. <c>null</c> for an unknown tag.</summary>
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
            tag.UpdatedAtUtc = Now;
            await SaveAsync(cancellationToken);
            var (feeds, articles) = await CountsAsync(id, cancellationToken);
            return (ExistingNewsTag?)ToDto(tag, feeds, articles);
        });

    /// <summary>Archive or unarchive; idempotent. Links stay, so an archived tag still filters. <c>null</c> when unknown.</summary>
    public Task<ExistingNewsTag?> SetArchivedAsync(Guid id, bool archived, CancellationToken cancellationToken) =>
        NewsQueryGuard.RunAsync(async () =>
        {
            var tag = await context.NewsTags.News().FirstOrDefaultAsync(t => t.NewsTagId == id, cancellationToken);
            if (tag is null)
            {
                return null;
            }

            if (archived != tag.ArchivedAtUtc is not null)
            {
                var now = Now;
                tag.ArchivedAtUtc = archived ? now : null;
                tag.UpdatedAtUtc = now;
                await context.SaveChangesAsync(cancellationToken);
            }

            var (feeds, articles) = await CountsAsync(id, cancellationToken);
            return (ExistingNewsTag?)ToDto(tag, feeds, articles);
        });

    /// <summary>
    /// <c>DELETE</c> (§5.4 item 25). Without <paramref name="detachLinks"/> a tag in use is <c>409 news-tag-in-use</c>
    /// with its counts. With it, each link class present needs its claim (<paramref name="rights"/>) — refused with
    /// <see cref="DomainForbiddenException"/>, changing nothing — and the links and the tag go in one transaction whose
    /// own reads decide both. Detaching feed links rotates those feeds' versions, as any feed-tag write does.
    /// </summary>
    public Task<bool> DeleteAsync(Guid id, bool detachLinks, NewsTagDetachRights rights, string userId, CancellationToken cancellationToken) =>
        NewsQueryGuard.RunAsync(async () =>
        {
            var deleted = await NewsTransactions.RunAsync(context, async () =>
            {
                var tag = await context.NewsTags.News().FirstOrDefaultAsync(t => t.NewsTagId == id, cancellationToken);
                if (tag is null)
                {
                    return false;
                }

                var feedLinks = await context.NewsFeedTags.News().Where(l => l.NewsTagId == id).ToListAsync(cancellationToken);
                var articleLinks = await context.NewsArticleTags.News().Where(l => l.NewsTagId == id).ToListAsync(cancellationToken);
                if (feedLinks.Count + articleLinks.Count > 0)
                {
                    if (!detachLinks)
                    {
                        throw NewsErrors.TagInUse(id, feedLinks.Count, articleLinks.Count);
                    }

                    if ((feedLinks.Count > 0 && !rights.FeedLinks) || (articleLinks.Count > 0 && !rights.ArticleLinks))
                    {
                        throw new DomainForbiddenException("You do not have permission to detach this tag from everything that uses it.");
                    }

                    var feedIds = feedLinks.Select(l => l.NewsFeedId).Distinct().ToList();
                    foreach (var feed in await context.NewsFeeds.News().Where(f => feedIds.Contains(f.NewsFeedId)).ToListAsync(cancellationToken))
                    {
                        feed.SettingsVersion = Guid.NewGuid();
                    }

                    context.NewsFeedTags.RemoveRange(feedLinks);
                    context.NewsArticleTags.RemoveRange(articleLinks);
                }

                context.NewsTags.Remove(tag);
                await context.SaveChangesAsync(cancellationToken);
                return true;
            }, cancellationToken);

            if (deleted)
            {
                logger.LogInformation("News tag {TagId} deleted by {ActorUserId} (detachLinks {Detach}).", id, userId, detachLinks);
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

    private async Task<(int Feeds, int Articles)> CountsAsync(Guid id, CancellationToken cancellationToken) =>
        (await context.NewsFeedTags.AsNoTracking().News().CountAsync(l => l.NewsTagId == id, cancellationToken),
            await context.NewsArticleTags.AsNoTracking().News().CountAsync(l => l.NewsTagId == id, cancellationToken));

    private static ExistingNewsTag ToDto(NewsTag tag, int feedCount, int articleCount) => new()
    {
        Id = tag.NewsTagId,
        Name = tag.Name,
        Description = tag.Description,
        Icon = tag.Icon is not null && TagIcons.IsKnown(tag.Icon) ? tag.Icon : null,
        Color = HexColor.Normalize(tag.Color),
        Archived = tag.ArchivedAtUtc is not null,
        CreatedAtUtc = tag.CreatedAtUtc,
        UpdatedAtUtc = tag.UpdatedAtUtc,
        FeedCount = feedCount,
        ArticleCount = articleCount,
    };

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
