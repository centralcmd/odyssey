using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Odyssey.Dtos;
using Odyssey.Dtos.Internet;

namespace Odyssey.Context;

// The news reader (issue #470 §4): the Internet module's first feature. The household shares one feed list, one
// read state and one tag set, so no table carries a user id; acting-user attribution goes to logs only. Each article's
// source, plain text and images are files on the NewsArticles storage feature, with NewsArticleFiles their one
// referencing table. Long natural keys are kept unique through a SHA-256 column (BINARY(32)), because an InnoDB key
// tops out at 768 utf8mb4 characters.

/// <summary>
/// A subscribed feed: the API-owned settings (§4.1). <see cref="SettingsVersion"/> is the API's concurrency token, and
/// the worker never writes this table — everything it writes is on the 1:1 <see cref="NewsFeedFetchState"/>, so the
/// two sides never check each other's token (§3.3).
/// </summary>
public class NewsFeed
{
    [Key]
    public Guid NewsFeedId { get; set; }

    /// <summary>The address as the user entered it.</summary>
    [Required]
    [StringLength(NewsLimits.MaxUrlLength)]
    public string Url { get; set; } = string.Empty;

    /// <summary>SHA-256 of the normalised <c>UrlKey</c> (§8.2): the duplicate check's unique key.</summary>
    [Required]
    public byte[] UrlKeyHash { get; set; } = [];

    [StringLength(NewsLimits.MaxCustomTitleLength)]
    public string? CustomTitle { get; set; }

    /// <summary>The feed's own interval, or <c>null</c> to follow the <c>NewsDefaultFetchIntervalMinutes</c> setting.</summary>
    [Range(NewsLimits.MinFetchIntervalMinutes, NewsLimits.MaxFetchIntervalMinutes)]
    public int? FetchIntervalMinutes { get; set; }

    public bool DownloadImages { get; set; }

    public bool IsActive { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public DateTime? ArchivedAtUtc { get; set; }

    /// <summary>Set by <c>DELETE</c>; the feed is then <c>404</c> everywhere and the worker's purge removes it.</summary>
    public DateTime? DeletionRequestedAtUtc { get; set; }

    /// <summary>Rotated by every API write to the feed's settings, tags, archive and delete; never by the worker.</summary>
    [ConcurrencyCheck]
    public Guid SettingsVersion { get; set; }

    /// <summary>What the feed carries (issue #556 §4.1); every feed predating podcasts is an article feed.</summary>
    public NewsFeedKind Kind { get; set; }

    /// <summary>When <see cref="Kind"/> last changed; a podcast baseline older than this no longer counts (§3.4).</summary>
    public DateTime? KindChangedAtUtc { get; set; }

    /// <summary>Queue newly published episodes for download (§3.4); podcast feeds only.</summary>
    public bool AutoDownload { get; set; }

    /// <summary>Release stored audio a set number of days after it was saved to a device (§3.7); podcast feeds only.</summary>
    public bool AutoDeleteEnabled { get; set; }

    /// <summary>Kept while the rule is off, so turning it back on restores the number.</summary>
    [Range(PodcastLimits.MinAutoDeleteDays, PodcastLimits.MaxAutoDeleteDays)]
    public int AutoDeleteAfterDays { get; set; } = PodcastLimits.DefaultAutoDeleteDays;

    /// <summary>When the rule was turned on or its days changed: the countdown never starts before it (§3.7).</summary>
    public DateTime? AutoDeleteChangedAtUtc { get; set; }
}

/// <summary>
/// The worker-owned fetch state of one feed (§4.2), inserted in the save that creates the feed. <see cref="FetchFence"/>
/// is rotated only by the worker's claim, renewals, batch saves and completion. The API writes the four
/// cross-boundary columns (<see cref="RefreshRequestedAtUtc"/>, <see cref="ValidatorsResetRequestedAtUtc"/>, the
/// quarantine pair and <see cref="SuggestedUrl"/>) only through conditional single-column updates that do not check
/// the fence.
/// </summary>
public class NewsFeedFetchState
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.None)]
    public Guid NewsFeedId { get; set; }

    [ConcurrencyCheck]
    public Guid FetchFence { get; set; }

    [StringLength(NewsLimits.MaxFeedTitleLength)]
    public string? Title { get; set; }

    [StringLength(NewsLimits.MaxUrlLength)]
    public string? SiteUrl { get; set; }

    [StringLength(NewsLimits.MaxFeedDescriptionLength)]
    public string? Description { get; set; }

    /// <summary>A validated BCP 47 tag, else <c>null</c>.</summary>
    [StringLength(NewsLimits.MaxLanguageLength)]
    public string? Language { get; set; }

    public NewsFeedFormat Format { get; set; }

    /// <summary>RSS <c>ttl</c>, or <c>sy:updatePeriod</c> ÷ <c>sy:updateFrequency</c>.</summary>
    public int? FeedTtlMinutes { get; set; }

    public DateTime NextFetchAtUtc { get; set; }

    public DateTime? RefreshRequestedAtUtc { get; set; }

    public DateTime? ValidatorsResetRequestedAtUtc { get; set; }

    /// <summary>A same-host permanent redirect target; only an API <c>PUT</c> applies it (§3.3).</summary>
    [StringLength(NewsLimits.MaxUrlLength)]
    public string? SuggestedUrl { get; set; }

    public Guid? LeaseOwner { get; set; }

    public DateTime? LeaseExpiresAtUtc { get; set; }

    public byte CrashSuspectCount { get; set; }

    public DateTime? QuarantinedAtUtc { get; set; }

    /// <summary>Set and cleared together with <see cref="QuotaReason"/> (<c>CK_NewsFeedFetchStates_QuotaReason</c>).</summary>
    public DateTime? QuotaReachedAtUtc { get; set; }

    public NewsFeedQuotaReason? QuotaReason { get; set; }

    public DateTime? LastFetchAttemptAtUtc { get; set; }

    public DateTime? LastSuccessAtUtc { get; set; }

    public DateTime? LastErrorAtUtc { get; set; }

    public int? LastHttpStatus { get; set; }

    public NewsFeedErrorKind LastErrorKind { get; set; }

    /// <summary>Server-composed, naming a host only.</summary>
    [StringLength(NewsLimits.MaxErrorMessageLength)]
    public string? LastErrorMessage { get; set; }

    public int ConsecutiveFailures { get; set; }

    public bool RedirectedFromHttps { get; set; }

    /// <summary>Printable ASCII only.</summary>
    [StringLength(NewsLimits.MaxETagLength)]
    public string? ETag { get; set; }

    /// <summary>Printable ASCII only.</summary>
    [StringLength(NewsLimits.MaxLastModifiedLength)]
    public string? LastModified { get; set; }

    public byte[]? LastBodySha256 { get; set; }

    public DateOnly? DailyQuotaDate { get; set; }

    public int ArticlesToday { get; set; }

    public long ImageBytesToday { get; set; }
}

/// <summary>
/// A feed's maintained counts (§4.3), inserted with zero counts in the save that creates the feed. Written only by
/// relative increments (<c>INewsCounterWriter</c>) and recounted hourly, so it carries no concurrency token.
/// </summary>
[Table("NewsFeedCounters")]
public class NewsFeedCounter
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.None)]
    public Guid NewsFeedId { get; set; }

    public int ArticleCount { get; set; }

    public int UnreadCount { get; set; }
}

/// <summary>
/// The single row of news storage totals (§4.4), seeded by the migration: without it every relative update would
/// affect no row and raise no error, so both quotas would read <c>0</c> and never trigger.
/// </summary>
[Table("NewsStorageTotals")]
public class NewsStorageTotal
{
    public const byte SingletonId = 1;

    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.None)]
    public byte Id { get; set; }

    public long ImageBytes { get; set; }

    public long TextAndSourceBytes { get; set; }

    /// <summary>Stored podcast audio (issue #556 §4.7).</summary>
    public long AudioBytes { get; set; }

    /// <summary>Released audio the store has not deleted yet (the deletion grace); counted toward the audio quota.</summary>
    public long AudioPendingReleaseBytes { get; set; }

    /// <summary>The expected size of every in-flight download, reserved under this row's lock (§3.6 step 4).</summary>
    public long AudioReservedBytes { get; set; }

    /// <summary>Stored podcast artwork and its renditions; counted toward the image quota with <see cref="ImageBytes"/>.</summary>
    public long ArtworkBytes { get; set; }
}

/// <summary>
/// A feed's bucket counter (§3.2, §4.5): the <c>PhotoOriginalBucket</c> pattern. The row is locked <c>FOR UPDATE</c>
/// in its own short transaction before an article's folder is first written; the row lock serialises the writers.
/// </summary>
public class NewsFeedBucket
{
    [Key]
    public Guid NewsFeedBucketId { get; set; }

    public Guid NewsFeedId { get; set; }

    public int CurrentBucket { get; set; }

    public int ArticlesInBucket { get; set; }

    public DateTime UpdatedAtUtc { get; set; }
}

/// <summary>
/// The per-host request spacing shared by every worker replica (§3.6). A request takes its host only when
/// <see cref="NextAllowedAtUtc"/> has passed, moving it forward in the same save; the column is the row's own
/// concurrency token.
/// </summary>
[Table("NewsHostThrottle")]
public class NewsHostThrottle
{
    /// <summary>SHA-256 of the lower-case host.</summary>
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.None)]
    public byte[] HostKeyHash { get; set; } = [];

    [ConcurrencyCheck]
    public DateTime NextAllowedAtUtc { get; set; }
}

/// <summary>
/// One article (§4.7). <see cref="NewsArticleId"/> is also its folder name, and <see cref="Bucket"/> is fixed at
/// insert so every later file lands in the same folder. <see cref="Excerpt"/> is the only body text in the database;
/// the body of record is the article's plain-text file.
/// </summary>
public class NewsArticle
{
    [Key]
    public Guid NewsArticleId { get; set; }

    public Guid NewsFeedId { get; set; }

    public int Bucket { get; set; }

    [Required]
    [StringLength(NewsLimits.MaxArticleTitleLength)]
    public string Title { get; set; } = string.Empty;

    [StringLength(NewsLimits.MaxAuthorLength)]
    public string? Author { get; set; }

    [Required]
    [StringLength(NewsLimits.MaxExcerptLength)]
    public string Excerpt { get; set; } = string.Empty;

    /// <summary><c>null</c> when the body was stored; when set, no plain-text file exists (§7.10).</summary>
    public NewsBodyOmittedReason? BodyOmittedReason { get; set; }

    /// <summary>http(s) only.</summary>
    [StringLength(NewsLimits.MaxUrlLength)]
    public string? Link { get; set; }

    public byte[]? LinkKeyHash { get; set; }

    /// <summary>The change detector over the full title, author, link and body (§8.8).</summary>
    [Required]
    public byte[] ContentHash { get; set; } = [];

    /// <summary>The title plus the first 4,096 body characters (§8.8).</summary>
    [Required]
    public byte[] IdentityHash { get; set; } = [];

    /// <summary>Characters in the plain-text file; <c>0</c> for an omitted body.</summary>
    public int BodyLength { get; set; }

    public DateTime? PublishedAtUtc { get; set; }

    public DateTime? FeedUpdatedAtUtc { get; set; }

    public DateTime FirstSeenAtUtc { get; set; }

    public DateTime LastSeenAtUtc { get; set; }

    /// <summary><c>min(coalesce(Published, FirstSeen), FirstSeen + 1 h)</c>, fixed at insert.</summary>
    public DateTime SortAtUtc { get; set; }

    public DateTime? ContentChangedAtUtc { get; set; }

    public int RevisionCount { get; set; }

    public bool IsRead { get; set; }

    public DateTime? ReadAtUtc { get; set; }

    /// <summary>An index aid only, eventually consistent with the feed's archive state — never an access check.</summary>
    public bool FeedVisible { get; set; }

    /// <summary>Copied from the feed.</summary>
    [StringLength(NewsLimits.MaxLanguageLength)]
    public string? Language { get; set; }

    /// <summary>
    /// A copy of the feed's <see cref="NewsFeed.Kind"/> (issue #556 §4.8): written at insert, rewritten by the purge loop
    /// after a type change. An index aid only, like <see cref="FeedVisible"/>; the join to the feed is the filter.
    /// </summary>
    public NewsFeedKind FeedKind { get; set; }
}

/// <summary>How an identity key was derived (§4.8, §8.8).</summary>
public enum NewsArticleIdentityKind : byte
{
    Guid = 1,
    Link = 2,
    ContentHash = 3,
}

/// <summary>
/// One identity key an article has had (§4.8): every key is kept and looked up by its hash. The only foreign key is
/// the composite <c>(NewsArticleId, NewsFeedId)</c> to the article, which also pins the identity to the article's feed.
/// </summary>
public class NewsArticleIdentity
{
    [Key]
    public Guid NewsArticleIdentityId { get; set; }

    public Guid NewsArticleId { get; set; }

    public Guid NewsFeedId { get; set; }

    public NewsArticleIdentityKind Kind { get; set; }

    /// <summary><c>g:</c>, <c>g#</c>, <c>l:</c> or <c>h:</c> plus the key.</summary>
    [Required]
    [StringLength(NewsLimits.MaxIdentityKeyLength)]
    public string IdentityKey { get; set; } = string.Empty;

    [Required]
    public byte[] IdentityKeyHash { get; set; } = [];

    public DateTime FirstSeenAtUtc { get; set; }

    public DateTime LastSeenAtUtc { get; set; }
}

/// <summary>
/// An earlier version of an article (§4.9). <c>1</c> is the original and is always kept. Its source and text are files:
/// the <see cref="NewsArticleFile"/> rows carrying this revision's id.
/// </summary>
public class NewsArticleRevision
{
    [Key]
    public Guid NewsArticleRevisionId { get; set; }

    public Guid NewsArticleId { get; set; }

    public int RevisionNumber { get; set; }

    [Required]
    [StringLength(NewsLimits.MaxArticleTitleLength)]
    public string Title { get; set; } = string.Empty;

    [StringLength(NewsLimits.MaxAuthorLength)]
    public string? Author { get; set; }

    [StringLength(NewsLimits.MaxUrlLength)]
    public string? Link { get; set; }

    [Required]
    public byte[] ContentHash { get; set; } = [];

    [Required]
    public byte[] IdentityHash { get; set; } = [];

    public int BodyLength { get; set; }

    /// <summary>When set, the revision has no text file; it keeps its source file.</summary>
    public NewsBodyOmittedReason? BodyOmittedReason { get; set; }

    public DateTime ValidFromUtc { get; set; }

    public DateTime ReplacedAtUtc { get; set; }
}

/// <summary>What a news file holds (§4.10).</summary>
public enum NewsArticleFileKind : byte
{
    Source = 1,
    PlainText = 2,
    Image = 3,
}

/// <summary>
/// One file of an article on the <c>NewsArticles</c> feature (§4.10) — the feature's one referencing table, with a
/// RESTRICT key to <c>StoredObjects</c>. A row with no revision id is the current version; a revision is recorded by
/// setting <see cref="NewsArticleRevisionId"/>, never by renaming the file.
/// </summary>
public class NewsArticleFile
{
    [Key]
    public Guid NewsArticleFileId { get; set; }

    public Guid NewsArticleId { get; set; }

    public Guid? NewsArticleRevisionId { get; set; }

    public NewsArticleFileKind Kind { get; set; }

    /// <summary>RESTRICT to <c>StoredObjects</c> (#346 consumer rule); released through <c>StageRelease</c>.</summary>
    public Guid StoredObjectId { get; set; }

    /// <summary>A bare type: <c>application/xml</c>, <c>application/json</c>, <c>text/plain</c> or a still image.</summary>
    [Required]
    [StringLength(NewsLimits.MaxContentTypeLength)]
    public string ContentType { get; set; } = string.Empty;

    public long SizeBytes { get; set; }

    [Required]
    public byte[] Sha256 { get; set; } = [];

    public DateTime CreatedAtUtc { get; set; }
}

/// <summary>Where an image reference came from, in the §6.2 order.</summary>
public enum NewsArticleImageOrigin : byte
{
    Enclosure = 1,
    MediaContent = 2,
    MediaThumbnail = 3,
    JsonImage = 4,
    JsonAttachment = 5,
    InlineImg = 6,
}

/// <summary>An image reference's state (§4.11).</summary>
public enum NewsArticleImageState : byte
{
    Pending = 0,
    Stored = 1,
    Failed = 2,
    Rejected = 3,
    Skipped = 4,
    Superseded = 5,
    Deferred = 6,
}

/// <summary>Why an image is <see cref="NewsArticleImageState.Deferred"/> (§6.3). Not the feed's quota reason.</summary>
public enum NewsImageDeferredReason : byte
{
    DailyCap = 1,
    StoreQuota = 2,
}

/// <summary>
/// One image reference of an article and its download state (§4.11). There is deliberately no CHECK tying
/// <see cref="State"/> to <see cref="NewsArticleFileId"/>, which would conflict with the key's <c>SET NULL</c>; the
/// image service keeps them consistent.
/// </summary>
public class NewsArticleImage
{
    [Key]
    public Guid NewsArticleImageId { get; set; }

    public Guid NewsArticleId { get; set; }

    /// <summary>The stored file; <c>SET NULL</c> when the file row goes.</summary>
    public Guid? NewsArticleFileId { get; set; }

    /// <summary>Append-only and unique per article; a reference past <see cref="NewsLimits.MaxImagePosition"/> is skipped.</summary>
    public int Position { get; set; }

    public int DisplayOrder { get; set; }

    public NewsArticleImageOrigin Origin { get; set; }

    /// <summary>http(s) only, and never served.</summary>
    [Required]
    [StringLength(NewsLimits.MaxUrlLength)]
    public string SourceUrl { get; set; } = string.Empty;

    [StringLength(NewsLimits.MaxImageAltTextLength)]
    public string? AltText { get; set; }

    public int? Width { get; set; }

    public int? Height { get; set; }

    public NewsArticleImageState State { get; set; }

    public NewsImageDeferredReason? DeferredReason { get; set; }

    public byte Attempts { get; set; }

    public DateTime? NextAttemptAtUtc { get; set; }

    public Guid? LeaseOwner { get; set; }

    public DateTime? LeaseExpiresAtUtc { get; set; }

    /// <summary>Stamped by the maintenance probe (§3.4 step 9).</summary>
    public DateTime? VerifiedAtUtc { get; set; }

    /// <summary>Limits a repair to once per 24 h.</summary>
    public DateTime? LastRequeuedAtUtc { get; set; }
}

/// <summary>A news tag (§4.12): the shape and guards of <c>JournalTag</c>. Tags also do the job of favourites.</summary>
public class NewsTag
{
    [Key]
    public Guid NewsTagId { get; set; }

    [Required]
    [StringLength(NewsLimits.MaxTagNameLength)]
    public string Name { get; set; } = string.Empty;

    /// <summary>The name's comparison key; the unique index that makes a duplicate name a <c>409</c>.</summary>
    [Required]
    [StringLength(NewsLimits.MaxTagNameLength)]
    public string NameKey { get; set; } = string.Empty;

    [StringLength(NewsLimits.MaxTagDescriptionLength)]
    public string? Description { get; set; }

    /// <summary>A <c>TagIcons</c> key, or <c>NULL</c> for the default; an unknown stored key is projected as <c>null</c>.</summary>
    [StringLength(TagIcons.MaxKeyLength)]
    public string? Icon { get; set; }

    /// <summary>An optional <c>#RRGGBB</c> stored upper-case; projected through <c>HexColor.Normalize</c>.</summary>
    [StringLength(HexColor.Length)]
    public string? Color { get; set; }

    public DateTime? ArchivedAtUtc { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public DateTime UpdatedAtUtc { get; set; }
}

/// <summary>A tag on a feed, inherited at query time by every article of the feed.</summary>
public class NewsFeedTag
{
    [Key]
    public Guid NewsFeedTagId { get; set; }

    public Guid NewsFeedId { get; set; }

    public Guid NewsTagId { get; set; }
}

/// <summary>
/// A tag on an article. <see cref="SortAtUtc"/> is a copy of the article's, taken when the link is inserted; the
/// article's never changes after ingest, so the copy needs no maintenance. It serves the own-tag list branches.
/// </summary>
public class NewsArticleTag
{
    [Key]
    public Guid NewsArticleTagId { get; set; }

    public Guid NewsArticleId { get; set; }

    public Guid NewsTagId { get; set; }

    public DateTime SortAtUtc { get; set; }
}

/// <summary>
/// A fetch lease that expired without completing (§3.4 step 7). There is deliberately no feed id, so deleting a feed
/// cannot reset the breaker. Pruned after 7 days.
/// </summary>
public class NewsFeedCrashEvent
{
    [Key]
    public Guid NewsFeedCrashEventId { get; set; }

    public Guid LeaseOwner { get; set; }

    public DateTime DetectedAtUtc { get; set; }

    /// <summary>The fetches the owner had in flight; a sole in-flight feed is the one the event is charged to.</summary>
    public int FeedsInFlight { get; set; }
}
