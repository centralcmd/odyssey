using Odyssey.Dtos;
using Odyssey.Dtos.Internet;

namespace Odyssey.ApiClient.Resources;

/// <summary>The article list's filters (issue #470 §5.2). Feeds are OR'd, tags are OR'd, and the two lists AND'd.</summary>
public sealed record NewsArticleListFilter
{
    public IReadOnlyCollection<Guid>? FeedIds { get; init; }

    public IReadOnlyCollection<Guid>? TagIds { get; init; }

    /// <summary>Sent only when exactly one of Unread and Read is selected.</summary>
    public bool? IsRead { get; init; }

    public string? Search { get; init; }

    public DateTimeOffset? From { get; init; }

    public DateTimeOffset? To { get; init; }

    public bool IncludeArchivedFeeds { get; init; }

    public NewsArticleSortBy SortBy { get; init; } = NewsArticleSortBy.Newest;
}

/// <summary>
/// The typed client for the news reader's endpoints (issue #470 §5). Every call returns an <see cref="ApiResult"/>;
/// a <c>news-busy</c>, <c>news-storage-unavailable</c> or <c>news-search-limited</c> answer stays on the result with its
/// problem and <c>Retry-After</c>, for the page to present.
/// </summary>
public interface INewsApiClient
{
    // Feeds
    Task<ApiResult<PagedResult<ExistingNewsFeed>>> ListFeedsAsync(
        int page, int pageSize, string? search = null, Guid? tagId = null, NewsFeedState? state = null,
        IReadOnlyCollection<NewsFeedHealth>? health = null, NewsFeedSortBy? sortBy = null, bool? ascending = null,
        CancellationToken ct = default);

    Task<ApiResult<List<NewsFeedCatalogueEntry>>> GetCatalogueAsync(CancellationToken ct = default);

    Task<ApiResult<NewsUnreadCounts>> GetUnreadCountsAsync(CancellationToken ct = default);

    Task<ApiResult<ExistingNewsFeed>> GetFeedAsync(Guid id, CancellationToken ct = default);

    /// <summary>A <c>409 news-feed-duplicate</c> carries <c>existingFeedId</c> on the problem's extensions.</summary>
    Task<ApiResult<ExistingNewsFeed>> CreateFeedAsync(NewNewsFeed feed, CancellationToken ct = default);

    /// <summary>A stale <see cref="UpdateNewsFeed.Version"/> is <c>409 news-feed-changed</c>.</summary>
    Task<ApiResult<ExistingNewsFeed>> UpdateFeedAsync(Guid id, UpdateNewsFeed feed, CancellationToken ct = default);

    Task<ApiResult<ExistingNewsFeed>> SetFeedTagsAsync(Guid id, IReadOnlyCollection<Guid> tagIds, CancellationToken ct = default);

    Task<ApiResult> RefreshFeedAsync(Guid id, CancellationToken ct = default);

    Task<ApiResult<ExistingNewsFeed>> ArchiveFeedAsync(Guid id, CancellationToken ct = default);

    Task<ApiResult<ExistingNewsFeed>> UnarchiveFeedAsync(Guid id, CancellationToken ct = default);

    /// <summary>Only an archived feed; <c>202</c> on success.</summary>
    Task<ApiResult> DeleteFeedAsync(Guid id, CancellationToken ct = default);

    Task<ApiResult<ExistingNewsFeed>> ReleaseQuarantineAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Exports feeds as an OPML 2.0 file (issue #508 §5.1, amendment v6): <c>null</c> is every feed
    /// (<c>GET /api/news-feeds/opml</c>); a list exports exactly those ids (<c>POST …/opml/export</c>), unknown ones and
    /// feeds pending deletion skipped. <see cref="NewsOpmlExportFile.Rows"/> is the number of feeds written.
    /// </summary>
    Task<ApiResult<NewsOpmlExportFile>> ExportOpmlAsync(IReadOnlyCollection<Guid>? feedIds = null, CancellationToken ct = default);

    /// <summary>Imports an OPML file in one step (<c>POST /api/news-feeds/opml</c>, issue #508 §5.2) and returns its report.</summary>
    Task<ApiResult<NewsOpmlImportResult>> ImportOpmlAsync(ApiUpload file, CancellationToken ct = default);

    // Articles

    /// <summary>
    /// One page of articles: the first when <paramref name="cursor"/> is null, else the page after it. A <c>400</c> keyed
    /// <c>cursor</c> means the list must restart from a fresh first page. Not a <see cref="PagedResult{T}"/>.
    /// </summary>
    Task<ApiResult<NewsArticlePage>> ListArticlesAsync(NewsArticleListFilter filter, int limit, string? cursor = null, CancellationToken ct = default);

    Task<ApiResult<NewsArticleDetail>> GetArticleAsync(Guid id, CancellationToken ct = default);

    Task<ApiResult<List<NewsArticleRevisionSummary>>> ListRevisionsAsync(Guid id, CancellationToken ct = default);

    Task<ApiResult<NewsArticleRevisionDetail>> GetRevisionAsync(Guid id, int revisionNumber, CancellationToken ct = default);

    /// <summary>The absolute URL of a stored image, for an <c>&lt;img src&gt;</c> only — never fetched through <see cref="HttpClient"/>.</summary>
    string ImageUrl(Guid articleId, Guid imageId);

    Task<ApiResult> SetReadAsync(Guid id, bool isRead, CancellationToken ct = default);

    Task<ApiResult<MarkNewsReadResult>> SetReadAsync(IReadOnlyCollection<Guid> articleIds, bool isRead, CancellationToken ct = default);

    /// <summary>One <c>mark-read</c> request; repeat it with the same arguments while <see cref="MarkNewsReadResult.HasMore"/>.</summary>
    Task<ApiResult<MarkNewsReadResult>> MarkReadAsync(NewsArticleListFilter filter, DateTime asOfUtc, CancellationToken ct = default);

    Task<ApiResult> SetArticleTagsAsync(Guid id, IReadOnlyCollection<Guid> tagIds, CancellationToken ct = default);

    Task<ApiResult> AddArticleTagAsync(Guid id, Guid tagId, CancellationToken ct = default);

    Task<ApiResult> RemoveArticleTagAsync(Guid id, Guid tagId, CancellationToken ct = default);

    // Tags
    Task<ApiResult<List<ExistingNewsTag>>> ListTagsAsync(CancellationToken ct = default);

    Task<ApiResult<ExistingNewsTag>> CreateTagAsync(NewNewsTag tag, CancellationToken ct = default);

    Task<ApiResult<ExistingNewsTag>> UpdateTagAsync(Guid id, UpdateNewsTag tag, CancellationToken ct = default);

    Task<ApiResult<ExistingNewsTag>> ArchiveTagAsync(Guid id, CancellationToken ct = default);

    Task<ApiResult<ExistingNewsTag>> UnarchiveTagAsync(Guid id, CancellationToken ct = default);

    /// <summary>A tag in use is <c>409 news-tag-in-use</c> with <c>feedCount</c>/<c>articleCount</c> unless <paramref name="detachLinks"/>.</summary>
    Task<ApiResult> DeleteTagAsync(Guid id, bool detachLinks = false, CancellationToken ct = default);

    // Status
    Task<ApiResult<NewsStatus>> GetStatusAsync(CancellationToken ct = default);
}

/// <inheritdoc cref="INewsApiClient" />
public sealed class NewsApiClient(IOdysseyApi api) : INewsApiClient
{
    private const string Feeds = "api/news-feeds";
    private const string Articles = "api/news-articles";
    private const string Tags = "api/news-tags";

    /// <summary>Used when the export response carries no <c>Content-Disposition</c> file name.</summary>
    private const string OpmlFileName = "odyssey-news-feeds.opml";

    public Task<ApiResult<PagedResult<ExistingNewsFeed>>> ListFeedsAsync(
        int page, int pageSize, string? search = null, Guid? tagId = null, NewsFeedState? state = null,
        IReadOnlyCollection<NewsFeedHealth>? health = null, NewsFeedSortBy? sortBy = null, bool? ascending = null,
        CancellationToken ct = default)
    {
        var query = PagedQuery.For(Feeds)
            .Window(page, pageSize)
            .Add("search", search)
            .Add("tagId", tagId?.ToString())
            .Add("state", state?.ToString())
            .AddMany("health", health?.Select(h => h.ToString()));
        if (sortBy is { } key)
        {
            query.Sort(key.ToString(), ascending ?? key == NewsFeedSortBy.Title);
        }

        return api.GetPagedAsync<ExistingNewsFeed>(query.Build(), ct);
    }

    public Task<ApiResult<List<NewsFeedCatalogueEntry>>> GetCatalogueAsync(CancellationToken ct = default) =>
        api.GetAsync<List<NewsFeedCatalogueEntry>>($"{Feeds}/catalogue", ct);

    public Task<ApiResult<NewsUnreadCounts>> GetUnreadCountsAsync(CancellationToken ct = default) =>
        api.GetAsync<NewsUnreadCounts>($"{Feeds}/unread-counts", ct);

    public Task<ApiResult<ExistingNewsFeed>> GetFeedAsync(Guid id, CancellationToken ct = default) =>
        api.GetAsync<ExistingNewsFeed>($"{Feeds}/{id}", ct);

    public Task<ApiResult<ExistingNewsFeed>> CreateFeedAsync(NewNewsFeed feed, CancellationToken ct = default) =>
        api.SendAsync<ExistingNewsFeed>(HttpMethod.Post, Feeds, feed, ct);

    public Task<ApiResult<ExistingNewsFeed>> UpdateFeedAsync(Guid id, UpdateNewsFeed feed, CancellationToken ct = default) =>
        api.SendAsync<ExistingNewsFeed>(HttpMethod.Put, $"{Feeds}/{id}", feed, ct);

    public Task<ApiResult<ExistingNewsFeed>> SetFeedTagsAsync(Guid id, IReadOnlyCollection<Guid> tagIds, CancellationToken ct = default) =>
        api.SendAsync<ExistingNewsFeed>(HttpMethod.Put, $"{Feeds}/{id}/tags", new NewsFeedTagsUpdate { TagIds = [.. tagIds] }, ct);

    public Task<ApiResult> RefreshFeedAsync(Guid id, CancellationToken ct = default) =>
        api.SendAsync(HttpMethod.Post, $"{Feeds}/{id}/refresh", null, ct);

    public Task<ApiResult<ExistingNewsFeed>> ArchiveFeedAsync(Guid id, CancellationToken ct = default) =>
        api.SendAsync<ExistingNewsFeed>(HttpMethod.Post, $"{Feeds}/{id}/archive", null, ct);

    public Task<ApiResult<ExistingNewsFeed>> UnarchiveFeedAsync(Guid id, CancellationToken ct = default) =>
        api.SendAsync<ExistingNewsFeed>(HttpMethod.Post, $"{Feeds}/{id}/unarchive", null, ct);

    public Task<ApiResult> DeleteFeedAsync(Guid id, CancellationToken ct = default) =>
        api.SendAsync(HttpMethod.Delete, $"{Feeds}/{id}", null, ct);

    public async Task<ApiResult<NewsOpmlExportFile>> ExportOpmlAsync(IReadOnlyCollection<Guid>? feedIds = null, CancellationToken ct = default)
    {
        var download = feedIds is null
            ? await api.DownloadExportAsync(HttpMethod.Get, $"{Feeds}/opml", null, OpmlFileName, ct)
            : await api.DownloadExportAsync(HttpMethod.Post, $"{Feeds}/opml/export", new NewsOpmlExportRequest { FeedIds = [.. feedIds] }, OpmlFileName, ct);
        return download.IsSuccess && download.Value is { } value
            ? ApiResult<NewsOpmlExportFile>.Success(new NewsOpmlExportFile(value.File, value.Rows), download.Status)
            : download.CastFailure<NewsOpmlExportFile>();
    }

    public Task<ApiResult<NewsOpmlImportResult>> ImportOpmlAsync(ApiUpload file, CancellationToken ct = default) =>
        api.UploadAsync<NewsOpmlImportResult>(
            $"{Feeds}/opml",
            string.IsNullOrWhiteSpace(file.FileName) ? file with { FileName = "feeds.opml" } : file,
            ct: ct);

    public Task<ApiResult<ExistingNewsFeed>> ReleaseQuarantineAsync(Guid id, CancellationToken ct = default) =>
        api.SendAsync<ExistingNewsFeed>(HttpMethod.Post, $"{Feeds}/{id}/release-quarantine", null, ct);

    public Task<ApiResult<NewsArticlePage>> ListArticlesAsync(NewsArticleListFilter filter, int limit, string? cursor = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        // Window(1, limit) keeps offset at 0, which the server accepts beside a cursor.
        var query = PagedQuery.For(Articles)
            .Window(1, limit)
            .AddMany("feedIds", filter.FeedIds?.Select(id => id.ToString()))
            .AddMany("tagIds", filter.TagIds?.Select(id => id.ToString()))
            .AddBool("isRead", filter.IsRead)
            .Add("search", filter.Search)
            .Add("from", filter.From?.ToString("o"))
            .Add("to", filter.To?.ToString("o"))
            .AddBool("includeArchivedFeeds", filter.IncludeArchivedFeeds ? true : null)
            .Add("sortBy", filter.SortBy.ToString())
            .Add("cursor", cursor);
        return api.GetAsync<NewsArticlePage>(query.Build(), ct);
    }

    public Task<ApiResult<NewsArticleDetail>> GetArticleAsync(Guid id, CancellationToken ct = default) =>
        api.GetAsync<NewsArticleDetail>($"{Articles}/{id}", ct);

    public Task<ApiResult<List<NewsArticleRevisionSummary>>> ListRevisionsAsync(Guid id, CancellationToken ct = default) =>
        api.GetAsync<List<NewsArticleRevisionSummary>>($"{Articles}/{id}/revisions", ct);

    public Task<ApiResult<NewsArticleRevisionDetail>> GetRevisionAsync(Guid id, int revisionNumber, CancellationToken ct = default) =>
        api.GetAsync<NewsArticleRevisionDetail>($"{Articles}/{id}/revisions/{revisionNumber}", ct);

    public string ImageUrl(Guid articleId, Guid imageId) =>
        new Uri(api.BaseAddress!, $"{Articles}/{articleId}/images/{imageId}/content").ToString();

    public Task<ApiResult> SetReadAsync(Guid id, bool isRead, CancellationToken ct = default) =>
        api.SendAsync(HttpMethod.Put, $"{Articles}/{id}/read-state", new NewsArticleReadState { IsRead = isRead }, ct);

    public Task<ApiResult<MarkNewsReadResult>> SetReadAsync(IReadOnlyCollection<Guid> articleIds, bool isRead, CancellationToken ct = default) =>
        api.SendAsync<MarkNewsReadResult>(HttpMethod.Post, $"{Articles}/read-state",
            new NewsArticlesReadStateBulk { ArticleIds = [.. articleIds], IsRead = isRead }, ct);

    public Task<ApiResult<MarkNewsReadResult>> MarkReadAsync(NewsArticleListFilter filter, DateTime asOfUtc, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        return api.SendAsync<MarkNewsReadResult>(HttpMethod.Post, $"{Articles}/mark-read", new MarkNewsRead
        {
            FeedIds = filter.FeedIds is null ? null : [.. filter.FeedIds],
            TagIds = filter.TagIds is null ? null : [.. filter.TagIds],
            Search = filter.Search,
            From = filter.From,
            To = filter.To,
            IncludeArchivedFeeds = filter.IncludeArchivedFeeds,
            AsOfUtc = new DateTimeOffset(DateTime.SpecifyKind(asOfUtc, DateTimeKind.Utc)),
        }, ct);
    }

    public Task<ApiResult> SetArticleTagsAsync(Guid id, IReadOnlyCollection<Guid> tagIds, CancellationToken ct = default) =>
        api.SendAsync(HttpMethod.Put, $"{Articles}/{id}/tags", new NewsArticleTagsUpdate { TagIds = [.. tagIds] }, ct);

    public Task<ApiResult> AddArticleTagAsync(Guid id, Guid tagId, CancellationToken ct = default) =>
        api.SendAsync(HttpMethod.Put, $"{Articles}/{id}/tags/{tagId}", null, ct);

    public Task<ApiResult> RemoveArticleTagAsync(Guid id, Guid tagId, CancellationToken ct = default) =>
        api.SendAsync(HttpMethod.Delete, $"{Articles}/{id}/tags/{tagId}", null, ct);

    public Task<ApiResult<List<ExistingNewsTag>>> ListTagsAsync(CancellationToken ct = default) =>
        api.GetAsync<List<ExistingNewsTag>>(Tags, ct);

    public Task<ApiResult<ExistingNewsTag>> CreateTagAsync(NewNewsTag tag, CancellationToken ct = default) =>
        api.SendAsync<ExistingNewsTag>(HttpMethod.Post, Tags, tag, ct);

    public Task<ApiResult<ExistingNewsTag>> UpdateTagAsync(Guid id, UpdateNewsTag tag, CancellationToken ct = default) =>
        api.SendAsync<ExistingNewsTag>(HttpMethod.Put, $"{Tags}/{id}", tag, ct);

    public Task<ApiResult<ExistingNewsTag>> ArchiveTagAsync(Guid id, CancellationToken ct = default) =>
        api.SendAsync<ExistingNewsTag>(HttpMethod.Post, $"{Tags}/{id}/archive", null, ct);

    public Task<ApiResult<ExistingNewsTag>> UnarchiveTagAsync(Guid id, CancellationToken ct = default) =>
        api.SendAsync<ExistingNewsTag>(HttpMethod.Post, $"{Tags}/{id}/unarchive", null, ct);

    public Task<ApiResult> DeleteTagAsync(Guid id, bool detachLinks = false, CancellationToken ct = default) =>
        api.SendAsync(HttpMethod.Delete, detachLinks ? $"{Tags}/{id}?detachLinks=true" : $"{Tags}/{id}", null, ct);

    public Task<ApiResult<NewsStatus>> GetStatusAsync(CancellationToken ct = default) =>
        api.GetAsync<NewsStatus>("api/news-status", ct);
}
