using System.Net;
using System.Net.Http.Json;
using Odyssey.Api.Tests.Infrastructure;
using Odyssey.Dtos.Authorization;
using Odyssey.Context;
using Odyssey.Dtos.Finance;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Odyssey.Api.Tests;

/// <summary>
/// HTTP-level coverage for PUT /api/exchange-rates/{id} (design-system update: in-place Rate/AsOf
/// correction, gated behind the new exchangerates.update claim) — the rest of the controller's CRUD
/// is exercised elsewhere via DtoValidationBoundaryTests (create) and ExchangeRateFeatureFlagTests
/// (the disabled-flag path); this file is specifically the claim boundary and the 404 path that
/// only a real routed request (not a direct controller construction) can prove.
/// </summary>
public class ExchangeRatesApiTests
{
    private const string ActorUserId = "exchange-rates-actor-id";

    private sealed class ApiFactory(IReadOnlyCollection<string>? permissions)
        : OdysseyApiFactory(permissions, ActorUserId);

    private static string RatePath(Guid id) => $"/api/exchange-rates/{id}";

    private static UpdateExchangeRate Update(decimal rate) => new()
    {
        Rate = rate,
        AsOf = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc),
    };

    private static async Task<Guid> SeedRateAsync(WebApplicationFactory<Program> factory)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        await context.Database.EnsureCreatedAsync();

        var rateId = Guid.NewGuid();
        context.ExchangeRates.Add(new ExchangeRate
        {
            ExchangeRateId = rateId,
            FromCurrencyCode = "USD",
            ToCurrencyCode = "EUR",
            Rate = 0.90m,
            AsOf = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            CreatedAt = DateTime.UtcNow,
        });
        await context.SaveChangesAsync();
        return rateId;
    }

    [Fact]
    public async Task Put_Unauthenticated_ReturnsUnauthorized()
    {
        await using var factory = new ApiFactory(permissions: null);
        using var client = factory.CreateClient();

        var response = await client.PutAsJsonAsync(RatePath(Guid.NewGuid()), Update(1m));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Put_WithoutUpdateClaim_ReturnsForbidden()
    {
        await using var factory = new ApiFactory([]);
        var rateId = await SeedRateAsync(factory);
        using var client = factory.CreateClient();

        var response = await client.PutAsJsonAsync(RatePath(rateId), Update(1m));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Put_WithReadOnlyClaim_ReturnsForbidden()
    {
        // The read claim alone must not satisfy the update policy — read and update are distinct
        // claims (mirroring create/delete), not a single "any exchangerates.* claim" check.
        await using var factory = new ApiFactory([PermissionClaims.ExchangeRatesRead]);
        var rateId = await SeedRateAsync(factory);
        using var client = factory.CreateClient();

        var response = await client.PutAsJsonAsync(RatePath(rateId), Update(1m));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Put_WithUpdateClaim_CorrectsRateAndPersists()
    {
        await using var factory = new ApiFactory([PermissionClaims.ExchangeRatesRead, PermissionClaims.ExchangeRatesUpdate]);
        var rateId = await SeedRateAsync(factory);
        using var client = factory.CreateClient();

        var response = await client.PutAsJsonAsync(RatePath(rateId), Update(0.93m));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ExistingExchangeRate>();
        Assert.NotNull(body);
        Assert.Equal(0.93m, body!.Rate);

        var getResponse = await client.GetAsync(RatePath(rateId));
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
        var persisted = await getResponse.Content.ReadFromJsonAsync<ExistingExchangeRate>();
        Assert.Equal(0.93m, persisted!.Rate);
    }

    [Fact]
    public async Task Put_WithUnknownId_ReturnsNotFound()
    {
        await using var factory = new ApiFactory([PermissionClaims.ExchangeRatesRead, PermissionClaims.ExchangeRatesUpdate]);
        using var client = factory.CreateClient();

        var response = await client.PutAsJsonAsync(RatePath(Guid.NewGuid()), Update(1m));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Put_ZeroRate_ReturnsBadRequest()
    {
        await using var factory = new ApiFactory([PermissionClaims.ExchangeRatesRead, PermissionClaims.ExchangeRatesUpdate]);
        var rateId = await SeedRateAsync(factory);
        using var client = factory.CreateClient();

        var response = await client.PutAsJsonAsync(RatePath(rateId), Update(0m));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
