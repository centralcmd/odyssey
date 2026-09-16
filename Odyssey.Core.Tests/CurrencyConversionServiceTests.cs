using Odyssey.Context;
using Odyssey.Dtos.Finance;
using Xunit;
using Odyssey.Core.Finance;

namespace Odyssey.Core.Tests;

public class CurrencyConversionServiceTests
{
    private static async Task SeedRate(ExchangeRateService service, string from, string to, decimal rate, DateTime asOf)
    {
        await service.Create(new NewExchangeRate { FromCurrencyCode = from, ToCurrencyCode = to, Rate = rate, AsOf = asOf });
    }

    [Fact]
    public async Task Convert_SameCurrency_ReturnsAmount_WithoutRate()
    {
        await using var context = TestContextFactory.Create();
        var conversion = new CurrencyConversionService(context);

        var result = await conversion.ConvertAsync(123.45m, "usd", "USD");

        Assert.Equal(123.45m, result);
    }

    [Fact]
    public async Task Convert_UsesLatestRate()
    {
        await using var context = TestContextFactory.Create();
        var rateService = new ExchangeRateService(context);
        var conversion = new CurrencyConversionService(context);

        await SeedRate(rateService, "USD", "EUR", 0.90m, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        await SeedRate(rateService, "USD", "EUR", 0.95m, new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc));

        var result = await conversion.ConvertAsync(100m, "USD", "EUR");

        Assert.Equal(95m, result);
    }

    [Fact]
    public async Task Convert_MissingRate_ReturnsNull()
    {
        await using var context = TestContextFactory.Create();
        var conversion = new CurrencyConversionService(context);

        var result = await conversion.ConvertAsync(100m, "USD", "EUR");

        Assert.Null(result);
    }

    [Fact]
    public async Task Convert_DoesNotInvert_ReverseRate()
    {
        await using var context = TestContextFactory.Create();
        var rateService = new ExchangeRateService(context);
        var conversion = new CurrencyConversionService(context);

        // Only a EUR->USD rate exists; a USD->EUR request must NOT be satisfied by inversion.
        await SeedRate(rateService, "EUR", "USD", 1.1m, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        var result = await conversion.ConvertAsync(100m, "USD", "EUR");

        Assert.Null(result);
    }

    [Fact]
    public async Task GetLatestRatesTo_ReturnsLatestPerPair_AndSkipsSameCurrency()
    {
        await using var context = TestContextFactory.Create();
        var rateService = new ExchangeRateService(context);
        var conversion = new CurrencyConversionService(context);

        await SeedRate(rateService, "USD", "SEK", 9m, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        await SeedRate(rateService, "USD", "SEK", 10m, new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc));
        await SeedRate(rateService, "EUR", "SEK", 11m, new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc));

        var map = await conversion.GetLatestRatesToAsync("SEK", new[] { "USD", "EUR", "SEK" });

        Assert.Equal(10m, map["USD"]); // latest USD->SEK
        Assert.Equal(11m, map["EUR"]);
        Assert.False(map.ContainsKey("SEK")); // same-currency is omitted (1:1 handled by caller)
    }

    [Fact]
    public async Task Convert_WhenTwoRatesShareAsOf_UsesTheMoreRecentlyCreatedOne()
    {
        // Same effective date, entered twice (a same-day correction). The CreatedAt tiebreak in
        // OrderByDescending(AsOf).ThenByDescending(CreatedAt) must pick the later-inserted row.
        await using var context = TestContextFactory.Create();
        var asOf = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        context.ExchangeRates.AddRange(
            new ExchangeRate { FromCurrencyCode = "USD", ToCurrencyCode = "EUR", Rate = 0.90m, AsOf = asOf, CreatedAt = new DateTime(2026, 1, 1, 9, 0, 0, DateTimeKind.Utc) },
            new ExchangeRate { FromCurrencyCode = "USD", ToCurrencyCode = "EUR", Rate = 0.95m, AsOf = asOf, CreatedAt = new DateTime(2026, 1, 1, 17, 0, 0, DateTimeKind.Utc) });
        await context.SaveChangesAsync();

        var conversion = new CurrencyConversionService(context);

        var result = await conversion.ConvertAsync(100m, "USD", "EUR");

        Assert.Equal(95m, result); // the 17:00 correction wins over the 09:00 entry
    }

    [Fact]
    public async Task GetLatestRatesTo_WhenTwoRatesShareAsOf_UsesTheMoreRecentlyCreatedOne()
    {
        await using var context = TestContextFactory.Create();
        var asOf = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        context.ExchangeRates.AddRange(
            new ExchangeRate { FromCurrencyCode = "USD", ToCurrencyCode = "SEK", Rate = 9m, AsOf = asOf, CreatedAt = new DateTime(2026, 1, 1, 17, 0, 0, DateTimeKind.Utc) },
            new ExchangeRate { FromCurrencyCode = "USD", ToCurrencyCode = "SEK", Rate = 8m, AsOf = asOf, CreatedAt = new DateTime(2026, 1, 1, 9, 0, 0, DateTimeKind.Utc) });
        await context.SaveChangesAsync();

        var conversion = new CurrencyConversionService(context);

        var map = await conversion.GetLatestRatesToAsync("SEK", new[] { "USD" });

        Assert.Equal(9m, map["USD"]); // the 17:00 row wins regardless of insertion order
    }

    // ── GetRateTimelineToAsync (issue #90 §5.5) ───────────────────────────────────────────────

    private static ExchangeRate Rate(string from, string to, decimal rate, DateTime asOf, DateTime? created = null) => new()
    {
        FromCurrencyCode = from,
        ToCurrencyCode = to,
        Rate = rate,
        AsOf = asOf,
        CreatedAt = created ?? asOf,
    };

    /// <summary>
    /// The timeline returns the in-window rows plus, per currency, the single latest row before the
    /// window — the carry-in that serves the first point of a series.
    /// </summary>
    [Fact]
    public async Task GetRateTimelineTo_ReturnsInWindowRowsPlusOneCarryInPerCurrency()
    {
        await using var context = TestContextFactory.Create();
        var windowStart = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        context.ExchangeRates.AddRange(
            // Three before the window: only the latest may carry in.
            Rate("EUR", "USD", 1.0m, windowStart.AddDays(-90)),
            Rate("EUR", "USD", 1.1m, windowStart.AddDays(-60)),
            Rate("EUR", "USD", 1.2m, windowStart.AddDays(-30)),
            Rate("EUR", "USD", 1.3m, windowStart.AddDays(10)),
            Rate("EUR", "USD", 1.4m, windowStart.AddDays(20)),
            // A second currency gets its own carry-in, not the first one's.
            Rate("CHF", "USD", 2.0m, windowStart.AddDays(-45)),
            Rate("CHF", "USD", 2.1m, windowStart.AddDays(15)),
            // Past the upper bound: out entirely.
            Rate("EUR", "USD", 9m, windowStart.AddDays(200)));
        await context.SaveChangesAsync();

        var timeline = await new CurrencyConversionService(context)
            .GetRateTimelineToAsync("USD", ["EUR", "CHF"], windowStart, windowStart.AddDays(100));

        Assert.Equal([1.2m, 1.3m, 1.4m], timeline["EUR"].Select(point => point.Rate));
        Assert.Equal([2.0m, 2.1m], timeline["CHF"].Select(point => point.Rate));
    }

    /// <summary>
    /// The carry-in is a join against a grouped MAX, so a TIE on <c>AsOf</c> brings back both rows
    /// rather than one. The method's contract is that the <c>(AsOf, UpdatedAt ?? CreatedAt)</c>
    /// ordering then resolves them the same way every other rate lookup does — the more recently
    /// entered correction wins.
    ///
    /// <para>
    /// Untested until review caught it: the doc comment promised this and nothing pinned it, so the
    /// rewrite from a correlated subquery to the grouped-MAX join could have inverted the tie-break
    /// silently. A consumer walking the timeline forward takes the LAST entry below its bound, so the
    /// winner has to sort last.
    /// </para>
    /// </summary>
    [Fact]
    public async Task GetRateTimelineTo_WhenTwoCarryInRatesShareAsOf_OrdersTheMoreRecentlyCreatedLast()
    {
        await using var context = TestContextFactory.Create();
        var windowStart = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var tied = windowStart.AddDays(-30);

        context.ExchangeRates.AddRange(
            Rate("EUR", "USD", 8m, tied, created: new DateTime(2025, 12, 2, 17, 0, 0, DateTimeKind.Utc)),
            Rate("EUR", "USD", 7m, tied, created: new DateTime(2025, 12, 2, 9, 0, 0, DateTimeKind.Utc)));
        await context.SaveChangesAsync();

        var timeline = await new CurrencyConversionService(context)
            .GetRateTimelineToAsync("USD", ["EUR"], windowStart, windowStart.AddDays(100));

        // Both tied rows come back; the 17:00 entry sorts last, so a forward walk lands on it.
        Assert.Equal([7m, 8m], timeline["EUR"].Select(point => point.Rate));
    }

    /// <summary>A currency with nothing before the window gets no carry-in rather than someone else's.</summary>
    [Fact]
    public async Task GetRateTimelineTo_OmitsACurrencyWithNoRowsAtAll()
    {
        await using var context = TestContextFactory.Create();
        var windowStart = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        context.ExchangeRates.Add(Rate("EUR", "USD", 1.1m, windowStart.AddDays(-10)));
        await context.SaveChangesAsync();

        var timeline = await new CurrencyConversionService(context)
            .GetRateTimelineToAsync("USD", ["EUR", "CHF"], windowStart, windowStart.AddDays(100));

        Assert.True(timeline.ContainsKey("EUR"));
        Assert.False(timeline.ContainsKey("CHF"));
    }

    /// <summary>The same-currency pair is omitted, as everywhere else — callers treat it as 1:1.</summary>
    [Fact]
    public async Task GetRateTimelineTo_OmitsTheSameCurrencyPair()
    {
        await using var context = TestContextFactory.Create();
        var windowStart = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        var timeline = await new CurrencyConversionService(context)
            .GetRateTimelineToAsync("USD", ["USD"], windowStart, windowStart.AddDays(100));

        Assert.Empty(timeline);
    }
}
