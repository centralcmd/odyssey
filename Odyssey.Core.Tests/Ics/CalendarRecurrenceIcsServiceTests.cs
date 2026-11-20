using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Odyssey.Core.Journal;
using Odyssey.Dtos.Journal;
using Xunit;
using ContextCalendarEvent = Odyssey.Context.CalendarEvent;
using DtoDays = Odyssey.Dtos.Journal.DaysOfWeekFlags;
using DtoFrequency = Odyssey.Dtos.Journal.RecurrenceFrequency;
using IcalCalendar = Ical.Net.Calendar;
using IcalEvent = Ical.Net.CalendarComponents.CalendarEvent;

namespace Odyssey.Core.Tests.Ics;

/// <summary>
/// The <c>.ics</c> half of calendar recurrence fidelity (issue #519) at the service tier: the zoned export with its
/// exclusion and override, the grouped import of a stored series (overrides, local edits and deletions, the removal
/// gate, past keys) and the import's zone mapping. The series writes over HTTP stay in
/// <c>Odyssey.Api.Tests/CalendarRecurrenceFidelityApiTests</c>; the series lock and the races are in
/// <c>Odyssey.IntegrationTests</c>.
/// </summary>
public class CalendarRecurrenceIcsServiceTests : IcsServiceTestBase
{
    // Every test starts at this instant, so "future" is decided by the test and not by the wall clock.
    private readonly FakeTimeProvider clock = new(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));

    protected override TimeProvider? ServiceClock => clock;

    // ------------------------------------------------------------------ AC 8: round trip

    [Fact]
    public async Task A_zoned_series_with_an_exclusion_and_an_override_round_trips_in_either_component_order()
    {
        var calendarId = await CreateCalendarAsync("Round trip");
        var seriesId = (await InScopeAsync(sp => sp.GetRequiredService<RecurrencePatternService>().Create(new NewRecurrencePattern
        {
            CalendarId = calendarId, Title = "Standup",
            StartDateTime = Wall(2026, 3, 16, 9), EndDateTime = Wall(2026, 3, 16, 10), TimeZoneId = "Europe/Oslo",
            Frequency = DtoFrequency.Weekly, DaysOfWeek = DtoDays.Monday, OccurrenceCount = 4,
        }, ActorUserId))).RecurrencePatternId;
        var rows = await SeriesRowsAsync(seriesId);
        await DeleteAsync(rows[1].CalendarEventId);
        await UpdateEventAsync(rows[2].CalendarEventId, new NewCalendarEvent
        {
            CalendarId = calendarId, Title = "Moved standup", StartDateTime = Wall(2026, 3, 30, 10),
            EndDateTime = Wall(2026, 3, 30, 11), TimeZoneId = "Europe/Oslo",
        });

        var body = await ExportAsync(CalendarIcsExportScope.ForPattern(seriesId));

        Assert.Contains("DTSTART;TZID=Europe/Oslo:20260316T090000", body);
        Assert.Contains("RRULE:FREQ=WEEKLY;COUNT=4;BYDAY=MO", body);
        Assert.Contains("EXDATE;TZID=Europe/Oslo:20260323T090000", body);
        Assert.Contains("RECURRENCE-ID;TZID=Europe/Oslo:20260330T090000", body);
        Assert.Equal(1, Count(body, "BEGIN:VTIMEZONE"));
        Assert.Equal(2, Count(body, "BEGIN:VEVENT"));

        // S9: an independent reader expands it to exactly the hand-written list.
        var expanded = IcalCalendar.Load(body)!.GetOccurrences<IcalEvent>()
            .Select(o => (o.Period.StartTime.AsUtc, ((IcalEvent)o.Source).Summary))
            .OrderBy(o => o.AsUtc)
            .ToList();
        Assert.Equal(
            [(Utc(2026, 3, 16, 8), "Standup"), (Utc(2026, 3, 30, 8), "Moved standup"), (Utc(2026, 4, 6, 7), "Standup")],
            expanded);

        var components = SplitComponents(body);
        foreach (var order in new[] { body, Reassemble(body, components.AsEnumerable().Reverse()) })
        {
            var target = await CreateCalendarAsync($"Target {Guid.NewGuid():N}");
            var result = await ImportAsync(target, order);

            Assert.Equal((1, 1, 1), (result.ImportedCount, result.OverridesApplied, result.ExclusionsApplied));
            Assert.Empty(result.Skipped);
            await WithContextAsync(async db =>
            {
                var imported = await db.RecurrencePatterns.SingleAsync(p => p.CalendarId == target);
                Assert.Equal("Europe/Oslo", imported.TimeZoneId);
                var importedRows = await db.CalendarEvents.Where(e => e.RecurrencePatternId == imported.RecurrencePatternId)
                    .OrderBy(e => e.OccurrenceKey).ToListAsync();
                Assert.Equal([Wall(2026, 3, 16, 9), Wall(2026, 3, 30, 9), Wall(2026, 4, 6, 9)], importedRows.Select(r => r.OccurrenceKey!.Value));
                var moved = Assert.Single(importedRows, r => r.IsOverride);
                Assert.Equal("Moved standup", moved.Title);
                Assert.Equal(Utc(2026, 3, 30, 8), moved.StartDateTime);
                Assert.Equal(Wall(2026, 3, 23, 9),
                    (await db.RecurrenceExclusions.SingleAsync(x => x.RecurrencePatternId == imported.RecurrencePatternId)).OccurrenceKey);
            });
        }
    }

    // ------------------------------------------------------------------ AC 9, 10, 11: import of a stored series

    [Fact]
    public async Task An_override_only_file_updates_the_stored_occurrence_and_marks_it_an_override()
    {
        var calendarId = await CreateCalendarAsync("Override only");
        await ImportAsync(calendarId, Vcalendar(Master("ext-9")));

        var result = await ImportAsync(calendarId, Vcalendar(Vevent("ext-9",
            "RECURRENCE-ID:20260209T090000", "DTSTART:20260209T110000", "DTEND:20260209T120000", "SUMMARY:Later today")));

        Assert.Equal(1, result.OverridesApplied);
        Assert.Empty(result.Skipped);
        var changed = Assert.Single(await StoredRowsAsync(calendarId), r => r.IsOverride);
        Assert.Equal("Later today", changed.Title);
        Assert.Equal(Wall(2026, 2, 9, 11), changed.StartDateTime);
        Assert.Equal(Wall(2026, 2, 9, 9), changed.OccurrenceKey);
    }

    [Fact]
    public async Task A_reimport_keeps_a_local_edit_and_a_local_deletion()
    {
        var calendarId = await CreateCalendarAsync("Keep local");
        var file = Vcalendar(Master("ext-10"));
        await ImportAsync(calendarId, file);
        var rows = await StoredRowsAsync(calendarId);
        await EditTitleAsync(rows[0], "Local edit");
        await DeleteAsync(rows[1].CalendarEventId);

        var result = await ImportAsync(calendarId, file);

        Assert.Equal((1, 0), (result.UpdatedCount, result.GatedSeriesCount));
        var after = await StoredRowsAsync(calendarId);
        Assert.Equal(3, after.Count);
        Assert.Equal("Local edit", after.Single(r => r.IsOverride).Title);
    }

    [Fact]
    public async Task A_rule_change_that_would_drop_a_stored_override_is_gated_and_applies_with_allow_removals()
    {
        var calendarId = await CreateCalendarAsync("Gated");
        await ImportAsync(calendarId, Vcalendar(Master("ext-11")));
        await EditTitleAsync((await StoredRowsAsync(calendarId))[1], "Local edit");
        var tuesdays = Vcalendar(Master("ext-11", "RRULE:FREQ=WEEKLY;COUNT=4;BYDAY=TU"));

        var gated = await ImportAsync(calendarId, tuesdays);

        Assert.Equal(1, gated.GatedSeriesCount);
        Assert.Equal(CalendarIcsService.GatedReason, Assert.Single(gated.Skipped).Reason);
        Assert.All(await StoredRowsAsync(calendarId), r => Assert.Equal(DayOfWeek.Monday, r.StartDateTime.DayOfWeek));

        var applied = await ImportAsync(calendarId, tuesdays, allowRemovals: true);

        Assert.Equal((0, 1), (applied.GatedSeriesCount, applied.DroppedOverrideCount));
        Assert.All(await StoredRowsAsync(calendarId), r => Assert.Equal(DayOfWeek.Tuesday, r.StartDateTime.DayOfWeek));
        var audit = Assert.Single((await HostAsync()).Logs.Entries,
            e => e.Category == typeof(CalendarIcsService).FullName && e.Message.Contains("discarded"));
        Assert.Contains("2026-02-09T09:00:00", audit.Message);
    }

    [Fact]
    public async Task An_exdate_on_a_past_key_with_a_stored_row_is_ignored_and_counted()
    {
        var calendarId = await CreateCalendarAsync("Past");
        await ImportAsync(calendarId, Vcalendar(Master("ext-12")));
        clock.SetUtcNow(new DateTimeOffset(2026, 2, 20, 0, 0, 0, TimeSpan.Zero));

        var result = await ImportAsync(calendarId,
            Vcalendar(Master("ext-12", "RRULE:FREQ=WEEKLY;COUNT=4", "EXDATE:20260209T090000")), allowRemovals: true);

        Assert.Equal(0, result.ExclusionsApplied);
        Assert.Equal(CalendarIcsService.PastRemovalReason, Assert.Single(result.Skipped).Reason);
        Assert.Equal(4, (await StoredRowsAsync(calendarId)).Count);
    }

    // ------------------------------------------------------------------ AC 28: a single exported occurrence

    [Fact]
    public async Task Reimporting_one_exported_occurrence_makes_it_an_override_that_a_series_edit_keeps()
    {
        var calendarId = await CreateCalendarAsync("Single");
        var seriesId = (await InScopeAsync(sp => sp.GetRequiredService<RecurrencePatternService>().Create(Rule(calendarId, 3), ActorUserId)))
            .RecurrencePatternId;
        var rows = await SeriesRowsAsync(seriesId);
        var exported = await ExportAsync(CalendarIcsExportScope.ForEvent(rows[1].CalendarEventId));

        var result = await ImportAsync(calendarId, exported.Replace("SUMMARY:Standup", "SUMMARY:From file"));
        Assert.Equal(1, result.UpdatedCount);
        Assert.NotNull(await InScopeAsync(sp => sp.GetRequiredService<RecurrencePatternService>().Update(
            seriesId, Rule(calendarId, 3) with { Description = "Agenda" }, ActorUserId)));

        var kept = Assert.Single(await SeriesRowsAsync(seriesId), r => r.IsOverride);
        Assert.Equal("From file", kept.Title);
        Assert.Equal(rows[1].CalendarEventId, kept.CalendarEventId);
    }

    // ------------------------------------------------------------------ Import zones

    [Fact]
    public async Task An_unknown_tzid_is_skipped_and_a_windows_tzid_is_mapped_and_a_floating_time_takes_the_default_zone()
    {
        var calendarId = await CreateCalendarAsync("Import zones");
        var file = Vcalendar(
            Vevent("mars", "DTSTART;TZID=Mars/Olympus:20300601T090000", "DTEND;TZID=Mars/Olympus:20300601T100000", "SUMMARY:Mars"),
            Vevent("win", "DTSTART;TZID=W. Europe Standard Time:20300601T090000", "DTEND;TZID=W. Europe Standard Time:20300601T100000", "SUMMARY:Windows"),
            Vevent("zulu", "DTSTART:20300601T090000Z", "DTEND:20300601T100000Z", "SUMMARY:Zulu"),
            Vevent("float", "DTSTART:20300601T090000", "DTEND:20300601T100000", "SUMMARY:Floating"));

        var result = await ImportAsync(calendarId, file, defaultTimeZoneId: "Asia/Tokyo");

        Assert.Equal(3, result.ImportedCount);
        var skip = Assert.Single(result.Skipped);
        Assert.Equal(CalendarIcsService.UnknownTimeZoneReason, skip.Reason);
        Assert.DoesNotContain("Mars", skip.Reason);
        var rows = (await StoredRowsAsync(calendarId)).ToDictionary(r => r.Title);
        Assert.Equal("Europe/Berlin", rows["Windows"].TimeZoneId);
        Assert.Equal(Utc(2030, 6, 1, 7), rows["Windows"].StartDateTime);
        Assert.Equal("UTC", rows["Zulu"].TimeZoneId);
        Assert.Equal("Asia/Tokyo", rows["Floating"].TimeZoneId);
        Assert.Equal(Utc(2030, 6, 1, 0), rows["Floating"].StartDateTime);
    }

    // ------------------------------------------------------------------ helpers

    private static DateTime Wall(int year, int month, int day, int hour, int minute = 0) =>
        new(year, month, day, hour, minute, 0, DateTimeKind.Unspecified);

    private static DateTime Utc(int year, int month, int day, int hour, int minute = 0) =>
        new(year, month, day, hour, minute, 0, DateTimeKind.Utc);

    // A floating weekly Monday 09:00 series from 2 February 2026: every occurrence is after the starting clock.
    private static NewRecurrencePattern Rule(Guid calendarId, int? count) => new()
    {
        CalendarId = calendarId, Title = "Standup",
        StartDateTime = Wall(2026, 2, 2, 9), EndDateTime = Wall(2026, 2, 2, 9, 30),
        Frequency = DtoFrequency.Weekly, DaysOfWeek = DtoDays.Monday, OccurrenceCount = count,
    };

    private static string Master(string uid, params string[] rule) => Vevent(uid,
        [
            "DTSTART:20260202T090000", "DTEND:20260202T093000", "SUMMARY:Standup",
            .. rule.Length == 0 ? ["RRULE:FREQ=WEEKLY;COUNT=4"] : rule,
        ]);

    private static string Vevent(string uid, params string[] lines) =>
        "BEGIN:VEVENT\r\nUID:" + uid + "\r\n" + string.Join("\r\n", lines) + "\r\nEND:VEVENT\r\n";

    private static string Vcalendar(params string[] vevents) =>
        "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//test//EN\r\n" + string.Concat(vevents) + "END:VCALENDAR\r\n";

    private static List<string> SplitComponents(string body)
    {
        var parts = new List<string>();
        var at = 0;
        while ((at = body.IndexOf("BEGIN:VEVENT", at, StringComparison.Ordinal)) >= 0)
        {
            var end = body.IndexOf("END:VEVENT\r\n", at, StringComparison.Ordinal) + "END:VEVENT\r\n".Length;
            parts.Add(body[at..end]);
            at = end;
        }

        return parts;
    }

    private static string Reassemble(string body, IEnumerable<string> components)
    {
        var head = body[..body.IndexOf("BEGIN:VEVENT", StringComparison.Ordinal)];
        return head + string.Concat(components) + "END:VCALENDAR\r\n";
    }

    private async Task<Guid> CreateCalendarAsync(string name) =>
        (await InScopeAsync(sp => sp.GetRequiredService<CalendarService>().Create(new NewCalendar { Name = name, Color = "#0369A1" }, ActorUserId)))
        .CalendarId;

    private Task<List<ContextCalendarEvent>> SeriesRowsAsync(Guid patternId) =>
        WithContextAsync(db => db.CalendarEvents.AsNoTracking().Where(e => e.RecurrencePatternId == patternId).OrderBy(e => e.OccurrenceKey).ToListAsync());

    private Task<List<ContextCalendarEvent>> StoredRowsAsync(Guid calendarId) =>
        WithContextAsync(db => db.CalendarEvents.AsNoTracking().Where(e => e.CalendarId == calendarId).OrderBy(e => e.StartDateTime).ToListAsync());

    private async Task UpdateEventAsync(Guid id, NewCalendarEvent request) =>
        Assert.NotNull(await InScopeAsync(sp => sp.GetRequiredService<CalendarEventService>().Update(id, request, ActorUserId)));

    // The row is floating, so its stored start is the wall-clock the edit sends back.
    private async Task EditTitleAsync(ContextCalendarEvent row, string title)
    {
        var updated = await InScopeAsync(sp => sp.GetRequiredService<CalendarEventService>().Update(row.CalendarEventId, new NewCalendarEvent
        {
            CalendarId = row.CalendarId, Title = title, StartDateTime = row.StartDateTime, EndDateTime = row.EndDateTime,
            IsAllDay = row.IsAllDay, TimeZoneId = row.TimeZoneId,
        }, ActorUserId));
        Assert.True(updated!.IsOverride);
    }

    private async Task DeleteAsync(Guid eventId) =>
        Assert.NotNull(await InScopeAsync(sp => sp.GetRequiredService<CalendarEventService>().Delete(eventId, userId: ActorUserId)));

    private async Task<IcsImportResult> ImportAsync(Guid calendarId, string ics, bool allowRemovals = false, string? defaultTimeZoneId = null)
    {
        await using var stream = Utf8(ics);
        return await InScopeAsync(sp => sp.GetRequiredService<CalendarIcsService>().ImportAsync(
            calendarId, stream, stream.Length, "text/calendar", ActorUserId,
            allowRemovals: allowRemovals, canDelete: true, defaultTimeZoneId: defaultTimeZoneId));
    }

    private async Task<string> ExportAsync(CalendarIcsExportScope scope)
    {
        var output = new MemoryStream();
        Assert.True(await InScopeAsync(sp => sp.GetRequiredService<CalendarIcsService>().ExportStreamingAsync(scope, output, (_, _) => { })));
        return Encoding.UTF8.GetString(output.ToArray());
    }
}
