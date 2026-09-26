using System.Net;
using System.Text;
using Odyssey.ApiClient.Resources;
using Odyssey.Dtos.Finance;
using Xunit;

namespace Odyssey.ApiClient.Tests;

/// <summary>
/// The typed client's <c>/properties</c> summary call (issue #167): the route, the optional base
/// currency on the query string, and the envelope it reads back — the value tile included.
/// </summary>
public class PropertiesApiClientTests
{
    private sealed class RecordingHandler(string body) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastRequest = request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }

    private static (IPropertiesApiClient Client, RecordingHandler Handler) Create(string body)
    {
        var handler = new RecordingHandler(body);
        var api = new OdysseyApi(new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") });
        return (new PropertiesApiClient(api), handler);
    }

    private const string Summary = """
        {"totalProperties":3,"byType":[{"type":0,"count":2},{"type":1,"count":1}],
         "byStatus":{"owned":2,"disposed":0,"archived":1},
         "value":{"baseCurrency":"NOK","byCurrency":[{"currencyCode":"NOK","total":5140000,"count":2}],
                  "total":5140000,"unconvertedCurrencies":["USD"]}}
        """;

    [Fact]
    public async Task GetSummaryAsync_WithoutABase_CallsTheBareRoute()
    {
        var (client, handler) = Create(Summary);

        await client.GetSummaryAsync();

        Assert.Equal("/api/properties/summary", handler.LastRequest!.RequestUri!.AbsolutePath);
        Assert.Equal("", handler.LastRequest.RequestUri.Query);
    }

    [Fact]
    public async Task GetSummaryAsync_WithABase_PutsItOnTheQueryString_AndReadsTheValueTile()
    {
        var (client, handler) = Create(Summary);

        var result = await client.GetSummaryAsync("NOK");

        Assert.Equal("?baseCurrency=NOK", handler.LastRequest!.RequestUri!.Query);
        var summary = result.Value!;
        Assert.Equal(3, summary.TotalProperties);
        Assert.Equal(1, summary.ByStatus.Archived);
        Assert.Equal(PropertyType.Vehicle, summary.ByType[1].Type);
        Assert.Equal(5_140_000m, summary.Value!.Total);
        Assert.Equal(["USD"], summary.Value.UnconvertedCurrencies);
    }

    private sealed class NoContentHandler : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastRequest = request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
        }
    }

    private static readonly Guid Parent = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Child = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static readonly NewProperty Body = new()
    {
        Name = "Storgata 14", Description = "Home", Type = PropertyType.RealEstate, CurrencyCode = "NOK",
        RealEstateDetails = new RealEstateDetailsDto { Kind = RealEstateKind.House },
    };

    private static readonly NewPropertyEstimate EstimateBody = new()
    {
        Value = 100m, CurrencyCode = "NOK", EffectiveFrom = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
    };

    /// <summary>
    /// Every route the client builds, and the method it uses. The estimate and smart-tag operations are
    /// addressed through their property id, never by their own id alone — the parent scoping the server's
    /// IDOR guarantee rests on, which a refactor flattening one route would otherwise break silently.
    /// </summary>
    public static TheoryData<string, string, string> Routes() => new()
    {
        { "list",             "/api/properties",                                  "GET" },
        { "get",              $"/api/properties/{Parent}",                        "GET" },
        { "create",           "/api/properties",                                  "POST" },
        { "update",           $"/api/properties/{Parent}",                        "PUT" },
        { "delete",           $"/api/properties/{Parent}",                        "DELETE" },
        { "estimates list",   $"/api/properties/{Parent}/estimates",              "GET" },
        { "estimate current", $"/api/properties/{Parent}/estimates/current",      "GET" },
        { "estimate add",     $"/api/properties/{Parent}/estimates",              "POST" },
        { "estimate update",  $"/api/properties/{Parent}/estimates/{Child}",      "PUT" },
        { "estimate delete",  $"/api/properties/{Parent}/estimates/{Child}",      "DELETE" },
        { "smart tags list",  $"/api/properties/{Parent}/smart-tags",             "GET" },
        { "smart tag add",    $"/api/properties/{Parent}/smart-tags/{Child}",     "POST" },
        { "smart tag remove", $"/api/properties/{Parent}/smart-tags/{Child}",     "DELETE" },
    };

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task Every_operation_hits_its_route_and_method(string label, string expectedPath, string method)
    {
        var handler = new NoContentHandler();
        var client = new PropertiesApiClient(new OdysseyApi(new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") }));

        Task call = label switch
        {
            "list" => client.ListAsync(1, 25),
            "get" => client.GetAsync(Parent),
            "create" => client.CreateAsync(Body),
            "update" => client.UpdateAsync(Parent, Body),
            "delete" => client.DeleteAsync(Parent),
            "estimates list" => client.ListEstimatesAsync(Parent),
            "estimate current" => client.GetCurrentEstimateAsync(Parent),
            "estimate add" => client.AddEstimateAsync(Parent, EstimateBody),
            "estimate update" => client.UpdateEstimateAsync(Parent, Child, EstimateBody),
            "estimate delete" => client.DeleteEstimateAsync(Parent, Child),
            "smart tags list" => client.ListSmartTagsAsync(Parent),
            "smart tag add" => client.AddSmartTagAsync(Parent, Child),
            _ => client.RemoveSmartTagAsync(Parent, Child),
        };
        await call;

        Assert.Equal(expectedPath, handler.LastRequest!.RequestUri!.AbsolutePath);
        Assert.Equal(method, handler.LastRequest.Method.Method);
    }

    /// <summary>The list query carries every filter as the server binds it: arrays as repeated pairs.</summary>
    [Fact]
    public async Task ListAsync_puts_every_filter_on_the_query_string()
    {
        var handler = new NoContentHandler();
        var client = new PropertiesApiClient(new OdysseyApi(new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") }));

        await client.ListAsync(2, 25, "storgata", ["RealEstate", "Vehicle"], ["Owned"], "value", "desc");

        var query = handler.LastRequest!.RequestUri!.Query;
        foreach (var part in new[] { "search=storgata", "types=RealEstate", "types=Vehicle", "statuses=Owned", "sortBy=value", "sortDir=desc" })
            Assert.Contains(part, query, StringComparison.Ordinal);
    }

    /// <summary>Both smart-tag writes are identified entirely by the path; there is no body to forge.</summary>
    [Fact]
    public async Task Smart_tag_writes_send_no_body()
    {
        var handler = new NoContentHandler();
        var client = new PropertiesApiClient(new OdysseyApi(new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") }));

        await client.AddSmartTagAsync(Parent, Child);
        Assert.Null(handler.LastRequest!.Content);

        await client.RemoveSmartTagAsync(Parent, Child);
        Assert.Null(handler.LastRequest!.Content);
    }
}
