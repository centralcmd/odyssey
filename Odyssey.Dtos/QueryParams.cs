using System.ComponentModel.DataAnnotations;

namespace Odyssey.Dtos;

/// <summary>
/// The list-query parameters common to every server-side list endpoint (issue #277): free-text search,
/// the sort key + direction, and the offset/limit window. Bound from the query string via
/// <c>[FromQuery]</c>.
/// </summary>
/// <typeparam name="TSortBy">
/// The resource's sort-key enum, so <see cref="SortBy"/> is strongly typed per endpoint (an unbindable
/// key is rejected rather than silently coerced).
/// </typeparam>
/// <remarks>
/// This is the abstract base. Each list endpoint derives its own type that closes <typeparamref name="TSortBy"/>
/// over its sort-key enum and adds the resource-specific filters (types[], statuses[], date bounds, …), so a
/// single <c>[FromQuery]</c> parameter carries the whole query for that endpoint. Mutable <c>get; set;</c> for
/// query-string model binding; defaults apply when a key is absent.
/// <para>
/// Constraints follow the project DTO convention: <see cref="Search"/>/<see cref="Offset"/>/<see cref="Limit"/>
/// carry data-annotation attributes, so an out-of-range value is rejected with a <c>400</c> ProblemDetails by
/// <c>[ApiController]</c> model validation (as is an unbindable <see cref="SortBy"/>/<see cref="SortDir"/> or
/// enum/Guid filter). The <c>ListQuery</c> clamp helpers remain in the services as defense-in-depth for direct
/// (non-HTTP) callers. These are <c>sealed class</c> rather than <c>record</c> only because they are
/// query-string binding models.
/// </para>
/// </remarks>
public abstract class QueryParams<TSortBy>
    where TSortBy : struct, Enum
{
    /// <summary>Case-insensitive contains term over the resource's curated searchable fields.</summary>
    [StringLength(ListDefaults.MaxSearchLength)]
    public string? Search { get; set; }

    /// <summary>Sort key; absent resolves to the resource default.</summary>
    public TSortBy? SortBy { get; set; }

    /// <summary>Sort direction; absent uses the field's natural default direction.</summary>
    public SortDirection? SortDir { get; set; }

    /// <summary>0-based row offset.</summary>
    [Range(0, int.MaxValue)]
    public int Offset { get; set; }

    /// <summary>Maximum rows to return; bounded by <see cref="ListDefaults.MaxLimit"/>.</summary>
    [Range(0, ListDefaults.MaxLimit)]
    public int Limit { get; set; } = ListDefaults.DefaultLimit;
}
