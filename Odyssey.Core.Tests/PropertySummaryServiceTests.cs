using Odyssey.Context;
using Odyssey.Core.Finance;
using Odyssey.Dtos.Finance;
using Xunit;

namespace Odyssey.Core.Tests;

/// <summary>
/// The <c>/properties</c> header overview (issue #167): counts by type and derived status, and the
/// owned properties' in-force estimates summed per currency with a converted total that NAMES any
/// currency it could not convert rather than folding it in at 1:1.
/// </summary>
public class PropertySummaryServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static DateTime Utc(int year, int month, int day) => new(year, month, day, 0, 0, 0, DateTimeKind.Utc);

    private static PropertySummaryService Summary(OdysseyContext context) =>
        new(context, new CurrencyConversionService(context), new FixedTimeProvider(Now));

    private static async Task<Guid> PropertyAsync(
        OdysseyContext context, NewProperty body, decimal? estimate = null, DateTime? disposed = null, bool archived = false)
    {
        body.DisposedDate = disposed;
        body.Archived = archived;
        var id = (await new PropertyService(context, TestContextFactory.ContactLookup(context), new FixedTimeProvider(Now)).Create(body, userId: null)).PropertyId;
        if (estimate is { } value)
        {
            context.PropertyEstimates.Add(new PropertyEstimate
            {
                PropertyId = id, Value = value, CurrencyCode = body.CurrencyCode, EffectiveFrom = Utc(2026, 1, 1), CreatedAtUtc = Utc(2026, 1, 1),
            });
            await context.SaveChangesAsync();
        }

        return id;
    }

    [Fact]
    public async Task Counts_TypeLeavesOutArchived_StatusPartitionsTheWholeFile()
    {
        await using var context = TestContextFactory.Create();
        await PropertyAsync(context, PropertyTestData.House("A"));
        await PropertyAsync(context, PropertyTestData.House("B"), archived: true);
        await PropertyAsync(context, PropertyTestData.Car("C"), disposed: Utc(2025, 1, 1));
        await PropertyAsync(context, PropertyTestData.Car("D"), disposed: Utc(2027, 1, 1));

        var summary = await Summary(context).GetAsync(null, includeValue: false);

        Assert.Equal(4, summary.TotalProperties);
        Assert.Equal(1, summary.ByType.Single(t => t.Type == PropertyType.RealEstate).Count);
        Assert.Equal(2, summary.ByType.Single(t => t.Type == PropertyType.Vehicle).Count);
        // A disposal dated in the future is still owned until then.
        Assert.Equal(2, summary.ByStatus.Owned);
        Assert.Equal(1, summary.ByStatus.Disposed);
        Assert.Equal(1, summary.ByStatus.Archived);
        Assert.Null(summary.Value);
    }

    [Fact]
    public async Task Value_SumsOwnedPropertiesPerCurrency_AndConvertsTheTotal()
    {
        await using var context = TestContextFactory.Create();
        await PropertyAsync(context, PropertyTestData.House("A", "SEK"), estimate: 1_000_000m);
        await PropertyAsync(context, PropertyTestData.Car("B", "SEK"), estimate: 200_000m);
        await PropertyAsync(context, PropertyTestData.House("C", "EUR"), estimate: 50_000m);
        // Not owned, so not summed however large.
        await PropertyAsync(context, PropertyTestData.House("D", "SEK"), estimate: 9_000_000m, disposed: Utc(2025, 1, 1));
        context.ExchangeRates.Add(new ExchangeRate
        {
            FromCurrencyCode = "EUR", ToCurrencyCode = "SEK", Rate = 11.5m, AsOf = Utc(2026, 1, 1), CreatedAt = Utc(2026, 1, 1),
        });
        await context.SaveChangesAsync();

        var value = (await Summary(context).GetAsync("SEK", includeValue: true)).Value!;

        Assert.Equal("SEK", value.BaseCurrency);
        Assert.Equal(["EUR", "SEK"], value.ByCurrency.Select(c => c.CurrencyCode));
        Assert.Equal(1_200_000m, value.ByCurrency.Single(c => c.CurrencyCode == "SEK").Total);
        Assert.Equal(2, value.ByCurrency.Single(c => c.CurrencyCode == "SEK").Count);
        Assert.Equal(1_200_000m + 50_000m * 11.5m, value.Total);
        Assert.Empty(value.UnconvertedCurrencies);
    }

    [Fact]
    public async Task Value_NamesACurrencyWithNoRate_AndLeavesItOutOfTheTotal()
    {
        await using var context = TestContextFactory.Create();
        await PropertyAsync(context, PropertyTestData.House("A", "SEK"), estimate: 1_000_000m);
        await PropertyAsync(context, PropertyTestData.House("B", "USD"), estimate: 400_000m);

        var value = (await Summary(context).GetAsync("SEK", includeValue: true)).Value!;

        Assert.Equal(1_000_000m, value.Total);
        Assert.Equal(["USD"], value.UnconvertedCurrencies);
        // The row is still listed, in its own currency: only the total leaves it out.
        Assert.Equal(400_000m, value.ByCurrency.Single(c => c.CurrencyCode == "USD").Total);
    }

    /// <summary>
    /// The properties behind an unconverted currency are named, so the page can point at the record.
    /// Only the set the total actually left out: an owned property with an estimate in force. A disposed
    /// one in the same currency was never in the total, and one without an estimate has no value.
    /// </summary>
    [Fact]
    public async Task Value_NamesEachOwnedPropertyLeftOutOfTheTotal()
    {
        await using var context = TestContextFactory.Create();
        await PropertyAsync(context, PropertyTestData.House("Home", "SEK"), estimate: 1_000_000m);
        var cabin = await PropertyAsync(context, PropertyTestData.House("Cabin", "USD"), estimate: 900_000m);
        var boat = await PropertyAsync(context, PropertyTestData.Car("Boat", "EUR"), estimate: 30_000m);
        await PropertyAsync(context, PropertyTestData.House("Sold flat", "USD"), estimate: 2_000_000m, disposed: Utc(2025, 1, 1));
        await PropertyAsync(context, PropertyTestData.Car("Unvalued", "USD"));

        var value = (await Summary(context).GetAsync("SEK", includeValue: true)).Value!;

        Assert.Equal(["EUR", "USD"], value.UnconvertedCurrencies);
        Assert.Equal([boat, cabin], value.UnconvertedProperties.Select(p => p.PropertyId));
        Assert.Equal(["Boat", "Cabin"], value.UnconvertedProperties.Select(p => p.Name));
        Assert.Equal(["EUR", "USD"], value.UnconvertedProperties.Select(p => p.CurrencyCode));
    }

    /// <summary>Two properties sharing a name keep a stable order, by id, so the panel does not reshuffle.</summary>
    [Fact]
    public async Task Value_OrdersUnconvertedPropertiesWithEqualNamesById()
    {
        await using var context = TestContextFactory.Create();
        var first = await PropertyAsync(context, PropertyTestData.House("Cabin", "EUR"), estimate: 1m);
        var second = await PropertyAsync(context, PropertyTestData.House("cabin", "EUR"), estimate: 1m);

        var value = (await Summary(context).GetAsync("SEK", includeValue: true)).Value!;

        Assert.Equal(new[] { first, second }.Order(), value.UnconvertedProperties.Select(p => p.PropertyId));
    }

    [Fact]
    public async Task Value_BlankBase_PicksTheCurrencyMostOwnedPropertiesUse()
    {
        await using var context = TestContextFactory.Create();
        await PropertyAsync(context, PropertyTestData.House("A", "EUR"), estimate: 10m);
        await PropertyAsync(context, PropertyTestData.House("B", "EUR"), estimate: 10m);
        await PropertyAsync(context, PropertyTestData.House("C", "SEK"), estimate: 10m);

        var value = (await Summary(context).GetAsync(null, includeValue: true)).Value!;

        Assert.Equal("EUR", value.BaseCurrency);
    }

    [Fact]
    public async Task Value_WithNothingInForce_HasNoRowsAndNoTotal()
    {
        await using var context = TestContextFactory.Create();
        await PropertyAsync(context, PropertyTestData.House("A"));

        var value = (await Summary(context).GetAsync("SEK", includeValue: true)).Value!;

        Assert.Empty(value.ByCurrency);
        Assert.Null(value.Total);
    }
}
