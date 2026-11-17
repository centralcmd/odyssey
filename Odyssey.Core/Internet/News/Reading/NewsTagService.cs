using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MySqlConnector;
using Odyssey.Context;
using Odyssey.Dtos;
using Odyssey.Dtos.Internet;

namespace Odyssey.Core.Internet.News.Reading;

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
