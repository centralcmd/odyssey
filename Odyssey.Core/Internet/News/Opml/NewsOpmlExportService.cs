using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Odyssey.Context;
using Odyssey.Core.Internet.News.Reading;

namespace Odyssey.Core.Internet.News.Opml;

/// <summary>
/// <c>GET /api/news-feeds/opml</c> and <c>POST /api/news-feeds/opml/export</c> (issue #508 §5.1, amendment v6): every
/// feed not pending deletion, or exactly the requested ones. Everything is loaded and sanitised BEFORE the first byte
/// is written (S5), so a query fault is still a clean <c>503 news-busy</c> rather than a truncated <c>200</c>.
/// </summary>
public sealed class NewsOpmlExportService(OdysseyContext context, TimeProvider time, ILogger<NewsOpmlExportService> logger)
{
    /// <summary>
    /// Loads the export. <paramref name="feedIds"/> <c>null</c> is every feed; otherwise the ids are de-duplicated and
    /// loaded in one bound-parameter <c>IN</c> query, and an unknown id or a feed pending deletion is skipped silently.
    /// </summary>
    public async Task<OpmlExportDocument> LoadAsync(IReadOnlyCollection<Guid>? feedIds, string userId, CancellationToken cancellationToken)
    {
        var ids = feedIds?.Distinct().ToList();
        var document = await NewsQueryGuard.RunAsync(async () =>
        {
            var query = context.NewsFeeds.AsNoTracking().News().Where(f => f.DeletionRequestedAtUtc == null);
            if (ids is not null)
            {
                query = query.Where(f => ids.Contains(f.NewsFeedId));
            }

            var feeds = await query.ToListAsync(cancellationToken);
            var feedIdList = feeds.Select(f => f.NewsFeedId).ToList();
            var states = feedIdList.Count == 0
                ? []
                : await context.NewsFeedFetchStates.AsNoTracking().News()
                    .Where(s => feedIdList.Contains(s.NewsFeedId))
                    .Select(s => new { s.NewsFeedId, s.Title, s.SiteUrl })
                    .ToDictionaryAsync(s => s.NewsFeedId, cancellationToken);
            var links = feedIdList.Count == 0
                ? []
                : await context.NewsFeedTags.AsNoTracking().News()
                    .Where(l => feedIdList.Contains(l.NewsFeedId))
                    .Select(l => new { l.NewsFeedId, l.NewsTagId })
                    .ToListAsync(cancellationToken);
            var tagIds = links.Select(l => l.NewsTagId).Distinct().ToList();
            var tagRows = tagIds.Count == 0
                ? []
                : await context.NewsTags.AsNoTracking().News()
                    .Where(t => tagIds.Contains(t.NewsTagId))
                    .Select(t => new { t.NewsTagId, t.Name })
                    .ToListAsync(cancellationToken);

            // Folders: only the exported feeds' tags (archived ones included), by name. A name that sanitises to the same
            // text as another becomes one folder.
            var tagNames = tagRows.ToDictionary(t => t.NewsTagId, t => OpmlDocumentWriter.Sanitize(t.Name).Trim());
            var folders = tagRows
                .OrderBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(t => t.NewsTagId)
                .Select(t => tagNames[t.NewsTagId])
                .Where(n => n.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            var folderOrder = folders.Select((name, index) => (name, index)).ToDictionary(p => p.name, p => p.index, StringComparer.Ordinal);
            var tagsByFeed = links.ToLookup(l => l.NewsFeedId, l => l.NewsTagId);

            var rows = feeds.Select(feed =>
                {
                    var state = states.GetValueOrDefault(feed.NewsFeedId);
                    var names = tagsByFeed[feed.NewsFeedId]
                        .Select(id => tagNames.GetValueOrDefault(id) ?? string.Empty)
                        .Where(n => n.Length > 0)
                        .Distinct(StringComparer.Ordinal)
                        .OrderBy(n => folderOrder[n])
                        .ToList();
                    var customTitle = OpmlDocumentWriter.Sanitize(feed.CustomTitle).Trim();
                    var siteUrl = OpmlDocumentWriter.Sanitize(state?.SiteUrl).Trim();
                    return new OpmlExportFeed(
                        DisplayTitle: OpmlDocumentWriter.Sanitize(NewsFeedProjection.DisplayTitle(feed.CustomTitle, state?.Title, state?.SiteUrl)),
                        Url: OpmlDocumentWriter.Sanitize(feed.Url),
                        SiteUrl: siteUrl.Length == 0 ? null : siteUrl,
                        CustomTitle: customTitle.Length == 0 ? null : customTitle,
                        FetchIntervalMinutes: feed.FetchIntervalMinutes,
                        DownloadImages: feed.DownloadImages,
                        Active: feed.IsActive && feed.ArchivedAtUtc is null,
                        TagNames: names);
                })
                .OrderBy(f => f.DisplayTitle, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(f => f.Url, StringComparer.Ordinal)
                .ToList();
            return new OpmlExportDocument(rows, folders, time.GetUtcNow().UtcDateTime);
        });

        // The actor, the variant and the row count: never an id, a URL, a title or a tag name (§7.9).
        logger.LogInformation("News OPML export ({Variant}) by {ActorUserId}: {Rows} feeds.",
            ids is null ? "all" : "filtered", userId, document.Feeds.Count);
        return document;
    }

    /// <summary>The download's name, by the UTC date (§5.1).</summary>
    public string FileName() => string.Create(CultureInfo.InvariantCulture, $"odyssey-news-feeds-{time.GetUtcNow().UtcDateTime:yyyy-MM-dd}.opml");
}
