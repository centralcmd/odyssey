using Bunit;
using Microsoft.AspNetCore.Components;
using MudBlazor;
using Odyssey.Client.Components;
using Odyssey.Client.Pages.Finance;
using Odyssey.Dtos.Finance;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// The one dropdown contract every select-style control shares (Odyssey Design System ·
/// docs/components-data-table-menu-form-controls.md · Dropdowns): one popover surface, a popover never narrower than its trigger, a filter-bar
/// trigger sized to a short option list, and Status filters that wear the overview's glyphs.
/// </summary>
public class DropdownConsistencyTests
{
    private static BunitContext NewContext() => NestedInteractiveControlTests.NewContext();

    [Fact]
    public void A_popup_menu_is_never_narrower_than_its_trigger_by_default() =>
        Assert.Equal(DropdownWidth.Adaptive, new OdsPopupMenu().RelativeWidth);

    [Theory]
    [InlineData(null)]
    [InlineData("odc-ms-pop-paper")]
    public async Task Every_popover_wears_the_shared_surface(string? consumerClass)
    {
        await using var ctx = NewContext();
        var cut = ctx.Render(builder =>
        {
            builder.OpenComponent<MudPopoverProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<OdsPopupMenu>(1);
            builder.AddAttribute(2, nameof(OdsPopupMenu.PopoverClass), consumerClass);
            builder.AddAttribute(3, nameof(OdsPopupMenu.TriggerContent), (RenderFragment)(b => b.AddContent(0, "Open")));
            builder.AddAttribute(4, nameof(OdsPopupMenu.ChildContent), (RenderFragment)(b => b.AddContent(0, "Body")));
            builder.CloseComponent();
        });

        cut.Find("button.odc-popmenu-trigger").Click();

        var paper = cut.Find(".mud-popover.odc-popmenu-paper");
        Assert.Contains("odc-popmenu-paper", paper.ClassList);
        if (consumerClass is not null)
        {
            Assert.Contains(consumerClass, paper.ClassList);
        }
        // The rendered popover carries the Adaptive width rule, not just the parameter default.
        Assert.Contains("mud-popover-adaptive-width", paper.ClassList);
    }

    private static IReadOnlyList<OdsOption> OptionsOf(int count) =>
        [.. Enumerable.Range(1, count).Select(i => new OdsOption($"v{i}", $"Option {i}") { Icon = "label" })];

    private static async Task<int> GhostCount(int optionCount, bool quiet = false)
    {
        await using var ctx = NewContext();
        var cut = ctx.Render<OdsMultiSelect>(p => p
            .Add(c => c.Label, "Status")
            .Add(c => c.Options, OptionsOf(optionCount))
            .Add(c => c.Quiet, quiet));
        return cut.FindAll(".odc-ms-fit-ghost").Count;
    }

    [Fact]
    public async Task A_short_option_list_sizes_the_trigger_to_its_widest_row() =>
        Assert.Equal(OdsMultiSelect.FitLimit, await GhostCount(OdsMultiSelect.FitLimit));

    [Fact]
    public async Task An_open_ended_list_keeps_the_trigger_floor_and_lets_the_popover_grow() =>
        Assert.Equal(0, await GhostCount(OdsMultiSelect.FitLimit + 1));

    [Fact]
    public async Task A_quiet_trigger_is_not_widened() => Assert.Equal(0, await GhostCount(3, quiet: true));

    [Fact]
    public async Task Fit_ghosts_are_hidden_from_the_accessible_name()
    {
        await using var ctx = NewContext();
        var cut = ctx.Render<OdsMultiSelect>(p => p
            .Add(c => c.Label, "Status")
            .Add(c => c.Options, OptionsOf(2)));

        Assert.All(cut.FindAll(".odc-ms-fit-ghost"), g => Assert.Equal("true", g.GetAttribute("aria-hidden")));
    }

    [Fact]
    public void A_breakdown_definition_becomes_a_filter_option_with_its_glyph_and_tone()
    {
        var option = OdsBreakdown.Option(new OdsBreakdownDef<bool>(true, "Archived", "outline", "inventory_2"),
            archived => archived ? "Archived" : "Active");

        Assert.Equal("Archived", option.Value);
        Assert.Equal("Archived", option.Label);
        Assert.Equal("inventory_2", option.Icon);
        Assert.Equal(OdsBreakdown.Tone("outline"), option.IconColor);
    }

    public static TheoryData<string, IReadOnlyList<OdsOption>> FinanceStatusFilters => new()
    {
        { "Accounts", AccountsCard._statusOptions },
        { "Budgets", BudgetsCard._statusOptions },
        { "Contacts", ContactsCard._statusOptions },
        { "Contracts", ContractsCard._statusOptions },
        { "Currencies", CurrenciesCard._statusOptions },
        { "Exchange rates", ExchangeRatesCard._statusOptions },
        { "Properties", PropertyVisuals.StatusOptions },
        { "Tax statements", TaxStatementsCard._statusOptions },
        { "Transactions status", TransactionsCard._statusOptions },
        { "Transactions direction", TransactionsCard._directionOptions },
    };

    /// <summary>The wire values each filter sends — what the list endpoint binds, unchanged by the move
    /// to shared definitions.</summary>
    public static TheoryData<string, IReadOnlyList<OdsOption>, string[]> FinanceFilterWireValues => new()
    {
        { "Accounts", AccountsCard._statusOptions, ["Open", "Closed", "Archived"] },
        { "Budgets", BudgetsCard._statusOptions, ["Active", "Archived"] },
        { "Contacts", ContactsCard._statusOptions, ["active", "archived"] },
        { "Contracts", ContractsCard._statusOptions, [.. OdsContractStatus.Order.Select(s => s.ToString())] },
        { "Currencies", CurrenciesCard._statusOptions, [.. OdsTypeRegistries.CurrencyStatuses.Keys.Select(k => k.ToString())] },
        { "Exchange rates", ExchangeRatesCard._statusOptions, ["Current", "Upcoming", "Historical"] },
        { "Tax statements", TaxStatementsCard._statusOptions, Enum.GetNames<TaxStatementStatusFilter>() },
        // Every TransactionStatus member: the filter was once derived from the enum, so a new member must
        // be added to StatusDefs or this fails rather than silently vanishing from the filter.
        { "Transactions status", TransactionsCard._statusOptions, Enum.GetNames<TransactionStatus>() },
        { "Transactions direction", TransactionsCard._directionOptions, ["income", "expense"] },
    };

    [Theory]
    [MemberData(nameof(FinanceFilterWireValues))]
    public void Every_filter_keeps_the_wire_values_the_list_binds(string page, IReadOnlyList<OdsOption> options, string[] expected) =>
        Assert.True(expected.SequenceEqual(options.Select(o => o.Value)),
            $"{page}: expected [{string.Join(", ", expected)}], got [{string.Join(", ", options.Select(o => o.Value))}]");

    [Fact]
    public void The_contracts_filter_wears_the_overview_tiles_glyph_and_dot_colour() =>
        Assert.All(OdsContractStatus.Order.Zip(ContractsCard._statusOptions), pair =>
        {
            var meta = OdsContractStatus.Meta(pair.First);
            Assert.Equal(meta.Icon, pair.Second.Icon);
            Assert.Equal(meta.DotColor, pair.Second.IconColor);
        });

    [Theory]
    [MemberData(nameof(FinanceStatusFilters))]
    public void Every_status_filter_option_carries_an_icon(string page, IReadOnlyList<OdsOption> options)
    {
        Assert.NotEmpty(options);
        Assert.All(options, o =>
        {
            Assert.False(string.IsNullOrEmpty(o.Icon), $"{page}: {o.Label} has no icon");
            Assert.False(string.IsNullOrEmpty(o.IconColor), $"{page}: {o.Label} has no colour");
        });
    }

}
