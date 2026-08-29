using Microsoft.EntityFrameworkCore;
using Odyssey.Dtos;

namespace Odyssey.Core.Pagination;

/// <summary>
/// Shared helpers for the server-side list-query contract (issue #277): offset/limit clamping,
/// <c>LIKE</c> metacharacter escaping, sort-direction resolution, and the <see cref="PagedResult{T}"/>
/// count + window materialisation.
/// </summary>
public static class ListQuery
{
    /// <summary>Clamp <c>limit</c> into <c>[0, MaxLimit]</c>; a missing value falls back to the default.</summary>
    public static int ClampLimit(int? limit) =>
        limit is null ? ListDefaults.DefaultLimit : Math.Clamp(limit.Value, 0, ListDefaults.MaxLimit);

    /// <summary>Trim, blank-ignore and length-cap a raw search term. Returns <c>null</c> when there is nothing to search.</summary>
    public static string? NormalizeSearch(string? search)
    {
        if (string.IsNullOrWhiteSpace(search))
        {
            return null;
        }

        var trimmed = search.Trim();
        return trimmed.Length > ListDefaults.MaxSearchLength ? trimmed[..ListDefaults.MaxSearchLength] : trimmed;
    }

    /// <summary>
    /// Escape <c>LIKE</c> metacharacters (<c>\ % _</c>) so a normalized term matches literally
    /// and cannot become an unbounded wildcard. Uses backslash — MariaDB's default LIKE escape
    /// character — so the resulting pattern needs no explicit <c>ESCAPE</c> clause.
    /// </summary>
    public static string EscapeLike(string term) => term
        .Replace("\\", "\\\\")
        .Replace("%", "\\%")
        .Replace("_", "\\_");

    /// <summary>Build a case-insensitive (per the MariaDB <c>utf8mb4_*_ci</c> collation) contains pattern for a normalized term.</summary>
    public static string ContainsPattern(string term) => $"%{EscapeLike(term)}%";

    /// <summary>Resolve <c>sortDir</c> to an ascending flag; absent falls back to the field's natural default.</summary>
    public static bool Ascending(SortDirection? sortDir, bool naturalDefaultAscending) => sortDir switch
    {
        SortDirection.Asc => true,
        SortDirection.Desc => false,
        _ => naturalDefaultAscending,
    };

    /// <summary>
    /// Clamp a requested window to safe bounds: <c>offset</c> to <c>&gt;= 0</c> and <c>limit</c> to
    /// <c>[0, MaxLimit]</c>. Shared by the hand-materialised paths (SQL-paged services + in-memory
    /// derived-status lists) so the clamp rules live in one place.
    /// </summary>
    public static (int Offset, int Limit) ResolveWindow(int offset, int limit) =>
        (Math.Max(0, offset), ClampLimit(limit));

    /// <summary>Materialise a <see cref="PagedResult{T}"/> from an already-ordered in-memory list (clamped window slice).</summary>
    public static PagedResult<T> ToPagedResult<T>(IReadOnlyList<T> ordered, int offset, int limit)
    {
        var (safeOffset, safeLimit) = ResolveWindow(offset, limit);
        return new PagedResult<T>
        {
            Items = ordered.Skip(safeOffset).Take(safeLimit).ToList(),
            Offset = safeOffset,
            Limit = safeLimit,
            TotalCount = ordered.Count,
        };
    }

    /// <summary>
    /// Count the (already scoped/filtered/sorted) query, then materialise the clamped
    /// <c>offset</c>/<c>limit</c> window and return a <see cref="PagedResult{T}"/> reporting that window.
    /// </summary>
    public static async Task<PagedResult<T>> ToPagedResultAsync<T>(
        this IQueryable<T> query, int offset, int limit, CancellationToken cancellationToken)
    {
        var totalCount = await query.CountAsync(cancellationToken);
        var (safeOffset, safeLimit) = ResolveWindow(offset, limit);

        var items = await query
            .Skip(safeOffset)
            .Take(safeLimit)
            .ToListAsync(cancellationToken);

        return new PagedResult<T>
        {
            Items = items,
            Offset = safeOffset,
            Limit = safeLimit,
            TotalCount = totalCount,
        };
    }

    /// <summary>
    /// Materialise a <see cref="PagedResult{T}"/> from a query whose window must be
    /// <b>projected/adapted after materialisation</b> (e.g. per-row DTO shaping the DB can't do).
    /// The count still runs in SQL over the full filtered query; only the window is mapped.
    /// </summary>
    public static async Task<PagedResult<TOut>> ToPagedResultAsync<TIn, TOut>(
        this IQueryable<TIn> query,
        int offset,
        int limit,
        Func<TIn, TOut> map,
        CancellationToken cancellationToken)
    {
        var totalCount = await query.CountAsync(cancellationToken);
        var (safeOffset, safeLimit) = ResolveWindow(offset, limit);

        var rows = await query
            .Skip(safeOffset)
            .Take(safeLimit)
            .ToListAsync(cancellationToken);

        return new PagedResult<TOut>
        {
            Items = rows.Select(map).ToList(),
            Offset = safeOffset,
            Limit = safeLimit,
            TotalCount = totalCount,
        };
    }
}
