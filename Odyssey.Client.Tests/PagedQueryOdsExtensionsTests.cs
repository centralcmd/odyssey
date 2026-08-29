using Odyssey.ApiClient;
using Odyssey.Client.Components;
using Odyssey.Client.Services;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// Covers <see cref="PagedQueryOdsExtensions"/> — the one-line adapter from the design system's
/// <see cref="OdsTableSort"/> to the host-agnostic <see cref="PagedQuery"/> builder.
/// </summary>
/// <remarks>
/// It is small, but it is the only place the UI's sort direction becomes a wire value, and getting
/// the boolean the wrong way round reverses every server-sorted list on the site while still
/// producing a perfectly valid request. Nothing else in the build would notice.
/// </remarks>
public class PagedQueryOdsExtensionsTests
{
    [Fact]
    public void An_ascending_sort_sends_sortDir_asc()
    {
        var url = PagedQuery.For("api/accounts").Sort(new OdsTableSort("name", OdsSortDirection.Asc)).Build();

        Assert.Contains("sortBy=name", url);
        Assert.Contains("sortDir=asc", url);
    }

    [Fact]
    public void A_descending_sort_sends_sortDir_desc()
    {
        var url = PagedQuery.For("api/accounts").Sort(new OdsTableSort("balance", OdsSortDirection.Desc)).Build();

        Assert.Contains("sortBy=balance", url);
        Assert.Contains("sortDir=desc", url);
    }

    /// <summary>A page with no resolved sort must send no sort at all, so the server applies its own
    /// default ordering rather than receiving a blank key.</summary>
    [Fact]
    public void A_null_sort_appends_nothing()
    {
        var url = PagedQuery.For("api/accounts").Sort((OdsTableSort?)null).Build();

        Assert.DoesNotContain("sortBy", url);
        Assert.DoesNotContain("sortDir", url);
    }

    [Fact]
    public void The_adapter_composes_with_the_rest_of_the_builder()
    {
        var url = PagedQuery.For("api/accounts")
            .Window(page: 3, pageSize: 25)
            .Add("search", "oslo")
            .Sort(new OdsTableSort("name", OdsSortDirection.Asc))
            .Build();

        Assert.Equal("api/accounts?offset=50&limit=25&search=oslo&sortBy=name&sortDir=asc", url);
    }
}
