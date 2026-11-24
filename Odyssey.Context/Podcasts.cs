using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Odyssey.Dtos;
using Odyssey.Dtos.Internet;

namespace Odyssey.Context;

// Podcasts in the news reader (issue #556 §4). A podcast is a news feed of kind Podcast; an episode is a 1:1
// extension of its NewsArticle, so identity, revisions, show notes (the plain-text body), tags and the feed purge are
// the news reader's. Audio and artwork are files on two storage features, PodcastAudio and PodcastArtwork, whose one
// referencing column each is PodcastEpisodeAudio.StoredObjectId and PodcastArtworkFile.StoredObjectId. Like the
// news tables, nothing here carries a user id.

/// <summary>A podcast's channel-level details (§4.2), written by the worker on a podcast fetch.</summary>
public class PodcastFeedDetails
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.None)]
    public Guid NewsFeedId { get; set; }

    [StringLength(PodcastLimits.MaxAuthorLength)]
    public string? Author { get; set; }

    public Guid? ArtworkId { get; set; }

    public bool? Explicit { get; set; }

    public PodcastType? PodcastType { get; set; }

    /// <summary>
    /// Stamped by the first podcast fetch that completed as a full parse (§3.4 step 7). Until then — and after a type
    /// change newer than it — nothing is auto-downloaded, so subscribing never downloads a back catalogue.
    /// </summary>
    public DateTime? BaselineAtUtc { get; set; }

    /// <summary>The UTC day <see cref="AutoDownloadsToday"/> counts.</summary>
    public DateOnly? AutoDownloadDate { get; set; }

    public int AutoDownloadsToday { get; set; }

    /// <summary>Render incidents this feed's artwork caused; at ≥ 1 its remaining artwork is rejected (§3.4).</summary>
    public int ArtworkIncidentCount { get; set; }

    public DateTime? ArtworkIncidentAtUtc { get; set; }
}

/// <summary>
/// One episode (§4.3): the episode-only metadata of a <see cref="NewsArticle"/> on a podcast feed. The worker writes the
/// metadata columns under the feed lease; the API writes only <see cref="LastPlayedAtUtc"/>, <see cref="LastListenedAtUtc"/>
/// and <see cref="PlaybackPositionSeconds"/>, each by a conditional update, so the row carries no concurrency token.
/// </summary>
public class PodcastEpisode
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.None)]
    public Guid NewsArticleId { get; set; }

    public Guid NewsFeedId { get; set; }

    /// <summary>http(s), no user info. Never served and never logged: a private feed puts tokens here.</summary>
    [Required]
    [StringLength(NewsLimits.MaxUrlLength)]
    public string EnclosureUrl { get; set; } = string.Empty;

    [StringLength(PodcastLimits.MaxDeclaredTypeLength)]
    public string? EnclosureDeclaredType { get; set; }

    /// <summary>The feed's claim of the file size; never trusted for anything but display.</summary>
    public long? EnclosureDeclaredLength { get; set; }

    /// <summary>Plain text: <c>itunes:subtitle</c>, else <c>itunes:summary</c>.</summary>
    [StringLength(PodcastLimits.MaxDescriptionLength)]
    public string? Description { get; set; }

    public int? DurationSeconds { get; set; }

    public int? SeasonNumber { get; set; }

    public int? EpisodeNumber { get; set; }

    public PodcastEpisodeType EpisodeType { get; set; }

    public bool? Explicit { get; set; }

    public Guid? ArtworkId { get; set; }

    /// <summary>The Podcasting 2.0 chapter file. Never served.</summary>
    [StringLength(NewsLimits.MaxUrlLength)]
    public string? ChaptersUrl { get; set; }

    public byte[]? ChaptersUrlKeyHash { get; set; }

    public PodcastChaptersFetchState ChaptersFetchState { get; set; }

    public byte ChaptersAttempts { get; set; }

    public DateTime? ChaptersNextAttemptAtUtc { get; set; }

    public Guid? ChaptersLeaseOwner { get; set; }

    public DateTime? ChaptersLeaseExpiresAtUtc { get; set; }

    /// <summary>The change detector over the episode-only fields (§8.5); equal means no write.</summary>
    [Required]
    public byte[] EpisodeHash { get; set; } = [];

    /// <summary>Copied from the article at insert (the <see cref="NewsArticleTag.SortAtUtc"/> precedent).</summary>
    public DateTime SortAtUtc { get; set; }

    /// <summary>
    /// The household's shared resume position in whole seconds (issue #593); <c>null</c> when never started or finished.
    /// Written only by <c>PUT …/playback</c>.
    /// </summary>
    public int? PlaybackPositionSeconds { get; set; }

    /// <summary>v1: the last completed device download of the whole file by a <c>news.update</c> holder (§5.3 #8).</summary>
    public DateTime? LastPlayedAtUtc { get; set; }

    /// <summary>
    /// When an in-browser listen last reached the end (issue #593): a trusted-client signal, written only while the audio
    /// is <c>Stored</c>. Auto-delete counts from the later of this and <see cref="LastPlayedAtUtc"/> (<c>PodcastConsumption</c>).
    /// </summary>
    public DateTime? LastListenedAtUtc { get; set; }
}

/// <summary>Where the Podcasting 2.0 chapter file stands (§4.3).</summary>
public enum PodcastChaptersFetchState : byte
{
    None = 0,
    Pending = 1,
    Fetched = 2,
    Failed = 3,
}

/// <summary>
/// An episode's audio on the server (§4.4). The worker writes it with <see cref="AudioFence"/>; the API only through
/// conditional single-statement updates that rotate the fence, so a worker holding the old fence loses its next save.
/// </summary>
public class PodcastEpisodeAudio
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.None)]
    public Guid NewsArticleId { get; set; }

    /// <summary>Denormalised for the per-feed rules and counts; fixed at insert.</summary>
    public Guid NewsFeedId { get; set; }

    /// <summary>SHA-256 of the lower-case enclosure host, for the one-download-per-host rule.</summary>
    [Required]
    public byte[] EnclosureHostKeyHash { get; set; } = [];

    public PodcastAudioState State { get; set; }

    public PodcastAudioFailureKind? FailureKind { get; set; }

    public PodcastAudioDeferredReason? DeferredReason { get; set; }

    public PodcastAudioRequestedBy? RequestedBy { get; set; }

    public DateTime? RequestedAtUtc { get; set; }

    /// <summary>The last user request, cancel, clear or remove: the re-request cooldown runs from it (§5.3 #9).</summary>
    public DateTime? LastUserRequestAtUtc { get; set; }

    public byte Attempts { get; set; }

    public DateTime? NextAttemptAtUtc { get; set; }

    public Guid? LeaseOwner { get; set; }

    public DateTime? LeaseExpiresAtUtc { get; set; }

    /// <summary>The expected size of an in-flight download, also summed in <see cref="NewsStorageTotal.AudioReservedBytes"/>.</summary>
    public long ReservedBytes { get; set; }

    /// <summary>RESTRICT to <c>StoredObjects</c>; the <c>PodcastAudio</c> feature's reference. Set exactly when stored.</summary>
    public Guid? StoredObjectId { get; set; }

    [StringLength(PodcastLimits.MaxAudioContentTypeLength)]
    public string? ContentType { get; set; }

    public long? SizeBytes { get; set; }

    public byte[]? Sha256 { get; set; }

    public DateTime? StoredAtUtc { get; set; }

    [ConcurrencyCheck]
    public Guid AudioFence { get; set; }

    /// <summary>
    /// <c>NewsFeedId</c>'s text while <c>Downloading</c>, else <c>NULL</c>; generated, uniquely indexed (§3.6 step 2). A
    /// string, not a <see cref="Guid"/>: MariaDB refuses an indexed generated column over a <c>CHAR</c> column unless the
    /// expression strips its padding, so the column is <c>VARCHAR(36)</c> over <c>RTRIM(NewsFeedId)</c>.
    /// </summary>
    public string? FeedDownloadGuard { get; private set; }

    /// <summary><c>EnclosureHostKeyHash</c> while <c>Downloading</c>, else <c>NULL</c>; generated, uniquely indexed.</summary>
    public byte[]? HostDownloadGuard { get; private set; }
}

/// <summary>One chapter (§4.5). The highest-precedence source present is the one returned; the others are kept.</summary>
public class PodcastChapter
{
    [Key]
    public Guid PodcastChapterId { get; set; }

    public Guid NewsArticleId { get; set; }

    public PodcastChapterSource Source { get; set; }

    public int Position { get; set; }

    public long StartMs { get; set; }

    public long? EndMs { get; set; }

    [Required]
    [StringLength(PodcastLimits.MaxChapterTitleLength)]
    public string Title { get; set; } = string.Empty;

    /// <summary>http(s), no user info.</summary>
    [StringLength(NewsLimits.MaxUrlLength)]
    public string? Url { get; set; }
}

/// <summary>An artwork reference's state (§4.6): the news image states that apply.</summary>
public enum PodcastArtworkState : byte
{
    Pending = 0,
    Stored = 1,
    Failed = 2,
    Rejected = 3,
    Deferred = 4,
}

/// <summary>Why artwork was rejected (§4.6). Only <see cref="FeedIncident"/> is ever revived.</summary>
public enum PodcastArtworkRejectedReason : byte
{
    Invalid = 1,
    RenderIncident = 2,
    FeedIncident = 3,
}

/// <summary>One show or episode image of a feed (§4.6), de-duplicated per feed by its source URL.</summary>
public class PodcastArtwork
{
    [Key]
    public Guid PodcastArtworkId { get; set; }

    public Guid NewsFeedId { get; set; }

    /// <summary>Never served.</summary>
    [Required]
    [StringLength(NewsLimits.MaxUrlLength)]
    public string SourceUrl { get; set; } = string.Empty;

    [Required]
    public byte[] SourceUrlKeyHash { get; set; } = [];

    public PodcastArtworkState State { get; set; }

    /// <summary><c>1</c> StoreQuota while <see cref="State"/> is <c>Deferred</c>.</summary>
    public byte? DeferredReason { get; set; }

    public PodcastArtworkRejectedReason? RejectedReason { get; set; }

    public int? Width { get; set; }

    public int? Height { get; set; }

    public byte Attempts { get; set; }

    public DateTime? NextAttemptAtUtc { get; set; }

    public Guid? LeaseOwner { get; set; }

    public DateTime? LeaseExpiresAtUtc { get; set; }

    /// <summary>Written before the renderer runs and cleared with its result: an expired lease that still has it was a crash.</summary>
    public DateTime? RenderStartedAtUtc { get; set; }

    public DateTime CreatedAtUtc { get; set; }
}

/// <summary>An artwork file's rendition (§4.6).</summary>
public enum PodcastArtworkFileKind : byte
{
    Original = 1,
    Small = 2,
    Medium = 3,
}

/// <summary>One file of an artwork on the <c>PodcastArtwork</c> feature — the feature's one referencing table.</summary>
public class PodcastArtworkFile
{
    [Key]
    public Guid PodcastArtworkFileId { get; set; }

    public Guid PodcastArtworkId { get; set; }

    public PodcastArtworkFileKind Kind { get; set; }

    /// <summary>RESTRICT to <c>StoredObjects</c>; released through <c>StageRelease</c>.</summary>
    public Guid StoredObjectId { get; set; }

    [Required]
    [StringLength(NewsLimits.MaxContentTypeLength)]
    public string ContentType { get; set; } = string.Empty;

    public long SizeBytes { get; set; }

    [Required]
    public byte[] Sha256 { get; set; } = [];
}

/// <summary>What an artwork render incident was (§4.6).</summary>
public enum PodcastArtworkIncidentKind : byte
{
    Crash = 1,
    Hang = 2,
}

/// <summary>
/// One artwork render crash or hang (§3.4), counted by the artwork breaker alone. No artwork or feed id, so a delete
/// cannot reset the breaker; per-feed attribution lives on <see cref="PodcastFeedDetails.ArtworkIncidentCount"/>.
/// </summary>
public class PodcastArtworkIncident
{
    [Key]
    public Guid PodcastArtworkIncidentId { get; set; }

    public PodcastArtworkIncidentKind Kind { get; set; }

    public DateTime DetectedAtUtc { get; set; }
}
