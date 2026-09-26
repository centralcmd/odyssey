using Odyssey.Core;
using Odyssey.Core.Finance;
using Odyssey.Dtos.Finance;
using Xunit;

namespace Odyssey.Core.Tests;

/// <summary>
/// Fast coverage for <see cref="PropertyEstimateService"/> (issue #167): the scalar rules it duplicates
/// from the account service, the two query rules it shares through <see cref="EstimateEffectiveDating"/>,
/// and owner scoping on update/delete (AC 27).
/// </summary>
public class PropertyEstimateServiceTests
{
    private static readonly DateTime Jan1 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static async Task<Guid> SeedProperty(Odyssey.Context.OdysseyContext context, string name = "Storgata 14", string currency = "SEK") =>
        (await new PropertyService(context).Create(PropertyTestData.House(name, currency), userId: null)).PropertyId;

    private static NewPropertyEstimate Estimate(decimal value, DateTime effectiveFrom, string? currency = null) =>
        new() { Value = value, EffectiveFrom = effectiveFrom, CurrencyCode = currency };

    [Fact]
    public async Task Create_OmittedCurrency_DefaultsToThePropertyCurrency()
    {
        await using var context = TestContextFactory.Create();
        var propertyId = await SeedProperty(context, currency: "EUR");

        var created = await new PropertyEstimateService(context).Create(propertyId, Estimate(350000m, Jan1));

        Assert.Equal("EUR", created.CurrencyCode);
        Assert.Equal(propertyId, created.PropertyId);
    }

    [Fact]
    public async Task Create_NegativeValue_AndForeignCurrency_AreRejected()
    {
        await using var context = TestContextFactory.Create();
        var propertyId = await SeedProperty(context);
        var service = new PropertyEstimateService(context);

        await Assert.ThrowsAsync<DomainValidationException>(() => service.Create(propertyId, Estimate(-1m, Jan1)));
        var mismatch = await Assert.ThrowsAsync<DomainValidationException>(
            () => service.Create(propertyId, Estimate(1m, Jan1, "EUR")));
        Assert.Contains("'SEK'", mismatch.Message);
        Assert.Empty(context.PropertyEstimates);
    }

    [Fact]
    public async Task Create_SameEffectiveFrom_IsAConflict_ButUpdatingInPlaceIsNot()
    {
        await using var context = TestContextFactory.Create();
        var propertyId = await SeedProperty(context);
        var service = new PropertyEstimateService(context);

        var first = await service.Create(propertyId, Estimate(1m, Jan1));
        await Assert.ThrowsAsync<DomainConflictException>(() => service.Create(propertyId, Estimate(2m, Jan1)));

        // The row being updated is excluded from its own conflict check.
        Assert.True(await service.Update(propertyId, first.PropertyEstimateId, Estimate(3m, Jan1)));
    }

    [Fact]
    public async Task Create_SameEffectiveFromOnAnotherProperty_IsNotAConflict()
    {
        await using var context = TestContextFactory.Create();
        var service = new PropertyEstimateService(context);
        var a = await SeedProperty(context, "A");
        var b = await SeedProperty(context, "B");

        await service.Create(a, Estimate(1m, Jan1));
        await service.Create(b, Estimate(1m, Jan1));

        Assert.Equal(2, context.PropertyEstimates.Count());
    }

    [Fact]
    public async Task Create_UnknownProperty_IsNotFound()
    {
        await using var context = TestContextFactory.Create();

        await Assert.ThrowsAsync<DomainNotFoundException>(
            () => new PropertyEstimateService(context).Create(Guid.NewGuid(), Estimate(1m, Jan1)));
    }

    [Fact]
    public async Task GetCurrent_ResolvesTheGreatestEffectiveFromOnOrBeforeTheCutoff()
    {
        await using var context = TestContextFactory.Create();
        var propertyId = await SeedProperty(context);
        var service = new PropertyEstimateService(context);
        await service.Create(propertyId, Estimate(100m, Jan1));
        await service.Create(propertyId, Estimate(200m, Jan1.AddMonths(6)));

        Assert.Null(await service.GetCurrent(propertyId, Jan1.AddDays(-1)));
        Assert.Equal(100m, (await service.GetCurrent(propertyId, Jan1))!.Value);
        Assert.Equal(100m, (await service.GetCurrent(propertyId, Jan1.AddMonths(6).AddDays(-1)))!.Value);
        Assert.Equal(200m, (await service.GetCurrent(propertyId, Jan1.AddYears(1)))!.Value);
    }

    [Fact]
    public async Task GetHistory_IsNewestFirst_CutOffAtAsOf_AndNullForAnUnknownProperty()
    {
        await using var context = TestContextFactory.Create();
        var propertyId = await SeedProperty(context);
        var service = new PropertyEstimateService(context);
        await service.Create(propertyId, Estimate(100m, Jan1));
        await service.Create(propertyId, Estimate(200m, Jan1.AddMonths(6)));

        Assert.Equal([200m, 100m], (await service.GetHistory(propertyId))!.Select(e => e.Value));
        Assert.Equal([100m], (await service.GetHistory(propertyId, Jan1.AddMonths(1)))!.Select(e => e.Value));
        Assert.Null(await service.GetHistory(Guid.NewGuid()));
    }

    [Fact]
    public async Task UpdateAndDelete_ScopeOnTheEstimateAndThePropertyTogether()
    {
        await using var context = TestContextFactory.Create();
        var service = new PropertyEstimateService(context);
        var owner = await SeedProperty(context, "Owner");
        var other = await SeedProperty(context, "Other");
        var estimate = await service.Create(owner, Estimate(100m, Jan1));

        // Routed through the wrong property: nothing is found and nothing moves.
        Assert.False(await service.Update(other, estimate.PropertyEstimateId, Estimate(999m, Jan1.AddDays(1))));
        Assert.False(await service.Delete(other, estimate.PropertyEstimateId));

        var stored = Assert.Single(context.PropertyEstimates);
        Assert.Equal(owner, stored.PropertyId);
        Assert.Equal(100m, stored.Value);
    }
}
