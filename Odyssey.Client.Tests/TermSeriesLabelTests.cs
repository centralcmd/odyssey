using System.Globalization;
using Odyssey.Client.Pages.Finance;
using Odyssey.Dtos.Finance;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// Pins the client half of the term-series label: a series is a kind <em>plus</em> a label, so one
/// kind can be in force several times over, and the display has to name each one.
/// <para>
/// The load-bearing case is <see cref="TermKindVisuals.DisplayName"/>'s fallback. It defers to
/// <see cref="TermKindVisuals.LabelFor"/>, not to the bare registry label, so an unlabelled cost rate
/// still reads "Interest charged" — the word that carries what the sign used to (WCAG 1.4.1, issue
/// #53). Reaching for <c>Info(kind).Label</c> there would undo that fix silently, on a surface that
/// still looks correct for every other term.
/// </para>
/// </summary>
public class TermSeriesLabelTests
{
    private static ExistingAccount Account(AccountType type) => new()
    {
        AccountId = Guid.NewGuid(),
        Name = "Test account",
        Description = "Test account",
        Opened = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        AccountType = type,
    };

    private static ExistingAccountTerm Term(
        string value, TermKind kind = TermKind.Fee, string? label = null,
        TermValueUnit unit = TermValueUnit.Amount) => new()
    {
        AccountTermId = Guid.NewGuid(),
        AccountId = Guid.NewGuid(),
        TermKind = kind,
        ValueUnit = unit,
        Value = decimal.Parse(value, CultureInfo.InvariantCulture),
        Label = label,
    };

    // ── DisplayName ───────────────────────────────────────────────────────────

    [Fact]
    public void DisplayName_PrefersTheAuthorsLabel() =>
        Assert.Equal(
            "ATM withdrawal · abroad",
            TermKindVisuals.DisplayName(Term("60", label: "ATM withdrawal · abroad"), Account(AccountType.CreditCard)));

    [Fact]
    public void DisplayName_NormalizesTheLabelForDisplay() =>
        Assert.Equal(
            "ATM Abroad",
            TermKindVisuals.DisplayName(Term("60", label: "  ATM   Abroad  "), Account(AccountType.CreditCard)));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void DisplayName_FallsBackToTheKindWhenUnlabelled(string? label) =>
        Assert.Equal(
            "Fee",
            TermKindVisuals.DisplayName(Term("5", label: label), Account(AccountType.CheckingAccount)));

    [Theory]
    [InlineData(AccountType.CarLoan)]
    [InlineData(AccountType.Mortgage)]
    [InlineData(AccountType.CreditCard)]
    public void DisplayName_KeepsTheCostRateCaptionWhenUnlabelled(AccountType type)
    {
        // The merge hazard: a rate carries no label, so it always takes the fallback arm — which must
        // stay LabelFor. Using the registry label here would drop the only non-color cue that a
        // liability's interest is money out.
        var term = Term("0.0690", TermKind.InterestRate, unit: TermValueUnit.Percentage);
        var account = Account(type);

        Assert.Equal("Interest charged", TermKindVisuals.DisplayName(term, account));
        Assert.Equal(TermKindVisuals.LabelFor(term, account), TermKindVisuals.DisplayName(term, account));
    }

    [Theory]
    [InlineData(AccountType.SavingsAccount)]
    [InlineData(AccountType.CheckingAccount)]
    public void DisplayName_LeavesAnAssetsRateAlone(AccountType type) =>
        Assert.Equal(
            "Interest rate",
            TermKindVisuals.DisplayName(Term("0.0325", TermKind.InterestRate, unit: TermValueUnit.Percentage), Account(type)));

    // ── SeriesKey ─────────────────────────────────────────────────────────────

    [Fact]
    public void SeriesKey_SeparatesTwoLabelledFeesOfOneKind() =>
        Assert.NotEqual(
            TermKindVisuals.SeriesKey(Term("25", label: "domestic")),
            TermKindVisuals.SeriesKey(Term("60", label: "abroad")));

    [Theory]
    [InlineData("ATM abroad", "atm abroad")]
    [InlineData("ATM abroad", "  ATM   abroad  ")]
    [InlineData("ATM abroad", "ATM ABROAD")]
    public void SeriesKey_FoldsCaseAndSpacing(string a, string b) =>
        Assert.Equal(TermKindVisuals.SeriesKey(Term("25", label: a)), TermKindVisuals.SeriesKey(Term("30", label: b)));

    [Fact]
    public void SeriesKey_TreatsAnUnlabelledTermAsItsOwnSeries()
    {
        // Null is a series, not a wildcard that absorbs the named ones.
        Assert.NotEqual(
            TermKindVisuals.SeriesKey(Term("1", label: null)),
            TermKindVisuals.SeriesKey(Term("2", label: "Wire transfer")));

        Assert.Equal(
            TermKindVisuals.SeriesKey(Term("1", label: null)),
            TermKindVisuals.SeriesKey(Term("2", label: "   ")));
    }

    [Fact]
    public void SeriesKey_SeparatesTheUnnamedSeriesOfDifferentKinds() =>
        // A rate carries no label and a fee must carry one, so the unnamed series of two kinds is the
        // only cross-kind pair the app can actually produce — and the kind still has to separate them.
        Assert.NotEqual(
            TermKindVisuals.SeriesKey(Term("0.03", TermKind.InterestRate, unit: TermValueUnit.Percentage)),
            TermKindVisuals.SeriesKey(Term("5", TermKind.Fee)));
}
