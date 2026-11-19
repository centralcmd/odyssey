using Odyssey.Context;
using Odyssey.Core.Journal;
using Xunit;
using ContextCalendarEvent = Odyssey.Context.CalendarEvent;

namespace Odyssey.Core.Tests;

/// <summary>
/// The pure rules of issue #519: zone resolution and the DST rules, the zone-aware generator and its keys, keying a
/// legacy series (§11) and reconciling a series by key-set membership alone (§3 component 2, AR1), and the view
/// zone's offset segments (v4 §1).
/// </summary>
public class CalendarRecurrenceFidelityTests
{
    private static readonly TimeZoneInfo Oslo = TimeZoneInfo.FindSystemTimeZoneById("Europe/Oslo");
    private static readonly DateTime Now = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    // ------------------------------------------------------------------ CalendarTimeZones

    [Fact]
    public void A_wall_clock_in_a_gap_moves_forward_by_the_gap_and_is_reported_adjusted()
    {
        var instant = CalendarTimeZones.ToUtc(Wall(2026, 3, 29, 2, 30), Oslo, out var adjusted);

        Assert.True(adjusted);
        Assert.Equal(Utc(2026, 3, 29, 1, 30), instant);
        Assert.Equal(Wall(2026, 3, 29, 3, 30), CalendarTimeZones.ToWall(instant, Oslo));
    }

    [Fact]
    public void An_ambiguous_wall_clock_takes_the_earlier_instant()
    {
        Assert.Equal(Utc(2026, 10, 25, 0, 30), CalendarTimeZones.ToUtc(Wall(2026, 10, 25, 2, 30), Oslo, out var adjusted));
        Assert.False(adjusted);
    }

    [Theory]
    [InlineData("Mars/Olympus")]
    [InlineData("../x")]
    [InlineData("posix/Europe/Oslo")]
    [InlineData("localtime")]
    [InlineData("W. Europe Standard Time")]
    public void A_request_zone_outside_the_iana_gate_is_a_400_keyed_on_its_field(string zone)
    {
        var ex = Assert.Throws<DomainValidationException>(() => CalendarTimeZones.ResolveRequest(zone, "defaultTimeZoneId"));
        Assert.Contains("defaultTimeZoneId", ex.Errors!.Keys);
    }

    [Theory]
    [InlineData("Europe/Oslo", "Europe/Oslo")]
    [InlineData("W. Europe Standard Time", "Europe/Berlin")]
    [InlineData("/Europe/Oslo", "Europe/Oslo")]
    [InlineData("Mars/Olympus", null)]
    [InlineData("../x", null)]
    public void A_file_tzid_resolves_an_iana_id_or_a_mapped_windows_name_and_nothing_else(string tzid, string? expected) =>
        Assert.Equal(expected, CalendarTimeZones.ResolveFileZone(tzid)?.Id);

    // ------------------------------------------------------------------ The generator

    [Fact]
    public void A_weekly_oslo_series_keeps_its_wall_clock_and_its_instants_move_at_the_march_change()
    {
        var pattern = Weekly(CalendarTimeZones.ToUtc(Wall(2026, 3, 16, 9), Oslo), "Europe/Oslo", count: 4);

        var occurrences = RecurrenceOccurrenceGenerator.Generate(pattern, 100);

        Assert.Equal([Wall(2026, 3, 16, 9), Wall(2026, 3, 23, 9), Wall(2026, 3, 30, 9), Wall(2026, 4, 6, 9)],
            occurrences.Select(o => o.Key));
        Assert.Equal([Utc(2026, 3, 16, 8), Utc(2026, 3, 23, 8), Utc(2026, 3, 30, 7), Utc(2026, 4, 6, 7)],
            occurrences.Select(o => o.Start));
        // Nominal duration (S7): 09:00–10:00 stays 09:00–10:00 on both sides of the change.
        Assert.All(occurrences, o => Assert.Equal(TimeSpan.FromHours(1), o.End - o.Start));
        Assert.All(occurrences, o => Assert.Equal(DateTimeKind.Unspecified, o.Key.Kind));
    }

    [Fact]
    public void A_weekly_monday_late_evening_los_angeles_series_steps_on_local_mondays()
    {
        var la = TimeZoneInfo.FindSystemTimeZoneById("America/Los_Angeles");
        var pattern = Weekly(CalendarTimeZones.ToUtc(Wall(2026, 3, 2, 23, 30), la), "America/Los_Angeles", count: 3);

        var occurrences = RecurrenceOccurrenceGenerator.Generate(pattern, 100);

        Assert.All(occurrences, o => Assert.Equal(DayOfWeek.Monday, o.Key.DayOfWeek));
        Assert.All(occurrences, o => Assert.Equal(DayOfWeek.Tuesday, o.Start.DayOfWeek));
    }

    [Fact]
    public void A_zoned_until_is_an_inclusive_instant()
    {
        var pattern = Weekly(CalendarTimeZones.ToUtc(Wall(2026, 3, 16, 9), Oslo), "Europe/Oslo", count: null);
        pattern.RecurrenceEndDate = Utc(2026, 3, 30, 7);

        Assert.Equal(3, RecurrenceOccurrenceGenerator.Generate(pattern, 100).Count);
    }

    [Fact]
    public void A_floating_series_keys_equal_its_stored_starts()
    {
        var pattern = Weekly(Wall(2026, 3, 16, 9), zone: null, count: 3);

        Assert.All(RecurrenceOccurrenceGenerator.Generate(pattern, 100), o => Assert.Equal(o.Key, o.Start));
    }

    // ------------------------------------------------------------------ Keying (§11)

    [Fact]
    public void Keying_matches_rows_by_start_flags_future_edits_detaches_the_rest_and_records_future_gaps()
    {
        var pattern = Weekly(Wall(2025, 12, 22, 9), zone: null, count: 6); // two past, four future
        var rows = new List<ContextCalendarEvent>
        {
            Row(pattern, Wall(2025, 12, 22, 9), title: "Past edit"),                  // past, differs: never flagged
            Row(pattern, Wall(2025, 12, 29, 9)),                                      // past
            Row(pattern, Wall(2026, 1, 5, 9), title: "Future edit"),                  // future, differs: override
            Row(pattern, Wall(2026, 1, 5, 9), createdOffsetMinutes: 5),               // duplicate start: detached
            Row(pattern, Wall(2026, 1, 12, 14)),                                      // rescheduled: detached
            Row(pattern, Wall(2026, 1, 19, 9)),
        };
        var state = new RecurrenceSeriesState { Pattern = pattern, Rows = [.. rows], Exclusions = [] };
        var added = new List<RecurrenceExclusion>();
        var version = pattern.Version;

        var detached = RecurrenceSeries.KeyInPlace(state, Now, "user", added.Add);

        Assert.Equal(2, detached);
        Assert.False(rows[0].IsOverride);
        Assert.Equal(Wall(2025, 12, 22, 9), rows[0].OccurrenceKey);
        Assert.True(rows[2].IsOverride);
        Assert.Null(rows[3].RecurrencePatternId);
        Assert.Null(rows[4].RecurrencePatternId);
        Assert.Null(rows[4].OccurrenceKey);
        // 12 Jan lost its row to the reschedule and 26 Jan never had one: both future, both recorded.
        Assert.Equal([Wall(2026, 1, 12, 9), Wall(2026, 1, 26, 9)], added.Select(x => x.OccurrenceKey).Order());
        Assert.NotNull(pattern.KeysBackfilledAt);
        Assert.NotEqual(version, pattern.Version);

        // A second run changes nothing (AC 12).
        Assert.Equal(0, RecurrenceSeries.KeyInPlace(state, Now, "user", added.Add));
        Assert.Equal(2, added.Count);
    }

    // ------------------------------------------------------------------ Reconcile (AR1)

    [Fact]
    public void A_template_edit_updates_rows_in_place_and_keeps_every_exception()
    {
        var (state, rows) = Keyed(count: 4);
        rows[1].IsOverride = true;
        rows[1].Title = "Special";
        Exclude(state, rows[2]);
        var target = RecurrenceSeries.CloneRule(state.Pattern);
        target.Description = "Agenda";

        var plan = RecurrenceSeries.Plan(state, target, Now, 100);

        Assert.Equal((0, 0, 0), (plan.Counts.DroppedOverrides, plan.Counts.DroppedExclusions, plan.Counts.RemovedRows));
        Assert.Equal(2, plan.Updates.Count); // the override and the excluded key are left alone
        Assert.Empty(plan.Inserts);
        Assert.False(plan.Counts.RemovesOrDiscards);
    }

    [Fact]
    public void Moving_mondays_to_tuesdays_drops_the_future_exceptions_and_replaces_the_rows()
    {
        var (state, rows) = Keyed(count: 4);
        rows[1].IsOverride = true;
        Exclude(state, rows[2]);
        var target = RecurrenceSeries.CloneRule(state.Pattern);
        target.DaysOfWeek = DaysOfWeekFlags.Tuesday;

        var plan = RecurrenceSeries.Plan(state, target, Now, 100);

        Assert.Equal(1, plan.Counts.DroppedOverrides);
        Assert.Equal(1, plan.Counts.DroppedExclusions);
        Assert.Equal(2, plan.Counts.RemovedRows);
        Assert.Equal(4, plan.Inserts.Count);
        Assert.True(plan.Counts.RemovesOrDiscards);
    }

    [Fact]
    public void An_interval_change_drops_exactly_the_keys_that_leave_the_set()
    {
        var (state, rows) = Keyed(count: null, until: Wall(2026, 3, 9, 9)); // weeks 0..5
        rows[1].IsOverride = true;  // odd
        rows[2].IsOverride = true;  // even
        Exclude(state, rows[3]);    // odd
        Exclude(state, rows[4]);    // even
        var target = RecurrenceSeries.CloneRule(state.Pattern);
        target.Interval = 2;

        var plan = RecurrenceSeries.Plan(state, target, Now, 100);

        Assert.Equal(1, plan.Counts.DroppedOverrides);
        Assert.Equal(1, plan.Counts.DroppedExclusions);
        Assert.Equal([rows[1].OccurrenceKey, rows[5].OccurrenceKey], plan.RowRemovals.Select(r => r.OccurrenceKey).Order());
    }

    [Fact]
    public void A_zone_change_keeps_every_key_and_reinterprets_each_row_by_its_wall_clock()
    {
        var (state, rows) = Keyed(count: 3);
        rows[1].IsOverride = true;
        var target = RecurrenceSeries.CloneRule(state.Pattern);
        target.TimeZoneId = "Europe/Oslo";
        target.StartDateTime = CalendarTimeZones.ToUtc(state.Pattern.StartDateTime, Oslo);
        target.EndDateTime = CalendarTimeZones.ToUtc(state.Pattern.EndDateTime, Oslo);

        var plan = RecurrenceSeries.Plan(state, target, Now, 100);

        Assert.Equal((0, 0, 0), (plan.Counts.DroppedOverrides, plan.Counts.DroppedExclusions, plan.Counts.RemovedRows));
        Assert.Equal(3, plan.Reinterpretations.Count);
        Assert.Contains(plan.Reinterpretations, r => r.Row == rows[1] && r.Start == Utc(2026, 2, 9, 8));
    }

    [Fact]
    public void Past_keys_and_rows_are_never_touched()
    {
        var (state, rows) = Keyed(count: 4, start: Wall(2025, 12, 22, 9)); // 22 and 29 Dec are past
        Exclude(state, rows[1]);
        var target = RecurrenceSeries.CloneRule(state.Pattern);
        target.DaysOfWeek = DaysOfWeekFlags.Friday;

        var plan = RecurrenceSeries.Plan(state, target, Now, 100);

        Assert.DoesNotContain(plan.RowRemovals, r => r.OccurrenceKey < Wall(2026, 1, 1, 12));
        Assert.Equal(0, plan.Counts.DroppedExclusions);
    }

    // ------------------------------------------------------------------ The window (v4 §1)

    [Fact]
    public void The_view_zone_segments_cover_a_dst_change_with_the_larger_offset_as_the_bound()
    {
        var segments = CalendarWindow.Segments(Utc(2026, 3, 20, 0), Utc(2026, 4, 5, 0), Oslo)!;

        Assert.Equal(2, segments.Count);
        Assert.Equal(60, segments[0].OffsetMinutes);
        Assert.Equal(120, segments[1].OffsetMinutes);
        // The transition is 01:00Z on 29 March; a wall-clock in the gap (02:xx) still takes the pre-transition offset.
        Assert.Equal(Wall(2026, 3, 29, 3), segments[0].WallUpper);
    }

    [Fact]
    public void A_utc_view_needs_no_segments_and_a_window_with_too_many_changes_falls_back_to_utc_ordering()
    {
        Assert.Single(CalendarWindow.Segments(Utc(2026, 3, 1, 0), Utc(2026, 4, 1, 0), TimeZoneInfo.Utc)!);
        Assert.Null(CalendarWindow.Segments(Utc(1990, 1, 1, 0), Utc(2026, 1, 1, 0), Oslo));
    }

    // ------------------------------------------------------------------ helpers

    private static DateTime Wall(int y, int m, int d, int h, int min = 0) => new(y, m, d, h, min, 0, DateTimeKind.Unspecified);

    private static DateTime Utc(int y, int m, int d, int h, int min = 0) => new(y, m, d, h, min, 0, DateTimeKind.Utc);

    private static RecurrencePattern Weekly(DateTime start, string? zone, int? count) => new()
    {
        RecurrencePatternId = Guid.NewGuid(), CalendarId = Guid.Empty, Title = "Standup",
        StartDateTime = start, EndDateTime = start.AddHours(1), TimeZoneId = zone,
        Frequency = RecurrenceFrequency.Weekly, Interval = 1, DaysOfWeek = DaysOfWeekFlags.Monday,
        OccurrenceCount = count, Version = Guid.NewGuid(),
    };

    private static ContextCalendarEvent Row(RecurrencePattern pattern, DateTime start, string? title = null, int createdOffsetMinutes = 0) => new()
    {
        CalendarEventId = Guid.NewGuid(), CalendarId = pattern.CalendarId, Title = title ?? pattern.Title,
        StartDateTime = start, EndDateTime = start.AddHours(1), RecurrencePatternId = pattern.RecurrencePatternId,
        CreatedAt = Now.AddDays(-30).AddMinutes(createdOffsetMinutes),
    };

    // A keyed floating series of weekly Mondays from 2 February 2026 (all future), with its rows.
    private static (RecurrenceSeriesState State, List<ContextCalendarEvent> Rows) Keyed(
        int? count, DateTime? until = null, DateTime? start = null)
    {
        var pattern = Weekly(start ?? Wall(2026, 2, 2, 9), zone: null, count);
        pattern.RecurrenceEndDate = until;
        pattern.KeysBackfilledAt = Now;
        var rows = RecurrenceOccurrenceGenerator.Generate(pattern, 100)
            .Select(o => RecurrenceSeries.NewOccurrence(pattern, o, "user", Now))
            .ToList();
        return (new RecurrenceSeriesState { Pattern = pattern, Rows = [.. rows], Exclusions = [] }, rows);
    }

    private static void Exclude(RecurrenceSeriesState state, ContextCalendarEvent row)
    {
        state.Rows.Remove(row);
        state.Exclusions.Add(new RecurrenceExclusion
        {
            RecurrencePatternId = state.Pattern.RecurrencePatternId, OccurrenceKey = row.OccurrenceKey!.Value,
        });
    }
}
