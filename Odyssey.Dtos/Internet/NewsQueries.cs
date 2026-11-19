using System.ComponentModel.DataAnnotations;

namespace Odyssey.Dtos.Internet;

// The two news list queries (issue #470 §5.1, §5.2). The page size is bounded by NewsLimits.MaxPageSize rather than
// the shared ListDefaults.MaxLimit; it is checked in Validate, the JobRunsQueryParams precedent, because hiding the base
// Limit with a second property of the same name would give model binding two members to bind. The tag list is not one of
// them: it takes the shared tag-list contract and its ListDefaults bounds unchanged.

/// <summary>News-tags list query: filter by archival status, as the journal and photo tag lists do.</summary>
public sealed class NewsTagsQueryParams : QueryParams<NewsTagSortBy>
{
    public ArchivalStatus? Status { get; set; }
}

/// <summary><c>GET /api/news-feeds</c>.</summary>
public sealed class NewsFeedsQueryParams : QueryParams<NewsFeedSortBy>, IValidatableObject
{
    public NewsFeedsQueryParams()
    {
        Limit = ListDefaults.DefaultLimit;
    }

    public Guid? TagId { get; set; }

    /// <summary>Absent means <see cref="NewsFeedState.Active"/>.</summary>
    public NewsFeedState? State { get; set; }

    /// <summary>Matches the DERIVED health (<see cref="NewsFeedHealthRules.Derive"/>), never the raw conditions.</summary>
    [EnumFilter(typeof(NewsFeedHealth), 8)]
    public NewsFeedHealth[]? Health { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Limit is < 1 or > NewsLimits.MaxPageSize)
        {
            yield return new ValidationResult($"The limit must be between 1 and {NewsLimits.MaxPageSize}.", [nameof(Limit)]);
        }

        if (State == NewsFeedState.Unknown)
        {
            yield return new ValidationResult("Unknown feed state.", [nameof(State)]);
        }
    }
}

/// <summary>
/// <c>GET /api/news-articles</c>. Within <see cref="FeedIds"/> and within <see cref="TagIds"/> the ids are OR; across
/// the two lists the filters are AND. A <see cref="Cursor"/> continues a keyset page and is bound to every filter here
/// except the page size; sending one with a non-zero <c>offset</c> is a <c>400</c>.
/// </summary>
public sealed class NewsArticlesQueryParams : QueryParams<NewsArticleSortBy>, IValidatableObject
{
    public NewsArticlesQueryParams()
    {
        Limit = ListDefaults.DefaultLimit;
    }

    public Guid[]? FeedIds { get; set; }

    public Guid[]? TagIds { get; set; }

    /// <summary>The older single read-state filter. Prefer <see cref="Statuses"/>; sending both is a <c>400</c>.</summary>
    public bool? IsRead { get; set; }

    /// <summary>Status members, OR'ed; absent or empty means every article.</summary>
    [EnumFilter(typeof(NewsArticleReadStatus), 3)]
    public NewsArticleReadStatus[]? Statuses { get; set; }

    public DateTimeOffset? From { get; set; }

    public DateTimeOffset? To { get; set; }

    public bool IncludeArchivedFeeds { get; set; }

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

        if (IsRead is not null && Statuses is { Length: > 0 })
        {
            yield return new ValidationResult("Send either isRead or statuses, not both.", [nameof(Statuses)]);
        }

        if (SortBy == NewsArticleSortBy.Relevance && string.IsNullOrWhiteSpace(Search))
        {
            yield return new ValidationResult("Relevance ordering needs a search.", [nameof(SortBy)]);
        }

        foreach (var result in NewsFilterIds.Validate(FeedIds, nameof(FeedIds)).Concat(NewsFilterIds.Validate(TagIds, nameof(TagIds))))
        {
            yield return result;
        }
    }
}
