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

    // ── The dashboard's wiring, extracted so it is testable (Home cannot render in a test) ────

    [Theory]
    [InlineData(NetWorthRangePreset.All, false, true)]
    [InlineData(NetWorthRangePreset.Custom, false, true)]
    [InlineData(NetWorthRangePreset.Custom, true, false)]
    [InlineData(NetWorthRangePreset.SixMonths, false, false)]
    [InlineData(NetWorthRangePreset.TwelveMonths, false, false)]
    public void Only_a_window_starting_at_the_earliest_opening_waits_for_the_account_list(
        NetWorthRangePreset preset, bool hasFrom, bool needs) =>
        Assert.Equal(needs, new NetWorthRange(preset, hasFrom ? new DateOnly(2024, 1, 1) : null).NeedsEarliest);

    private static ExistingAccount Account(DateTime opened, DateTime? archived = null) => new()
    {
        AccountId = Guid.NewGuid(),
        Name = "a",
        Description = "a",
        Opened = opened,
        Archived = archived,
        CurrencyCode = "USD",
    };

    [Fact]
    public void The_earliest_opening_counts_archived_accounts_and_is_null_with_none()
    {
        var accounts = new[]
        {
            Account(new DateTime(2020, 5, 1)),
            Account(new DateTime(2012, 3, 9), archived: new DateTime(2024, 1, 1)),
        };

        Assert.Equal(new DateOnly(2012, 3, 9), NetWorthRange.EarliestOpening(accounts));
        Assert.Null(NetWorthRange.EarliestOpening([]));
    }

    // ── Calendar edges ───────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(2026, 8, 31, NetWorthRangePreset.SixMonths, 2026, 2, 28)]
    [InlineData(2024, 8, 31, NetWorthRangePreset.SixMonths, 2024, 2, 29)]
    [InlineData(2025, 2, 28, NetWorthRangePreset.TwelveMonths, 2024, 2, 28)]
    [InlineData(2024, 2, 29, NetWorthRangePreset.TwelveMonths, 2023, 2, 28)]
    public void A_preset_from_a_month_end_or_leap_day_clamps_to_a_real_day(
        int y, int m, int d, NetWorthRangePreset preset, int fy, int fm, int fd) =>
        Assert.Equal(new DateOnly(fy, fm, fd), new NetWorthRange(preset).ResolveRequest(new DateOnly(y, m, d), null).From);

    [Fact]
    public void The_interval_turns_quarterly_exactly_past_the_monthly_cap()
    {
        // 120 monthly points: Oct 2016 … Sep 2026 inclusive. One month earlier is 121.
        Assert.Equal(NetWorthInterval.Monthly, NetWorthRange.Default.ResolveRequest(Today, new DateOnly(2016, 10, 1)).Interval);
        Assert.Equal(NetWorthInterval.Quarterly, NetWorthRange.Default.ResolveRequest(Today, new DateOnly(2016, 9, 30)).Interval);
    }
}
