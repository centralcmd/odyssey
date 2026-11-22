using System.ComponentModel.DataAnnotations;

namespace Odyssey.Dtos.Internet;

/// <summary>
/// <c>GET /api/podcast-episodes</c> (issue #556 §5.2 #6). Feeds are OR'd and the audio states OR'd; the two lists and
/// <see cref="SavedToDevice"/> are AND'd. A <see cref="Cursor"/> continues a keyset page and is bound to every filter here
/// except the page size.
/// </summary>
public sealed class PodcastEpisodesQueryParams : QueryParams<PodcastEpisodeSortBy>, IValidatableObject
{
    public PodcastEpisodesQueryParams()
    {
        Limit = ListDefaults.DefaultLimit;
    }

    [MaxLength(NewsLimits.MaxFilterIds)]
    public Guid[]? FeedIds { get; set; }

    [EnumFilter(typeof(PodcastAudioState), 6)]
    public PodcastAudioState[]? AudioState { get; set; }

    public bool IncludeArchivedFeeds { get; set; }

    /// <summary>
    /// <c>true</c>: only episodes whose audio was ever saved to a device (<c>LastPlayedAtUtc</c> set); <c>false</c>: only
    /// those never saved; omitted: both. v1 has no player, so this is the episode list's only "played" signal (#559).
    /// </summary>
    public bool? SavedToDevice { get; set; }

    [StringLength(NewsLimits.MaxCursorLength)]
    public string? Cursor { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Limit is < 1 or > NewsLimits.MaxPageSize)
        {
            yield return new ValidationResult($"The limit must be between 1 and {NewsLimits.MaxPageSize}.", [nameof(Limit)]);
        }

        if (Cursor is not null && Offset != 0)
        {
            yield return new ValidationResult("Send either a cursor or an offset, not both.", [nameof(Cursor)]);
        }

        if (SortBy is { } sort && !Enum.IsDefined(sort))
        {
            yield return new ValidationResult("Unknown sort.", [nameof(SortBy)]);
        }
    }
}

/// <summary>An episode's audio as the API shows it (§5.2 #6): never a URL, a path or a stored-object id.</summary>
public sealed record PodcastEpisodeAudioInfo
{
    public PodcastAudioState State { get; set; }

    public PodcastAudioFailureKind? FailureKind { get; set; }

    public PodcastAudioDeferredReason? DeferredReason { get; set; }

    public long? SizeBytes { get; set; }

    [StringLength(PodcastLimits.MaxAudioContentTypeLength)]
    public string? ContentType { get; set; }

    public DateTime? StoredAtUtc { get; set; }

    public PodcastAudioRequestedBy? RequestedBy { get; set; }

    /// <summary>When the re-request cooldown ends, while it is running; computed server-side so no client restates it.</summary>
    public DateTime? RequestAvailableAtUtc { get; set; }
}

/// <summary>
/// One episode in a list (§5.2 #6). Not sealed: <see cref="PodcastEpisodeDetail"/> is the summary plus its body, the
/// one deliberate base type here.
/// </summary>
public record PodcastEpisodeSummary
{
    public Guid Id { get; set; }

    public Guid FeedId { get; set; }

    public string FeedTitle { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public DateTime? PublishedAtUtc { get; set; }

    public DateTime SortAtUtc { get; set; }

    public string Excerpt { get; set; } = string.Empty;

    public int? DurationSeconds { get; set; }

    public int? SeasonNumber { get; set; }

    public int? EpisodeNumber { get; set; }

    public PodcastEpisodeType EpisodeType { get; set; }

    public bool? Explicit { get; set; }

    /// <summary>The episode's artwork when stored, else the show's when stored, else <c>null</c>.</summary>
    public Guid? ArtworkId { get; set; }

    public bool HasChapters { get; set; }

    public long? EnclosureDeclaredLength { get; set; }

    public List<Guid> TagIds { get; set; } = [];

    public PodcastEpisodeAudioInfo Audio { get; set; } = new();

    public DateTime? LastPlayedAtUtc { get; set; }

    public int? PlaybackPositionSeconds { get; set; }

    /// <summary>When the auto-delete rule will release this audio, when it applies; computed server-side.</summary>
    public DateTime? AutoDeleteAtUtc { get; set; }
}

public sealed record PodcastEpisodeChapterItem
{
    public long StartMs { get; set; }

    public long? EndMs { get; set; }

    [StringLength(PodcastLimits.MaxChapterTitleLength)]
    public string Title { get; set; } = string.Empty;

    public string? Url { get; set; }
}

/// <summary><c>GET /api/podcast-episodes/{id}</c> (§5.2 #7): the summary plus the description, show notes and chapters.</summary>
public sealed record PodcastEpisodeDetail : PodcastEpisodeSummary
{
    [StringLength(PodcastLimits.MaxDescriptionLength)]
    public string? Description { get; set; }

    /// <summary>The article's plain-text body; <c>null</c> when omitted or unavailable.</summary>
    public string? ShowNotes { get; set; }

    public bool ShowNotesUnavailable { get; set; }

    public NewsBodyOmittedReason? ShowNotesOmittedReason { get; set; }

    public List<PodcastEpisodeChapterItem> Chapters { get; set; } = [];

    public PodcastChapterSource? ChaptersSource { get; set; }

    public List<NewsArticleImageItem> Images { get; set; } = [];
}

/// <summary>One page of episodes (the <see cref="NewsArticlePage"/> shape).</summary>
public sealed record PodcastEpisodePage
{
    public List<PodcastEpisodeSummary> Items { get; set; } = [];

    public int? TotalCount { get; set; }

    public bool? TotalCountCapped { get; set; }

    public string? NextCursor { get; set; }

    public DateTime AsOfUtc { get; set; }
}
