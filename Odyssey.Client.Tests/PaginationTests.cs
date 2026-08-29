using Odyssey.Client.Components;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// Unit coverage for the pure pagination arithmetic behind the list pager UI (issue #277 follow-up):
/// the <see cref="OdsPageSizes"/> presets and the <see cref="OdsPagerMath"/> summary/bounds derivation
/// that <c>OdsPager</c> and the pages' live-region announcements share.
/// </summary>
/// <remarks>
/// The query-builder half of this file moved to <c>Odyssey.ApiClient.Tests.PagedQueryTests</c> along
/// with <c>PagedQuery</c> itself; what remains here is the UI arithmetic.
/// </remarks>
public class PaginationTests
{
    // ── OdsPageSizes.Format ───────────────────────────────────────────────────
    [Theory]
    [InlineData(OdsPageSizes.All, "All")]
    [InlineData(25, "25")]
    [InlineData(1000, "1,000")]
    [InlineData(0, "0")]
    public void Format_renders_size(int size, string expected) =>
        Assert.Equal(expected, OdsPageSizes.Format(size));

    // ── OdsPagerMath.TotalPages ───────────────────────────────────────────────
    [Theory]
    [InlineData(0, 25, 0)]     // empty → no pages
    [InlineData(25, 25, 1)]
    [InlineData(26, 25, 2)]
    [InlineData(2743, 25, 110)]
    [InlineData(100, OdsPageSizes.All, 1)]
    public void TotalPages_counts_pages(int total, int pageSize, int expected) =>
        Assert.Equal(expected, OdsPagerMath.TotalPages(total, pageSize));

    // ── OdsPagerMath.FirstShown / LastShown ───────────────────────────────────
    [Theory]
    [InlineData(1, 25, 100, 1, 25)]
    [InlineData(2, 25, 100, 26, 50)]
    [InlineData(4, 25, 100, 76, 100)]   // last page full
    [InlineData(1, 25, 10, 1, 10)]      // single short page
    [InlineData(3, 25, 55, 51, 55)]     // last page partial
    [InlineData(1, OdsPageSizes.All, 100, 1, 100)]
    [InlineData(1, 25, 0, 0, 0)]        // empty
    public void First_and_last_shown(int page, int pageSize, int total, int expectedFirst, int expectedLast)
    {
        Assert.Equal(expectedFirst, OdsPagerMath.FirstShown(page, pageSize, total));
        Assert.Equal(expectedLast, OdsPagerMath.LastShown(page, pageSize, total));
    }

    // ── OdsPagerMath bounds ───────────────────────────────────────────────────
    [Theory]
    [InlineData(1, 25, 100, true, false)]
    [InlineData(2, 25, 100, false, false)]
    [InlineData(4, 25, 100, false, true)]
    [InlineData(1, 25, 10, true, true)]      // single page: both bounds
    [InlineData(1, 25, 0, true, true)]       // empty: both bounds
    public void AtFirst_and_AtLast(int page, int pageSize, int total, bool atFirst, bool atLast)
    {
        Assert.Equal(atFirst, OdsPagerMath.AtFirst(page, total));
        Assert.Equal(atLast, OdsPagerMath.AtLast(page, pageSize, total));
    }

    // ── OdsPagerMath.Summary ──────────────────────────────────────────────────
    [Theory]
    [InlineData(1, 25, 0, "0 results")]
    [InlineData(1, 25, 25, "Showing 1–25 of 25")]
    [InlineData(2, 25, 2743, "Showing 26–50 of 2,743")]
    [InlineData(110, 25, 2743, "Showing 2,726–2,743 of 2,743")]
    [InlineData(1, OdsPageSizes.All, 2743, "Showing 1–2,743 of 2,743")]
    public void Summary_reads_the_range(int page, int pageSize, int total, string expected) =>
        Assert.Equal(expected, OdsPagerMath.Summary(page, pageSize, total));
}
