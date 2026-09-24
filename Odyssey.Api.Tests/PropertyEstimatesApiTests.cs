using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Odyssey.Api.Tests.Infrastructure;
using Odyssey.Context;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos.Finance;
using Xunit;
using static Odyssey.Api.Tests.PropertyApiTestSupport;

namespace Odyssey.Api.Tests;

/// <summary>
/// The property estimate endpoints over HTTP (issue #167, AC 12-14, 17, 27): the value and currency
/// rules, the duplicate-date conflict, the null-body <c>current</c> contract, and route-owned writes.
/// </summary>
public class PropertyEstimatesApiTests
{
    private const string ActorUserId = "property-estimates-actor-id";

    private static readonly string[] ReadWrite =
        [PermissionClaims.PropertiesEstimatesRead, PermissionClaims.PropertiesEstimatesWrite];

    private static readonly DateTime JanFirst = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Post_ValidEstimate_ReturnsCreated_AndIsListedAndCurrent()
    {
        await using var factory = new ApiFactory(ReadWrite);
        var propertyId = await SeedPropertyAsync(factory);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(EstimatesPath(propertyId), EstimateBody(350_000m, JanFirst, "USD"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = (await response.Content.ReadFromJsonAsync<ExistingPropertyEstimate>())!;
        Assert.Equal(propertyId, created.PropertyId);
        Assert.EndsWith(EstimatesPath(propertyId), response.Headers.Location!.ToString(), StringComparison.OrdinalIgnoreCase);

        var listed = Assert.Single((await client.GetFromJsonAsync<List<ExistingPropertyEstimate>>(EstimatesPath(propertyId)))!);
        Assert.Equal(350_000m, listed.Value);
        var current = (await client.GetFromJsonAsync<CurrentPropertyEstimate>(CurrentEstimatePath(propertyId)))!;
        Assert.Equal(350_000m, current.Value);
        Assert.Equal("USD", current.CurrencyCode);
    }

    /// <summary>AC 12 — a negative value is a 400, and nothing is stored.</summary>
    [Fact]
    public async Task Post_NegativeValue_ReturnsBadRequest()
    {
        await using var factory = new ApiFactory(ReadWrite);
        var propertyId = await SeedPropertyAsync(factory);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(EstimatesPath(propertyId), EstimateBody(-1m, JanFirst));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, await CountAsync<PropertyEstimate>(factory));
    }

    /// <summary>AC 13 — a currency other than the property's is a 400 whose detail names the property currency.</summary>
    [Fact]
    public async Task Post_ForeignCurrency_ReturnsBadRequestNamingThePropertyCurrency()
    {
        await using var factory = new ApiFactory(ReadWrite);
        var propertyId = await SeedPropertyAsync(factory, currencyCode: "USD");
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(EstimatesPath(propertyId), EstimateBody(1000m, JanFirst, "EUR"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("USD", await ReadDetailAsync(response) ?? "", StringComparison.Ordinal);
        Assert.Equal(0, await CountAsync<PropertyEstimate>(factory));
    }

    /// <summary>AC 13 — an omitted currency is stored as the property currency.</summary>
    [Fact]
    public async Task Post_OmittedCurrency_IsStoredAsThePropertyCurrency()
    {
        await using var factory = new ApiFactory(ReadWrite);
        var propertyId = await SeedPropertyAsync(factory, currencyCode: "EUR");
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(EstimatesPath(propertyId), EstimateBody(1000m, JanFirst));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("EUR", (await response.Content.ReadFromJsonAsync<ExistingPropertyEstimate>())!.CurrencyCode);
        var stored = await ReadAsync(factory, context => context.PropertyEstimates.AsNoTracking().SingleAsync());
        Assert.Equal("EUR", stored.CurrencyCode);
    }

    /// <summary>AC 14 — a second estimate on the same effective date is a 409; the first is kept.</summary>
    [Fact]
    public async Task Post_DuplicateEffectiveFrom_ReturnsCreatedThenConflict()
    {
        await using var factory = new ApiFactory(ReadWrite);
        var propertyId = await SeedPropertyAsync(factory);
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.Created,
            (await client.PostAsJsonAsync(EstimatesPath(propertyId), EstimateBody(1000m, JanFirst))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict,
            (await client.PostAsJsonAsync(EstimatesPath(propertyId), EstimateBody(2000m, JanFirst))).StatusCode);

        var stored = await ReadAsync(factory, context => context.PropertyEstimates.AsNoTracking().SingleAsync());
        Assert.Equal(1000m, stored.Value);
    }

    /// <summary>
    /// AC 17 — "no estimate in force" is a 200 with a JSON <c>null</c> body: not a 204 (which
    /// <c>Ok(null)</c> would produce) and not a 404.
    /// </summary>
    [Fact]
    public async Task Current_WithNoEstimates_ReturnsOkWithAJsonNullBody()
    {
        await using var factory = new ApiFactory(ReadWrite);
        var propertyId = await SeedPropertyAsync(factory);
        using var client = factory.CreateClient();

        var response = await client.GetAsync(CurrentEstimatePath(propertyId));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("null", (await response.Content.ReadAsStringAsync()).Trim());
    }

    /// <summary>AC 17 — an estimate that only takes effect in the future is not in force yet.</summary>
    [Fact]
    public async Task Current_WithOnlyAFutureEstimate_ReturnsOkWithAJsonNullBody_AndAsOfReachesIt()
    {
        await using var factory = new ApiFactory(ReadWrite);
        var propertyId = await SeedPropertyAsync(factory);
        var future = DateTime.UtcNow.Date.AddYears(1);
        await SeedEstimateAsync(factory, propertyId, 5000m, future);
        using var client = factory.CreateClient();

        var now = await client.GetAsync(CurrentEstimatePath(propertyId));
        Assert.Equal(HttpStatusCode.OK, now.StatusCode);
        Assert.Equal("null", (await now.Content.ReadAsStringAsync()).Trim());

        var later = await client.GetFromJsonAsync<CurrentPropertyEstimate>(
            $"{CurrentEstimatePath(propertyId)}?asOf={future.AddDays(1):yyyy-MM-dd}");
        Assert.Equal(5000m, later!.Value);
    }

    /// <summary>An unknown property is a 404 on every estimate endpoint, distinct from "no estimate".</summary>
    [Fact]
    public async Task UnknownProperty_ReturnsNotFound_OnReadsAndCreate()
    {
        await using var factory = new ApiFactory(ReadWrite);
        await EnsureCreatedAsync(factory);
        using var client = factory.CreateClient();
        var unknown = Guid.NewGuid();

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(EstimatesPath(unknown))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(CurrentEstimatePath(unknown))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.PostAsJsonAsync(EstimatesPath(unknown), EstimateBody(1000m, JanFirst))).StatusCode);
        Assert.Equal(0, await CountAsync<PropertyEstimate>(factory));
    }

    /// <summary>
    /// AC 27 — the owner comes from the route. A body naming another property, and an account, writes
    /// to the routed property only, and nothing reaches <c>AccountEstimates</c>.
    /// </summary>
    [Fact]
    public async Task Post_WithOwnerIdsInTheBody_WritesToTheRoutedPropertyOnly()
    {
        await using var factory = new ApiFactory(ReadWrite);
        var routed = await SeedPropertyAsync(factory, "Routed");
        var other = await SeedPropertyAsync(factory, "Other");
        var accountId = await SeedAccountAsync(factory);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(EstimatesPath(routed), new
        {
            value = 1234m,
            effectiveFrom = JanFirst,
            propertyId = other,
            accountId,
            propertyEstimateId = Guid.NewGuid(),
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        await ReadAsync(factory, async context =>
        {
            var estimate = await context.PropertyEstimates.AsNoTracking().SingleAsync();
            Assert.Equal(routed, estimate.PropertyId);
            Assert.False(await context.PropertyEstimates.AnyAsync(e => e.PropertyId == other));
            Assert.Equal(0, await context.AccountEstimates.CountAsync());
            return true;
        });
    }

    /// <summary>AC 27 — an estimate addressed through a property it does not belong to is a 404 that mutates nothing.</summary>
    [Fact]
    public async Task PutAndDelete_ViaAnotherProperty_ReturnNotFound_AndMutateNothing()
    {
        await using var factory = new ApiFactory(ReadWrite);
        var owner = await SeedPropertyAsync(factory, "Owner");
        var intruder = await SeedPropertyAsync(factory, "Intruder");
        var estimateId = await SeedEstimateAsync(factory, owner, 1000m, JanFirst);
        using var client = factory.CreateClient();

        var put = await client.PutAsJsonAsync(EstimatePath(intruder, estimateId), EstimateBody(9999m, JanFirst.AddDays(1)));
        var delete = await client.DeleteAsync(EstimatePath(intruder, estimateId));

        Assert.Equal(HttpStatusCode.NotFound, put.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, delete.StatusCode);
        var stored = await ReadAsync(factory, context => context.PropertyEstimates.AsNoTracking().SingleAsync());
        Assert.Equal(estimateId, stored.PropertyEstimateId);
        Assert.Equal(owner, stored.PropertyId);
        Assert.Equal(1000m, stored.Value);
        Assert.Equal(JanFirst, stored.EffectiveFrom);
    }

    [Fact]
    public async Task PutAndDelete_ViaTheOwningProperty_Succeed()
    {
        await using var factory = new ApiFactory(ReadWrite);
        var owner = await SeedPropertyAsync(factory);
        var estimateId = await SeedEstimateAsync(factory, owner, 1000m, JanFirst);
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.NoContent,
            (await client.PutAsJsonAsync(EstimatePath(owner, estimateId), EstimateBody(1500m, JanFirst))).StatusCode);
        Assert.Equal(1500m, (await ReadAsync(factory, context => context.PropertyEstimates.AsNoTracking().SingleAsync())).Value);

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync(EstimatePath(owner, estimateId))).StatusCode);
        Assert.Equal(0, await CountAsync<PropertyEstimate>(factory));
    }

    /// <summary>A PUT that moves an estimate onto a date another estimate holds is a 409.</summary>
    [Fact]
    public async Task Put_OntoAnotherEstimatesDate_ReturnsConflict()
    {
        await using var factory = new ApiFactory(ReadWrite);
        var owner = await SeedPropertyAsync(factory);
        await SeedEstimateAsync(factory, owner, 1000m, JanFirst);
        var second = await SeedEstimateAsync(factory, owner, 2000m, JanFirst.AddMonths(6));
        using var client = factory.CreateClient();

        var response = await client.PutAsJsonAsync(EstimatePath(owner, second), EstimateBody(2000m, JanFirst));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static async Task<Guid> SeedAccountAsync(ApiFactory factory)
    {
        var accountId = Guid.NewGuid();
        await ReadAsync(factory, async context =>
        {
            context.Accounts.Add(new Account
            {
                AccountId = accountId,
                Name = "Checking",
                Description = "Test account",
                Opened = DateTime.UtcNow,
                AccountType = Odyssey.Context.AccountType.CheckingAccount,
                CurrencyCode = "USD",
            });
            return await context.SaveChangesAsync();
        });
        return accountId;
    }

    private sealed class ApiFactory(IReadOnlyCollection<string>? permissions)
        : OdysseyApiFactory(permissions, ActorUserId);
}
