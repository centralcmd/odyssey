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
        Assert.Equal(TermVisuals.Info.Label, TermVisuals.DisplayName(Term(label)));

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
