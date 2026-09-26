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
}
