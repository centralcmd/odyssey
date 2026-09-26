using Odyssey.Context;
using Odyssey.Core.Finance;
using Odyssey.Dtos.Finance;
using Xunit;

namespace Odyssey.Core.Tests;

/// <summary>
/// The per-row figures the <c>/properties</c> cards read (issue #167): the smart-tag count for every
/// reader, and the estimate count and in-force value only for a reader holding
/// <c>properties.estimates.read</c> — which <see cref="PropertyService"/> is told through
/// <c>includeEstimates</c>.
/// </summary>
public class PropertyListEnrichmentTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static DateTime Utc(int year, int month, int day) => new(year, month, day, 0, 0, 0, DateTimeKind.Utc);

    private static async Task<(PropertyService Service, Guid PropertyId)> SeedAsync(OdysseyContext context)
    {
        var service = new PropertyService(context, new FixedTimeProvider(Now));
        var propertyId = (await service.Create(PropertyTestData.House())).PropertyId;

        context.PropertyEstimates.AddRange(
            new PropertyEstimate { PropertyId = propertyId, Value = 3_000_000m, CurrencyCode = "SEK", EffectiveFrom = Utc(2025, 1, 1), CreatedAtUtc = Utc(2025, 1, 1) },
            // Two entries on one date: the newer-created one is in force.
            new PropertyEstimate { PropertyId = propertyId, Value = 3_200_000m, CurrencyCode = "SEK", EffectiveFrom = Utc(2026, 1, 1), CreatedAtUtc = Utc(2026, 1, 2) },
            new PropertyEstimate { PropertyId = propertyId, Value = 3_150_000m, CurrencyCode = "SEK", EffectiveFrom = Utc(2026, 1, 1), CreatedAtUtc = Utc(2026, 1, 1) },
            // Scheduled: counted, never in force today.
            new PropertyEstimate { PropertyId = propertyId, Value = 9_999_999m, CurrencyCode = "SEK", EffectiveFrom = Utc(2027, 1, 1), CreatedAtUtc = Utc(2026, 5, 1) });

        var tag = new TransactionTag { Name = "Home maintenance" };
        context.TransactionTags.Add(tag);
        context.PropertySmartTags.Add(new PropertySmartTag { PropertyId = propertyId, TransactionTag = tag, AddedAt = Utc(2026, 2, 1) });
        await context.SaveChangesAsync();

        return (service, propertyId);
    }

    [Fact]
    public async Task List_WithEstimates_CarriesTheCountsAndTheValueInForceNow()
    {
        await using var context = TestContextFactory.Create();
        var (service, _) = await SeedAsync(context);

        var row = Assert.Single((await service.ListAsync(new PropertiesQueryParams(), includeEstimates: true)).Items);

        Assert.Equal(1, row.SmartTagCount);
        Assert.Equal(4, row.EstimateCount);
        Assert.Equal(3_200_000m, row.CurrentEstimatedValue);
        Assert.Equal(Utc(2026, 1, 1), row.CurrentEstimatedValueEffectiveFrom);
    }

    [Fact]
    public async Task List_WithoutEstimates_LeavesEveryEstimateFigureNull_ButKeepsTheSmartTagCount()
    {
        await using var context = TestContextFactory.Create();
        var (service, _) = await SeedAsync(context);

        var row = Assert.Single((await service.ListAsync(new PropertiesQueryParams(), includeEstimates: false)).Items);

        Assert.Equal(1, row.SmartTagCount);
        Assert.Null(row.EstimateCount);
        Assert.Null(row.CurrentEstimatedValue);
        Assert.Null(row.CurrentEstimatedValueEffectiveFrom);
    }

    [Fact]
    public async Task Get_FollowsTheSameGate()
    {
        await using var context = TestContextFactory.Create();
        var (service, propertyId) = await SeedAsync(context);

        var withEstimates = await service.Get(propertyId, includeEstimates: true);
        var without = await service.Get(propertyId, includeEstimates: false);

        Assert.Equal(3_200_000m, withEstimates!.CurrentEstimatedValue);
        Assert.Equal(4, withEstimates.EstimateCount);
        Assert.Null(without!.CurrentEstimatedValue);
        Assert.Null(without.EstimateCount);
        Assert.Equal(1, without.SmartTagCount);
    }

    [Fact]
    public async Task A_PropertyWithOnlyAScheduledEstimate_HasACountButNoValue()
    {
        await using var context = TestContextFactory.Create();
        var service = new PropertyService(context, new FixedTimeProvider(Now));
        var propertyId = (await service.Create(PropertyTestData.Car())).PropertyId;
        context.PropertyEstimates.Add(new PropertyEstimate
        {
            PropertyId = propertyId, Value = 250_000m, CurrencyCode = "SEK", EffectiveFrom = Utc(2026, 12, 1), CreatedAtUtc = Utc(2026, 5, 1),
        });
        await context.SaveChangesAsync();

        var row = (await service.Get(propertyId, includeEstimates: true))!;

        Assert.Equal(1, row.EstimateCount);
        Assert.Null(row.CurrentEstimatedValue);
        Assert.Equal(0, row.SmartTagCount);
    }
}
