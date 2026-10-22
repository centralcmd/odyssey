using Odyssey.Client.Pages.Finance;
using Odyssey.Dtos.Finance;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// The Properties page's missing-exchange-rate signal: the Accounts pattern applied to the estimated
/// value total. The rows come from <see cref="PropertyValueSummary.UnconvertedProperties"/>, so these
/// pin what the page does with them — which rows it keeps and what the copy says.
/// </summary>
public class PropertyRateGapTests
{
    private static readonly Guid Cabin = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Boat = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static PropertySummary Summary(params UnconvertedProperty[] gaps) => new()
    {
        Value = new PropertyValueSummary { BaseCurrency = "USD", UnconvertedProperties = [.. gaps] },
    };

    private static UnconvertedProperty Gap(Guid id, string name, string currency) =>
        new() { PropertyId = id, Name = name, CurrencyCode = currency };

    /// <summary>
    /// A row naming a property the filtered list is not showing is dropped: its View would scroll to
    /// nothing, silently, since the jump's scroll is best-effort.
    /// </summary>
    [Fact]
    public void Rate_gaps_keep_only_properties_the_list_is_showing()
    {
        var summary = Summary(Gap(Cabin, "Cabin", "NOK"), Gap(Boat, "Boat", "EUR"));

        var gaps = PropertiesCard.RateGaps(summary, new HashSet<Guid> { Cabin }).ToList();

        Assert.Equal(["Cabin"], gaps.Select(g => g.Name));
    }

    /// <summary>
    /// Without <c>properties.estimates.read</c> the value block is null, and so is every row: a caller
    /// who cannot see values is never told one is missing. No summary yet reads the same.
    /// </summary>
    [Fact]
    public void Rate_gaps_are_empty_without_a_value_block()
    {
        var listed = new HashSet<Guid> { Cabin };

        Assert.Empty(PropertiesCard.RateGaps(new PropertySummary { Value = null }, listed));
        Assert.Empty(PropertiesCard.RateGaps(null, listed));
    }

    [Fact]
    public void Rate_gap_copy_names_the_pair_and_what_was_left_out()
    {
        var gap = Gap(Cabin, "Cabin", "NOK");

        Assert.Equal(
            "No NOK → USD rate, so this property is left out of the estimated value total.",
            PropertiesCard.RateGapSummary(gap, "USD"));
        var detail = PropertiesCard.RateGapDetail(gap, "USD");
        Assert.Contains("main currency (USD)", detail, StringComparison.Ordinal);
        Assert.Contains("no NOK → USD rate stored", detail, StringComparison.Ordinal);
    }
}
