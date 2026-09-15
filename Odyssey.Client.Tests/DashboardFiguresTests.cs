using System.Globalization;
using Odyssey.Client.Pages.Finance;
using Odyssey.Dtos.Finance;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// The dashboard's money formatting and advisory wording — the branches
/// <see cref="DashboardNetWorthSourceTests"/> cannot reach.
///
/// <para>
/// Those lints prove the page asks the server for its net worth. They cannot prove the answer is then
/// rendered in the right currency, or that the advisory names the right pair of currencies: a
/// <c>Symbol</c>/<c>MinorUnits</c> swap, or a transposed FROM/TO, passes every one of them. Hence pure
/// functions and real assertions, per <see cref="AllocationLegend"/>'s precedent — <c>Home</c> itself
/// cannot be rendered in a test, since its <c>OnInitializedAsync</c> early-returns off the browser and
/// it has no <c>InteractiveCheck</c> seam.
/// </para>
/// </summary>
public class DashboardFiguresTests
{
    private static ExistingCurrency Currency(string code, string symbol, int minorUnits) =>
        new() { CurrencyCode = code, Name = code, Symbol = symbol, MinorUnits = minorUnits };

    // ── Currency resolution ──

    [Fact]
    public void MoneyFormat_UsesTheCurrencysSymbolAndMinorUnits()
    {
        var format = DashboardFigures.MoneyFormat("NOK", Currency("NOK", "kr", 2));

        Assert.Equal("kr", format.CurrencySymbol);
        Assert.Equal(2, format.CurrencyDecimalDigits);
    }

    /// <summary>
    /// Minor units are not always 2, and they are the half of the pair a symbol-only assertion would
    /// miss. JPY has none — formatting a yen figure with two decimals invents precision the currency
    /// does not have.
    /// </summary>
    [Fact]
    public void MoneyFormat_HonoursANonDefaultMinorUnits()
    {
        var format = DashboardFigures.MoneyFormat("JPY", Currency("JPY", "¥", 0));

        Assert.Equal("¥", format.CurrencySymbol);
        Assert.Equal(0, format.CurrencyDecimalDigits);

        // Asserted through the format's own separator rather than a literal: the group separator is
        // culture-supplied, so a literal expectation would pin the test host's culture, not the rule.
        var rendered = 1234.56m.ToString("C", format);
        Assert.DoesNotContain(format.CurrencyDecimalSeparator, rendered, StringComparison.Ordinal);
    }

    /// <summary>
    /// A known code whose reference row is missing or carries no symbol falls back to the CODE. Not
    /// to "$": a wrong sigil misreports the denomination outright, where the bare code is merely
    /// unpolished and still true.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void MoneyFormat_WithNoUsableSymbol_FallsBackToTheCodeNotADollar(string? symbol)
    {
        var currency = symbol is null ? null : Currency("XYZ", symbol, 2);

        var format = DashboardFigures.MoneyFormat("XYZ", currency);

        Assert.Equal("XYZ", format.CurrencySymbol);
    }

    /// <summary>No main currency at all (totals unavailable) is the one case that keeps the generic symbol.</summary>
    [Fact]
    public void MoneyFormat_WithNoCurrencyCode_IsTheGenericFormat()
    {
        Assert.Equal("$", DashboardFigures.MoneyFormat(null, null).CurrencySymbol);
    }

    /// <summary>A negative net worth is a real state, and it must read as a minus sign, not accounting parentheses.</summary>
    [Fact]
    public void MoneyFormat_RendersNegativesWithALeadingMinus()
    {
        var format = DashboardFigures.MoneyFormat("USD", Currency("USD", "$", 2));

        var rendered = (-1234.5m).ToString("C", format);

        Assert.StartsWith("-", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("(", rendered, StringComparison.Ordinal);
    }

    // ── Axis label ──

    /// <summary>
    /// The separator rule. An alphabetic symbol needs one and a sigil does not — and NOK, whose symbol
    /// is "kr", is the app's own default main currency, so the unseparated form is what most users
    /// would have seen.
    /// </summary>
    [Theory]
    [InlineData("$", 52000, "$52k")]
    [InlineData("€", 52000, "€52k")]
    [InlineData("kr", 52000, "kr 52k")]
    [InlineData("CHF", 52000, "CHF 52k")]
    [InlineData("kr", 640, "kr 640")]
    [InlineData("$", 640, "$640")]
    public void AxisLabel_SeparatesAnAlphabeticSymbolFromItsNumber(string symbol, decimal value, string expected)
    {
        Assert.Equal(expected, DashboardFigures.AxisLabel(value, symbol));
    }

    /// <summary>The thousands form applies below zero too; a liability-heavy axis would otherwise print a raw six-digit number.</summary>
    [Fact]
    public void AxisLabel_AbbreviatesLargeNegativesAsWell()
    {
        Assert.Equal("$-52k", DashboardFigures.AxisLabel(-52000m, "$"));
    }

    [Fact]
    public void AxisLabel_WithAnEmptySymbol_IsJustTheNumber()
    {
        Assert.Equal("52k", DashboardFigures.AxisLabel(52000m, string.Empty));
    }

    // ── Advisory wording ──

    /// <summary>
    /// The direction is the whole content of this sentence. Transposed, it names a conversion the user
    /// never asked for and points them at the wrong currency to fix.
    /// </summary>
    [Fact]
    public void UnconvertedMessage_ReadsFromTheAccountCurrencyToTheMainOne()
    {
        var message = DashboardFigures.UnconvertedMessage("SEK", "NOK");

        Assert.Contains("from SEK to NOK", message, StringComparison.Ordinal);
        Assert.DoesNotContain("from NOK to SEK", message, StringComparison.Ordinal);
    }

    [Fact]
    public void UnconvertedMessage_SaysTheAccountCountsAsZero()
    {
        Assert.Contains("counts as 0", DashboardFigures.UnconvertedMessage("SEK", "NOK"), StringComparison.Ordinal);
    }

    // ── Chart empty state ──

    /// <summary>
    /// Two causes, two sentences. Reporting "no balances" when the totals call failed tells the reader
    /// their accounts are empty when they are not.
    /// </summary>
    [Fact]
    public void ChartEmptyLabel_DistinguishesNoDataFromNoTotals()
    {
        var noData = DashboardFigures.ChartEmptyLabel(hasTotals: true);
        var noTotals = DashboardFigures.ChartEmptyLabel(hasTotals: false);

        Assert.NotEqual(noData, noTotals);
        Assert.Contains("balances", noData, StringComparison.Ordinal);
        Assert.Contains("unavailable", noTotals, StringComparison.Ordinal);
    }
}
