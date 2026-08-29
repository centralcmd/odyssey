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
}
