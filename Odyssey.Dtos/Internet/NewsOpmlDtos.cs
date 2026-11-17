using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace Odyssey.Dtos.Internet;

// OPML import and export (issue #508 §5.2, §8.6, amendments v4–v7). The import body is a file, not a DTO, so it has
// no request type; the filtered export takes feed ids.

/// <summary>What became of one distinct feed of an imported OPML file (§8.4). Decided in document order.</summary>
[JsonConverter(typeof(TolerantEnumConverter<NewsOpmlImportOutcome>))]
public enum NewsOpmlImportOutcome : byte
{
    /// <summary>The client-side sentinel for a value this build does not know: render it as "Not imported".</summary>
    Unknown = 0,
    Created = 1,
    AlreadySubscribed = 2,
    PendingDeletion = 3,
    Invalid = 4,
    FeedLimit = 5,
}

/// <summary>Why a tag a created feed named was not linked (§5.2, §8.3.7).</summary>
[JsonConverter(typeof(TolerantEnumConverter<NewsOpmlTagSkipReason>))]
public enum NewsOpmlTagSkipReason : byte
{
    Unknown = 0,

    /// <summary>The tag does not exist and the caller lacks <c>news-tags.create</c>.</summary>
    NoPermission = 1,

    /// <summary>The tag does not exist and <see cref="NewsLimits.MaxTags"/> was reached.</summary>
    TagLimit = 2,

    /// <summary>The name is longer than <see cref="NewsLimits.MaxTagNameLength"/>; it is never truncated.</summary>
    NameTooLong = 3,

    /// <summary>The tag exists but is archived, and a new link to an archived tag is refused.</summary>
    Archived = 4,
}

/// <summary>
/// The report <c>POST /api/news-feeds/opml</c> returns, also when nothing was created. The five outcome counts are
/// exact and sum to <see cref="Feeds"/>; <see cref="Items"/> may be cut (<see cref="ItemsTruncated"/>).
/// </summary>
public sealed record NewsOpmlImportResult
{
    /// <summary>Distinct feeds after merging outlines that share a URL key.</summary>
    public int Feeds { get; set; }

    /// <summary>Outlines folded into an earlier outline of the same feed.</summary>
    public int MergedOutlines { get; set; }

    public int Created { get; set; }

    public int AlreadySubscribed { get; set; }

    public int PendingDeletion { get; set; }

    public int Invalid { get; set; }

    public int FeedLimit { get; set; }

    /// <summary>
    /// Tags this import created. A tag created for a feed that the in-transaction re-count then cut to
    /// <see cref="NewsOpmlImportOutcome.FeedLimit"/>, or whose feed save then failed, is still listed and stays,
    /// linked to no feed.
    /// </summary>
    public List<string> TagsCreated { get; set; } = [];

    /// <summary>Distinct by name, at most <see cref="NewsOpmlLimits.MaxSkippedTagRows"/>; <see cref="TagsSkippedTotal"/> is exact.</summary>
    public List<NewsOpmlSkippedTag> TagsSkipped { get; set; } = [];

    public int TagsSkippedTotal { get; set; }

    /// <summary>Every created feed, then up to <see cref="NewsOpmlLimits.MaxOtherReportRows"/> other rows, in document order.</summary>
    public List<NewsOpmlImportItem> Items { get; set; } = [];

    public bool ItemsTruncated { get; set; }
}

/// <summary>One distinct feed of the import report.</summary>
public sealed record NewsOpmlImportItem
{
    /// <summary>The feed's 1-based order among distinct feeds.</summary>
    public int Position { get; set; }

    /// <summary>The submitted address, trimmed and clamped to <see cref="NewsOpmlLimits.MaxReportUrlLength"/>.</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>The submitted title, if any, clamped to <see cref="NewsLimits.MaxCustomTitleLength"/>.</summary>
    public string? Title { get; set; }

    public NewsOpmlImportOutcome Outcome { get; set; }

    /// <summary>The new feed for <c>Created</c>, the existing one for <c>AlreadySubscribed</c>, otherwise <c>null</c>.</summary>
    public Guid? FeedId { get; set; }

    /// <summary>The tag names linked to the feed (created feeds only), at most <see cref="NewsLimits.MaxTagIdsPerRequest"/>.</summary>
    public List<string> Tags { get; set; } = [];

    /// <summary>Tag-name occurrences beyond the first 32 distinct names, across the feed's merged outlines.</summary>
    public int TagsDropped { get; set; }

    /// <summary>For <c>Invalid</c>: the URL rule's message, which never echoes the address.</summary>
    public string? Message { get; set; }
}

/// <summary>A tag name a created feed named that was not linked.</summary>
public sealed record NewsOpmlSkippedTag
{
    public string Name { get; set; } = string.Empty;

    public NewsOpmlTagSkipReason Reason { get; set; }
}

/// <summary>
/// The body of <c>POST /api/news-feeds/opml/export</c> (amendment v6): the feeds to export, by id. Duplicates are
/// ignored after validation, so <c>[MaxLength]</c> counts them; unknown ids and feeds pending deletion are skipped.
/// </summary>
public sealed record NewsOpmlExportRequest
{
    [Required]
    [MinLength(1)]
    [MaxLength(SystemSettingsBounds.NewsMaxFeedsMax)]
    public List<Guid>? FeedIds { get; set; }
}

/// <summary>The compile-time bounds of the OPML import report and reader (§5.2, §7.5, §7.7).</summary>
public static class NewsOpmlLimits
{
    /// <summary>Report rows other than <c>Created</c> ones.</summary>
    public const int MaxOtherReportRows = 1_000;

    public const int MaxSkippedTagRows = 100;

    public const int MaxReportUrlLength = 512;

    /// <summary><c>category</c> segments read per outline.</summary>
    public const int MaxCategorySegments = 64;

    /// <summary>The longest run without a <c>&lt;</c>, and the longest comment, CDATA section, PI or DOCTYPE.</summary>
    public const int MaxTokenLength = 65_536;

    /// <summary>The byte size of the filtered export's JSON body (amendment notes v4–v6 item 2).</summary>
    public const int MaxExportRequestBytes = 131_072;
}
