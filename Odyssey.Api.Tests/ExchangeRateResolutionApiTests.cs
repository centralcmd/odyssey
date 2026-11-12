using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Odyssey.Api.Tests.Infrastructure;
using Odyssey.Context;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos.Finance;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Odyssey.Api.Tests;

/// <summary>
/// The HTTP surface of issue #472 release 1: <c>GET /api/exchange-rates/latest</c> answering the reverse pair from
/// an inverted fetched row, and the new <c>GET /api/exchange-rates/resolved</c>. Both bind the shared
/// <see cref="CurrencyPairQuery"/>, so a malformed code is a <c>400</c> before anything echoes it.
/// </summary>
public class ExchangeRateResolutionApiTests
{
    private const string ActorUserId = "exchange-rate-resolution-actor";

    private sealed class ApiFactory(IReadOnlyCollection<string>? permissions)
        : OdysseyApiFactory(permissions, ActorUserId);

    private static readonly DateTime AsOf = DateTime.UtcNow.AddDays(-2);

    private static async Task<Guid> SeedFetchedAsync(WebApplicationFactory<Program> factory, string from, decimal rate)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        await context.Database.EnsureCreatedAsync();

        var id = Guid.NewGuid();
        context.ExchangeRates.Add(new ExchangeRate
        {
            ExchangeRateId = id,
            FromCurrencyCode = from,
            ToCurrencyCode = "NOK",
            Rate = rate,
            AsOf = AsOf,
            CreatedAt = AsOf,
            Provider = ExchangeRateProvider.NorgesBank,
        });
        await context.SaveChangesAsync();
        return id;
    }

    [Fact] // AC 14
    public async Task Latest_returns_the_inverted_fetched_row_for_the_reverse_pair()
    {
        await using var factory = new ApiFactory([PermissionClaims.ExchangeRatesRead]);
        var id = await SeedFetchedAsync(factory, "USD", 10m);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/exchange-rates/latest?from=NOK&to=USD");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = (await response.Content.ReadFromJsonAsync<ExistingExchangeRate>())!;
        Assert.True(body.Inverted);
        Assert.Equal(("NOK", "USD", 0.1m, id), (body.FromCurrencyCode, body.ToCurrencyCode, body.Rate, body.ExchangeRateId));

        // Enums stay numbers on the wire (spec §5.2): no converter was added.
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(JsonValueKind.Number, json.RootElement.GetProperty("source").ValueKind);
        Assert.True(json.RootElement.GetProperty("inverted").GetBoolean());
    }

    [Fact] // AC 16, AC 26, AC 27
    public async Task Latest_404s_without_a_hop_for_the_same_currency_and_for_an_unknown_one_echoing_only_codes()
    {
        await using var factory = new ApiFactory([PermissionClaims.ExchangeRatesRead]);
        await SeedFetchedAsync(factory, "USD", 10m);
        await SeedFetchedAsync(factory, "EUR", 11m);
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/exchange-rates/latest?from=USD&to=EUR")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/exchange-rates/latest?from=NOK&to=NOK")).StatusCode);

        var unknown = await client.GetAsync("/api/exchange-rates/latest?from=xxx&to=nok");
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        var text = await unknown.Content.ReadAsStringAsync();
        Assert.Contains("XXX -> NOK", text);
        Assert.DoesNotContain("xxx", text);
    }

    [Theory] // AC 21, AC 26
    [InlineData("latest", "from=%20US&to=NOK")]
    [InlineData("latest", "from=U1D&to=NOK")]
    [InlineData("latest", "from=USD%0A&to=NOK")]
    [InlineData("latest", "from=USD")]
    [InlineData("resolved", "from=%20US&to=NOK")]
    [InlineData("resolved", "from=U1D&to=NOK")]
    [InlineData("resolved", "from=USD%0A&to=NOK")]
    [InlineData("resolved", "from=US&to=EUR")]
    [InlineData("resolved", "to=EUR")]
    public async Task A_malformed_code_is_a_400(string endpoint, string query)
    {
        await using var factory = new ApiFactory([PermissionClaims.ExchangeRatesRead]);
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"/api/exchange-rates/{endpoint}?{query}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact] // AC 19, AC 20
    public async Task Resolved_reports_a_hop_with_an_inverted_leg()
    {
        await using var factory = new ApiFactory([PermissionClaims.ExchangeRatesRead]);
        var usd = await SeedFetchedAsync(factory, "USD", 9.3601m);
        var eur = await SeedFetchedAsync(factory, "EUR", 10.488m);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/exchange-rates/resolved?from=usd&to=eur");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = (await response.Content.ReadFromJsonAsync<ResolvedExchangeRate>())!;
        Assert.Equal(("USD", "EUR", ExchangeRateResolutionMethod.ViaNok), (body.FromCurrencyCode, body.ToCurrencyCode, body.Method));
        Assert.Equal(9.3601m * (1m / 10.488m), body.Rate);
        Assert.Equal([usd, eur], body.Legs.Select(leg => leg.ExchangeRateId));
        Assert.Equal([("USD", "NOK", false), ("NOK", "EUR", true)],
            body.Legs.Select(leg => (leg.FromCurrencyCode, leg.ToCurrencyCode, leg.Inverted)));

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(3, json.RootElement.GetProperty("method").GetInt32());
    }

    [Fact] // AC 21, AC 26
    public async Task Resolved_answers_the_same_currency_and_404s_an_unknown_or_unconvertible_pair()
    {
        await using var factory = new ApiFactory([PermissionClaims.ExchangeRatesRead]);
        await SeedFetchedAsync(factory, "USD", 10m);
        using var client = factory.CreateClient();

        var same = await client.GetAsync("/api/exchange-rates/resolved?from=NOK&to=NOK");
        Assert.Equal(HttpStatusCode.OK, same.StatusCode);
        Assert.Equal(ExchangeRateResolutionMethod.SameCurrency, (await same.Content.ReadFromJsonAsync<ResolvedExchangeRate>())!.Method);

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/exchange-rates/resolved?from=XXX&to=XXX")).StatusCode);

        var unconvertible = await client.GetAsync("/api/exchange-rates/resolved?from=EUR&to=SEK");
        Assert.Equal(HttpStatusCode.NotFound, unconvertible.StatusCode);
        var text = await unconvertible.Content.ReadAsStringAsync();
        Assert.DoesNotContain("rate\"", text);
        Assert.DoesNotContain("exchangeRateId", text);
    }

    [Theory] // AC 22
    [InlineData("/api/exchange-rates/latest?from=NOK&to=USD")]
    [InlineData("/api/exchange-rates/resolved?from=NOK&to=USD")]
    public async Task Both_reads_need_the_read_claim(string path)
    {
        await using (var anonymous = new ApiFactory(permissions: null))
        {
            using var client = anonymous.CreateClient();
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(path)).StatusCode);
        }

        await using (var unprivileged = new ApiFactory([]))
        {
            using var client = unprivileged.CreateClient();
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(path)).StatusCode);
        }
    }
}
