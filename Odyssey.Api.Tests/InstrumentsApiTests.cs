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

    private sealed class ApiFactory(IReadOnlyCollection<string>? permissions) : OdysseyApiFactory(permissions, ActorUserId);

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

    private static DateOnly Today => OsloDate.Today(TimeProvider.System);

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
    [InlineData(InstrumentType.Fund, "EQNR", null, null, "Isin")]
    [InlineData(InstrumentType.Stock, "EQNR", null, "NO0010096985", "MicCode")]
    [InlineData(InstrumentType.Fund, "ODIN", null, "NO0010096986", "Isin")]
    public async Task Create_IdentityRules_Return422OnTheField(InstrumentType type, string symbol, string? mic, string? isin, string field)
    {
        await using var factory = new ApiFactory(Owner);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/instruments", new NewInstrument
        {
            Symbol = symbol, MicCode = mic, Isin = isin, Name = "x", Type = type, CurrencyCode = "NOK",
        });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var errors = (await ProblemAsync(response)).GetProperty("errors");
        Assert.True(errors.TryGetProperty(field, out _) || errors.TryGetProperty(char.ToLowerInvariant(field[0]) + field[1..], out _));
    }

    [Fact]
    public async Task Price_Rules_FutureDate_Decimals_Duplicate_AndLockedIdentity()
    {
        await using var factory = new ApiFactory(Owner);
        using var client = factory.CreateClient();
        var instrument = await CreateAsync(client, Stock());

        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await AddPriceAsync(client, instrument.InstrumentId, Today.AddDays(1), 10m)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await AddPriceAsync(client, instrument.InstrumentId, Today, 0m)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await AddPriceAsync(client, instrument.InstrumentId, Today, 274.123456789m)).StatusCode);

        var first = await AddPriceAsync(client, instrument.InstrumentId, Today, 274.12345678m);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(274.12345678m, (await first.Content.ReadFromJsonAsync<ExistingInstrumentPrice>())!.Close);

        var duplicate = await AddPriceAsync(client, instrument.InstrumentId, Today, 275m);
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
        await AddPriceAsync(client, instrument.InstrumentId, Today.AddDays(-1), 10m);
        await AddPriceAsync(client, instrument.InstrumentId, Today, 11m);

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
        await AddPriceAsync(client, nok.InstrumentId, Today.AddDays(-1), 100m);
        await AddPriceAsync(client, nok.InstrumentId, Today, 110m);
        await AddPriceAsync(client, usd.InstrumentId, Today, 200m);

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
        await using var owner = new ApiFactory(Owner);
        using (var client = owner.CreateClient())
        {
            var instrument = await CreateAsync(client, Stock());
            await AddPriceAsync(client, instrument.InstrumentId, Today, 10m);
        }

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
                InstrumentPriceId = Guid.NewGuid(), InstrumentId = id, TradingDate = Today, Close = 10m, CreatedAtUtc = DateTime.UtcNow,
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

    private static class InstrumentProblemCodesForTests
    {
        public const string InstrumentDuplicate = "instrument-duplicate";
        public const string PriceDuplicate = "price-duplicate";
    }
}
