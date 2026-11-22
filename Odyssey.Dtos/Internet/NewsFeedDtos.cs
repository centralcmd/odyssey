using System.ComponentModel.DataAnnotations;

namespace Odyssey.Dtos.Internet;

// Feed DTOs (issue #470 §5.1, §8.1). Every relationship is set by a scalar id; no request carries a nested object.

/// <summary>The body of <c>POST /api/news-feeds</c>. The API makes no outbound request on it.</summary>
public sealed record NewNewsFeed
{
    /// <summary>The feed's address. <c>NewsFeedUrlRules</c> in the service decides the rest (a <c>422</c> keyed <c>url</c>).</summary>
    [Required]
    [StringLength(NewsLimits.MaxUrlLength)]
    public string? Url { get; set; }

    [StringLength(NewsLimits.MaxCustomTitleLength)]
    public string? CustomTitle { get; set; }

    /// <summary><c>null</c> follows the <c>NewsDefaultFetchIntervalMinutes</c> setting.</summary>
    [Range(NewsLimits.MinFetchIntervalMinutes, NewsLimits.MaxFetchIntervalMinutes)]
    public int? FetchIntervalMinutes { get; set; }

    public bool DownloadImages { get; set; } = true;

    [MaxLength(NewsLimits.MaxTagIdsPerRequest)]
    public List<Guid>? TagIds { get; set; }

    /// <summary>What the feed carries (issue #556 §5.1 #1); absent is <see cref="NewsFeedKind.Article"/>.</summary>
    [EnumDataType(typeof(NewsFeedKind))]
    public NewsFeedKind Kind { get; set; } = NewsFeedKind.Article;

    /// <summary>Queue newly published episodes for download; <c>true</c> on an article feed is a <c>422</c>.</summary>
    public bool AutoDownload { get; set; }

    /// <summary>Remove audio a set number of days after it was saved to a device; <c>true</c> on an article feed is a <c>422</c>.</summary>
    public bool AutoDeleteEnabled { get; set; }

    [Range(PodcastLimits.MinAutoDeleteDays, PodcastLimits.MaxAutoDeleteDays)]
    public int AutoDeleteAfterDays { get; set; } = PodcastLimits.DefaultAutoDeleteDays;
}

/// <summary>
/// The body of <c>PUT /api/news-feeds/{id}</c>: full replacement of the API-owned settings, not an upsert.
/// <see cref="Version"/> is the feed's <c>SettingsVersion</c>; a mismatch is <c>409 news-feed-changed</c>.
/// </summary>
public sealed record UpdateNewsFeed
{
    [Required]
    public Guid? Version { get; set; }

    /// <summary>
    /// Equal to the pending <c>suggestedUrl</c> accepts it; any other change overrides it; unchanged leaves it
    /// pending. Every change runs the same URL validation and uniqueness check.
    /// </summary>
    [Required]
    [StringLength(NewsLimits.MaxUrlLength)]
    public string? Url { get; set; }

    [StringLength(NewsLimits.MaxCustomTitleLength)]
    public string? CustomTitle { get; set; }

    /// <summary><c>null</c> follows the <c>NewsDefaultFetchIntervalMinutes</c> setting.</summary>
    [Range(NewsLimits.MinFetchIntervalMinutes, NewsLimits.MaxFetchIntervalMinutes)]
    public int? FetchIntervalMinutes { get; set; }

    /// <summary>Required for the same reason as <see cref="IsActive"/>: an omitted bool must not bind to <c>false</c>.</summary>
    [Required]
    public bool? DownloadImages { get; set; }

    [Required]
    public bool? IsActive { get; set; }

    /// <summary>
    /// The feed's whole tag set, saved with the settings in one write. <c>null</c> keeps the current tags; <c>[]</c>
    /// clears them. Same rule as <c>PUT …/tags</c>: a kept tag may be archived, a new one must exist and not be.
    /// </summary>
    [MaxLength(NewsLimits.MaxTagIdsPerRequest)]
    public List<Guid>? TagIds { get; set; }

    // Podcasts (issue #556 §5.1 #2). Required and nullable like DownloadImages: a PUT is a full replacement, and an
    // omitted value must not bind to a default.

    [Required]
    [EnumDataType(typeof(NewsFeedKind))]
    public NewsFeedKind? Kind { get; set; }

    [Required]
    public bool? AutoDownload { get; set; }

    [Required]
    public bool? AutoDeleteEnabled { get; set; }

    [Required]
    [Range(PodcastLimits.MinAutoDeleteDays, PodcastLimits.MaxAutoDeleteDays)]
    public int? AutoDeleteAfterDays { get; set; }
}

/// <summary>The body of <c>PUT /api/news-feeds/{id}/tags</c>: the feed's whole tag set.</summary>
public sealed record NewsFeedTagsUpdate
{
    [Required]
    [MaxLength(NewsLimits.MaxTagIdsPerRequest)]
    public List<Guid>? TagIds { get; set; }
}

/// <summary>
/// A feed as the list and detail endpoints return it. The settings fields come from <c>NewsFeeds</c>, the fetch fields
/// from <c>NewsFeedFetchStates</c> and the two counts from <c>NewsFeedCounters</c>.
/// </summary>
public sealed record ExistingNewsFeed
{
    public Guid Id { get; set; }

    /// <summary>The feed's <c>SettingsVersion</c>, which <see cref="UpdateNewsFeed.Version"/> echoes.</summary>
    public Guid Version { get; set; }

    /// <summary><c>CustomTitle ?? Title ?? host(SiteUrl) ?? "Untitled feed"</c>; never the fetch URL's host.</summary>
    public string DisplayTitle { get; set; } = string.Empty;

    public string? Title { get; set; }

    public string? CustomTitle { get; set; }

    public string? SiteUrl { get; set; }

    public string? Description { get; set; }

    public string? Language { get; set; }

    public NewsFeedFormat Format { get; set; }

    /// <summary>The feed's own interval, or <c>null</c> when it follows the default setting.</summary>
    public int? FetchIntervalMinutes { get; set; }

    public int EffectiveIntervalMinutes { get; set; }

    public bool DownloadImages { get; set; }

    public bool IsActive { get; set; }

    public DateTime? ArchivedAtUtc { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public DateTime? LastSuccessAtUtc { get; set; }

    public DateTime? LastCheckedAtUtc { get; set; }

    public DateTime? NextFetchAtUtc { get; set; }

    public NewsFeedHealth Health { get; set; }

    /// <summary>Non-null exactly when <see cref="Health"/> is <see cref="NewsFeedHealth.QuotaReached"/>.</summary>
    public NewsFeedQuotaReason? QuotaReason { get; set; }

    public int UnreadCount { get; set; }

    public int ArticleCount { get; set; }

    public List<Guid> TagIds { get; set; } = [];

    /// <summary>The fetch URL and error detail: <c>null</c> without <c>news-feeds.update</c>.</summary>
    public NewsFeedManagement? Management { get; set; }

    /// <summary>What the feed carries (issue #556 §5.1 #3).</summary>
    public NewsFeedKind Kind { get; set; }

    /// <summary>The podcast details and automation; <c>null</c> on an article feed. All of it is <c>news.read</c> data.</summary>
    public NewsFeedPodcast? Podcast { get; set; }
}

/// <summary>A podcast feed's details, automation and stored-audio figures (issue #556 §5.1 #3).</summary>
public sealed record NewsFeedPodcast
{
    public string? Author { get; set; }

    /// <summary>The show artwork, when stored; served by <c>GET /api/podcast-artwork/{id}/content</c>.</summary>
    public Guid? ArtworkId { get; set; }

    public bool? Explicit { get; set; }

    public bool AutoDownload { get; set; }

    public bool AutoDeleteEnabled { get; set; }

    public int AutoDeleteAfterDays { get; set; }

    public int EpisodeCount { get; set; }

    /// <summary>Episodes whose audio was ever saved to a device (<c>LastPlayedAtUtc</c> set), stored now or not.</summary>
    public int SavedEpisodeCount { get; set; }

    public int StoredAudioCount { get; set; }

    public long StoredAudioBytes { get; set; }
}

/// <summary>The part of a feed only a <c>news-feeds.update</c> holder sees.</summary>
public sealed record NewsFeedManagement
{
    public string Url { get; set; } = string.Empty;

    public NewsFeedErrorKind LastErrorKind { get; set; }

    public int? LastHttpStatus { get; set; }

    public DateTime? LastErrorAtUtc { get; set; }

    /// <summary>Server-composed, naming a host only.</summary>
    public string? LastErrorMessage { get; set; }

    public int ConsecutiveFailures { get; set; }

    public DateTime? QuarantinedAtUtc { get; set; }

    public bool RedirectedFromHttps { get; set; }

    /// <summary>A same-host permanent redirect target the user may accept with a <c>PUT</c>.</summary>
    public string? SuggestedUrl { get; set; }

    public int ArticlesToday { get; set; }

    public long ImageBytesToday { get; set; }
}

/// <summary>One entry of <c>GET /api/news-feeds/catalogue</c>: every feed not pending deletion, with no URL.</summary>
public sealed record NewsFeedCatalogueEntry
{
    public Guid Id { get; set; }

    public string DisplayTitle { get; set; } = string.Empty;

    public NewsFeedHealth Health { get; set; }

    public NewsFeedQuotaReason? QuotaReason { get; set; }

    public string? Language { get; set; }

    public int UnreadCount { get; set; }

    /// <summary>The same maintained counter <see cref="ExistingNewsFeed.ArticleCount"/> exposes.</summary>
    public int ArticleCount { get; set; }

    /// <summary>Read articles the publisher changed after they were read: a subset of the read ones.</summary>
    public int UpdatedCount { get; set; }

    /// <summary><see cref="NewsFeedState.Active"/>, <see cref="NewsFeedState.Paused"/> or <see cref="NewsFeedState.Archived"/>.</summary>
    public NewsFeedState State { get; set; }

    /// <summary>What the feed carries (issue #556 §5.1 #4).</summary>
    public NewsFeedKind Kind { get; set; }
}

/// <summary><c>GET /api/news-feeds/unread-counts</c>. Archived feeds and feeds pending deletion are excluded.</summary>
public sealed record NewsUnreadCounts
{
    public int Total { get; set; }

    public List<NewsFeedUnreadCount> ByFeed { get; set; } = [];

    /// <summary>Counts by effective tag: the article's own and the ones it inherits from its feed.</summary>
    public List<NewsTagUnreadCount> ByTag { get; set; } = [];
}

public sealed record NewsFeedUnreadCount
{
    public Guid FeedId { get; set; }

    public int UnreadCount { get; set; }
}

public sealed record NewsTagUnreadCount
{
    public Guid TagId { get; set; }

    public int UnreadCount { get; set; }
}
