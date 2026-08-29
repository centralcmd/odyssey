using Odyssey.Client.Components;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>Unit coverage for <see cref="CalendarGridMath"/> — the pure date/coverage math behind
/// <see cref="OdsCalendarGrid"/> (issue #323).</summary>
public class CalendarGridMathTests
{
    private static CalendarEventVm Vm(DateTime start, DateTime end, bool isAllDay = false, Guid calendarId = default) =>
        new(Guid.NewGuid(), calendarId, "Test", "Event", start, end, isAllDay, "#0369A1", "#FFFFFF", false);

    // ── DaysInGrid ──────────────────────────────────────────────────────────────

    [Fact]
    public void DaysInGrid_returns_42_days()
    {
        var days = CalendarGridMath.DaysInGrid(new DateOnly(2026, 8, 1), weekStartsOn: 1);
        Assert.Equal(42, days.Count);
    }

    [Fact]
    public void DaysInGrid_MondayFirst_starts_on_the_monday_before_or_on_the_1st()
    {
        // August 2026 starts on a Saturday; Monday-first grid should back up to the preceding Monday.
        var days = CalendarGridMath.DaysInGrid(new DateOnly(2026, 8, 1), weekStartsOn: 1);
        Assert.Equal(DayOfWeek.Monday, days[0].DayOfWeek);
        Assert.Contains(new DateOnly(2026, 8, 1), days);
    }

    [Fact]
    public void DaysInGrid_SundayFirst_starts_on_a_sunday()
    {
        var days = CalendarGridMath.DaysInGrid(new DateOnly(2026, 8, 1), weekStartsOn: 0);
        Assert.Equal(DayOfWeek.Sunday, days[0].DayOfWeek);
    }

    [Fact]
    public void DaysInGrid_is_stable_regardless_of_which_day_in_the_month_is_passed()
    {
        var fromFirst = CalendarGridMath.DaysInGrid(new DateOnly(2026, 8, 1), weekStartsOn: 1);
        var fromMid = CalendarGridMath.DaysInGrid(new DateOnly(2026, 8, 15), weekStartsOn: 1);
        Assert.Equal(fromFirst, fromMid);
    }

    // ── WeekdayHeaders ──────────────────────────────────────────────────────────

    [Fact]
    public void WeekdayHeaders_MondayFirst_starts_with_Mon_ends_with_Sun()
    {
        var headers = CalendarGridMath.WeekdayHeaders(weekStartsOn: 1);
        Assert.Equal("Mon", headers[0]);
        Assert.Equal("Sun", headers[^1]);
        Assert.Equal(7, headers.Count);
    }

    [Fact]
    public void WeekdayHeaders_SundayFirst_starts_with_Sun()
    {
        var headers = CalendarGridMath.WeekdayHeaders(weekStartsOn: 0);
        Assert.Equal("Sun", headers[0]);
    }

    // ── StartOfWeek ─────────────────────────────────────────────────────────────

    [Fact]
    public void StartOfWeek_MondayFirst_returns_the_monday_of_that_week()
    {
        // 2026-08-06 is a Thursday.
        var start = CalendarGridMath.StartOfWeek(new DateOnly(2026, 8, 6), weekStartsOn: 1);
        Assert.Equal(new DateOnly(2026, 8, 3), start);
    }

    [Fact]
    public void StartOfWeek_on_the_week_start_day_returns_itself()
    {
        var monday = new DateOnly(2026, 8, 3);
        Assert.Equal(monday, CalendarGridMath.StartOfWeek(monday, weekStartsOn: 1));
    }

    // ── CoversDay ───────────────────────────────────────────────────────────────

    [Fact]
    public void CoversDay_timed_event_only_covers_its_start_date()
    {
        var vm = Vm(new DateTime(2026, 8, 10, 9, 0, 0), new DateTime(2026, 8, 10, 10, 0, 0));
        Assert.True(CalendarGridMath.CoversDay(vm, new DateOnly(2026, 8, 10)));
        Assert.False(CalendarGridMath.CoversDay(vm, new DateOnly(2026, 8, 11)));
    }

    [Fact]
    public void CoversDay_allday_event_covers_start_through_the_day_before_the_exclusive_end()
    {
        // A 3-day all-day event: Aug 10, 11, 12 (End is the exclusive midnight after the last day).
        var vm = Vm(new DateTime(2026, 8, 10), new DateTime(2026, 8, 13), isAllDay: true);

        Assert.True(CalendarGridMath.CoversDay(vm, new DateOnly(2026, 8, 10)));
        Assert.True(CalendarGridMath.CoversDay(vm, new DateOnly(2026, 8, 11)));
        Assert.True(CalendarGridMath.CoversDay(vm, new DateOnly(2026, 8, 12)));
        Assert.False(CalendarGridMath.CoversDay(vm, new DateOnly(2026, 8, 13)));
        Assert.False(CalendarGridMath.CoversDay(vm, new DateOnly(2026, 8, 9)));
    }

    [Fact]
    public void CoversDay_singleday_allday_event_covers_exactly_one_day()
    {
        var vm = Vm(new DateTime(2026, 8, 10), new DateTime(2026, 8, 11), isAllDay: true);
        Assert.True(CalendarGridMath.CoversDay(vm, new DateOnly(2026, 8, 10)));
        Assert.False(CalendarGridMath.CoversDay(vm, new DateOnly(2026, 8, 11)));
    }

    // ── IsStartDay / IsEndDay / ShowsLabel ──────────────────────────────────────

    [Fact]
    public void IsEndDay_allday_event_is_true_on_the_day_before_the_exclusive_end()
    {
        var vm = Vm(new DateTime(2026, 8, 10), new DateTime(2026, 8, 13), isAllDay: true);
        Assert.False(CalendarGridMath.IsEndDay(vm, new DateOnly(2026, 8, 11)));
        Assert.True(CalendarGridMath.IsEndDay(vm, new DateOnly(2026, 8, 12)));
    }

    [Fact]
    public void IsEndDay_timed_event_is_always_true_on_its_only_covered_day()
    {
        var vm = Vm(new DateTime(2026, 8, 10, 9, 0, 0), new DateTime(2026, 8, 10, 10, 0, 0));
        Assert.True(CalendarGridMath.IsEndDay(vm, new DateOnly(2026, 8, 10)));
    }

    [Fact]
    public void ShowsLabel_is_true_on_start_day_and_on_the_first_column_of_a_continuing_week()
    {
        var vm = Vm(new DateTime(2026, 8, 10), new DateTime(2026, 8, 20), isAllDay: true);

        Assert.True(CalendarGridMath.ShowsLabel(vm, new DateOnly(2026, 8, 10), columnIndex: 0));
        Assert.False(CalendarGridMath.ShowsLabel(vm, new DateOnly(2026, 8, 11), columnIndex: 1));
        // A new week: not the start day, but column 0 → label repeats so the strip still reads as labelled.
        Assert.True(CalendarGridMath.ShowsLabel(vm, new DateOnly(2026, 8, 17), columnIndex: 0));
    }

    // ── EffectiveEndDate / CrossesMidnight / TimeGridOverlapsDay (issue #329 time-grid) ──────────────

    [Fact]
    public void EffectiveEndDate_treats_exact_midnight_as_the_previous_day()
    {
        // 22:00 → 00:00 ends exactly at midnight: it belongs to its start day, not the next.
        Assert.Equal(new DateOnly(2026, 8, 10), CalendarGridMath.EffectiveEndDate(new DateTime(2026, 8, 11, 0, 0, 0)));
        // A non-midnight end stays on its own day.
        Assert.Equal(new DateOnly(2026, 8, 11), CalendarGridMath.EffectiveEndDate(new DateTime(2026, 8, 11, 2, 0, 0)));
    }

    [Fact]
    public void CrossesMidnight_false_for_same_evening_event_ending_at_midnight()
    {
        var vm = Vm(new DateTime(2026, 8, 10, 22, 0, 0), new DateTime(2026, 8, 11, 0, 0, 0));
        Assert.False(CalendarGridMath.CrossesMidnight(vm)); // 22:00→00:00 keeps its drag handles
    }

    [Fact]
    public void CrossesMidnight_true_for_event_ending_after_midnight()
    {
        var vm = Vm(new DateTime(2026, 8, 10, 22, 0, 0), new DateTime(2026, 8, 11, 2, 0, 0));
        Assert.True(CalendarGridMath.CrossesMidnight(vm)); // 22:00→02:00 genuinely spans two days
    }

    [Fact]
    public void CrossesMidnight_is_false_for_all_day_events()
    {
        var vm = Vm(new DateTime(2026, 8, 10), new DateTime(2026, 8, 12), isAllDay: true);
        Assert.False(CalendarGridMath.CrossesMidnight(vm));
    }

    [Fact]
    public void TimeGridOverlapsDay_covers_every_day_a_cross_midnight_event_touches()
    {
        var vm = Vm(new DateTime(2026, 8, 10, 22, 0, 0), new DateTime(2026, 8, 11, 2, 0, 0));
        Assert.True(CalendarGridMath.TimeGridOverlapsDay(vm, new DateOnly(2026, 8, 10)));
        Assert.True(CalendarGridMath.TimeGridOverlapsDay(vm, new DateOnly(2026, 8, 11)));
        Assert.False(CalendarGridMath.TimeGridOverlapsDay(vm, new DateOnly(2026, 8, 12)));
    }

    [Fact]
    public void TimeGridOverlapsDay_midnight_end_does_not_leak_into_the_next_day()
    {
        var vm = Vm(new DateTime(2026, 8, 10, 22, 0, 0), new DateTime(2026, 8, 11, 0, 0, 0));
        Assert.True(CalendarGridMath.TimeGridOverlapsDay(vm, new DateOnly(2026, 8, 10)));
        Assert.False(CalendarGridMath.TimeGridOverlapsDay(vm, new DateOnly(2026, 8, 11)));
    }
}
