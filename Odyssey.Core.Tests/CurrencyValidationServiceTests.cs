using Odyssey.Core;
using Odyssey.Core.Finance;
using Xunit;

namespace Odyssey.Core.Tests;

public class CurrencyValidationServiceTests
{
    [Theory]
    [InlineData("usd", "USD")]
    [InlineData("  EUR  ", "EUR")]
    [InlineData("sek", "SEK")]
    [InlineData("  jpy  ", "JPY")]
    public void Normalize_TrimsAndUppercases(string input, string expected)
    {
        Assert.Equal(expected, CurrencyValidationService.Normalize(input));
    }

    [Theory]
    [InlineData("USD", true)]
    [InlineData("EUR", true)]
    [InlineData("SEK", true)]
    [InlineData("JPY", true)]
    public void IsIsoFormat_ReturnsTrueForValidCodes(string code, bool expected)
    {
        Assert.Equal(expected, CurrencyValidationService.IsIsoFormat(code));
    }

    [Theory]
    [InlineData("US", false)]
    [InlineData("USDX", false)]
    [InlineData("usd", false)]
    [InlineData("123", false)]
    [InlineData("", false)]
    [InlineData("U$D", false)]
    public void IsIsoFormat_ReturnsFalseForInvalidCodes(string code, bool expected)
    {
        Assert.Equal(expected, CurrencyValidationService.IsIsoFormat(code));
    }

    [Fact]
    public async Task EnsureSupportedAndActive_ThrowsForNonIsoCode()
    {
        await using var context = TestContextFactory.Create();

        await Assert.ThrowsAsync<DomainValidationException>(() =>
            CurrencyValidationService.EnsureSupportedAndActive(context, "USDX", "currency"));
    }

    [Fact]
    public async Task EnsureSupportedAndActive_ThrowsForUnsupportedCurrency()
    {
        await using var context = TestContextFactory.Create();

        await Assert.ThrowsAsync<DomainValidationException>(() =>
            CurrencyValidationService.EnsureSupportedAndActive(context, "JPY", "currency"));
    }

    [Fact]
    public async Task EnsureSupportedAndActive_ThrowsForArchivedCurrency()
    {
        await using var context = TestContextFactory.Create();

        var usd = context.Currencies.First(c => c.CurrencyCode == "USD");
        usd.Archived = DateTime.UtcNow;
        await context.SaveChangesAsync();

        await Assert.ThrowsAsync<DomainValidationException>(() =>
            CurrencyValidationService.EnsureSupportedAndActive(context, "USD", "currency"));
    }

    [Fact]
    public async Task EnsureSupportedAndActive_SucceedsForActiveCurrency()
    {
        await using var context = TestContextFactory.Create();

        var ex = await Record.ExceptionAsync(() =>
            CurrencyValidationService.EnsureSupportedAndActive(context, "USD", "currency"));

        Assert.Null(ex);
    }

    [Fact]
    public async Task EnsureSupportedAndActive_AcceptsLowercaseInput()
    {
        await using var context = TestContextFactory.Create();

        var ex = await Record.ExceptionAsync(() =>
            CurrencyValidationService.EnsureSupportedAndActive(context, "eur", "currency"));

        Assert.Null(ex);
    }
}
