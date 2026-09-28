using System.Text.Json;
using Odyssey.Client.Components;
using Odyssey.Client.Pages.Finance;
using Odyssey.Dtos.Finance;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// The dashboard's net-worth chart period (Odyssey Design System · Dashboard · NetWorthRangeDialog)
/// and the allocation donuts' slices and sub-lines.
/// </summary>
public class NetWorthRangeTests
{
    private static readonly DateOnly Today = new(2026, 9, 28);

    [Fact]
    public void The_default_is_all_time() => Assert.Equal(NetWorthRangePreset.All, NetWorthRange.Default.Preset);

    [Theory]
    [InlineData(NetWorthRangePreset.SixMonths, 2026, 3, 28)]
    [InlineData(NetWorthRangePreset.TwelveMonths, 2025, 9, 28)]
    public void A_preset_asks_for_n_months_back_and_leaves_today_to_the_server(
        NetWorthRangePreset preset, int year, int month, int day)
    {
        var (from, to, interval) = new NetWorthRange(preset).ResolveRequest(Today, new DateOnly(2010, 1, 1));

        Assert.Equal(new DateOnly(year, month, day), from);
        Assert.Null(to);
        Assert.Equal(NetWorthInterval.Monthly, interval);
    }

    [Fact]
    public void All_time_starts_at_the_earliest_account_and_coarsens_to_fit_the_cap()
    {
        var recent = NetWorthRange.Default.ResolveRequest(Today, new DateOnly(2020, 1, 15));
        Assert.Equal((new DateOnly(2020, 1, 15), (DateOnly?)null, NetWorthInterval.Monthly), recent);

        // 120 months is the monthly cap; beyond it the window is quarterly, then yearly.
        Assert.Equal(NetWorthInterval.Quarterly, NetWorthRange.Default.ResolveRequest(Today, new DateOnly(2010, 1, 1)).Interval);
        Assert.Equal(NetWorthInterval.Yearly, NetWorthRange.Default.ResolveRequest(Today, new DateOnly(1980, 1, 1)).Interval);
    }

    [Fact]
    public void All_time_with_no_accounts_keeps_the_server_default_window()
    {
        var (from, to, interval) = NetWorthRange.Default.ResolveRequest(Today, null);

        Assert.Null(from);
        Assert.Null(to);
        Assert.Equal(NetWorthHistoryQuery.DefaultInterval, interval);
    }

    [Fact]
    public void Every_resolved_window_fits_the_servers_point_cap()
    {
        foreach (var earliest in new[] { new DateOnly(1950, 1, 1), new DateOnly(2000, 6, 1), new DateOnly(2016, 9, 1) })
        {
            var (from, _, interval) = NetWorthRange.Default.ResolveRequest(Today, earliest);
            var query = new NetWorthHistoryQuery { From = from, Interval = interval };
            Assert.Empty(query.Validate(Today));
        }
    }

    [Fact]
    public void A_custom_range_sends_its_ends_and_an_open_start_runs_from_the_earliest()
    {
        var range = new NetWorthRange(NetWorthRangePreset.Custom, new DateOnly(2024, 1, 1), new DateOnly(2025, 6, 30));
        Assert.Equal((new DateOnly(2024, 1, 1), new DateOnly(2025, 6, 30), NetWorthInterval.Monthly),
            range.ResolveRequest(Today, new DateOnly(2010, 1, 1)));

        var open = new NetWorthRange(NetWorthRangePreset.Custom, null, new DateOnly(2025, 6, 30));
        Assert.Equal(new DateOnly(2021, 3, 1), open.ResolveRequest(Today, new DateOnly(2021, 3, 1)).From);
    }

    [Fact]
    public void An_earliest_date_after_the_chosen_end_is_dropped_rather_than_inverting_the_window()
    {
        var range = new NetWorthRange(NetWorthRangePreset.Custom, null, new DateOnly(2020, 1, 1));

        Assert.Null(range.ResolveRequest(Today, new DateOnly(2022, 1, 1)).From);
    }

    [Theory]
    [InlineData(30, true)]
    [InlineData(31, false)]
    public void A_custom_span_shorter_than_a_month_is_refused(int days, bool refused)
    {
        var from = new DateOnly(2025, 1, 1);
        var range = new NetWorthRange(NetWorthRangePreset.Custom, from, from.AddDays(days));

        Assert.Equal(refused ? NetWorthRange.SpanTooShort : null, range.Error);
    }

    [Fact]
    public void An_open_ended_custom_range_is_never_too_short() =>
        Assert.Null(new NetWorthRange(NetWorthRangePreset.Custom, new DateOnly(2026, 9, 1)).Error);

    [Fact]
    public void A_preset_drops_any_dates_when_stored()
    {
        var stored = new NetWorthRange(NetWorthRangePreset.SixMonths, new DateOnly(2025, 1, 1), new DateOnly(2025, 6, 1)).Normalized;

        Assert.Equal(new NetWorthRange(NetWorthRangePreset.SixMonths), stored);
    }

    [Fact]
    public void The_page_state_round_trips_through_the_web_serializer()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var state = new HomePageState
        {
            NetWorthRange = new NetWorthRange(NetWorthRangePreset.Custom, new DateOnly(2024, 2, 1), null),
        };

        var back = JsonSerializer.Deserialize<HomePageState>(JsonSerializer.Serialize(state, options), options);

        Assert.Equal(state.NetWorthRange, back!.NetWorthRange);
    }

    // ── Allocation donuts ────────────────────────────────────────────────────────────────────

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
