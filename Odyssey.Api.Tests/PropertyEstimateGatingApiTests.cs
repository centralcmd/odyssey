using System.Net;
using System.Net.Http.Json;
using Odyssey.Api.Tests.Infrastructure;
using Odyssey.Dtos;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos.Finance;
using Xunit;
using static Odyssey.Api.Tests.PropertyApiTestSupport;

namespace Odyssey.Api.Tests;

/// <summary>
/// The estimate figures the <c>/properties</c> page reads outside the estimate endpoints — the row's
/// estimate count and in-force value, the summary's value tile, and the value sort — follow
/// <c>properties.estimates.read</c>, not <c>properties.read</c> (issue #167). A reader with the property
/// claim alone gets the property and nothing about what it is worth.
/// </summary>
public class PropertyEstimateGatingApiTests
{
    private const string ActorUserId = "property-estimate-gating-actor-id";

    private static readonly DateTime Jan2025 = new(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static readonly string[] ReadAndEstimates =
        [PermissionClaims.PropertiesRead, PermissionClaims.PropertiesEstimatesRead];

    private static readonly string[] ReadOnly = [PermissionClaims.PropertiesRead];

    private static async Task<Guid> SeedValuedPropertyAsync(ApiFactory factory)
    {
        var propertyId = await SeedPropertyAsync(factory, "Storgata 14");
        await SeedEstimateAsync(factory, propertyId, 450_000m, Jan2025);
        await LinkSmartTagAsync(factory, propertyId, await SeedTagAsync(factory, "Home maintenance"));
        return propertyId;
    }

    [Fact]
    public async Task ListAndGet_WithTheEstimatesClaim_CarryTheCountAndTheValueInForce()
    {
        await using var factory = new ApiFactory(ReadAndEstimates);
        var propertyId = await SeedValuedPropertyAsync(factory);
        using var client = factory.CreateClient();

        var row = Assert.Single((await client.GetFromJsonAsync<PagedResult<ExistingProperty>>(PropertiesPath))!.Items);
        var one = (await client.GetFromJsonAsync<ExistingProperty>(PropertyPath(propertyId)))!;

        foreach (var property in new[] { row, one })
        {
            Assert.Equal(1, property.EstimateCount);
            Assert.Equal(450_000m, property.CurrentEstimatedValue);
            Assert.Equal(Jan2025, property.CurrentEstimatedValueEffectiveFrom);
            Assert.Equal(1, property.SmartTagCount);
        }
    }

    [Fact]
    public async Task ListAndGet_WithoutTheEstimatesClaim_LeaveEveryEstimateFigureNull()
    {
        await using var factory = new ApiFactory(ReadOnly);
        var propertyId = await SeedValuedPropertyAsync(factory);
        using var client = factory.CreateClient();

        var row = Assert.Single((await client.GetFromJsonAsync<PagedResult<ExistingProperty>>(PropertiesPath))!.Items);
        var one = (await client.GetFromJsonAsync<ExistingProperty>(PropertyPath(propertyId)))!;

        foreach (var property in new[] { row, one })
        {
            Assert.Null(property.EstimateCount);
            Assert.Null(property.CurrentEstimatedValue);
            Assert.Null(property.CurrentEstimatedValueEffectiveFrom);
            // The smart-tag count is properties.read data, so it stays.
            Assert.Equal(1, property.SmartTagCount);
        }
    }

    /// <summary>
    /// Ordering by value would rank the properties by worth — estimate data by another route — so the
    /// sort is refused outright rather than quietly falling back to another order the caller did not ask for.
    /// </summary>
    [Fact]
    public async Task SortByValue_WithoutTheEstimatesClaim_IsForbidden()
    {
        await using var factory = new ApiFactory(ReadOnly);
        await SeedValuedPropertyAsync(factory);
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"{PropertiesPath}?sortBy=Value")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"{PropertiesPath}?sortBy=Name")).StatusCode);
    }

    [Fact]
    public async Task SortByValue_WithTheEstimatesClaim_Succeeds()
    {
        await using var factory = new ApiFactory(ReadAndEstimates);
        await SeedValuedPropertyAsync(factory);
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"{PropertiesPath}?sortBy=Value")).StatusCode);
    }

    [Fact]
    public async Task Summary_WithTheEstimatesClaim_SumsTheOwnedValuePerCurrency()
    {
        await using var factory = new ApiFactory(ReadAndEstimates);
        await SeedValuedPropertyAsync(factory);
        await SeedPropertyAsync(factory, "Old car", PropertyType.Vehicle, archived: true);
        using var client = factory.CreateClient();

        var summary = (await client.GetFromJsonAsync<PropertySummary>($"{SummaryPath}?baseCurrency=USD"))!;

        Assert.Equal(2, summary.TotalProperties);
        Assert.Equal(1, summary.ByStatus.Owned);
        Assert.Equal(1, summary.ByStatus.Archived);
        Assert.Equal(0, summary.ByType.Single(t => t.Type == PropertyType.Vehicle).Count);
        var value = Assert.IsType<PropertyValueSummary>(summary.Value);
        Assert.Equal("USD", value.BaseCurrency);
        Assert.Equal(450_000m, value.Total);
        Assert.Equal(450_000m, Assert.Single(value.ByCurrency).Total);
    }

    [Fact]
    public async Task Summary_WithoutTheEstimatesClaim_KeepsTheCountsAndDropsTheValue()
    {
        await using var factory = new ApiFactory(ReadOnly);
        await SeedValuedPropertyAsync(factory);
        using var client = factory.CreateClient();

        var summary = (await client.GetFromJsonAsync<PropertySummary>(SummaryPath))!;

        Assert.Equal(1, summary.TotalProperties);
        Assert.Null(summary.Value);
    }

    [Fact]
    public async Task Summary_RejectsABaseCurrencyLongerThanACode()
    {
        await using var factory = new ApiFactory(ReadAndEstimates);
        await EnsureCreatedAsync(factory);
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"{SummaryPath}?baseCurrency=DOLLARS")).StatusCode);
    }

    private sealed class ApiFactory(IReadOnlyCollection<string>? permissions)
        : OdysseyApiFactory(permissions, ActorUserId);
}
