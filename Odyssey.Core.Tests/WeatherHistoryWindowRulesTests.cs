using Odyssey.Core.Weather;
using Odyssey.Dtos.Journal;
using Xunit;

namespace Odyssey.Core.Tests;

/// <summary>The shared window rules and the planner (issue #436 §3.1, §5.1, §8) — pure, so no database.</summary>
public class WeatherHistoryWindowRulesTests
{
    private static readonly DateOnly Today = new(2026, 11, 7);

    private static WeatherHistoryWindowDto W(string? from, string? to = null) => new()
    {
        From = from is null ? null : DateOnly.Parse(from),
        To = to is null ? null : DateOnly.Parse(to),
    };

    private static IReadOnlyList<WindowRuleViolation> Validate(params WeatherHistoryWindowDto[] windows) =>
        WeatherHistoryWindowRules.Validate(windows, Today, requireAny: true);

    [Fact]
    public void Non_overlapping_and_adjacent_windows_are_valid()
    {
        Assert.Empty(Validate(W("2026-06-10", "2026-06-17"), W("2026-06-18", "2026-06-20"), W("2026-07-01")));
    }

    [Fact]
    public void Sharing_one_day_is_an_overlap_reported_on_the_later_window_by_request_index()
    {
        // Request order is not start order: the later-starting window is index 0.
        var v = Assert.Single(Validate(W("2026-06-17", "2026-06-20"), W("2026-06-10", "2026-06-17")));
        Assert.Equal((0, WindowField.Row, WindowRule.Overlap, (int?)1), (v.Index, v.Field, v.Rule, v.Other));
    }

    [Fact]
    public void An_ongoing_window_that_is_not_the_latest_is_reported_on_itself_naming_the_latest()
    {
        var v = Assert.Single(Validate(W("2025-01-01"), W("2026-06-10", "2026-06-17")));
        Assert.Equal((0, WindowField.Row, WindowRule.OpenEndNotLast, (int?)1), (v.Index, v.Field, v.Rule, v.Other));
    }

    [Fact]
    public void Two_ongoing_windows_are_refused()
    {
        Assert.Contains(Validate(W("2025-01-01"), W("2026-01-01")), v => v.Rule == WindowRule.OpenEndNotLast);
    }

    [Theory]
    [InlineData("1949-12-31", null, WindowField.From, WindowRule.OutOfRange)]
    [InlineData("2026-06-10", "2026-06-09", WindowField.To, WindowRule.ToBeforeFrom)]
    [InlineData("2028-11-07", null, WindowField.From, WindowRule.OutOfRange)]
    [InlineData("2026-06-10", "2028-11-07", WindowField.To, WindowRule.OutOfRange)]
    [InlineData(null, "2026-06-09", WindowField.From, WindowRule.FromMissing)]
    public void A_bad_element_is_reported_on_its_field(string? from, string? to, WindowField field, WindowRule rule)
    {
        var v = Assert.Single(Validate(W(from, to)));
        Assert.Equal((0, field, rule), (v.Index, v.Field, v.Rule));
    }

    [Fact]
    public void Today_plus_730_is_the_last_allowed_date()
    {
        Assert.Empty(Validate(W("2028-11-06")));
    }

    [Fact]
    public void An_empty_set_is_refused_only_while_history_is_on()
    {
        Assert.Equal(WindowRule.Empty, Assert.Single(WeatherHistoryWindowRules.Validate([], Today, requireAny: true)).Rule);
        Assert.Empty(WeatherHistoryWindowRules.Validate([], Today, requireAny: false));
    }

    [Fact]
    public void A_set_larger_than_the_days_it_could_cover_gets_one_error_and_no_per_element_work()
    {
        var count = WeatherHistoryWindowRules.MaxValidCount(Today) + 1;
        var windows = Enumerable.Range(0, count).Select(_ => new WeatherHistoryWindowDto()).ToList();
        var v = Assert.Single(WeatherHistoryWindowRules.Validate(windows, Today, requireAny: true));
        Assert.Equal((-1, WindowField.List, WindowRule.Impossible), (v.Index, v.Field, v.Rule));
    }

    [Fact]
    public void A_maximal_valid_set_validates_quickly()
    {
        // One window per day from the floor to today + 730: the largest valid set (§7.7, AC 23).
        var windows = new List<WeatherHistoryWindowDto>();
        for (var d = new DateOnly(1950, 1, 1); d <= Today.AddDays(730); d = d.AddDays(1))
        {
            windows.Add(new WeatherHistoryWindowDto { From = d, To = d });
        }

        windows.Reverse();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        Assert.Empty(WeatherHistoryWindowRules.Validate(windows, Today, requireAny: true));
        Assert.True(watch.ElapsedMilliseconds < 1000, $"took {watch.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void Server_keys_name_the_member_path()
    {
        Assert.Equal("HistoryWindows[2].To", WeatherHistoryWindowRules.ToValidationResult(new(2, WindowField.To, WindowRule.ToBeforeFrom), Today).MemberNames.Single());
        Assert.Equal("HistoryWindows[1]", WeatherHistoryWindowRules.ToValidationResult(new(1, WindowField.Row, WindowRule.Overlap, 0), Today).MemberNames.Single());
        Assert.Equal("HistoryWindows", WeatherHistoryWindowRules.ToValidationResult(new(-1, WindowField.List, WindowRule.Empty), Today).MemberNames.Single());
    }

    [Fact]
    public void Window_dtos_redact_their_dates()
    {
        Assert.DoesNotContain("2026", W("2026-06-10", "2026-06-17").ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("2026", new ExistingWeatherHistoryWindow { From = new DateOnly(2026, 6, 10) }.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("2026", new WeatherHistoryWindowSummary { EarliestFrom = new DateOnly(2026, 6, 10) }.ToString(), StringComparison.Ordinal);
    }

    // ── Planner ──────────────────────────────────────────────────────────────────────────────────

    private static WeatherWindow Win(string from, string? to = null) => new(DateOnly.Parse(from), to is null ? null : DateOnly.Parse(to));

    private static WeatherDateRange R(string from, string to) => new(DateOnly.Parse(from), DateOnly.Parse(to));

    [Fact]
    public void Four_past_trips_give_four_gaps_newest_first_and_nothing_between_them()
    {
        var windows = new[] { Win("2023-07-01", "2023-07-07"), Win("2024-02-10", "2024-02-17"), Win("2025-07-01", "2025-07-07"), Win("2026-02-10", "2026-02-17") };
        var gaps = WeatherHistoryPlan.Gaps(windows, [], DateOnly.MinValue, new DateOnly(2026, 11, 6));
        Assert.Equal([R("2026-02-10", "2026-02-17"), R("2025-07-01", "2025-07-07"), R("2024-02-10", "2024-02-17"), R("2023-07-01", "2023-07-07")], gaps);
    }

    [Fact]
    public void Coverage_is_subtracted_and_recent_uncovered_days_are_ordinary_gaps()
    {
        var gaps = WeatherHistoryPlan.Gaps([Win("2026-10-01")], [R("2026-10-01", "2026-10-20")], DateOnly.MinValue, new DateOnly(2026, 11, 6));
        Assert.Equal([R("2026-10-21", "2026-11-06")], gaps);
    }

    [Fact]
    public void A_window_before_the_current_site_is_not_fetched_and_reads_unavailable()
    {
        var firstOwned = new DateOnly(2026, 6, 14);
        var windows = new[] { Win("2025-02-14", "2025-02-23") };
        Assert.Empty(WeatherHistoryPlan.Gaps(windows, [], firstOwned, new DateOnly(2026, 11, 6)));
        var d = Assert.Single(WeatherHistoryPlan.Describe(windows, [], firstOwned, new DateOnly(2026, 11, 6)));
        Assert.Equal((WeatherHistoryWindowStatus.Unavailable, 0, (int?)0), (d.Status, d.CoveredDays, d.FetchableDays));
    }

    [Fact]
    public void A_future_window_is_upcoming_then_backfilling_then_collecting()
    {
        var windows = new[] { Win("2026-12-27", "2027-01-03") };
        Assert.Equal(WeatherHistoryWindowStatus.Upcoming, WeatherHistoryPlan.Describe(windows, [], DateOnly.MinValue, new DateOnly(2026, 12, 26)).Single().Status);
        Assert.Equal(WeatherHistoryWindowStatus.Backfilling, WeatherHistoryPlan.Describe(windows, [], DateOnly.MinValue, new DateOnly(2026, 12, 27)).Single().Status);
        Assert.Equal(WeatherHistoryWindowStatus.Collecting,
            WeatherHistoryPlan.Describe(windows, [R("2026-12-27", "2026-12-27")], DateOnly.MinValue, new DateOnly(2026, 12, 27)).Single().Status);
    }

    [Fact]
    public void Statuses_and_progress_follow_the_table()
    {
        var lastComplete = new DateOnly(2026, 11, 6);
        var described = WeatherHistoryPlan.Describe(
            [Win("2026-07-04", "2026-07-18"), Win("2026-08-10", "2026-08-24")],
            [R("2026-07-04", "2026-07-18"), R("2026-08-19", "2026-08-24")],
            DateOnly.MinValue, lastComplete);
        Assert.Equal((WeatherHistoryWindowStatus.Complete, 15, (int?)15), (described[0].Status, described[0].CoveredDays, described[0].FetchableDays));
        Assert.Equal((WeatherHistoryWindowStatus.Backfilling, 6, (int?)15), (described[1].Status, described[1].CoveredDays, described[1].FetchableDays));
    }

    [Fact]
    public void Consecutive_short_gaps_merge_into_one_request_while_the_hull_fits_a_chunk()
    {
        var gaps = new[] { R("2026-02-10", "2026-02-17"), R("2026-01-01", "2026-01-05"), R("2024-01-01", "2024-01-05") };
        var parts = WeatherFetchService.BackfillParts(gaps, 121);
        Assert.Equal([R("2026-01-01", "2026-01-05"), R("2026-02-10", "2026-02-17")], parts);
    }

    [Fact]
    public void A_long_gap_is_fetched_newest_chunk_first()
    {
        var parts = WeatherFetchService.BackfillParts([R("2020-01-01", "2026-01-01")], 365);
        Assert.Equal([R("2025-01-02", "2026-01-01")], parts);
    }

    [Fact]
    public void Subtract_handles_coverage_inside_spanning_and_beside_a_range()
    {
        var result = WeatherHistoryPlan.Subtract([R("2026-01-01", "2026-01-31")], [R("2025-12-01", "2026-01-03"), R("2026-01-10", "2026-01-12"), R("2026-01-30", "2026-02-10")]);
        Assert.Equal([R("2026-01-04", "2026-01-09"), R("2026-01-13", "2026-01-29")], result);
    }
}
