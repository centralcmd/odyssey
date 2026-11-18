using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Odyssey.Api.Tests.Infrastructure;
using Odyssey.Context;
using Odyssey.Dtos;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos.Finance;
using Xunit;

namespace Odyssey.Api.Tests;

/// <summary>HTTP coverage of the instrument watchlist, manual prices, purge and dashboard read (issue #446 §12).</summary>
public class InstrumentsApiTests
{
    private const string ActorUserId = "instruments-actor-id";

    private static readonly string[] Owner =
    [
        PermissionClaims.InstrumentsRead, PermissionClaims.InstrumentsCreate, PermissionClaims.InstrumentsUpdate,
        PermissionClaims.InstrumentsDelete, PermissionClaims.InstrumentPricesRead, PermissionClaims.InstrumentPricesCreate,
        PermissionClaims.InstrumentPricesUpdate, PermissionClaims.InstrumentPricesDelete,
    ];

    private static readonly string[] Admin = [.. Owner, PermissionClaims.SystemSettingsRead, PermissionClaims.SystemSettingsUpdate];

    private sealed class ApiFactory(IReadOnlyCollection<string>? permissions, OdysseyApiFactory? sharing = null)
        : OdysseyApiFactory(permissions, ActorUserId, sharingStoreWith: sharing);

    private static NewInstrument Stock(string symbol = "EQNR", string mic = "XOSL", string currency = "NOK") => new()
    {
        Symbol = symbol,
        MicCode = mic,
        Name = $"{symbol} ASA",
        Type = InstrumentType.Stock,
        CurrencyCode = currency,
    };

    private static async Task<ExistingInstrument> CreateAsync(HttpClient client, NewInstrument instrument)
    {
        var response = await client.PostAsJsonAsync("/api/instruments", instrument);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ExistingInstrument>())!;
    }

    private static async Task<HttpResponseMessage> AddPriceAsync(HttpClient client, Guid instrumentId, DateOnly date, decimal close) =>
        await client.PostAsJsonAsync("/api/instrument-prices", new NewInstrumentPrice { InstrumentId = instrumentId, TradingDate = date, Close = close });

    // The server reads the real clock, so dates stay clear of today: a test running across Oslo midnight would
    // otherwise see "today" move between the request it builds and the check the server makes.
    private static DateOnly Day => OsloDate.Today(TimeProvider.System).AddDays(-3);

    private static DateOnly Future => OsloDate.Today(TimeProvider.System).AddDays(3);

    private static async Task<JsonElement> ProblemAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>());

    [Fact]
    public async Task Create_Get_AndDuplicate_AreHandled()
    {
        await using var factory = new ApiFactory(Owner);
        using var client = factory.CreateClient();

        var created = await CreateAsync(client, Stock());
        Assert.Null(created.LatestClose);
        Assert.False(created.IdentityLocked);

        var duplicate = await client.PostAsJsonAsync("/api/instruments", Stock(symbol: "eqnr", mic: "xosl"));
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        var problem = await ProblemAsync(duplicate);
        Assert.Equal(InstrumentProblemCodesForTests.InstrumentDuplicate, problem.GetProperty("code").GetString());
        Assert.Equal(created.InstrumentId, problem.GetProperty("existingInstrumentId").GetGuid());
    }

    [Theory]
    [InlineData(InstrumentType.Fund, "EQNR", null, "Isin")]
    [InlineData(InstrumentType.Stock, "EQNR", "NO0010096985", "MicCode")]
    [InlineData(InstrumentType.Fund, "ODIN", "NO0010096986", "Isin")]
    public async Task Create_IdentityRules_Return422OnTheField(InstrumentType type, string symbol, string? isin, string field)
    {
        await using var factory = new ApiFactory(Owner);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/instruments", new NewInstrument
        {
            Symbol = symbol, Isin = isin, Name = "x", Type = type, CurrencyCode = "NOK",
        });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var errors = (await ProblemAsync(response)).GetProperty("errors");
        Assert.True(errors.TryGetProperty(field, out _), $"Expected an error keyed {field}: {errors}");
    }

    [Fact]
    public async Task Price_Rules_FutureDate_Decimals_Duplicate_AndLockedIdentity()
    {
        await using var factory = new ApiFactory(Owner);
        using var client = factory.CreateClient();
        var instrument = await CreateAsync(client, Stock());

        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await AddPriceAsync(client, instrument.InstrumentId, Future, 10m)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await AddPriceAsync(client, instrument.InstrumentId, Day, 0m)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await AddPriceAsync(client, instrument.InstrumentId, Day, 274.123456789m)).StatusCode);

        var first = await AddPriceAsync(client, instrument.InstrumentId, Day, 274.12345678m);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(274.12345678m, (await first.Content.ReadFromJsonAsync<ExistingInstrumentPrice>())!.Close);

        var duplicate = await AddPriceAsync(client, instrument.InstrumentId, Day, 275m);
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Equal(InstrumentProblemCodesForTests.PriceDuplicate, (await ProblemAsync(duplicate)).GetProperty("code").GetString());

        // Identity is now locked; the name still edits.
        var renamed = Stock() with { Name = "Equinor" };
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"/api/instruments/{instrument.InstrumentId}", renamed)).StatusCode);
        var retyped = Stock() with { CurrencyCode = "USD" };
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await client.PutAsJsonAsync($"/api/instruments/{instrument.InstrumentId}", retyped)).StatusCode);
    }

    [Fact]
    public async Task Purge_RequiresTheExactCount_ThenDeleteSucceeds()
    {
        await using var factory = new ApiFactory(Admin);
        using var client = factory.CreateClient();
        var instrument = await CreateAsync(client, Stock());
        await AddPriceAsync(client, instrument.InstrumentId, Day.AddDays(-1), 10m);
        await AddPriceAsync(client, instrument.InstrumentId, Day, 11m);

        var blocked = await client.DeleteAsync($"/api/instruments/{instrument.InstrumentId}");
        Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);

        Assert.Equal(HttpStatusCode.BadRequest, (await client.DeleteAsync($"/api/instruments/{instrument.InstrumentId}/prices?source=All")).StatusCode);

        var stale = await client.DeleteAsync($"/api/instruments/{instrument.InstrumentId}/prices?source=All&expectedCount=5");
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal(2, (await ProblemAsync(stale)).GetProperty("actualCount").GetInt32());

        var purged = await client.DeleteAsync($"/api/instruments/{instrument.InstrumentId}/prices?source=All&expectedCount=2");
        Assert.Equal(HttpStatusCode.OK, purged.StatusCode);
        Assert.Equal(2, (await purged.Content.ReadFromJsonAsync<InstrumentPricePurgeResult>())!.Deleted);

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/instruments/{instrument.InstrumentId}")).StatusCode);
    }

    [Fact]
    public async Task Purge_OfFetchedRows_WithoutSystemSettingsUpdate_IsForbidden()
    {
        await using var factory = new ApiFactory(Owner);
        using var client = factory.CreateClient();
        var instrument = await CreateAsync(client, Stock());

        var response = await client.DeleteAsync($"/api/instruments/{instrument.InstrumentId}/prices?source=Fetched&expectedCount=0");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Watchlist_ShowsChangeInTheDisplayCurrency_AndNamesAnUnconvertibleRow()
    {
        await using var factory = new ApiFactory(Owner);
        using var client = factory.CreateClient();
        var nok = await CreateAsync(client, Stock());
        var usd = await CreateAsync(client, Stock(symbol: "AAPL", mic: "XNAS", currency: "USD"));
        await AddPriceAsync(client, nok.InstrumentId, Day.AddDays(-1), 100m);
        await AddPriceAsync(client, nok.InstrumentId, Day, 110m);
        await AddPriceAsync(client, usd.InstrumentId, Day, 200m);

        var watchlist = await client.GetFromJsonAsync<InstrumentWatchlist>("/api/instruments/watchlist?mainCurrency=NOK&sparklineDays=30");

        var eqnr = watchlist!.Items.Single(i => i.Symbol == "EQNR");
        Assert.Equal(110m, eqnr.Close);
        Assert.Equal(10m, eqnr.Change);
        Assert.Equal(10m, eqnr.ChangePercent);
        Assert.Equal(2, eqnr.Sparkline.Count);

        var aapl = watchlist.Items.Single(i => i.Symbol == "AAPL");
        Assert.True(aapl.Unconvertible);
        Assert.Null(aapl.Close);
        Assert.Equal(200m, aapl.NativeClose);
        Assert.Equal(SystemSettingsDefaults.InstrumentPriceStaleAfterDays, watchlist.StaleAfterDays);
    }

    [Fact]
    public async Task Watchlist_UnknownCurrency_Is422_AndMalformedIs400()
    {
        await using var factory = new ApiFactory(Owner);
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await client.GetAsync("/api/instruments/watchlist?mainCurrency=XXQ")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/instruments/watchlist?mainCurrency=XX1")).StatusCode);
    }

    [Fact]
    public async Task InstrumentsReadOnly_SeesNoPriceFields_AndCannotSortByLatestDate()
    {
        await using var reader = new ApiFactory([PermissionClaims.InstrumentsRead]);
        using var readerClient = reader.CreateClient();
        // A fresh factory has its own database: seed the reader's directly.
        using (var scope = reader.Services.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
            await context.Database.EnsureCreatedAsync();
            var id = Guid.NewGuid();
            context.Instruments.Add(new Instrument
            {
                InstrumentId = id, Symbol = "EQNR", MicCode = "XOSL", Name = "Equinor", Type = InstrumentType.Stock,
                CurrencyCode = "NOK", ListingKey = "EQNR@XOSL", CreatedAtUtc = DateTime.UtcNow, UpdatedAtUtc = DateTime.UtcNow,
            });
            context.InstrumentPrices.Add(new InstrumentPrice
            {
                InstrumentPriceId = Guid.NewGuid(), InstrumentId = id, TradingDate = Day, Close = 10m, CreatedAtUtc = DateTime.UtcNow,
            });
            await context.SaveChangesAsync();
        }

        var page = await readerClient.GetFromJsonAsync<PagedResult<ExistingInstrument>>("/api/instruments");
        var row = Assert.Single(page!.Items);
        Assert.Null(row.LatestClose);
        Assert.Null(row.PriceCount);
        Assert.True(row.IdentityLocked);

        Assert.Equal(HttpStatusCode.Forbidden, (await readerClient.GetAsync("/api/instruments?sortBy=LatestDate")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await readerClient.GetAsync("/api/instrument-prices")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await readerClient.PostAsJsonAsync("/api/instruments", Stock("X"))).StatusCode);
    }

    [Theory]
    [InlineData("EQNR\n")]
    [InlineData("EQNR\r\n")]
    public async Task Create_RefusesAnIdentifierWithATrailingNewline(string symbol)
    {
        await using var factory = new ApiFactory(Owner);
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/instruments", Stock(symbol))).StatusCode);
    }

    [Fact]
    public async Task List_StatusNarrowsToActiveOrArchived_AndOverridesIncludeArchived()
    {
        await using var factory = new ApiFactory(Owner);
        using var client = factory.CreateClient();
        var kept = await CreateAsync(client, Stock("EQNR"));
        var archived = await CreateAsync(client, Stock("TEL"));
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync($"/api/instruments/{archived.InstrumentId}/archive", null)).StatusCode);

        async Task<IReadOnlyList<Guid>> IdsAsync(string query) =>
            [.. (await client.GetFromJsonAsync<PagedResult<ExistingInstrument>>($"/api/instruments{query}"))!.Items.Select(i => i.InstrumentId)];

        Assert.Equal([kept.InstrumentId], await IdsAsync("?status=Active&includeArchived=true"));
        Assert.Equal([archived.InstrumentId], await IdsAsync("?status=Archived"));
        Assert.Equal(2, (await IdsAsync("?includeArchived=true")).Count);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/instruments?status=Gone")).StatusCode);
    }

    [Fact]
    public async Task Archive_HidesFromTheListAndTheWatchlist_AndUnarchiveRestores()
    {
        await using var factory = new ApiFactory(Owner);
        using var client = factory.CreateClient();
        var instrument = await CreateAsync(client, Stock());

        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync($"/api/instruments/{instrument.InstrumentId}/archive", null)).StatusCode);
        Assert.Empty((await client.GetFromJsonAsync<PagedResult<ExistingInstrument>>("/api/instruments"))!.Items);
        Assert.Single((await client.GetFromJsonAsync<PagedResult<ExistingInstrument>>("/api/instruments?includeArchived=true"))!.Items);
        Assert.Empty((await client.GetFromJsonAsync<InstrumentWatchlist>("/api/instruments/watchlist?mainCurrency=NOK"))!.Items);
        Assert.Equal(1, (await client.GetFromJsonAsync<InstrumentSummary>("/api/instruments/summary"))!.ArchivedCount);

        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync($"/api/instruments/{instrument.InstrumentId}/unarchive", null)).StatusCode);
        Assert.Single((await client.GetFromJsonAsync<PagedResult<ExistingInstrument>>("/api/instruments"))!.Items);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync($"/api/instruments/{Guid.NewGuid()}/archive", null)).StatusCode);
    }

    [Fact]
    public async Task ManualPrice_UpdatesAndDeletes()
    {
        await using var factory = new ApiFactory(Owner);
        using var client = factory.CreateClient();
        var instrument = await CreateAsync(client, Stock());
        var price = (await (await AddPriceAsync(client, instrument.InstrumentId, Day, 10m)).Content.ReadFromJsonAsync<ExistingInstrumentPrice>())!;

        var updated = await client.PutAsJsonAsync($"/api/instrument-prices/{price.InstrumentPriceId}", new UpdateInstrumentPrice { TradingDate = Day, Close = 12.5m });
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        Assert.Equal(12.5m, (await updated.Content.ReadFromJsonAsync<ExistingInstrumentPrice>())!.Close);

        Assert.Equal(HttpStatusCode.UnprocessableEntity,
            (await client.PutAsJsonAsync($"/api/instrument-prices/{price.InstrumentPriceId}", new UpdateInstrumentPrice { TradingDate = Future, Close = 1m })).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/instrument-prices/{price.InstrumentPriceId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/instrument-prices/{price.InstrumentPriceId}")).StatusCode);
    }

    [Fact]
    public async Task FetchedPrice_CannotBeEditedOrDeletedOnItsOwn()
    {
        await using var factory = new ApiFactory(Admin);
        using var client = factory.CreateClient();
        var instrument = await CreateAsync(client, Stock());
        var fetched = await SeedFetchedAsync(factory, instrument.InstrumentId, Day);

        var edit = await client.PutAsJsonAsync($"/api/instrument-prices/{fetched}", new UpdateInstrumentPrice { TradingDate = Day, Close = 1m });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, edit.StatusCode);
        Assert.True((await ProblemAsync(edit)).GetProperty("errors").TryGetProperty("source", out _));

        var delete = await client.DeleteAsync($"/api/instrument-prices/{fetched}");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, delete.StatusCode);
    }

    [Fact]
    public async Task PurgeFetched_DeletesOnlyFetchedRows_AndResetsTheBindingWatermark()
    {
        await using var factory = new ApiFactory(Admin);
        using var client = factory.CreateClient();
        var instrument = await CreateAsync(client, Stock());
        await AddPriceAsync(client, instrument.InstrumentId, Day, 10m);
        await SeedFetchedAsync(factory, instrument.InstrumentId, Day);
        await SeedFetchedAsync(factory, instrument.InstrumentId, Day.AddDays(-1));
        using (var scope = factory.Services.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
            context.InstrumentPriceSourceBindings.Add(new InstrumentPriceSourceBinding
            {
                InstrumentId = instrument.InstrumentId, SourceKey = "fake-primary", SourceSymbol = "EQNR.OL", Priority = 1, Enabled = true,
                NewestFetched = Day, BackfilledFrom = Day.AddDays(-1), UpdatedAtUtc = DateTime.UtcNow,
            });
            await context.SaveChangesAsync();
        }

        var purged = await client.DeleteAsync($"/api/instruments/{instrument.InstrumentId}/prices?source=Fetched&expectedCount=2");

        Assert.Equal(HttpStatusCode.OK, purged.StatusCode);
        Assert.Equal(2, (await purged.Content.ReadFromJsonAsync<InstrumentPricePurgeResult>())!.Deleted);
        using var check = factory.Services.CreateScope();
        var store = check.ServiceProvider.GetRequiredService<OdysseyContext>();
        var left = Assert.Single(await store.InstrumentPrices.AsNoTracking().ToListAsync());
        Assert.Null(left.SourceKey);
        var binding = await store.InstrumentPriceSourceBindings.AsNoTracking().SingleAsync();
        Assert.Null(binding.NewestFetched);
        Assert.Null(binding.BackfilledFrom);
    }

    [Fact]
    public async Task Summary_AndSeries_ReportThePrices()
    {
        await using var factory = new ApiFactory(Owner);
        using var client = factory.CreateClient();
        var instrument = await CreateAsync(client, Stock());
        await AddPriceAsync(client, instrument.InstrumentId, Day.AddDays(-1), 10m);
        await AddPriceAsync(client, instrument.InstrumentId, Day, 11m);

        var summary = await client.GetFromJsonAsync<InstrumentSummary>("/api/instruments/summary");
        Assert.Equal(1, summary!.TotalCount);
        Assert.Equal(1, summary.FollowedCount);
        Assert.Equal(Day, summary.NewestPriceDate);

        var series = await client.GetFromJsonAsync<InstrumentPriceSeries>(
            $"/api/instruments/{instrument.InstrumentId}/prices/series?from={Day.AddDays(-5):yyyy-MM-dd}&to={Day:yyyy-MM-dd}");
        Assert.Equal("NOK", series!.CurrencyCode);
        Assert.Equal([10m, 11m], series.Points.Select(p => p.Close));

        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.GetAsync($"/api/instruments/{instrument.InstrumentId}/prices/series?from=2010-01-01&to={Day:yyyy-MM-dd}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/instruments/{Guid.NewGuid()}/prices/series")).StatusCode);
    }

    [Fact]
    public async Task EveryWrite_IsForbiddenToAReader()
    {
        await using var owner = new ApiFactory(Owner);
        using var ownerClient = owner.CreateClient();
        var instrument = await CreateAsync(ownerClient, Stock());
        var price = (await (await AddPriceAsync(ownerClient, instrument.InstrumentId, Day, 10m)).Content.ReadFromJsonAsync<ExistingInstrumentPrice>())!;

        // A Guest: both read claims, nothing else.
        await using var guest = new ApiFactory([PermissionClaims.InstrumentsRead, PermissionClaims.InstrumentPricesRead], sharing: owner);
        using var client = guest.CreateClient();
        var id = instrument.InstrumentId;

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/instruments/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/instrument-prices")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/instruments", Stock("NEW"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PutAsJsonAsync($"/api/instruments/{id}", Stock())).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync($"/api/instruments/{id}/archive", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync($"/api/instruments/{id}/unarchive", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.DeleteAsync($"/api/instruments/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await AddPriceAsync(client, id, Day.AddDays(-1), 1m)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await client.PutAsJsonAsync($"/api/instrument-prices/{price.InstrumentPriceId}", new UpdateInstrumentPrice { TradingDate = Day, Close = 2m })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.DeleteAsync($"/api/instrument-prices/{price.InstrumentPriceId}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.DeleteAsync($"/api/instruments/{id}/prices?source=Manual&expectedCount=1")).StatusCode);
    }

    private static async Task<Guid> SeedFetchedAsync(OdysseyApiFactory factory, Guid instrumentId, DateOnly date)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        var id = Guid.NewGuid();
        context.InstrumentPrices.Add(new InstrumentPrice
        {
            InstrumentPriceId = id, InstrumentId = instrumentId, TradingDate = date, Close = 9m, SourceKey = "fake-primary", CreatedAtUtc = DateTime.UtcNow,
        });
        await context.SaveChangesAsync();
        return id;
    }

    private static class InstrumentProblemCodesForTests
    {
        public const string InstrumentDuplicate = "instrument-duplicate";
        public const string PriceDuplicate = "price-duplicate";
    }
}
