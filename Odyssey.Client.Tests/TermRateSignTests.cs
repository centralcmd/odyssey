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
/// framing survives as color only: a liability's interest rate is still expense-tinted.
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
}
