namespace Odyssey.Client.Components;

// ─────────────────────────────────────────────────────────────────────────────
//  Pagination — the page-size presets every list surface offers and the pure
//  arithmetic behind OdsPager / OdsPageSizeSelect / OdsInlinePager (Odyssey
//  Design System · components/Pager + PageSizeSelect). No rendering here, which
//  is what makes it unit-testable (see Odyssey.Client.Tests/PaginationTests).
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Rows-per-page presets for <c>OdsPager</c> / <c>OdsPageSizeSelect</c> (Odyssey Design System ·
/// components/Pager + PageSizeSelect). A page size is an <see cref="int"/>; the sentinel
/// <see cref="All"/> (−1) means "every matching row" — the pager reports one page and the server
/// query requests the full window. Formatting renders <see cref="All"/> as "All", numbers as
/// thousands-grouped.
/// </summary>
public static class OdsPageSizes
{
    /// <summary>Sentinel page size meaning "all matching rows" (renders "All").</summary>
    public const int All = Odyssey.ApiClient.PagedQuery.SizeAll;

    /// <summary>The default flat-table presets: 25 · 100 · 1000 · All.</summary>
    public static readonly IReadOnlyList<int> Default = [25, 100, 1000, All];

    /// <summary>The card-list batch presets: 25 · 50 · 100 (no "All" — the list windows on scroll).</summary>
    public static readonly IReadOnlyList<int> Batch = [25, 50, 100];

    /// <summary>
    /// The page size to start from when restoring persisted page state: the stored value, or the
    /// first preset when nothing has been stored yet. Persisted UI state defaults <c>int</c> fields
    /// to <c>0</c>, which is not a legal page size, so every list page needs this fallback — it was
    /// previously written out as <c>state.PageSize == 0 ? 25 : state.PageSize</c> on thirteen pages,
    /// each repeating the literal 25 that the presets already define.
    /// </summary>
    /// <param name="persisted">The size read back from page state; <c>0</c> when never stored.</param>
    /// <param name="presets">The page's presets — pass <see cref="Batch"/> for a card list.</param>
    public static int Restore(int persisted, IReadOnlyList<int>? presets = null) =>
        persisted == 0 ? (presets ?? Default)[0] : persisted;

    /// <summary>Render a page size for display ("All" for the sentinel, thousands-grouped otherwise).</summary>
    public static string Format(int size) => size == All ? "All" : size.ToString("N0");
}

/// <summary>
/// Pure pagination arithmetic shared by <c>OdsPager</c> and the pages' live-region announcements
/// (so the "Showing X–Y of N" summary and the announced range never drift). <paramref name="page"/>
/// is 1-based; <see cref="OdsPageSizes.All"/> collapses to a single page spanning the whole result
/// set. Extracted from the component so it is unit-testable without a bUnit harness.
/// </summary>
public static class OdsPagerMath
{
    /// <summary>Number of pages for the result set (0 when empty, 1 for the "All" size).</summary>
    public static int TotalPages(int totalCount, int pageSize) =>
        totalCount <= 0 ? 0
        : pageSize == OdsPageSizes.All ? 1
        : (int)Math.Ceiling((double)totalCount / pageSize);

    /// <summary>1-based index of the first row shown on <paramref name="page"/> (0 when empty).</summary>
    public static int FirstShown(int page, int pageSize, int totalCount) =>
        totalCount <= 0 ? 0
        : pageSize == OdsPageSizes.All ? 1
        : (page - 1) * pageSize + 1;

    /// <summary>1-based index of the last row shown on <paramref name="page"/> (0 when empty).</summary>
    public static int LastShown(int page, int pageSize, int totalCount) =>
        totalCount <= 0 ? 0
        : pageSize == OdsPageSizes.All ? totalCount
        : Math.Min(page * pageSize, totalCount);

    /// <summary><c>true</c> when <paramref name="page"/> is the first page (or the set is empty).</summary>
    public static bool AtFirst(int page, int totalCount) => page <= 1 || totalCount <= 0;

    /// <summary><c>true</c> when <paramref name="page"/> is the last page (or the set is empty).</summary>
    public static bool AtLast(int page, int pageSize, int totalCount) =>
        totalCount <= 0 || page >= TotalPages(totalCount, pageSize);

    /// <summary>The canonical "Showing X–Y of N" summary (or "0 results" when empty).</summary>
    public static string Summary(int page, int pageSize, int totalCount) =>
        totalCount <= 0
            ? "0 results"
            : $"Showing {FirstShown(page, pageSize, totalCount):N0}–{LastShown(page, pageSize, totalCount):N0} of {totalCount:N0}";
}
