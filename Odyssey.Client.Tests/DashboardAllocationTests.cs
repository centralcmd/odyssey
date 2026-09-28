using Odyssey.Client.Components;
using Odyssey.Client.Pages.Finance;
using Odyssey.Dtos.Finance;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// The dashboard's allocation donuts (Odyssey Design System · components/AllocationDonuts): which well
/// each totals row lands in, and what each well's sub-line says is counted and left out.
/// </summary>
public class DashboardAllocationTests
{
    private static NetWorthAllocation Row(NetWorthAllocationKind kind, string name, decimal value) =>
        new() { Kind = kind, Id = Guid.NewGuid(), Name = name, Value = value };

    private static AccountTotals Totals(bool includeProperties, params NetWorthAllocation[] rows) => new()
    {
        MainCurrencyCode = "USD",
        TotalAssets = 0,
        TotalLiabilities = 0,
        NetWorth = 0,
        PropertiesIncluded = includeProperties,
        Allocations = [.. rows],
    };

    [Fact]
    public void Positive_rows_are_asset_slices_and_negative_rows_liability_slices()
    {
        var totals = Totals(true,
            Row(NetWorthAllocationKind.Account, "Checking", 1000),
            Row(NetWorthAllocationKind.Account, "Card", -300),
            Row(NetWorthAllocationKind.Property, "House", 500_000));

        var (assets, liabilities) = DashboardFigures.AllocationSlices(totals);

        Assert.Equal(["Checking", "House"], assets.Select(s => s.Label));
        Assert.Equal(["Card"], liabilities.Select(s => s.Label));
        Assert.Equal(-300m, liabilities[0].Value);
    }

    /// <summary>
    /// The wells split by sign, not account type: an overdrawn checking account is owed money and
    /// draws in the liability well, an overpaid card in the asset well.
    /// </summary>
    [Fact]
    public void An_overdrawn_asset_and_an_overpaid_liability_draw_in_the_well_their_sign_names()
    {
        var totals = Totals(false,
            Row(NetWorthAllocationKind.Account, "Overdrawn checking", -50),
            Row(NetWorthAllocationKind.Account, "Overpaid card", 20));

        var (assets, liabilities) = DashboardFigures.AllocationSlices(totals);

        Assert.Equal(["Overpaid card"], assets.Select(s => s.Label));
        Assert.Equal(["Overdrawn checking"], liabilities.Select(s => s.Label));
    }

    [Fact]
    public void An_empty_totals_draws_two_empty_wells()
    {
        var (assets, liabilities) = DashboardFigures.AllocationSlices(null);

        Assert.Empty(assets);
        Assert.Empty(liabilities);
    }

    [Fact]
    public void The_asset_sub_line_counts_accounts_and_properties_and_names_a_property_with_no_rate()
    {
        var totals = Totals(true,
            Row(NetWorthAllocationKind.Account, "Checking", 1000),
            Row(NetWorthAllocationKind.Property, "House", 500_000));
        totals.UnconvertedProperties = [new() { PropertyId = Guid.NewGuid(), Name = "Boat", CurrencyCode = "GBP" }];
        var (assets, liabilities) = DashboardFigures.AllocationSlices(totals);

        Assert.Equal("Accounts and property · 1 account, 1 property · Boat excl. (no rate)",
            DashboardFigures.AllocationAssetsSub(totals, assets));
        Assert.Equal("What you owe · 0 accounts", DashboardFigures.AllocationLiabilitiesSub(liabilities));
    }

    /// <summary>
    /// An unconverted ACCOUNT is never named in the asset well: it carries no type, so it may be a
    /// liability, and the header's problem rollup already names it.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void An_unconverted_account_is_not_named_in_the_asset_sub_line(bool includeProperties)
    {
        var totals = Totals(includeProperties, Row(NetWorthAllocationKind.Account, "Checking", 1000));
        totals.UnconvertedAccounts = [new() { AccountId = Guid.NewGuid(), Name = "GBP card", CurrencyCode = "GBP" }];
        var (assets, _) = DashboardFigures.AllocationSlices(totals);

        var sub = DashboardFigures.AllocationAssetsSub(totals, assets);

        Assert.DoesNotContain("GBP card", sub);
        Assert.DoesNotContain("excl.", sub);
    }

    [Fact]
    public void An_accounts_only_figure_says_where_the_money_sits()
    {
        var totals = Totals(false,
            Row(NetWorthAllocationKind.Account, "Checking", 1000),
            Row(NetWorthAllocationKind.Account, "Savings", 50));
        var (assets, _) = DashboardFigures.AllocationSlices(totals);

        Assert.Equal("Where your money sits · 2 accounts", DashboardFigures.AllocationAssetsSub(totals, assets));
    }
}
