using System.Text.Json.Serialization;

namespace Odyssey.Dtos.Internet;

// The news reader's wire enums (issue #470 §4, §8.5). Each serialises by name through TolerantEnumConverter, so a
// client built before a member was added reads it as Unknown rather than failing. Stored ordinals are a persistence
// contract and are never renumbered.

/// <summary>
/// The syndication format the worker detected (§4.2). <see cref="Unknown"/> is ordinal <c>0</c>, the stored value of a
/// feed not yet fetched, and doubles as the tolerant converter's sentinel: both mean "the format is not known".
/// </summary>
[JsonConverter(typeof(TolerantEnumConverter<NewsFeedFormat>))]
public enum NewsFeedFormat : byte
{
    Unknown = 0,
    Rss = 1,
    Atom = 2,
    RssRdf = 3,
    JsonFeed = 4,
    Unsupported = 5,
}

/// <summary>Why the last fetch failed (§4.2). Reaching a daily cap is not an error; it is <c>QuotaReached</c> health.</summary>
[JsonConverter(typeof(TolerantEnumConverter<NewsFeedErrorKind>))]
public enum NewsFeedErrorKind : byte
{
    None = 0,
    Network = 1,
    Timeout = 2,
    HttpStatus = 3,
    TooLarge = 4,
    ParseError = 5,
    BlockedAddress = 6,
    TooManyRedirects = 7,
    Gone = 8,
    Tls = 9,
    Unsupported = 10,
    NotAFeed = 11,
    Quarantined = 12,
    Unknown = byte.MaxValue,
}

/// <summary>
/// A feed's one health value, derived at read time and never stored (§4.2). The ordinals are not a persistence
/// contract; <see cref="NewsFeedHealthRules.Derive"/> decides the value by precedence.
/// </summary>
[JsonConverter(typeof(TolerantEnumConverter<NewsFeedHealth>))]
public enum NewsFeedHealth : byte
{
    Pending = 0,
    Healthy = 1,
    Failing = 2,
    Gone = 3,
    Paused = 4,
    Archived = 5,
    Quarantined = 6,
    QuotaReached = 7,
    Unknown = byte.MaxValue,
}

/// <summary>
/// Why a feed reports <see cref="NewsFeedHealth.QuotaReached"/> (§4.2, §6.3). Its own enum: the image
/// <c>DeferredReason</c>'s ordinal 2 means something else.
/// </summary>
[JsonConverter(typeof(TolerantEnumConverter<NewsFeedQuotaReason>))]
public enum NewsFeedQuotaReason : byte
{
    DailyCap = 1,
    TextQuota = 2,
    Unknown = byte.MaxValue,
}

/// <summary>Why an article (or a revision) has no plain-text file (§7.10). Only the body sets it.</summary>
[JsonConverter(typeof(TolerantEnumConverter<NewsBodyOmittedReason>))]
public enum NewsBodyOmittedReason : byte
{
    TooLarge = 1,
    TooComplex = 2,
    Unknown = byte.MaxValue,
}

/// <summary>The feed list's <c>state</c> filter, and a catalogue entry's state (§5.1).</summary>
[JsonConverter(typeof(TolerantEnumConverter<NewsFeedState>))]
public enum NewsFeedState : byte
{
    Active = 0,
    Paused = 1,
    Archived = 2,
    All = 3,
    Unknown = byte.MaxValue,
}

/// <summary>Sortable keys for the feed list; every sort ends with the id as a tie-break.</summary>
public enum NewsFeedSortBy
{
    Title,
    UnreadCount,
    LastSuccessAt,
    CreatedAt,
}

/// <summary>Sort orders for the article list. <see cref="Relevance"/> is valid only with a search.</summary>
public enum NewsArticleSortBy
{
    Newest,
    Oldest,
    Relevance,
}
