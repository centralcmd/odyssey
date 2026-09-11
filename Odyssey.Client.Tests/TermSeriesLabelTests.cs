using System.Globalization;
using Odyssey.Client.Pages.Finance;
using Odyssey.Dtos.Finance;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// Pins the client half of account-term series labels: what a term is CALLED, which kinds take a
/// label at all, and the consequences of cutting <see cref="TermKind"/> to three values.
///
/// <para>
/// The load-bearing one is <see cref="TermKindVisuals.DisplayName"/>'s fallback arm. It falls back to
/// <see cref="TermKindVisuals.LabelFor"/> and NOT to the bare registry label, so an unlabelled
/// interest rate on a liability still reads "Interest charged" — the non-colour cue introduced by
/// #53 / #55. Reaching for <c>Info(kind).Label</c> there would undo that silently, on a surface that
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

    private static ExistingAccountTerm Term(TermKind kind, string? label = null, string value = "0.0500") => new()
    {
        AccountTermId = Guid.NewGuid(),
        AccountId = Guid.NewGuid(),
        TermKind = kind,
        Label = label,
        ValueUnit = kind == TermKind.Fee ? TermValueUnit.Amount : TermValueUnit.Percentage,
        Value = decimal.Parse(value, CultureInfo.InvariantCulture),
    };

    // ── What a term is called ────────────────────────────────────────────────

    [Fact]
    public void DisplayName_IsTheLabelWhenThereIsOne() =>
        Assert.Equal(
            "ATM withdrawal · abroad",
            TermKindVisuals.DisplayName(Term(TermKind.Fee, "ATM withdrawal · abroad"), Account(AccountType.CreditCard)));

    [Fact]
    public void DisplayName_NormalizesTheLabelItShows() =>
        Assert.Equal(
            "ATM Abroad",
            TermKindVisuals.DisplayName(Term(TermKind.Fee, "  ATM   Abroad  "), Account(AccountType.CreditCard)));

    [Fact]
    public void DisplayName_FallsBackToTheCostRateCaption_NotTheRegistryLabel()
    {
        // Criterion 13: the #55 caption must survive the introduction of labels.
        var term = Term(TermKind.InterestRate, value: "0.0690");
        var loan = Account(AccountType.CarLoan);

        Assert.Equal("Interest charged", TermKindVisuals.DisplayName(term, loan));
        Assert.NotEqual(TermKindVisuals.Info(TermKind.InterestRate).Label, TermKindVisuals.DisplayName(term, loan));
    }

    [Fact]
    public void DisplayName_OfAnAssetsRate_IsItsKindWording() =>
        Assert.Equal(
            "Interest rate",
            TermKindVisuals.DisplayName(Term(TermKind.InterestRate, value: "0.0325"), Account(AccountType.SavingsAccount)));

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("Annual card fee", true)]
    public void IsLabelled_TracksTheNormalizedLabel(string? label, bool expected) =>
        Assert.Equal(expected, TermKindVisuals.IsLabelled(Term(TermKind.Fee, label)));

    [Fact]
    public void ALabelledTermRendersBothItsNameAndItsKindWording()
    {
        // Criterion 14: meaning is in text, never in the glyph or its hue alone — so a labelled term
        // supplies two distinct strings, the name and the caption beneath it.
        var term = Term(TermKind.Fee, "Paper statement");
        var account = Account(AccountType.CreditCard);

        Assert.True(TermKindVisuals.IsLabelled(term));
        Assert.Equal("Paper statement", TermKindVisuals.DisplayName(term, account));
        Assert.Equal("Fee", TermKindVisuals.LabelFor(term, account));
    }

    // ── Which kinds take a label ─────────────────────────────────────────────

    [Theory]
    [InlineData(TermKind.InterestRate, TermLabelRule.Refused)]
    [InlineData(TermKind.ExpectedReturn, TermLabelRule.Refused)]
    [InlineData(TermKind.Fee, TermLabelRule.Required)]
    public void RuleFor_RefusesALabelOnARateAndRequiresOneOnAFee(TermKind kind, TermLabelRule expected) =>
        Assert.Equal(expected, TermLabel.RuleFor(kind));

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

    // ── Three kinds, not six ─────────────────────────────────────────────────

    [Fact]
    public void TheRegistryCarriesExactlyTheThreeKinds() =>
        Assert.Equal(
            new[] { TermKind.InterestRate, TermKind.ExpectedReturn, TermKind.Fee },
            TermKindVisuals.All);

    [Fact]
    public void FeeKeepsTheHeadOfTheOldFeeBand() =>
        // A wire contract: Fee stays at ordinal 10 (the former ManagementFee) so no persisted or
        // in-flight value shifts meaning.
        Assert.Equal(10, (int)TermKind.Fee);

    [Theory]
    // Criterion 19: the account types with no eligible rate leave exactly one kind, and the dialog
    // renders no kind picker at all for them.
    [InlineData(AccountType.Cash)]
    [InlineData(AccountType.Property)]
    [InlineData(AccountType.Vehicle)]
    public void AnAccountTypeWithNoRateLeavesOneEligibleKind(AccountType type)
    {
        var eligible = TermKindVisuals.EligibleKinds(type);
        Assert.Equal([TermKind.Fee], eligible);
    }

    [Theory]
    [InlineData(AccountType.CreditCard)]
    [InlineData(AccountType.SavingsAccount)]
    [InlineData(AccountType.InvestmentAccount)]
    public void AnInterestBearingOrInvestmentAccountStillHasAChoice(AccountType type) =>
        Assert.True(TermKindVisuals.EligibleKinds(type).Count > 1);

    [Fact]
    public void TheKindPickerIsGuardedOnHavingMoreThanOneOption()
    {
        // The eligibility arithmetic above only matters if the dialog actually acts on it, and the
        // guard is markup rather than a method, so it is asserted at the source.
        var markup = File.ReadAllText(Path.Combine(ClientSource.Root, "Pages", "Finance", "AddTermDialog.razor"));

        Assert.Contains("@if (IsEdit || _eligibleKinds.Count > 1)", markup, StringComparison.Ordinal);
    }

    [Fact]
    public void AFeeOpensOnOneHonestBillingDefault() =>
        // One default, since there is no longer a fee kind to guess from.
        Assert.Equal(BillingPeriod.Monthly, TermKindVisuals.DefaultFeeBillingPeriod);
}
