using System.Globalization;
using Odyssey.Client.Components;
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

    /// <summary>
    /// Minor units are not always 2, and they are what a figure's precision depends on now that the
    /// symbol is gone. JPY has none — formatting a yen figure with two decimals invents precision the
    /// currency does not have.
    /// </summary>
    [Theory]
    [InlineData("NOK", 2)]
    [InlineData("JPY", 0)]
    public void MinorUnits_ComeFromTheCurrencysOwnRow(string code, int minorUnits)
    {
        Assert.Equal(minorUnits, DashboardFigures.MinorUnits(Currency(code, "x", minorUnits)));
    }

    /// <summary>A missing reference row is the default two, never a guess at the currency's precision.</summary>
    [Fact]
    public void MinorUnits_WithNoReferenceRow_IsTheDefault()
    {
        Assert.Equal(OdsMoney.DefaultMinorUnits, DashboardFigures.MinorUnits(null));
    }

    /// <summary>
    /// The denomination is now carried by the CODE, trailing the figure — never a symbol. Odyssey is
    /// multi-currency and several shipped currencies share a glyph, so a symbol is ambiguous exactly
    /// where the figure matters. The currency's own <c>Symbol</c> must not appear even when it is known.
    /// </summary>
    [Fact]
    public void AFormattedFigure_CarriesItsCodeAndNeverASymbol()
    {
        var rendered = OdsMoney.Format(1234.5m, "NOK", Currency("NOK", "kr", 2));

        Assert.EndsWith(" NOK", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("kr", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("$", rendered, StringComparison.Ordinal);
    }

    /// <summary>
    /// No currency at all — a naive cross-currency aggregate — carries NO code. The old behaviour was
    /// a generic "$", which asserted USD about a figure that might be in anything.
    /// </summary>
    [Fact]
    public void AFigureWithNoCurrency_CarriesNoCodeAtAll()
    {
        var rendered = OdsMoney.Format(1234.5m, currencyCode: null);

        Assert.DoesNotContain("$", rendered, StringComparison.Ordinal);
        Assert.Equal(rendered.TrimEnd(), rendered);
    }

    /// <summary>A negative is a real state, and it must read as a minus sign, not accounting parentheses.</summary>
    [Fact]
    public void ANegative_RendersWithALeadingMinusAndNoParentheses()
    {
        var rendered = OdsMoney.Format(-1234.5m, "USD");

        Assert.StartsWith(OdsMoney.MinusSign.ToString(), rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("(", rendered, StringComparison.Ordinal);
    }

    // ── A recent-transaction row's amount ──

    /// <summary>
    /// A transaction row names the currency the amount is actually IN — its own account's — not the
    /// page's main one. Nothing on this page converts a transaction, so labelling one with the main
    /// currency asserts a denomination it may not have.
    /// </summary>
    [Fact]
    public void TransactionAmount_NamesTheTransactionsOwnCurrency()
    {
        var byCode = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["USD"] = 2, ["NOK"] = 2 };

        Assert.EndsWith(" USD", DashboardFigures.TransactionAmount(211.04m, "USD", byCode), StringComparison.Ordinal);
        Assert.EndsWith(" NOK", DashboardFigures.TransactionAmount(211.04m, "NOK", byCode), StringComparison.Ordinal);
    }

    /// <summary>
    /// The decimals come from THAT currency's row, not the main one's — so a JPY row renders whole
    /// while a USD row beside it keeps its cents. Reading the main currency's decimals would round one
    /// of the two wrong, and neither would look broken.
    /// </summary>
    [Fact]
    public void TransactionAmount_UsesTheTransactionsOwnMinorUnits()
    {
        var byCode = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["USD"] = 2, ["JPY"] = 0 };
        var separator = CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator;

        Assert.Contains(separator, DashboardFigures.TransactionAmount(1234.5m, "USD", byCode), StringComparison.Ordinal);
        Assert.DoesNotContain(separator, DashboardFigures.TransactionAmount(1234.5m, "JPY", byCode), StringComparison.Ordinal);
    }

    /// <summary>
    /// A row is presented as SIGNED: on a ledger the direction is the point, so a positive carries an
    /// explicit plus rather than reading as an ordinary total.
    /// </summary>
    [Fact]
    public void TransactionAmount_IsSignedInBothDirections()
    {
        var byCode = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["USD"] = 2 };

        Assert.StartsWith($"+{OdsMoney.FigureSpace}",
            DashboardFigures.TransactionAmount(211.04m, "USD", byCode), StringComparison.Ordinal);
        Assert.StartsWith($"{OdsMoney.MinusSign}{OdsMoney.FigureSpace}",
            DashboardFigures.TransactionAmount(-211.04m, "USD", byCode), StringComparison.Ordinal);
    }

    /// <summary>
    /// An unknown code, or a reference-data load that failed outright, degrades to the default two
    /// decimals and still names the currency. A blank dashboard would be a worse answer than a
    /// two-decimal yen.
    /// </summary>
    [Theory]
    [InlineData("CHF")]
    [InlineData("JPY")]
    public void TransactionAmount_WithNoKnownMinorUnits_FallsBackToTheDefault(string code)
    {
        var empty = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        var rendered = DashboardFigures.TransactionAmount(1234.5m, code, empty);

        Assert.EndsWith($" {code}", rendered, StringComparison.Ordinal);
        Assert.Contains(CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator, rendered, StringComparison.Ordinal);
    }

    /// <summary>A transaction with no currency code at all carries none, rather than a guessed one.</summary>
    [Fact]
    public void TransactionAmount_WithNoCurrency_CarriesNoCode()
    {
        var byCode = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["USD"] = 2 };

        var rendered = DashboardFigures.TransactionAmount(211.04m, currencyCode: null, byCode);

        Assert.DoesNotContain("$", rendered, StringComparison.Ordinal);
        Assert.Equal(rendered.TrimEnd(), rendered);
    }

    // ── Axis label ──

    /// <summary>
    /// The axis carries the same trailing code as the headline figure above it, so the two read as one
    /// denomination rather than two. The old rule — separate an alphabetic symbol from its number —
    /// is gone with the symbol it separated.
    /// </summary>
    [Theory]
    [InlineData("USD", 52000, "52k USD")]
    [InlineData("NOK", 52000, "52k NOK")]
    [InlineData("CHF", 640, "640 CHF")]
    public void AxisLabel_PutsTheCodeAfterTheFigure(string code, decimal value, string expected)
    {
        Assert.Equal(expected, DashboardFigures.AxisLabel(value, code));
    }

    /// <summary>The thousands form applies below zero too; a liability-heavy axis would otherwise print a raw six-digit number.</summary>
    [Fact]
    public void AxisLabel_AbbreviatesLargeNegativesAsWell()
    {
        Assert.Equal($"{OdsMoney.MinusSign}{OdsMoney.FigureSpace}52k USD", DashboardFigures.AxisLabel(-52000m, "USD"));
    }

    [Fact]
    public void AxisLabel_WithNoCurrency_IsJustTheNumber()
    {
        Assert.Equal("52k", DashboardFigures.AxisLabel(52000m, null));
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
    /// One sentence per state, and no two alike. The server-side causes are carried on the response
    /// precisely because they cannot be inferred — several produce otherwise byte-identical payloads —
    /// and "no data yet" tells a reader with a full portfolio that their accounts are empty.
    /// </summary>
    /// <remarks>
    /// Driven off <c>Enum.GetValues</c> rather than a written-out list, so a cause added server-side
    /// fails here instead of silently landing on the <c>_</c> arm — which is the "could not be loaded"
    /// copy, a statement about the REQUEST, and therefore the one wrong thing to say about a cause the
    /// server did name. Issue #99 added <c>WindowAfterAllAccountsClosed</c> and this is how it is kept
    /// from being the last one.
    /// </remarks>
    [Fact]
    public void ChartEmptyLabel_GivesEachCauseItsOwnSentence()
    {
        var copy = Enum.GetValues<NetWorthEmptyReason>()
            .Select(reason => DashboardFigures.ChartEmptyLabel(reason, "NOK"))
            .Append(DashboardFigures.ChartEmptyLabel(null, "NOK"))
            .ToList();

        Assert.Equal(copy.Count, copy.Distinct(StringComparer.Ordinal).Count());
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
    ///
    /// <para>
    /// Every monthly label carries its year, repetition and all. The chart draws every Nth label and
    /// picks N from the point count, so the page cannot know which labels survive — and showing the
    /// year only where it changes puts it on exactly the labels a stride can drop. A real 24-point
    /// series rendered "Feb" and "May" twice with nothing to separate the two years.
    /// </para>
    /// </summary>
    [Fact]
    public void PointLabel_CarriesTheYearOnEveryMonthlyTick()
    {
        var labels = new[]
        {
            new DateOnly(2024, 11, 1), new DateOnly(2025, 2, 1),
            new DateOnly(2025, 5, 1), new DateOnly(2026, 2, 1), new DateOnly(2026, 5, 1),
        }.Select(date => DashboardFigures.PointLabel(date, NetWorthInterval.Monthly)).ToList();

        Assert.Equal(labels.Count, labels.Distinct(StringComparer.Ordinal).Count());
        Assert.All(labels, label => Assert.Matches(@"^[A-Za-z]{3} .\d\d$", label));
    }

    [Theory]
    [InlineData(NetWorthInterval.Daily, "16 Sep")]
    [InlineData(NetWorthInterval.Weekly, "16 Sep")]
    [InlineData(NetWorthInterval.Yearly, "2026")]
    public void PointLabel_MatchesTheIntervalsResolution(NetWorthInterval interval, string expected)
    {
        Assert.Equal(expected, DashboardFigures.PointLabel(new DateOnly(2026, 9, 16), interval));
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
