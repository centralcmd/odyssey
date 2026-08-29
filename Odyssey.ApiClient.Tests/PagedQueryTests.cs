using Odyssey.Dtos;
using Xunit;

namespace Odyssey.ApiClient.Tests;

/// <summary>
/// Unit coverage for the list-query builder (issue #277 follow-up): the
/// <see cref="PagedQuery.Window"/> offset/limit arithmetic and filter escaping.
/// </summary>
/// <remarks>
/// Split from the client's PaginationTests when <see cref="PagedQuery"/> moved into this library —
/// the <c>OdsPagerMath</c> / <c>OdsPageSizes</c> half of that file is UI and stayed behind in
/// <c>Odyssey.Client.Tests</c>.
/// </remarks>
public class PagedQueryTests
{
    [Fact]
    public void Build_without_Window_requests_the_full_window()
    {
        // Reference-data / "load all" callers omit Window() and must still get the whole set.
        Assert.Equal($"api/accounts?offset=0&limit={ListDefaults.MaxLimit}", PagedQuery.For("api/accounts").Build());
    }

    [Theory]
    [InlineData(1, 25, 0, 25)]
    [InlineData(2, 25, 25, 25)]
    [InlineData(3, 25, 50, 25)]
    [InlineData(2, 100, 100, 100)]
    [InlineData(0, 25, 0, 25)]   // page < 1 clamps offset to 0
    [InlineData(-5, 25, 0, 25)]
    public void Window_computes_offset_and_limit(int page, int pageSize, int expectedOffset, int expectedLimit) =>
        Assert.Equal($"api/x?offset={expectedOffset}&limit={expectedLimit}",
            PagedQuery.For("api/x").Window(page, pageSize).Build());

    [Fact]
    public void Window_with_SizeAll_requests_the_full_window()
    {
        Assert.Equal($"api/x?offset=0&limit={ListDefaults.MaxLimit}",
            PagedQuery.For("api/x").Window(3, PagedQuery.SizeAll).Build());
    }

    [Fact]
    public void Window_composes_with_escaped_filters()
    {
        var url = PagedQuery.For("api/transactions")
            .Window(2, 50)
            .Add("search", "rent & utilities")
            .Build();

        Assert.Equal("api/transactions?offset=50&limit=50&search=rent%20%26%20utilities", url);
    }

    [Theory]
    [InlineData("title", true, "sortBy=title&sortDir=asc")]
    [InlineData("title", false, "sortBy=title&sortDir=desc")]
    public void Sort_appends_key_and_direction(string key, bool ascending, string expected) =>
        Assert.Contains(expected, PagedQuery.For("api/x").Sort(key, ascending).Build());

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Sort_with_a_blank_key_is_a_no_op(string? key) =>
        Assert.DoesNotContain("sortBy", PagedQuery.For("api/x").Sort(key, true).Build());
}
