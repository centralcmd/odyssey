using System.Net;
using Odyssey.ApiClient.Resources;
using Odyssey.Dtos.Finance;
using Xunit;

namespace Odyssey.ApiClient.Tests;

public class CurrenciesApiClientTests
{
    private sealed class RecordingHandler : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        public HttpResponseMessage Response { get; set; } =
            new(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"items":[],"offset":0,"limit":50,"totalCount":0}""",
                                            System.Text.Encoding.UTF8, "application/json"),
            };

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastRequest = request;
            return Task.FromResult(Response);
        }
    }

    private static (T Client, RecordingHandler Handler) Create<T>(Func<IOdysseyApi, T> factory)
    {
        var handler = new RecordingHandler();
        var api = new OdysseyApi(new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") });
        return (factory(api), handler);
    }

    // ── Currencies ────────────────────────────────────────────────────────────

    [Fact]
    public async Task ListAllAsync_requests_the_full_window()
    {
        var (client, handler) = Create(api => new CurrenciesApiClient(api));

        await client.ListAllAsync();

        Assert.Contains($"offset=0&limit={PagedQuery.LimitAll}", handler.LastRequest!.RequestUri!.Query);
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("Active,Archived", false)]
    [InlineData("Archived", true)]
    public async Task ListAsync_filters_status_only_when_one_is_selected(string selected, bool expectFilter)
    {
        var (client, handler) = Create(api => new CurrenciesApiClient(api));
        var status = selected.Split(',', StringSplitOptions.RemoveEmptyEntries);

        await client.ListAsync(1, 25, status: status);

        Assert.Equal(expectFilter, handler.LastRequest!.RequestUri!.Query.Contains("status="));
    }

    // Currencies are keyed by ISO code, not a surrogate id — the single-row routes must carry the code.
    [Fact]
    public async Task DeleteAsync_targets_the_currency_code_route()
    {
        var (client, handler) = Create(api => new CurrenciesApiClient(api));
        handler.Response = new HttpResponseMessage(HttpStatusCode.NoContent);

        await client.DeleteAsync("NOK");

        Assert.Equal("/api/currencies/NOK", handler.LastRequest!.RequestUri!.AbsolutePath);
        Assert.Equal(HttpMethod.Delete, handler.LastRequest.Method);
    }

    // ── Exchange rates ────────────────────────────────────────────────────────

    [Fact]
    public async Task Rates_ListAsync_composes_target_and_status_filters()
    {
        var (client, handler) = Create(api => new ExchangeRatesApiClient(api));

        await client.ListAsync(2, 50, search: "nok", toCurrencies: ["USD", "EUR"], status: ["Current"],
                               sortBy: "asOf", sortDir: "desc");

        var query = handler.LastRequest!.RequestUri!.Query;
        Assert.Contains("offset=50&limit=50", query);
        Assert.Contains("search=nok", query);
        Assert.Contains("toCurrencies=USD", query);
        Assert.Contains("toCurrencies=EUR", query);
        Assert.Contains("status=Current", query);
        Assert.Contains("sortBy=asOf", query);
    }

    // Issue #349: status and source each repeat their key, the shape the server binds into an array.
    [Fact]
    public async Task Rates_ListAsync_repeats_the_status_and_source_keys_for_several_values()
    {
        var (client, handler) = Create(api => new ExchangeRatesApiClient(api));

        await client.ListAsync(1, 50, status: ["Current", "Upcoming"], source: ["Fetched", "Manual"]);

        var query = handler.LastRequest!.RequestUri!.Query;
        Assert.Contains("status=Current", query);
        Assert.Contains("status=Upcoming", query);
        Assert.Contains("source=Fetched", query);
        Assert.Contains("source=Manual", query);
    }

    [Fact]
    public async Task Rates_ListAsync_sends_no_status_or_source_when_none_is_chosen()
    {
        var (client, handler) = Create(api => new ExchangeRatesApiClient(api));

        await client.ListAsync(1, 50, status: null, source: []);

        var query = handler.LastRequest!.RequestUri!.Query;
        Assert.DoesNotContain("status=", query);
        Assert.DoesNotContain("source=", query);
    }

    [Fact]
    public async Task Rates_GetSummaryAsync_hits_the_summary_route()
    {
        var (client, handler) = Create(api => new ExchangeRatesApiClient(api));
        handler.Response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"totalCount":5,"byStatus":{"current":3,"historical":1,"upcoming":1},"bySource":{"manual":2,"fetched":3},"pairCount":3,"attribution":"Source: Norges Bank (NLOD 2.0)"}""",
                System.Text.Encoding.UTF8, "application/json"),
        };

        var summary = await client.GetSummaryAsync();

        Assert.Equal("/api/exchange-rates/summary", handler.LastRequest!.RequestUri!.AbsolutePath);
        Assert.Equal(HttpMethod.Get, handler.LastRequest.Method);
        Assert.Equal(1, summary.Value!.ByStatus.Upcoming);
        Assert.Equal(3, summary.Value.BySource.Fetched);
    }
}
