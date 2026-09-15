using System.Text.RegularExpressions;
using Odyssey.Client.Pages.Finance;
using Odyssey.Dtos.Finance;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// Pins the rule that the Accounts page's liability donut prints magnitudes. The legend used to print
/// each account's stored value, so the ring that sized its slices by <c>Math.Abs</c> closed with
/// "Total owed −128 450" — a minus sign in front of a number the panel's own label calls owed, which
/// negates the sentence and reads as a credit rather than a debt.
///
/// The sibling assertion is the one that keeps the fix honest: the sign is dropped in this ring ALONE.
/// <c>AccountAllocation.Value</c> and <c>AccountSummary.TotalLiabilities</c> stay signed, so the
/// accounts table and the per-account pages are unaffected — the opposite failure to the one
/// <see cref="TermRateSignTests"/> records, where a display invented a sign the data did not have.
/// </summary>
public class AccountsOverviewLegendTests
{
    private static AccountAllocation Allocation(decimal value, string currency = "NOK") => new()
    {
        AccountId = Guid.NewGuid(),
        Name = "Test account",
        CurrencyCode = currency,
        Value = value,
    };

    /// <summary>Stands in for the page's own money formatter; a null code is the cross-currency total.</summary>
    private static string Money(decimal value, string? currency) => $"{value} {currency ?? "¤"}";

    [Theory]
    [InlineData(-500, "500 NOK")]
    [InlineData(-128450.75, "128450.75 NOK")]
    [InlineData(-0.01, "0.01 NOK")]
    public void LiabilityRow_PrintsTheMagnitude(decimal value, string expected) =>
        Assert.Equal(expected, AllocationLegend.LiabilityRow(Allocation(value), Money));

    [Fact]
    public void LiabilityRow_KeepsTheAccountsOwnCurrency() =>
        Assert.Equal("1200 SEK", AllocationLegend.LiabilityRow(Allocation(-1200m, "SEK"), Money));

    [Fact]
    public void LiabilityTotal_PrintsTheMagnitudeOfANegativeAggregate() =>
        Assert.Equal("128450 ¤", AllocationLegend.LiabilityTotal(-128450m, Money));

    [Fact]
    public void LiabilityTotal_OfAnEmptyPanelIsNotANegativeZero() =>
        Assert.Equal("0 ¤", AllocationLegend.LiabilityTotal(0m, Money));

    [Fact]
    public void AssetRow_PrintsTheValueAsStored() =>
        Assert.Equal("500 NOK", AllocationLegend.AssetRow(Allocation(500m), Money));

    [Fact]
    public void AssetTotal_PrintsTheAggregateAsStored() =>
        Assert.Equal("98000 ¤", AllocationLegend.AssetTotal(98000m, Money));

    /// <summary>
    /// The magnitude is a property of the LEGEND, not of the data behind it: the allocation the
    /// formatter was handed still reads negative afterwards, which is what the accounts table and the
    /// per-account pages go on.
    /// </summary>
    [Fact]
    public void LiabilityRow_LeavesTheAllocationSigned()
    {
        var allocation = Allocation(-500m);

        AllocationLegend.LiabilityRow(allocation, Money);

        Assert.Equal(-500m, allocation.Value);
    }

    /// <summary>
    /// Label in Name (WCAG 2.5.3): each donut's accessible name has to contain its visible title, so a
    /// voice-control user asking for what they can see matches. The liability panel's AriaLabel was
    /// left saying "Liabilities by account" when its title was renamed, which is what this catches.
    /// </summary>
    [Fact]
    public void EachDonutsAccessibleNameContainsItsVisibleTitle()
    {
        var donuts = Regex.Matches(
            OverviewSource(),
            @"<OdsDonut\b(?<attrs>[^>]*)>(?<body>.*?)</OdsDonut>",
            RegexOptions.Singleline);

        Assert.Equal(2, donuts.Count);

        foreach (Match donut in donuts)
        {
            var aria = Regex.Match(donut.Groups["attrs"].Value, @"AriaLabel=""(?<v>[^""]*)""").Groups["v"].Value;
            var title = Regex.Match(donut.Groups["body"].Value, @"<Title>(?<v>[^<]*)</Title>").Groups["v"].Value;

            Assert.False(string.IsNullOrWhiteSpace(title), "A donut panel has no <Title>.");
            Assert.True(
                aria.Contains(title, StringComparison.Ordinal),
                $"""The donut titled "{title}" has the accessible name "{aria}", which does not contain it.""");
        }
    }

    /// <summary>
    /// A panel with no accounts renders its own markup rather than an OdsDonut, so its heading can
    /// drift from the populated one. Pinning them as a pair means a retitle has to reach both.
    /// </summary>
    [Fact]
    public void EmptyAndPopulatedPanelsCarryTheSameTitles()
    {
        var source = OverviewSource();
        var emptyTitles = Regex.Matches(source, @"<div class=""chart-ttl"">(?<v>[^<]*)</div>")
            .Select(m => m.Groups["v"].Value)
            .ToArray();
        var populatedTitles = Regex.Matches(source, @"<Title>(?<v>[^<]*)</Title>")
            .Select(m => m.Groups["v"].Value)
            .ToArray();

        Assert.Equal(new[] { "Asset allocation", "Liability allocation" }, populatedTitles);
        Assert.Equal(populatedTitles, emptyTitles);
    }

    private static string OverviewSource() =>
        File.ReadAllText(Path.Combine(ClientSource.Root, "Pages", "Finance", "AccountsOverview.razor"));
}
