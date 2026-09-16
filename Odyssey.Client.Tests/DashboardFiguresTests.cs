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
    /// Five states, five sentences. The four server-side causes are carried on the response precisely
    /// because they cannot be inferred — two of them produce otherwise byte-identical payloads — and
    /// "no data yet" tells a reader with a full portfolio that their accounts are empty.
    /// </summary>
    [Fact]
    public void ChartEmptyLabel_GivesEachCauseItsOwnSentence()
    {
        var copy = new[]
        {
            DashboardFigures.ChartEmptyLabel(NetWorthEmptyReason.NotBuilt, "NOK"),
            DashboardFigures.ChartEmptyLabel(NetWorthEmptyReason.NoAccounts, "NOK"),
            DashboardFigures.ChartEmptyLabel(NetWorthEmptyReason.NothingConvertible, "NOK"),
            DashboardFigures.ChartEmptyLabel(NetWorthEmptyReason.WindowBeforeFirstAccount, "NOK"),
            DashboardFigures.ChartEmptyLabel(null, "NOK"),
        };

        Assert.Equal(copy.Length, copy.Distinct(StringComparer.Ordinal).Count());
        Assert.All(copy, sentence => Assert.False(string.IsNullOrWhiteSpace(sentence)));
    }

    /// <summary>
    /// A reader cannot act on "could not be converted" without knowing what it could not be converted
    /// TO, and the currency is admin- and user-settable, so it is interpolated rather than written in.
    /// </summary>
    [Fact]
    public void ChartEmptyLabel_NamesTheCurrencyItCouldNotConvertTo()
    {
        Assert.Contains("NOK",
            DashboardFigures.ChartEmptyLabel(NetWorthEmptyReason.NothingConvertible, "NOK"),
            StringComparison.Ordinal);

        // …and stays a sentence rather than a gap when the currency is unknown.
        var unknown = DashboardFigures.ChartEmptyLabel(NetWorthEmptyReason.NothingConvertible, null);
        Assert.DoesNotContain("  ", unknown, StringComparison.Ordinal);
        Assert.EndsWith("for any period.", unknown, StringComparison.Ordinal);
    }

    /// <summary>
    /// A failed CALL is not one of the server's causes. "Not available yet" is a statement about the
    /// data; this is a statement about the request, and conflating them tells the reader their history
    /// does not exist when it may be fine.
    /// </summary>
    [Fact]
    public void ChartEmptyLabel_SeparatesAFailedCallFromAnEmptyResult()
    {
        Assert.NotEqual(
            DashboardFigures.ChartEmptyLabel(NetWorthEmptyReason.NotBuilt, "NOK"),
            DashboardFigures.ChartEmptyLabel(null, "NOK"));

        Assert.Contains("could not be loaded",
            DashboardFigures.ChartEmptyLabel(null, "NOK"), StringComparison.Ordinal);
    }

    // ── Chart labels, caption and notes ──

    /// <summary>
    /// A point is dated at its period END, and the tick label is derived from that date. Deriving it
    /// from anything else is how a financial figure gets mislabelled by one period.
    /// </summary>
    [Fact]
    public void PointLabel_ShowsTheYearOnTheFirstPointAndWhereverItChanges()
    {
        var first = DashboardFigures.PointLabel(new DateOnly(2024, 11, 1), NetWorthInterval.Monthly, null);
        var sameYear = DashboardFigures.PointLabel(new DateOnly(2024, 12, 1), NetWorthInterval.Monthly, new DateOnly(2024, 11, 1));
        var newYear = DashboardFigures.PointLabel(new DateOnly(2025, 1, 1), NetWorthInterval.Monthly, new DateOnly(2024, 12, 1));

        Assert.Contains("Nov", first, StringComparison.Ordinal);
        Assert.Contains("24", first, StringComparison.Ordinal);

        // No year repeated under every tick inside one year…
        Assert.Equal("Dec", sameYear);
        // …but the year comes back the moment it changes, or a reader cannot place the point at all.
        Assert.Contains("25", newYear, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(NetWorthInterval.Daily, "16 Sep")]
    [InlineData(NetWorthInterval.Weekly, "16 Sep")]
    [InlineData(NetWorthInterval.Yearly, "2026")]
    public void PointLabel_MatchesTheIntervalsResolution(NetWorthInterval interval, string expected)
    {
        Assert.Equal(expected, DashboardFigures.PointLabel(new DateOnly(2026, 9, 16), interval, null));
    }

    [Fact]
    public void ChartCaption_SaysWhatTheChartIs()
    {
        var caption = DashboardFigures.ChartCaption(24, "Jul ’24", "Jun ’26", NetWorthInterval.Monthly, "NOK");

        Assert.Contains("24 monthly points", caption, StringComparison.Ordinal);
        Assert.Contains("NOK", caption, StringComparison.Ordinal);
        // The caption it replaced said "Since 2016", which described the span of a curve that had no
        // real span — so a year on its own is exactly what must not come back.
        Assert.DoesNotContain("Since", caption, StringComparison.Ordinal);
    }

    [Fact]
    public void ChartCaption_DropsTheSpanRatherThanPrintingAnEmptyOne()
    {
        var caption = DashboardFigures.ChartCaption(0, null, null, NetWorthInterval.Monthly, "NOK");

        Assert.DoesNotContain("–", caption, StringComparison.Ordinal);
        Assert.DoesNotContain(" ·  ·", caption, StringComparison.Ordinal);
    }

    [Fact]
    public void ChartAriaLabel_NamesTheResolutionAndTheSpan()
    {
        var label = DashboardFigures.ChartAriaLabel(24, "Jul ’24", "Jun ’26", NetWorthInterval.Monthly);

        Assert.StartsWith("Net worth over time,", label, StringComparison.Ordinal);
        Assert.Contains("24 monthly points", label, StringComparison.Ordinal);
        Assert.Contains("from Jul ’24 to Jun ’26", label, StringComparison.Ordinal);
    }

    [Fact]
    public void ChartAriaLabel_FallsBackToThePlainNameWithNoPoints()
    {
        Assert.Equal("Net worth over time",
            DashboardFigures.ChartAriaLabel(0, null, null, NetWorthInterval.Monthly));
    }

    /// <summary>
    /// The markers rest on shape and stroke, so a reader who cannot see the plot gets neither. Every
    /// condition the chart marks is therefore also stated in a sentence.
    /// </summary>
    [Fact]
    public void UnderstatedNote_NamesThePeriodAndTheAccountWhenThereIsOnlyOneOfEach()
    {
        var note = DashboardFigures.UnderstatedNote(
            ["Jul ’25"],
            [new UnconvertedAccount { AccountId = Guid.NewGuid(), Name = "Zurich brokerage", CurrencyCode = "CHF" }],
            deltaWithheld: false);

        Assert.NotNull(note);
        Assert.Contains("Jul ’25 is understated", note, StringComparison.Ordinal);
        Assert.Contains("Zurich brokerage (CHF)", note, StringComparison.Ordinal);
        Assert.DoesNotContain("withheld", note, StringComparison.Ordinal);
    }

    [Fact]
    public void UnderstatedNote_ExplainsTheWithheldDeltaOnlyWhenItIsWithheld()
    {
        var accounts = new List<UnconvertedAccount>
        {
            new() { AccountId = Guid.NewGuid(), Name = "A", CurrencyCode = "CHF" },
            new() { AccountId = Guid.NewGuid(), Name = "B", CurrencyCode = "SEK" },
        };

        var withheld = DashboardFigures.UnderstatedNote(["Jul", "Aug"], accounts, deltaWithheld: true);
        Assert.NotNull(withheld);
        Assert.Contains("2 periods are understated", withheld, StringComparison.Ordinal);
        Assert.Contains("withheld", withheld, StringComparison.Ordinal);
        // More than one unconvertible account: naming them all would turn a caption into a roster.
        Assert.Contains("an account had no exchange rate", withheld, StringComparison.Ordinal);
    }

    [Fact]
    public void TheTwoNotes_AreSeparateSentencesWithDifferentWording()
    {
        var understated = DashboardFigures.UnderstatedNote(
            ["Jul"],
            [new UnconvertedAccount { AccountId = Guid.NewGuid(), Name = "A", CurrencyCode = "CHF" }],
            deltaWithheld: false);
        var revalued = DashboardFigures.RevaluedNote(["Jun"]);

        Assert.NotNull(understated);
        Assert.NotNull(revalued);
        Assert.NotEqual(understated, revalued);

        // An understatement is a MISSING movement and a revaluation is a real one. One sentence
        // serving both would make opposites read alike.
        Assert.Contains("understated", understated, StringComparison.Ordinal);
        Assert.Contains("a real movement, not a correction", revalued, StringComparison.Ordinal);
        Assert.DoesNotContain("understated", revalued, StringComparison.Ordinal);
    }

    [Fact]
    public void TheNotes_AreAbsentWhenThereIsNothingToDisclose()
    {
        Assert.Null(DashboardFigures.UnderstatedNote([], [], deltaWithheld: false));
        Assert.Null(DashboardFigures.RevaluedNote([]));
    }
}
