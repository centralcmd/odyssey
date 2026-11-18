using System.ComponentModel.DataAnnotations;

namespace Odyssey.Dtos.Internet;

// Article DTOs (issue #470 §5.2, §8.1). No response carries a raw source, an image source URL, a stored-object id or
// a path; every returned URL is http(s) without user info, or null.

/// <summary>
/// The fields an article list item and an article detail share. A base type by design, so the detail is the summary
/// plus its body without a nested object on the wire.
/// </summary>
public abstract record NewsArticleFields
{
    public Guid Id { get; set; }

    public Guid FeedId { get; set; }

    /// <summary>The feed's display title (§8.5).</summary>
    public string FeedTitle { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string? Author { get; set; }

    /// <summary>The first 300 characters of the plain text, cut at a word boundary.</summary>
    public string Excerpt { get; set; } = string.Empty;

    public string? Link { get; set; }

    public DateTime? PublishedAtUtc { get; set; }

    public DateTime SortAtUtc { get; set; }

    public DateTime FirstSeenAtUtc { get; set; }

    public DateTime? ContentChangedAtUtc { get; set; }

    public int RevisionCount { get; set; }

    public bool IsRead { get; set; }

    public DateTime? ReadAtUtc { get; set; }

    /// <summary>The article's own tags.</summary>
    public List<Guid> TagIds { get; set; } = [];

    /// <summary>The tags the article inherits from its feed, returned separately from <see cref="TagIds"/>.</summary>
    public List<Guid> FeedTagIds { get; set; } = [];

    /// <summary>The number of stored images.</summary>
    public int ImageCount { get; set; }

    public string? Language { get; set; }
}

/// <summary>One item of <see cref="NewsArticlePage"/>.</summary>
public sealed record NewsArticleSummary : NewsArticleFields;

/// <summary><c>GET /api/news-articles/{id}</c>: the summary plus the body read from the article's plain-text file.</summary>
public sealed record NewsArticleDetail : NewsArticleFields
{
    /// <summary>At most <see cref="NewsLimits.MaxBodyLength"/> characters; <c>null</c> when omitted or unavailable.</summary>
    public string? Body { get; set; }

    /// <summary>The <c>Stored</c> images only, in display order.</summary>
    public List<NewsArticleImageItem> Images { get; set; } = [];

    /// <summary>The store reports the plain-text file missing; an error has been logged.</summary>
    public bool BodyUnavailable { get; set; }

    /// <summary>Set when the body was refused before parsing (§7.10); <see cref="Body"/> is then <c>null</c>.</summary>
    public NewsBodyOmittedReason? BodyOmittedReason { get; set; }
}

/// <summary>A stored image of an article, served by <c>…/images/{imageId}/content</c>. Never its source URL.</summary>
public sealed record NewsArticleImageItem
{
    public Guid Id { get; set; }

    public int DisplayOrder { get; set; }

    public string? AltText { get; set; }

    public int? Width { get; set; }

    public int? Height { get; set; }
}

/// <summary>
/// <c>GET /api/news-articles</c>: its own shape, not a <see cref="PagedResult{T}"/>. <see cref="TotalCount"/> and
/// <see cref="TotalCountCapped"/> are set on the first page only.
/// </summary>
public sealed record NewsArticlePage
{
    public List<NewsArticleSummary> Items { get; set; } = [];

    /// <summary>Exact up to <see cref="NewsLimits.MaxCountedArticles"/>, then that number with <see cref="TotalCountCapped"/>.</summary>
    public int? TotalCount { get; set; }

    public bool? TotalCountCapped { get; set; }

    /// <summary>The opaque continuation, or <c>null</c> on the last page.</summary>
    public string? NextCursor { get; set; }

    /// <summary>The server clock at the first page; later pages exclude articles first seen after it.</summary>
    public DateTime AsOfUtc { get; set; }
}

/// <summary>A revision's metadata (<c>GET …/revisions</c>, newest first). Never a body, never a raw source.</summary>
public sealed record NewsArticleRevisionSummary
{
    /// <summary><c>1</c> is the original.</summary>
    public int RevisionNumber { get; set; }

    public string Title { get; set; } = string.Empty;

    public string? Author { get; set; }

    public string? Link { get; set; }

    public DateTime ValidFromUtc { get; set; }

    public DateTime ReplacedAtUtc { get; set; }

    public int BodyLength { get; set; }
}

/// <summary>One revision with its body (<c>GET …/revisions/{n}</c>), read from its revision text file.</summary>
public sealed record NewsArticleRevisionDetail
{
    public int RevisionNumber { get; set; }

    public string Title { get; set; } = string.Empty;

    public string? Author { get; set; }

    public string? Link { get; set; }

    public DateTime ValidFromUtc { get; set; }

    public DateTime ReplacedAtUtc { get; set; }

    public int BodyLength { get; set; }

    /// <summary><c>null</c> when <see cref="BodyOmittedReason"/> is set: such a revision has no text file.</summary>
    public string? Body { get; set; }

    public NewsBodyOmittedReason? BodyOmittedReason { get; set; }
}

/// <summary>The body of <c>PUT /api/news-articles/{id}/read-state</c>. Idempotent.</summary>
public sealed record NewsArticleReadState
{
    [Required]
    public bool? IsRead { get; set; }
}

/// <summary>The body of <c>POST /api/news-articles/read-state</c>: one read state for 1–500 articles.</summary>
public sealed record NewsArticlesReadStateBulk
{
    [Required]
    [MinLength(1)]
    [MaxLength(NewsLimits.MaxBulkReadStateIds)]
    public List<Guid>? ArticleIds { get; set; }

    [Required]
    public bool? IsRead { get; set; }
}

/// <summary>
/// The body of <c>POST /api/news-articles/mark-read</c>: the list's own filters plus the first page's echoed
/// <see cref="AsOfUtc"/>. The cursor is not involved. Unknown ids and a future <see cref="AsOfUtc"/> are the
/// service's <c>422</c>; the id lists are de-duplicated before their cap, as on the list.
/// </summary>
public sealed record MarkNewsRead : IValidatableObject
{
    public List<Guid>? FeedIds { get; set; }

    public List<Guid>? TagIds { get; set; }

    [StringLength(ListDefaults.MaxSearchLength)]
    public string? Search { get; set; }

    public DateTimeOffset? From { get; set; }

    public DateTimeOffset? To { get; set; }

    public bool IncludeArchivedFeeds { get; set; }

    [Required]
    public DateTimeOffset? AsOfUtc { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext) =>
        NewsFilterIds.Validate(FeedIds, nameof(FeedIds)).Concat(NewsFilterIds.Validate(TagIds, nameof(TagIds)));
}

/// <summary>What one <c>mark-read</c> request did; the client repeats the request while <see cref="HasMore"/> is true.</summary>
public sealed record MarkNewsReadResult
{
    public int MarkedCount { get; set; }

    public bool HasMore { get; set; }
}

/// <summary>The body of <c>PUT /api/news-articles/{id}/tags</c>: the article's own tags, replaced.</summary>
public sealed record NewsArticleTagsUpdate
{
    [Required]
    [MaxLength(NewsLimits.MaxTagIdsPerRequest)]
    public List<Guid>? TagIds { get; set; }
}

/// <summary>
/// The id-list rule shared by the article list and <c>mark-read</c> (§5.2): ids are de-duplicated first, and only then
/// is the <see cref="NewsLimits.MaxFilterIds"/> cap applied, so 51 copies of one id are one id while 51 distinct ids
/// are a <c>400</c>.
/// </summary>
public static class NewsFilterIds
{
    public static IEnumerable<ValidationResult> Validate(IEnumerable<Guid>? ids, string memberName)
    {
        if (ids is not null && ids.Distinct().Count() > NewsLimits.MaxFilterIds)
        {
            yield return new ValidationResult(
                $"At most {NewsLimits.MaxFilterIds} distinct ids may be given.", [memberName]);
        }
    }

    /// <summary>The ids in first-seen order, duplicates removed.</summary>
    public static IReadOnlyList<Guid> Normalize(IEnumerable<Guid>? ids) => ids is null ? [] : ids.Distinct().ToList();
}
