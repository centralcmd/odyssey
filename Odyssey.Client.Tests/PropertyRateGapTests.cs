using Microsoft.AspNetCore.Components;
using Odyssey.Client.Components;
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

    /// <summary>
    /// Each gap becomes one ungrouped WARNING row — the only kind this panel carries — led by the
    /// property's name, with the base-currency copy and a View bound to that property's id.
    /// </summary>
    [Fact]
    public async Task Rate_gap_problems_are_ungrouped_warning_rows()
    {
        Guid? viewed = null;

        var row = Assert.Single(PropertiesCard.RateGapProblems(
            Summary(Gap(Cabin, "Cabin", "NOK")), new HashSet<Guid> { Cabin },
            id => EventCallback.Factory.Create(this, () => viewed = id)));

        Assert.Null(row.Group);
        Assert.Equal(PageHeaderSeverity.Warning, row.Severity);
        Assert.Equal("Cabin", row.Lead);
        Assert.Equal("No NOK → USD rate, so this property is left out of the estimated value total.", row.Message);
        await row.OnView.InvokeAsync();
        Assert.Equal(Cabin, viewed);
    }

    /// <summary>
    /// The page composes the signal: an "Attention" button whose open state persists with the rest of
    /// the page state, a View that opens and scrolls to the record, and the chip and alert on the record
    /// with the fix routed to Exchange rates. Read from source because the page needs a full service
    /// graph to render.
    /// </summary>
    [Fact]
    public void The_properties_page_wires_the_missing_rate_signal_chip_and_alert()
    {
        var finance = Path.Combine(ClientSource.Root, "Pages", "Finance");
        var markup = File.ReadAllText(Path.Combine(finance, "PropertiesCard.razor"));
        var code = File.ReadAllText(Path.Combine(finance, "PropertiesCard.razor.cs"));

        Assert.Contains("Problems=\"HeaderProblems\"", markup, StringComparison.Ordinal);
        Assert.Contains("ProblemsLabel=\"Attention\"", markup, StringComparison.Ordinal);
        Assert.Contains("ProblemsOpenChanged=\"OnProblemsToggled\"", markup, StringComparison.Ordinal);
        Assert.Contains("ProblemsOpen = _problemsOpen", code, StringComparison.Ordinal);
        Assert.Contains("_problemsOpen = state.ProblemsOpen", code, StringComparison.Ordinal);

        var jump = code[code.IndexOf("private async Task JumpTo(Guid id)", StringComparison.Ordinal)..];
        jump = jump[..jump.IndexOf("private async Task Flash", StringComparison.Ordinal)];
        Assert.Contains("_expandedId = id", jump, StringComparison.Ordinal);
        Assert.Contains("$\"#prop-{id}\"", jump, StringComparison.Ordinal);
        Assert.Contains("Id=\"@($\"prop-{p.PropertyId}\")\"", markup, StringComparison.Ordinal);

        var chips = markup[markup.IndexOf("<Chips>", StringComparison.Ordinal)..markup.IndexOf("</Chips>", StringComparison.Ordinal)];
        Assert.Contains("<OdsProblemFlag>No rate</OdsProblemFlag>", chips, StringComparison.Ordinal);
        var alert = markup[markup.IndexOf("<Alert>", StringComparison.Ordinal)..markup.IndexOf("</Alert>", StringComparison.Ordinal)];
        Assert.Contains("ActionLabel=\"Set exchange rate\" OnAction=\"GoToExchangeRates\"", alert, StringComparison.Ordinal);
        Assert.Contains("NavigationManager.NavigateTo(\"exchange-rates\")", code, StringComparison.Ordinal);
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
