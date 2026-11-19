namespace Odyssey.Dtos.Internet;

/// <summary>
/// The <c>code</c> problem-details extension of every news error (issue #470 §9.1). Shared with the client, so a page
/// tells two same-status answers apart by one symbol rather than by a copied string.
/// </summary>
public static class NewsErrorCodes
{
    /// <summary><c>503</c> + <c>Retry-After: 30</c>: detail, revisions and image content only.</summary>
    public const string StorageUnavailable = "news-storage-unavailable";

    /// <summary><c>503</c> + <c>Retry-After: 2</c>: a <c>news-query</c> statement hit its time limit or a lock wait.</summary>
    public const string Busy = "news-busy";

    /// <summary><c>429</c> + <c>Retry-After</c>: the search limiter.</summary>
    public const string SearchLimited = "news-search-limited";

    /// <summary><c>409</c>, with <c>existingFeedId</c> when that feed is not pending deletion.</summary>
    public const string FeedDuplicate = "news-feed-duplicate";

    /// <summary><c>409</c>: the feed's <c>version</c> no longer matches.</summary>
    public const string FeedChanged = "news-feed-changed";

    public const string FeedLimit = "news-feed-limit";

    public const string FeedInactive = "news-feed-inactive";

    public const string FeedNotQuarantined = "news-feed-not-quarantined";

    /// <summary><c>422</c> keyed <c>url</c>.</summary>
    public const string FeedUrlRejected = "news-feed-url-rejected";

    /// <summary><c>422</c> keyed <c>tagIds</c>: an unknown tag, or an archived one newly added.</summary>
    public const string TagNotUsable = "news-tag-not-usable";

    public const string TagDuplicate = "news-tag-duplicate";

    /// <summary><c>409</c>, with <c>feedCount</c> and <c>articleCount</c>.</summary>
    public const string TagInUse = "news-tag-in-use";

    public const string TagLimit = "news-tag-limit";

    /// <summary><c>422</c> keyed <c>tagId</c>: an archived tag cannot be added to an article.</summary>
    public const string TagArchived = "news-tag-archived";

    /// <summary><c>422</c> keyed <c>feedIds</c>, <c>tagIds</c> or <c>asOfUtc</c> on <c>mark-read</c>.</summary>
    public const string MarkReadScopeInvalid = "news-mark-read-scope-invalid";

    /// <summary><c>422</c> keyed <c>feedIds</c> or <c>tagIds</c> on the article list: an unknown or pending-deletion id.</summary>
    public const string FilterUnknown = "news-filter-unknown";

    /// <summary><c>400</c> keyed <c>cursor</c>: tampered, foreign, bound to other filters, or no longer decryptable.</summary>
    public const string CursorInvalid = "news-cursor-invalid";

    /// <summary><c>400</c> keyed <c>search</c>: no term of at least <see cref="NewsLimits.MinSearchTermLength"/> letters.</summary>
    public const string SearchUnusable = "news-search-unusable";

    // ── OPML import (issue #508 §5.2, amendment v7). 413 and 429 come from middleware and carry no code. ──────────

    /// <summary><c>400</c>: no <c>file</c> part, or an empty one.</summary>
    public const string OpmlFileRequired = "news-opml-file-required";

    /// <summary><c>400</c>: the extension is not <c>.opml</c>/<c>.xml</c>, or the declared content type is not accepted.</summary>
    public const string OpmlWrongType = "news-opml-wrong-type";

    /// <summary><c>400</c>: not an OPML document — malformed, wrong root, no <c>body</c>, too deep, or a token over the cap.</summary>
    public const string OpmlUnreadable = "news-opml-unreadable";

    /// <summary><c>422</c> keyed <c>file</c>, with the <c>limit</c> extension: more distinct feeds than the entry cap.</summary>
    public const string OpmlTooManyEntries = "news-opml-too-many-entries";

    /// <summary><c>409</c>: a concurrent write took one of the import's URLs; no feed from it was committed.</summary>
    public const string OpmlConflict = "news-opml-conflict";
}
