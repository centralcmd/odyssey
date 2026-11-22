using System.Text.Json.Serialization;

namespace Odyssey.Dtos.Internet;

/// <summary>The news feature's view of its storage volume (§5.5), from the object store's volume state.</summary>
[JsonConverter(typeof(TolerantEnumConverter<NewsStorageState>))]
public enum NewsStorageState : byte
{
    Ready = 0,
    Degraded = 1,
    Unconfigured = 2,
    Unknown = byte.MaxValue,
}

/// <summary>
/// <c>GET /api/news-status</c> (<c>file-storage.read</c>), which always answers <c>200</c>. The quota byte figures are
/// the resolved settings; the client never computes one from the settings page or a default.
/// </summary>
public sealed record NewsStatus
{
    public NewsStorageState Storage { get; set; }

    /// <summary>The volume <c>NewsArticles</c> is bound to, or <c>null</c> when unbound.</summary>
    public string? Volume { get; set; }

    public int ArticleCount { get; set; }

    public int FeedCount { get; set; }

    public int QuarantinedFeedCount { get; set; }

    public long ImageBytes { get; set; }

    public long ImageQuotaBytes { get; set; }

    public bool ImageQuotaReached { get; set; }

    public long TextAndSourceBytes { get; set; }

    public long TextAndSourceQuotaBytes { get; set; }

    public bool TextAndSourceQuotaReached { get; set; }

    public long VolumeFreeBytes { get; set; }

    public List<NewsFeedImageUsage> TopFeedsByImageBytes { get; set; } = [];

    // ── Podcasts (issue #556 §5.5 #12) ──────────────────────────────────────────────────────────────

    public long AudioBytes { get; set; }

    /// <summary>Audio released but still inside the deletion grace: it counts against the quota until the store deletes it.</summary>
    public long AudioPendingReleaseBytes { get; set; }

    /// <summary>The resolved <c>PodcastAudioStoreMaxGiB</c> in bytes.</summary>
    public long AudioQuotaBytes { get; set; }

    public bool AudioQuotaReached { get; set; }

    /// <summary>Podcast artwork, counted inside the image quota.</summary>
    public long ArtworkBytes { get; set; }

    public List<NewsFeedAudioUsage> TopFeedsByAudioBytes { get; set; } = [];
}

/// <summary>One feed of <see cref="NewsStatus.TopFeedsByAudioBytes"/>.</summary>
public sealed record NewsFeedAudioUsage
{
    public Guid FeedId { get; set; }

    public string DisplayTitle { get; set; } = string.Empty;

    public string? Language { get; set; }

    public long AudioBytes { get; set; }
}

/// <summary>One feed of <see cref="NewsStatus.TopFeedsByImageBytes"/>.</summary>
public sealed record NewsFeedImageUsage
{
    public Guid FeedId { get; set; }

    /// <summary>The feed's display title; every <c>file-storage.read</c> holder also holds <c>news.read</c>.</summary>
    public string DisplayTitle { get; set; } = string.Empty;

    /// <summary>The feed's language, so the client can set <c>lang</c> on the title.</summary>
    public string? Language { get; set; }

    public long ImageBytes { get; set; }
}
