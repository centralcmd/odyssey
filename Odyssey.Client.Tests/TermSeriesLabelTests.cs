using System.Globalization;
using Odyssey.Client.Pages.Finance;
using Odyssey.Dtos.Finance;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// Pins the client half of term series labels: what a term is CALLED, and how a label folds into the
/// series key. Every term is a labelled series; there is no kind taxonomy left to fall back on.
/// </summary>
public class TermSeriesLabelTests
{
    private static ExistingTerm Term(string? label, string value = "12") => new()
    {
        TermId = Guid.NewGuid(),
        AccountId = Guid.NewGuid(),
        Label = label,
        ValueUnit = TermValueUnit.Amount,
        Value = decimal.Parse(value, CultureInfo.InvariantCulture),
    };

    // ── What a term is called ────────────────────────────────────────────────

    [Fact]
    public void DisplayName_IsTheLabel() =>
        Assert.Equal("ATM withdrawal · abroad", TermVisuals.DisplayName(Term("ATM withdrawal · abroad")));

    [Fact]
    public void DisplayName_NormalizesTheLabelItShows() =>
        Assert.Equal("ATM Abroad", TermVisuals.DisplayName(Term("  ATM   Abroad  ")));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void DisplayName_OfAnUnlabelledRow_IsThePlainNoun_NeverBlank(string? label) =>
        Assert.Equal(TermVisuals.DefaultName, TermVisuals.DisplayName(Term(label)));

    // ── How a term looks ─────────────────────────────────────────────────────

    /// <summary>
    /// With no kind left, the glyph and hue follow the UNIT (the design system's termUnits), so a rate
    /// and a price still read apart at a glance.
    /// </summary>
    [Fact]
    public void The_glyph_and_hue_follow_the_unit()
    {
        var pct = TermVisuals.UnitInfo(TermValueUnit.Percentage);
        var amt = TermVisuals.UnitInfo(TermValueUnit.Amount);

        Assert.Equal("percent", pct.Icon);
        Assert.Equal("oklch(0.78 0.13 200)", pct.Color);
        Assert.Equal("payments", amt.Icon);
        Assert.Equal("oklch(0.77 0.14 55)", amt.Color);

        Assert.Same(amt, TermVisuals.Info(Term("Annual fee")));
        Assert.Same(pct, TermVisuals.Info(Term("Interest rate") with { ValueUnit = TermValueUnit.Percentage }));
    }

    // ── The series key ───────────────────────────────────────────────────────

    [Theory]
    [InlineData("ATM abroad", "atm abroad")]
    [InlineData("  ATM   abroad  ", "atm abroad")]
    [InlineData("AtM AbRoAd", "atm abroad")]
    public void Key_FoldsCaseAndSpacingSoATypoContinuesItsOwnSeries(string raw, string expected) =>
        Assert.Equal(expected, TermLabel.Key(raw));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("\t \n ")]
    public void Normalize_TreatsBlankAsTheUnnamedSeries(string? raw) =>
        Assert.Null(TermLabel.Normalize(raw));

    // ── The dialog ───────────────────────────────────────────────────────────

    [Fact]
    public void TheDialogOffersNoKindPicker()
    {
        // Every term is one kind of thing now, so the dialog has nothing to choose between.
        var markup = File.ReadAllText(Path.Combine(ClientSource.Root, "Pages", "Finance", "AddTermDialog.razor"));

        Assert.DoesNotContain("OdsCardSelect", markup, StringComparison.Ordinal);
    }

    [Fact]
    public void ANewTermOpensOnOneHonestBillingDefault() =>
        Assert.Equal(Interval.Monthly, TermVisuals.DefaultInterval);
}
