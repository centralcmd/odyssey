using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Odyssey.ApiClient.Resources;
using Odyssey.Dtos;
using Odyssey.Dtos.Finance;
using Xunit;

namespace Odyssey.ApiClient.Tests;

/// <summary>
/// The three instrument typed clients (issue #452 §5.2): each call reaches the route, verb and query names the #446
/// controllers bind.
/// </summary>
public class InstrumentsApiClientTests
{
    private static readonly Guid Id = Guid.Parse("7c9e6679-7425-40de-944b-e07fc1f90ae7");

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

    private static (OdysseyApi Api, RecordingHandler Handler) Create()
    {
        var handler = new RecordingHandler();
        return (new OdysseyApi(new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") }), handler);
    }

    private static string Path(RecordingHandler handler) => handler.LastRequest!.RequestUri!.AbsolutePath;

    private static string Query(RecordingHandler handler) => Uri.UnescapeDataString(handler.LastRequest!.RequestUri!.Query);

    [Fact]
    public async Task Instruments_list_sends_the_window_filters_and_sort()
    {
        var (api, handler) = Create();
        var client = new InstrumentsApiClient(api);

        await client.ListAsync(3, 20, new InstrumentsFilter
        {
            Search = "eq nr",
            Types = [InstrumentType.Stock, InstrumentType.Fund],
            CurrencyCodes = ["NOK", "USD"],
            IncludeArchived = true,
            SortBy = InstrumentSortBy.LatestDate,
            Ascending = false,
        });

        Assert.Equal(HttpMethod.Get, handler.LastRequest!.Method);
        Assert.Equal("/api/instruments", Path(handler));
        var query = Query(handler);
        Assert.Contains("offset=40", query);
        Assert.Contains("limit=20", query);
        Assert.Contains("search=eq nr", query);
        Assert.Contains("types=Stock", query);
        Assert.Contains("types=Fund", query);
        Assert.Contains("currencyCodes=NOK", query);
        Assert.Contains("currencyCodes=USD", query);
        Assert.Contains("includeArchived=true", query);
        Assert.Contains("sortBy=LatestDate", query);
        Assert.Contains("sortDir=desc", query);
    }

    [Fact]
    public async Task Instruments_list_sends_the_archival_status_only_when_set()
    {
        var (api, handler) = Create();
        var client = new InstrumentsApiClient(api);

        await client.ListAsync(1, 20, new InstrumentsFilter { Status = ArchivalStatus.Archived });
        Assert.Contains("status=Archived", Query(handler));

        await client.ListAsync(1, 20, new InstrumentsFilter());
        Assert.DoesNotContain("status=", Query(handler));
    }

    [Fact]
    public async Task Instruments_list_without_a_filter_sends_only_the_window()
    {
        var (api, handler) = Create();

        await new InstrumentsApiClient(api).ListAsync(1, 25);

        Assert.Equal("?offset=0&limit=25", handler.LastRequest!.RequestUri!.Query);
    }

    [Fact]
    public async Task Instrument_reads_reach_their_routes()
    {
        var (api, handler) = Create();
        var client = new InstrumentsApiClient(api);

        handler.ResponseJson = """{"instrumentId":"7c9e6679-7425-40de-944b-e07fc1f90ae7","symbol":"EQNR","name":"Equinor","type":1,"currencyCode":"NOK"}""";
        var one = await client.GetAsync(Id);
        Assert.Equal($"/api/instruments/{Id}", Path(handler));
        Assert.Equal(InstrumentType.Stock, one.Value!.Type);

        handler.ResponseJson = """{"totalCount":3,"byType":[{"type":3,"count":1}]}""";
        var summary = await client.GetSummaryAsync();
        Assert.Equal("/api/instruments/summary", Path(handler));
        Assert.Equal(3, summary.Value!.TotalCount);

        handler.ResponseJson = """{"currencyCode":"NOK","points":[]}""";
        await client.GetSeriesAsync(Id, new DateOnly(2026, 1, 2), new DateOnly(2026, 3, 4));
        Assert.Equal($"/api/instruments/{Id}/prices/series", Path(handler));
        Assert.Equal("?from=2026-01-02&to=2026-03-04", handler.LastRequest!.RequestUri!.Query);

        await client.GetSeriesAsync(Id);
        Assert.Equal(string.Empty, handler.LastRequest!.RequestUri!.Query);
    }

    [Fact]
    public async Task Watchlist_sends_the_display_currency_and_sparkline_days()
    {
        var (api, handler) = Create();
        var client = new InstrumentsApiClient(api);
        handler.ResponseJson = """{"displayCurrencyCode":"EUR","staleAfterDays":5,"truncated":true,"items":[]}""";

        var result = await client.GetWatchlistAsync("EUR", 30);

        Assert.Equal(HttpMethod.Get, handler.LastRequest!.Method);
        Assert.Equal("/api/instruments/watchlist", Path(handler));
        Assert.Equal("?sparklineDays=30&mainCurrency=EUR", handler.LastRequest.RequestUri!.Query);
        Assert.True(result.Value!.Truncated);

        await client.GetWatchlistAsync(null);
        Assert.Equal("?sparklineDays=0", handler.LastRequest!.RequestUri!.Query);
    }

    [Fact]
    public async Task Instrument_writes_use_their_verbs_and_routes()
    {
        var (api, handler) = Create();
        var client = new InstrumentsApiClient(api);
        var body = new NewInstrument { Symbol = "EQNR", MicCode = "XOSL", Name = "Equinor", Type = InstrumentType.Stock, CurrencyCode = "NOK" };
        handler.ResponseJson = """{"instrumentId":"7c9e6679-7425-40de-944b-e07fc1f90ae7","symbol":"EQNR","name":"Equinor","type":1,"currencyCode":"NOK"}""";

        handler.Status = HttpStatusCode.Created;
        var created = await client.CreateAsync(body);
        Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
        Assert.Equal("/api/instruments", Path(handler));
        using (var json = JsonDocument.Parse(handler.LastBody!))
        {
            Assert.Equal("XOSL", json.RootElement.GetProperty("micCode").GetString());
        }

        Assert.Equal(Id, created.Value!.InstrumentId);

        handler.Status = HttpStatusCode.OK;
        await client.UpdateAsync(Id, body);
        Assert.Equal(HttpMethod.Put, handler.LastRequest!.Method);
        Assert.Equal($"/api/instruments/{Id}", Path(handler));

        handler.Status = HttpStatusCode.NoContent;
        handler.ResponseJson = string.Empty;
        await client.ArchiveAsync(Id);
        Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
        Assert.Equal($"/api/instruments/{Id}/archive", Path(handler));

        await client.UnarchiveAsync(Id);
        Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
        Assert.Equal($"/api/instruments/{Id}/unarchive", Path(handler));

        var deleted = await client.DeleteAsync(Id);
        Assert.Equal(HttpMethod.Delete, handler.LastRequest!.Method);
        Assert.Equal($"/api/instruments/{Id}", Path(handler));
        Assert.True(deleted.IsSuccess);
    }

    [Fact]
    public async Task Purge_deletes_the_scope_with_the_confirmed_count()
    {
        var (api, handler) = Create();
        handler.ResponseJson = """{"deleted":251}""";

        var result = await new InstrumentsApiClient(api).PurgePricesAsync(Id, InstrumentPricePurgeScope.Fetched, 251);

        Assert.Equal(HttpMethod.Delete, handler.LastRequest!.Method);
        Assert.Equal($"/api/instruments/{Id}/prices", Path(handler));
        Assert.Equal("?source=Fetched&expectedCount=251", handler.LastRequest.RequestUri!.Query);
        Assert.Equal(251, result.Value!.Deleted);
    }

    [Fact]
    public async Task Prices_list_sends_the_window_filters_and_sort()
    {
        var (api, handler) = Create();

        await new InstrumentPricesApiClient(api).ListAsync(1, 1, new InstrumentPricesFilter
        {
            Search = "EQ",
            InstrumentIds = [Id],
            Statuses = [InstrumentPriceStatus.Current],
            Sources = [InstrumentPriceSource.Manual, InstrumentPriceSource.Fetched],
            From = new DateOnly(2026, 1, 1),
            To = new DateOnly(2026, 2, 28),
            SortBy = InstrumentPriceSortBy.TradingDate,
            Ascending = false,
        });

        Assert.Equal("/api/instrument-prices", Path(handler));
        var query = Query(handler);
        Assert.Contains("offset=0", query);
        Assert.Contains("limit=1", query);
        Assert.Contains("search=EQ", query);
        Assert.Contains($"instrumentIds={Id}", query);
        Assert.Contains("status=Current", query);
        Assert.Contains("sources=Manual", query);
        Assert.Contains("sources=Fetched", query);
        Assert.Contains("from=2026-01-01", query);
        Assert.Contains("to=2026-02-28", query);
        Assert.Contains("sortBy=TradingDate", query);
        Assert.Contains("sortDir=desc", query);
    }

    [Fact]
    public async Task Price_crud_uses_its_verbs_and_routes()
    {
        var (api, handler) = Create();
        var client = new InstrumentPricesApiClient(api);
        handler.ResponseJson = """{"instrumentPriceId":"7c9e6679-7425-40de-944b-e07fc1f90ae7","instrumentId":"7c9e6679-7425-40de-944b-e07fc1f90ae7","symbol":"EQNR","instrumentName":"Equinor","currencyCode":"NOK","tradingDate":"2026-11-06","close":274.12345678}""";

        var one = await client.GetAsync(Id);
        Assert.Equal(HttpMethod.Get, handler.LastRequest!.Method);
        Assert.Equal($"/api/instrument-prices/{Id}", Path(handler));
        Assert.Equal(274.12345678m, one.Value!.Close);

        handler.Status = HttpStatusCode.Created;
        await client.CreateAsync(new NewInstrumentPrice { InstrumentId = Id, TradingDate = new DateOnly(2026, 11, 6), Close = 274.12345678m });
        Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
        Assert.Equal("/api/instrument-prices", Path(handler));
        using (var json = JsonDocument.Parse(handler.LastBody!))
        {
            Assert.Equal("2026-11-06", json.RootElement.GetProperty("tradingDate").GetString());
            Assert.Equal(274.12345678m, json.RootElement.GetProperty("close").GetDecimal());
        }

        handler.Status = HttpStatusCode.OK;
        await client.UpdateAsync(Id, new UpdateInstrumentPrice { TradingDate = new DateOnly(2026, 11, 5), Close = 1m });
        Assert.Equal(HttpMethod.Put, handler.LastRequest!.Method);
        Assert.Equal($"/api/instrument-prices/{Id}", Path(handler));

        handler.Status = HttpStatusCode.NoContent;
        handler.ResponseJson = string.Empty;
        await client.DeleteAsync(Id);
        Assert.Equal(HttpMethod.Delete, handler.LastRequest!.Method);
        Assert.Equal($"/api/instrument-prices/{Id}", Path(handler));
    }

    [Fact]
    public async Task Price_sources_reach_the_system_settings_routes()
    {
        var (api, handler) = Create();
        var client = new InstrumentPriceSourcesApiClient(api);
        const string Base = "/api/system-settings/instrument-price-sources";

        handler.ResponseJson = """{"sources":[{"key":"demo","displayName":"Demo","attribution":"Demo data","requestsToday":3,"maxRequestsPerDay":3}],"maxBindingsPerInstrument":9}""";
        var status = await client.GetStatusAsync();
        Assert.Equal(HttpMethod.Get, handler.LastRequest!.Method);
        Assert.Equal(Base, Path(handler));
        Assert.Equal(9, status.Value!.MaxBindingsPerInstrument);
        Assert.Equal(3, status.Value.Sources[0].MaxRequestsPerDay);

        handler.ResponseJson = """[{"sourceKey":"demo","sourceSymbol":"EQNR.OL","priority":1,"enabled":true}]""";
        var bindings = await client.GetBindingsAsync(Id);
        Assert.Equal(HttpMethod.Get, handler.LastRequest!.Method);
        Assert.Equal($"{Base}/bindings/{Id}", Path(handler));
        Assert.Equal("EQNR.OL", Assert.Single(bindings.Value!).SourceSymbol);

        var replaced = await client.ReplaceBindingsAsync(Id, new InstrumentPriceBindingsUpdate
        {
            Bindings = [new InstrumentPriceBindingInput { SourceKey = "demo", SourceSymbol = "EQNR.OL", Enabled = true }],
        });
        Assert.Equal(HttpMethod.Put, handler.LastRequest!.Method);
        Assert.Equal($"{Base}/bindings/{Id}", Path(handler));
        using (var json = JsonDocument.Parse(handler.LastBody!))
        {
            Assert.Equal("demo", json.RootElement.GetProperty("bindings")[0].GetProperty("sourceKey").GetString());
        }

        Assert.Single(replaced.Value!);
    }

    [Fact]
    public void The_clients_are_registered()
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => new HttpClient { BaseAddress = new Uri("http://localhost/") });
        services.AddOdysseyApiClient();
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        Assert.IsType<InstrumentsApiClient>(scope.ServiceProvider.GetRequiredService<IInstrumentsApiClient>());
        Assert.IsType<InstrumentPricesApiClient>(scope.ServiceProvider.GetRequiredService<IInstrumentPricesApiClient>());
        Assert.IsType<InstrumentPriceSourcesApiClient>(scope.ServiceProvider.GetRequiredService<IInstrumentPriceSourcesApiClient>());
    }
}
