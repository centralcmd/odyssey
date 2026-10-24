using System.Net;
using System.Text.Json;
using Odyssey.ApiClient.Resources;
using Odyssey.Dtos.Finance;
using Xunit;

namespace Odyssey.ApiClient.Tests;

/// <summary>
/// The exchange-rate sync typed client (issue #351 §5): each call reaches the route and verb #349 §5
/// defines, with the query the page builds — and a supported-only view sends <c>supported=true</c>,
/// which keeps a currency of unknown status listed while the provider's list is unreachable.
/// </summary>
public class ExchangeRateSyncApiClientTests
{
    private const string Base = "/api/system-settings/exchange-rate-sync";

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        public string? LastBody { get; private set; }

        public string ResponseJson { get; set; } = """{"items":[],"offset":0,"limit":25,"totalCount":0}""";

        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastRequest = request;
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return new HttpResponseMessage(Status)
            {
                Content = new StringContent(ResponseJson, System.Text.Encoding.UTF8, "application/json"),
            };
        }
    }

    private static (ExchangeRateSyncApiClient Client, RecordingHandler Handler) Create()
    {
        var handler = new RecordingHandler();
        var api = new OdysseyApi(new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") });
        return (new ExchangeRateSyncApiClient(api), handler);
    }

    [Fact]
    public async Task ListCurrencies_sends_the_window_search_filters_and_sort()
    {
        var (client, handler) = Create();

        await client.ListCurrenciesAsync(2, 25, search: "kr", enabled: null, supported: true, sortBy: "Name", sortDir: "desc");

        Assert.Equal($"{Base}/currencies", handler.LastRequest!.RequestUri!.AbsolutePath);
        var query = handler.LastRequest.RequestUri.Query;
        Assert.Contains("offset=25", query);
        Assert.Contains("limit=25", query);
        Assert.Contains("search=kr", query);
        Assert.Contains("supported=true", query);
        Assert.DoesNotContain("enabled=", query);
        Assert.Contains("sortBy=Name", query);
        Assert.Contains("sortDir=desc", query);
    }

    [Fact]
    public async Task UpdateCurrency_puts_the_update_to_the_escaped_code()
    {
        var (client, handler) = Create();
        handler.ResponseJson = """{"currencyCode":"USD","name":"US dollar","enabled":true}""";

        var result = await client.UpdateCurrencyAsync("USD",
            new ExchangeRateSyncCurrencyUpdate { Enabled = true, HistoryStartDate = new DateOnly(2020, 1, 1) });

        Assert.Equal(HttpMethod.Put, handler.LastRequest!.Method);
        Assert.Equal($"{Base}/currencies/USD", handler.LastRequest.RequestUri!.AbsolutePath);
        using var body = JsonDocument.Parse(handler.LastBody!);
        Assert.Equal("2020-01-01", body.RootElement.GetProperty("historyStartDate").GetString());
        Assert.True(result.Value!.Enabled);
    }

    [Fact]
    public async Task PurgeCurrency_deletes_the_currencys_fetched_rates()
    {
        var (client, handler) = Create();
        handler.ResponseJson = """{"rowsDeleted":157}""";

        var result = await client.PurgeCurrencyAsync("DKK");

        Assert.Equal(HttpMethod.Delete, handler.LastRequest!.Method);
        Assert.Equal($"{Base}/currencies/DKK/rates", handler.LastRequest.RequestUri!.AbsolutePath);
        Assert.Equal(157, result.Value!.RowsDeleted);
    }

    [Fact]
    public async Task Status_runs_and_run_now_reach_their_routes()
    {
        var (client, handler) = Create();

        handler.ResponseJson = """{"state":1,"enabledCount":4}""";
        var status = await client.GetStatusAsync();
        Assert.Equal($"{Base}/status", handler.LastRequest!.RequestUri!.AbsolutePath);
        Assert.Equal(ExchangeRateSyncState.Scheduled, status.Value!.State);

        handler.ResponseJson = """{"items":[],"offset":10,"limit":10,"totalCount":0}""";
        await client.ListRunsAsync(2, 10);
        Assert.Equal($"{Base}/runs", handler.LastRequest!.RequestUri!.AbsolutePath);
        Assert.Contains("offset=10", handler.LastRequest.RequestUri.Query);

        handler.Status = HttpStatusCode.Accepted;
        handler.ResponseJson = """{"runId":"7c9e6679-7425-40de-944b-e07fc1f90ae7"}""";
        var started = await client.RunNowAsync();
        Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
        Assert.Equal($"{Base}/runs", handler.LastRequest.RequestUri!.AbsolutePath);
        Assert.Equal(Guid.Parse("7c9e6679-7425-40de-944b-e07fc1f90ae7"), started.Value!.RunId);
    }
}
