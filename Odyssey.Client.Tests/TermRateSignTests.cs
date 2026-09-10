using System.Globalization;
using Odyssey.Client.Pages.Finance;
using Odyssey.Dtos.Finance;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// Pins the rule that a term's rate renders with the sign it was stored with. The display used to
/// re-sign a liability's interest rate as <c>-Math.Abs(value)</c>, which made a genuinely negative
/// rate (a real thing — negative-rate deposits and mortgages both exist) indistinguishable from an
/// ordinary one, and inverted the rate-history delta so a rising APR trended downward. The cost
/// framing survives without it: a liability's interest rate is still expense-tinted, and — since the
/// sign is no longer there to be the second cue — captioned "Interest charged" in words.
/// </summary>
public class TermRateSignTests
{
    private static ExistingAccount Account(AccountType type) => new()
    {
        AccountId = Guid.NewGuid(),
        Name = "Test account",
        Description = "Test account",
        Opened = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        AccountType = type,
    };

    private static ExistingAccountTerm Rate(string value, TermKind kind = TermKind.InterestRate) => new()
    {
        AccountTermId = Guid.NewGuid(),
        AccountId = Guid.NewGuid(),
        TermKind = kind,
        ValueUnit = TermValueUnit.Percentage,
        Value = decimal.Parse(value, CultureInfo.InvariantCulture),
    };

    private static string Money(decimal value, string? currency) => $"{value} {currency}";

    [Theory]
    // A loan APR is no longer flipped: it reads as the positive rate it is.
    [InlineData("0.0395", "3.95%")]
    [InlineData("0.0690", "6.9%")]
    [InlineData("0.1999", "19.99%")]
    [InlineData("0.0325", "3.25%")]
    // A genuinely negative rate keeps its sign.
    [InlineData("-0.0050", "−0.50%")]
    public void FormatValue_CarriesTheStoredSign(string value, string expected) =>
        Assert.Equal(expected, TermKindVisuals.FormatValue(Rate(value), Money));

    [Fact]
    public void FormatValue_DistinguishesANegativeRateFromItsPositiveCounterpart()
    {
        var positive = TermKindVisuals.FormatValue(Rate("0.0100"), Money);
        var negative = TermKindVisuals.FormatValue(Rate("-0.0100"), Money);

        Assert.Equal("1%", positive);
        Assert.Equal("−1%", negative);
    }

    [Fact]
    public void CostColor_StillTintsALiabilitysInterestRate()
    {
        Assert.NotNull(TermKindVisuals.CostColor(Rate("0.0690"), Account(AccountType.CarLoan)));
        Assert.Null(TermKindVisuals.CostColor(Rate("0.0325"), Account(AccountType.SavingsAccount)));
        // Only an interest rate reads as a cost — an expected return never does.
        Assert.Null(TermKindVisuals.CostColor(
            Rate("0.0700", TermKind.ExpectedReturn), Account(AccountType.PensionAccount)));
    }

    // ── The cost cue is in words, not only in color (WCAG 1.4.1) ────────────────
    // Removing the sign left the coral tint as the only signal that a liability's interest is money
    // out. The label carries it now, so the two must agree everywhere: exactly the terms CostColor
    // tints are the terms LabelFor re-captions.

    [Fact]
    public void LabelFor_CaptionsACostRateInWords()
    {
        Assert.Equal("Interest charged", TermKindVisuals.LabelFor(Rate("0.0690"), Account(AccountType.CarLoan)));
        Assert.Equal("Interest charged", TermKindVisuals.LabelFor(Rate("0.0395"), Account(AccountType.Mortgage)));
    }

    [Theory]
    [InlineData(AccountType.SavingsAccount, "Interest rate")]
    [InlineData(AccountType.CheckingAccount, "Interest rate")]
    public void LabelFor_LeavesAnAssetsRateAlone(AccountType type, string expected) =>
        Assert.Equal(expected, TermKindVisuals.LabelFor(Rate("0.0325"), Account(type)));

    [Fact]
    public void LabelFor_LeavesAnExpectedReturnAlone() =>
        Assert.Equal(
            "Expected return",
            TermKindVisuals.LabelFor(Rate("0.0700", TermKind.ExpectedReturn), Account(AccountType.PensionAccount)));

    [Theory]
    [InlineData(AccountType.CarLoan)]
    [InlineData(AccountType.Mortgage)]
    [InlineData(AccountType.CreditCard)]
    [InlineData(AccountType.SavingsAccount)]
    [InlineData(AccountType.CheckingAccount)]
    public void LabelFor_RecaptionsExactlyWhatCostColorTints(AccountType type)
    {
        var term = Rate("0.0500");
        var account = Account(type);

        var tinted = TermKindVisuals.CostColor(term, account) is not null;
        var recaptioned = TermKindVisuals.LabelFor(term, account) != TermKindVisuals.Info(term.TermKind).Label;

        Assert.Equal(tinted, recaptioned);
    }

    // ── Delta direction ────────────────────────────────────────────────────────
    // Extracted out of AccountTermsSection.BuildHero so it can be asserted at all: the old code
    // computed it from the sign-flipped series, so a liability's rate RISE trended downward.

    [Theory]
    // A rise is a rise, on either side of the balance sheet.
    [InlineData("0.0450", "0.0395", "arrow_upward")]
    [InlineData("0.0395", "0.0450", "arrow_downward")]
    [InlineData("0.0395", "0.0395", "remove")]
    // Crossing zero, and moving within negative territory.
    [InlineData("0.0010", "-0.0010", "arrow_upward")]
    [InlineData("-0.0050", "-0.0010", "arrow_downward")]
    public void DeltaIcon_FollowsTheStoredRate(string current, string previous, string expected) =>
        Assert.Equal(
            expected,
            TermKindVisuals.DeltaIcon(
                decimal.Parse(current, CultureInfo.InvariantCulture),
                decimal.Parse(previous, CultureInfo.InvariantCulture)));
}
