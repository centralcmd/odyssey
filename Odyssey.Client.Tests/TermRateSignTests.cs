using System.Globalization;
using Odyssey.Client.Pages.Finance;
using Odyssey.Dtos.Finance;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// Pins the rule that a percentage term renders with the sign it was stored with. A genuinely
/// negative rate (a real thing — negative-rate deposits and mortgages both exist) must stay
/// distinguishable from an ordinary one.
/// </summary>
public class TermRateSignTests
{
    private static ExistingTerm Rate(string value) => new()
    {
        TermId = Guid.NewGuid(),
        AccountId = Guid.NewGuid(),
        Label = "Interest rate",
        ValueUnit = TermValueUnit.Percentage,
        Value = decimal.Parse(value, CultureInfo.InvariantCulture),
    };

    private static string Money(decimal value, string? currency) => $"{value} {currency}";

    [Theory]
    [InlineData("0.0395", "3.95%")]
    [InlineData("0.0690", "6.9%")]
    [InlineData("0.1999", "19.99%")]
    [InlineData("0.0325", "3.25%")]
    // A genuinely negative rate keeps its sign.
    [InlineData("-0.0050", "−0.50%")]
    public void FormatValue_CarriesTheStoredSign(string value, string expected) =>
        Assert.Equal(expected, TermVisuals.FormatValue(Rate(value), Money));

    [Fact]
    public void FormatValue_DistinguishesANegativeRateFromItsPositiveCounterpart()
    {
        var positive = TermVisuals.FormatValue(Rate("0.0100"), Money);
        var negative = TermVisuals.FormatValue(Rate("-0.0100"), Money);

        Assert.Equal("1%", positive);
        Assert.Equal("−1%", negative);
    }
}
