namespace Odyssey.Dtos;

/// <summary>
/// The compile-time bounds of the news reader (issue #470 §8.1). Every DTO annotation, every entity length and every
/// client-side check names one of these, so the server, the database and the WebAssembly client cannot drift apart.
/// Admin-editable caps are <c>SystemSettings</c> rows instead, and are never restated here.
/// </summary>
public static class NewsLimits
{
    /// <summary>A feed URL, a site URL, an article link and an image source URL.</summary>
    public const int MaxUrlLength = 2048;

    public const int MaxCustomTitleLength = 256;

    public const int MinFetchIntervalMinutes = 15;

    public const int MaxFetchIntervalMinutes = 1440;

    /// <summary>The largest <c>limit</c> any news list accepts.</summary>
    public const int MaxPageSize = 200;

    /// <summary>Revisions kept per article; the original is always among them.</summary>
    public const int MaxRevisions = 20;

    /// <summary>The news tag cap: <c>POST /api/news-tags</c> answers <c>422 news-tag-limit</c> beyond it.</summary>
    public const int MaxTags = 500;

    /// <summary>Tag ids in one feed or article tag write.</summary>
    public const int MaxTagIdsPerRequest = 32;

    /// <summary>Ids in one <c>feedIds</c> or <c>tagIds</c> list filter, counted after de-duplication.</summary>
    public const int MaxFilterIds = 50;

    public const int MaxTagNameLength = 64;

    public const int MaxTagDescriptionLength = 256;

    /// <summary>Characters in an article's plain-text file, the trailing <c>\n\n[…]</c> included.</summary>
    public const int MaxBodyLength = 262_144;

    /// <summary>The HTML nesting cap, shared by the pre-scan and the DOM walk (§7.10, decision B).</summary>
    public const int MaxHtmlDepth = 256;

    /// <summary>Article folders per bucket, the object store's per-folder limit (§3.2).</summary>
    public const int BucketCapacity = 10_000;

    public const int MaxIdentitiesPerArticle = 32;

    /// <summary>The shortest search term kept, matching <c>innodb_ft_min_token_size</c> (§8.4).</summary>
    public const int MinSearchTermLength = 3;

    /// <summary>News writes stop this far above the user-upload threshold (§6.3).</summary>
    public const long ReserveMarginBytes = 1L << 30;

    /// <summary>Ids in one bulk read-state write (<c>POST /api/news-articles/read-state</c>).</summary>
    public const int MaxBulkReadStateIds = 500;

    /// <summary>Articles one <c>mark-read</c> request marks; <c>hasMore</c> says whether to repeat it.</summary>
    public const int MaxMarkReadPerRequest = 5_000;

    /// <summary>The opaque list cursor's bound (§5.2).</summary>
    public const int MaxCursorLength = 512;

    /// <summary>The exact <c>totalCount</c> ceiling, and the end of <c>Relevance</c> paging.</summary>
    public const int MaxCountedArticles = 10_000;

    // ── Column lengths (§4) ───────────────────────────────────────────────────────────────────────

    public const int MaxFeedTitleLength = 256;

    public const int MaxFeedDescriptionLength = 1024;

    /// <summary>A BCP 47 tag's longest form.</summary>
    public const int MaxLanguageLength = 35;

    public const int MaxErrorMessageLength = 512;

    public const int MaxETagLength = 256;

    public const int MaxLastModifiedLength = 64;

    public const int MaxArticleTitleLength = 1024;

    public const int MaxAuthorLength = 256;

    /// <summary>The excerpt: the only body text in the database (§4.7).</summary>
    public const int MaxExcerptLength = 300;

    /// <summary>An identity key: a GUID of up to 2000 characters plus its prefix (§8.7, §8.8).</summary>
    public const int MaxIdentityKeyLength = 2100;

    public const int MaxImageAltTextLength = 512;

    public const int MaxContentTypeLength = 64;

    /// <summary>The largest image <c>Position</c>; a reference past it is skipped (§4.11).</summary>
    public const int MaxImagePosition = 999;

    /// <summary>The length of every SHA-256 key column (<c>BINARY(32)</c>).</summary>
    public const int HashLength = 32;
}
