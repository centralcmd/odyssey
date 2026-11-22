using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Odyssey.Core.Journal;
using Odyssey.Dtos.Journal;
using Xunit;

namespace Odyssey.Core.Tests.Ics;

/// <summary>
/// Calendar .ics import and export rules (issue #330 and its follow-ups) at the service tier: how a series is written
/// (one RRULE, a flattened clamped rule, an override as a RECURRENCE-ID), how VEVENTs map on import (zones, all-day,
/// UIDs, EXDATEs, unmappable rules) and the import caps. The endpoints' claim gates, status codes, headers and the
/// transport limits stay in <c>Odyssey.Api.Tests/CalendarIcsApiTests</c>; what needs real MariaDB is in
/// <c>Odyssey.IntegrationTests</c>.
/// </summary>
public class CalendarIcsServiceTests : IcsServiceTestBase
{
    private const int TestMaxImportEvents = 5;

    // ------------------------------------------------------------------ export

    [Fact]
    public async Task Export_EmptyCalendar_IsAValidVCalendar_WithNoVEvent()
    {
        var calendarId = await CreateCalendarAsync("Empty");

        var body = await ExportAsync(calendarId);

        Assert.Contains("BEGIN:VCALENDAR", body);
        Assert.Contains("END:VCALENDAR", body);
        Assert.DoesNotContain("BEGIN:VEVENT", body);
    }

    [Fact]
    public async Task Export_UnmodifiedSeries_EmitsSingleRRule()
    {
        var calendarId = await CreateCalendarAsync("Series");
        await CreatePatternAsync(new NewRecurrencePattern
        {
            CalendarId = calendarId,
            Title = "Standup",
            StartDateTime = new DateTime(2030, 1, 7, 9, 0, 0, DateTimeKind.Utc),
            EndDateTime = new DateTime(2030, 1, 7, 9, 30, 0, DateTimeKind.Utc),
            Frequency = RecurrenceFrequency.Weekly,
            DaysOfWeek = DaysOfWeekFlags.Monday,
            OccurrenceCount = 6,
        });

        var body = await ExportAsync(calendarId);

        Assert.Equal(1, Count(body, "BEGIN:VEVENT"));
        Assert.Contains("RRULE:", body);
        Assert.Contains("FREQ=WEEKLY", body);
    }

    [Fact]
    public async Task Export_UnclampedMonthlySeries_EmitsSingleRRule()
    {
        var calendarId = await CreateCalendarAsync("Monthly");
        // DayOfMonth=15 never clamps (every month has a 15th), so the stored rows match the unclamped RFC projection
        // exactly → a single RRULE VEVENT.
        await CreatePatternAsync(new NewRecurrencePattern
        {
            CalendarId = calendarId,
            Title = "Rent",
            StartDateTime = new DateTime(2030, 1, 15, 9, 0, 0, DateTimeKind.Utc),
            EndDateTime = new DateTime(2030, 1, 15, 10, 0, 0, DateTimeKind.Utc),
            Frequency = RecurrenceFrequency.Monthly,
            DayOfMonth = 15,
            OccurrenceCount = 4,
        });

        var body = await ExportAsync(calendarId);

        Assert.Equal(1, Count(body, "BEGIN:VEVENT"));
        Assert.Contains("FREQ=MONTHLY", body);
        Assert.Contains("BYMONTHDAY=15", body);
    }

    [Fact]
    public async Task Export_ClampedMonthlySeries_FlattensToStandaloneVEvents()
    {
        var calendarId = await CreateCalendarAsync("Clamped");
        // DayOfMonth=31 starting Jan 31: the generator clamps Feb→28 and Apr→30, so the stored dates differ from a strict
        // RFC reading of the same rule (which would skip those months). Exporting must NOT assert an RRULE an external
        // reader would interpret differently — it flattens.
        await CreatePatternAsync(new NewRecurrencePattern
        {
            CalendarId = calendarId,
            Title = "Month end",
            StartDateTime = new DateTime(2030, 1, 31, 9, 0, 0, DateTimeKind.Utc),
            EndDateTime = new DateTime(2030, 1, 31, 10, 0, 0, DateTimeKind.Utc),
            Frequency = RecurrenceFrequency.Monthly,
            DayOfMonth = 31,
            OccurrenceCount = 4,
        });

        var body = await ExportAsync(calendarId);

        Assert.Equal(4, Count(body, "BEGIN:VEVENT"));
        Assert.DoesNotContain("RRULE:", body);
        // The flattened rows carry the generator's clamped dates, not just the right count (issue #243). A series written
        // without a zone is floating, so its times are local date-times with no Z (issue #519 AC 3).
        var starts = Regex.Matches(body, @"DTSTART[^:\r\n]*:(\d{8}T\d{6}Z?)").Select(m => m.Groups[1].Value).Order();
        Assert.Equal(["20300131T090000", "20300228T090000", "20300331T090000", "20300430T090000"], starts);
    }

    [Fact]
    public async Task Export_AllDayRecurringSeries_UntilIsDateValued()
    {
        var calendarId = await CreateCalendarAsync("AllDaySeries");
        // All-day weekly series bounded by RecurrenceEndDate → the exported RRULE UNTIL must be a DATE (yyyyMMdd), not a
        // DATE-TIME, to match the VALUE=DATE DTSTART (RFC 5545 §3.3.10).
        await CreatePatternAsync(new NewRecurrencePattern
        {
            CalendarId = calendarId,
            Title = "Bin day",
            StartDateTime = new DateTime(2030, 1, 7, 0, 0, 0, DateTimeKind.Utc),
            EndDateTime = new DateTime(2030, 1, 8, 0, 0, 0, DateTimeKind.Utc),
            IsAllDay = true,
            Frequency = RecurrenceFrequency.Weekly,
            DaysOfWeek = DaysOfWeekFlags.Monday,
            RecurrenceEndDate = new DateTime(2030, 2, 25, 0, 0, 0, DateTimeKind.Utc),
        });

        var body = await ExportAsync(calendarId);

        Assert.Contains("RRULE:", body);
        Assert.Matches(@"UNTIL=\d{8}(;|\r|\n)", body); // DATE form
        Assert.DoesNotMatch(@"UNTIL=\d{8}T", body);     // never a DATE-TIME under an all-day DTSTART
    }

    [Fact]
    public async Task Export_EditedOccurrence_IsARecurrenceIdComponentOfTheRule()
    {
        var calendarId = await CreateCalendarAsync("Edited");
        var pattern = await CreatePatternAsync(new NewRecurrencePattern
        {
            CalendarId = calendarId,
            Title = "Standup",
            StartDateTime = new DateTime(2030, 1, 7, 9, 0, 0, DateTimeKind.Utc),
            EndDateTime = new DateTime(2030, 1, 7, 9, 30, 0, DateTimeKind.Utc),
            Frequency = RecurrenceFrequency.Weekly,
            DaysOfWeek = DaysOfWeekFlags.Monday,
            OccurrenceCount = 3,
        });
        var firstEventId = await WithContextAsync(c => c.CalendarEvents
            .Where(e => e.RecurrencePatternId == pattern.RecurrencePatternId).OrderBy(e => e.StartDateTime)
            .Select(e => e.CalendarEventId).FirstAsync());

        // Individually edit one generated occurrence's title.
        await UpdateEventAsync(firstEventId, new NewCalendarEvent
        {
            CalendarId = calendarId,
            Title = "Standup (special)",
            StartDateTime = new DateTime(2030, 1, 7, 9, 0, 0, DateTimeKind.Utc),
            EndDateTime = new DateTime(2030, 1, 7, 9, 30, 0, DateTimeKind.Utc),
        });

        var body = await ExportAsync(calendarId);

        // Issue #519 §8.3: the edit no longer flattens the series — it is one RECURRENCE-ID component beside the master.
        Assert.Equal(2, Count(body, "BEGIN:VEVENT"));
        Assert.Equal(1, Count(body, "RRULE:"));
        Assert.Contains("RECURRENCE-ID:20300107T090000", body);
        Assert.Contains("SUMMARY:Standup (special)", body);
    }

    // ------------------------------------------------------------------ import

    [Fact]
    public async Task Import_ThreeStandaloneEvents_CreatesThreeRowsWithExternalUid()
    {
        var calendarId = await CreateCalendarAsync("Import");

        var result = await ImportAsync(calendarId, Vcalendar(
            Vevent("a1", "DTSTART:20300101T090000Z", "DTEND:20300101T100000Z", "SUMMARY:One"),
            Vevent("a2", "DTSTART:20300102T090000Z", "DTEND:20300102T100000Z", "SUMMARY:Two"),
            Vevent("a3", "DTSTART:20300103T090000Z", "DTEND:20300103T100000Z", "SUMMARY:Three")));

        Assert.Equal((3, 0), (result.ImportedCount, result.UpdatedCount));
        Assert.Empty(result.Skipped);
        var rows = await EventsAsync(calendarId);
        Assert.Equal(3, rows.Count);
        Assert.All(rows, r => Assert.False(string.IsNullOrEmpty(r.ExternalUid)));
    }

    // Security review (PR #345, F1): a synthetic "{primary-key}@odyssey.local" UID must never be persisted as
    // ExternalUid, even when it belongs to no row in the target calendar (e.g. it arrived via an aggregate export's
    // cross-calendar file). Persisting it would let a later export of *this* row carry that foreign synthetic UID, and a
    // re-import into the UID's true origin calendar would then match and silently overwrite the unrelated original row.
    [Fact]
    public async Task Import_ForeignSyntheticUid_DoesNotPersistAsExternalUid()
    {
        var calendarId = await CreateCalendarAsync("ForeignSynthetic");

        var result = await ImportAsync(calendarId, Vcalendar(
            Vevent($"{Guid.NewGuid()}@odyssey.local", "DTSTART:20300101T090000Z", "DTEND:20300101T100000Z", "SUMMARY:Foreign")));

        Assert.Equal(1, result.ImportedCount);
        Assert.Null(Assert.Single(await EventsAsync(calendarId)).ExternalUid);
    }

    [Fact]
    public async Task Import_ExportedFile_IsIdempotent_CreatesZeroNewRows()
    {
        var calendarId = await CreateCalendarAsync("RoundTrip");
        await InScopeAsync(sp => sp.GetRequiredService<CalendarEventService>().Create(new NewCalendarEvent
        {
            CalendarId = calendarId,
            Title = "Lunch",
            StartDateTime = new DateTime(2030, 5, 1, 11, 0, 0, DateTimeKind.Utc),
            EndDateTime = new DateTime(2030, 5, 1, 12, 0, 0, DateTimeKind.Utc),
        }, ActorUserId));
        await CreatePatternAsync(new NewRecurrencePattern
        {
            CalendarId = calendarId,
            Title = "Weekly",
            StartDateTime = new DateTime(2030, 6, 2, 9, 0, 0, DateTimeKind.Utc),
            EndDateTime = new DateTime(2030, 6, 2, 10, 0, 0, DateTimeKind.Utc),
            Frequency = RecurrenceFrequency.Weekly,
            DaysOfWeek = DaysOfWeekFlags.Monday,
            OccurrenceCount = 5,
        });
        var patternsBefore = await WithContextAsync(c => c.RecurrencePatterns.CountAsync(p => p.CalendarId == calendarId));
        var eventsBefore = (await EventsAsync(calendarId)).Count;

        var result = await ImportAsync(calendarId, await ExportAsync(calendarId));

        Assert.Equal((0, 2), (result.ImportedCount, result.UpdatedCount)); // the standalone event + the series (1 RRULE VEVENT)
        Assert.Equal(patternsBefore, await WithContextAsync(c => c.RecurrencePatterns.CountAsync(p => p.CalendarId == calendarId)));
        Assert.Equal(eventsBefore, (await EventsAsync(calendarId)).Count);
    }

    [Fact]
    public async Task Import_WeeklyCountNoByDay_Succeeds()
    {
        var calendarId = await CreateCalendarAsync("WeeklyDefault");

        var result = await ImportAsync(calendarId, Vcalendar(Vevent("w1",
            "DTSTART:20300106T090000Z", "DTEND:20300106T100000Z", "SUMMARY:Weekly", "RRULE:FREQ=WEEKLY;COUNT=6")));

        Assert.Equal(1, result.ImportedCount);
        Assert.Empty(result.Skipped);
        Assert.Equal(6, (await EventsAsync(calendarId)).Count);
    }

    [Fact]
    public async Task Import_WkstNonDefault_IsAccepted()
    {
        var calendarId = await CreateCalendarAsync("Wkst");

        var result = await ImportAsync(calendarId, Vcalendar(Vevent("k1",
            "DTSTART:20300106T090000Z", "DTEND:20300106T100000Z", "SUMMARY:Wkst", "RRULE:FREQ=WEEKLY;COUNT=3;WKST=SU")));

        Assert.Equal(1, result.ImportedCount);
        Assert.Empty(result.Skipped);
    }

    [Fact]
    public async Task Import_ExdateOnSeries_ImportsTheSeriesWithItsExclusion()
    {
        var calendarId = await CreateCalendarAsync("Exdate");

        var result = await ImportAsync(calendarId, Vcalendar(Vevent("e1",
            "DTSTART:20300101T090000Z", "DTEND:20300101T100000Z", "SUMMARY:Ex", "RRULE:FREQ=DAILY;COUNT=5", "EXDATE:20300103T090000Z")));

        // Issue #519 §8.4 supersedes the Spec A skip: the EXDATE is an exclusion of the new series.
        Assert.Equal((1, 1), (result.ImportedCount, result.ExclusionsApplied));
        Assert.Empty(result.Skipped);
        Assert.Equal(4, (await EventsAsync(calendarId)).Count);
        Assert.Equal(new DateTime(2030, 1, 3, 9, 0, 0), (await WithContextAsync(c => c.RecurrenceExclusions.SingleAsync())).OccurrenceKey);
    }

    [Fact]
    public async Task Import_DuplicateUidInFile_LastComponentWins()
    {
        // Issue #516 R2: no whole-file UID set; a UID seen again updates what the earlier component wrote.
        var calendarId = await CreateCalendarAsync("Dupe");

        var result = await ImportAsync(calendarId, Vcalendar(
            Vevent("same", "DTSTART:20300101T090000Z", "DTEND:20300101T100000Z", "SUMMARY:First"),
            Vevent("same", "DTSTART:20300102T090000Z", "DTEND:20300102T100000Z", "SUMMARY:Second")));

        Assert.Equal((1, 1), (result.ImportedCount, result.UpdatedCount));
        Assert.Empty(result.Skipped);
        Assert.Equal("Second", Assert.Single(await EventsAsync(calendarId)).Title);
    }

    [Fact]
    public async Task Import_TzidEvent_ConvertsToUtc()
    {
        var calendarId = await CreateCalendarAsync("Tzid");

        var result = await ImportAsync(calendarId, Vcalendar(Vevent("t1",
            "DTSTART;TZID=America/New_York:20300101T090000", "DTEND;TZID=America/New_York:20300101T100000", "SUMMARY:Tz")));

        Assert.Equal(1, result.ImportedCount);
        var row = Assert.Single(await EventsAsync(calendarId));
        Assert.Equal(new DateTime(2030, 1, 1, 14, 0, 0, DateTimeKind.Utc), DateTime.SpecifyKind(row.StartDateTime, DateTimeKind.Utc));
    }

    [Fact]
    public async Task Import_AllDayEvent_HasExclusiveEnd()
    {
        var calendarId = await CreateCalendarAsync("AllDay");

        var result = await ImportAsync(calendarId, Vcalendar(Vevent("d1",
            "DTSTART;VALUE=DATE:20300704", "DTEND;VALUE=DATE:20300705", "SUMMARY:Holiday")));

        Assert.Equal(1, result.ImportedCount);
        var row = Assert.Single(await EventsAsync(calendarId));
        Assert.True(row.IsAllDay);
        Assert.Equal(new DateTime(2030, 7, 4), row.StartDateTime.Date);
        Assert.Equal(new DateTime(2030, 7, 5), row.EndDateTime.Date);
    }

    [Fact]
    public async Task Import_MalformedFile_IsRefused()
    {
        var calendarId = await CreateCalendarAsync("Bad");

        await Assert.ThrowsAsync<DomainValidationException>(() => ImportAsync(calendarId, "this is not an ics file"));
    }

    [Fact]
    public async Task Import_WrongContentType_IsRefused_AndOctetStreamIsAccepted()
    {
        var calendarId = await CreateCalendarAsync("Ct");
        var ics = Vcalendar(Vevent("c1", "DTSTART:20300101T090000Z", "DTEND:20300101T100000Z", "SUMMARY:x"));

        await Assert.ThrowsAsync<DomainValidationException>(() => ImportAsync(calendarId, ics, contentType: "application/json"));
        // Browsers/OSes routinely send application/octet-stream for .ics — it must be accepted.
        Assert.Equal(1, (await ImportAsync(calendarId, ics, contentType: "application/octet-stream")).ImportedCount);
    }

    [Fact]
    public async Task Import_MissingCalendar_IsNotFound()
    {
        var ics = Vcalendar(Vevent("m1", "DTSTART:20300101T090000Z", "DTEND:20300101T100000Z", "SUMMARY:x"));

        await Assert.ThrowsAsync<DomainNotFoundException>(() => ImportAsync(Guid.NewGuid(), ics));
    }

    [Fact]
    public async Task Import_ReImportSeries_WithEditedFutureOccurrence_KeepsTheEdit()
    {
        var calendarId = await CreateCalendarAsync("Regen");
        // FREQ=WEEKLY on a Monday DTSTART, no BYDAY → the series' own weekday. Future dates so every occurrence is
        // regenerated on update.
        var ics = Vcalendar(Vevent("series-1",
            "DTSTART:20300107T090000Z", "DTEND:20300107T093000Z", "SUMMARY:Series", "RRULE:FREQ=WEEKLY;COUNT=4"));
        Assert.Equal(1, (await ImportAsync(calendarId, ics)).ImportedCount);

        // Individually edit ONE future occurrence's title, exactly as the calendar UI's per-occurrence edit does.
        var editedId = (await EventsAsync(calendarId)).OrderBy(e => e.StartDateTime).First().CalendarEventId;
        await UpdateEventAsync(editedId, new NewCalendarEvent
        {
            CalendarId = calendarId,
            Title = "EDITED-OCCURRENCE",
            StartDateTime = new DateTime(2030, 1, 7, 9, 0, 0, DateTimeKind.Utc),
            EndDateTime = new DateTime(2030, 1, 7, 9, 30, 0, DateTimeKind.Utc),
        });

        var second = await ImportAsync(calendarId, ics);

        Assert.Equal((0, 1), (second.ImportedCount, second.UpdatedCount));
        Assert.False(second.AnySeriesRegenerated);
        // Issue #519 AC 10: the edited occurrence is an override whose key still exists, so the re-import keeps it.
        var rows = await EventsAsync(calendarId);
        Assert.Equal(4, rows.Count);
        Assert.True(Assert.Single(rows, r => r.Title == "EDITED-OCCURRENCE").IsOverride);
        Assert.Equal(3, rows.Count(r => r.Title == "Series"));
    }

    [Fact]
    public async Task Import_TooManyVEvents_IsRefused_NothingImported()
    {
        // The setting ships unlimited since issue #516, so an administrator's finite cap is what this pins.
        Limits.CalendarIcsMaxExportEvents = TestMaxImportEvents;
        Limits.CalendarIcsMaxImportEvents = TestMaxImportEvents;
        var calendarId = await CreateCalendarAsync("Flood");
        var events = Enumerable.Range(0, TestMaxImportEvents + 1)
            .Select(i => Vevent($"u{i}", "DTSTART:20300101T090000Z", "DTEND:20300101T100000Z", $"SUMMARY:E{i}"));

        await Assert.ThrowsAsync<DomainValidationException>(() => ImportAsync(calendarId, Vcalendar([.. events])));

        Assert.Empty(await EventsAsync(calendarId));
    }

    [Fact]
    public async Task Import_ExceedsAggregateOccurrenceCap_IsRefused_NothingImported()
    {
        var calendarId = await CreateCalendarAsync("Aggregate");
        // 8 daily series of 730 occurrences = 5840 rows > the shipped 5000 aggregate cap.
        var events = Enumerable.Range(0, 8).Select(i => Vevent($"agg{i}", "DTSTART:20300101T090000Z", "DTEND:20300101T093000Z",
            $"SUMMARY:Agg{i}", "RRULE:FREQ=DAILY;COUNT=730"));

        await Assert.ThrowsAsync<DomainValidationException>(() => ImportAsync(calendarId, Vcalendar([.. events])));

        Assert.Empty(await EventsAsync(calendarId));
    }

    [Fact]
    public async Task Import_UpdatePathExceedsAggregateCap_IsRefused_WithoutMutating()
    {
        var calendarId = await CreateCalendarAsync("AggUpdate");
        // Seed 7 daily series of 730 occurrences via 7 SEPARATE imports (each under the 5000 cap): the only way to
        // accumulate patterns whose combined re-import regeneration then exceeds the cap on the UPDATE path (the
        // security-F9 fix — the cap must cover updates too).
        var series = Enumerable.Range(0, 7).Select(i => Vevent($"agg-upd-{i}", "DTSTART:20300101T090000Z", "DTEND:20300101T093000Z",
            $"SUMMARY:AggUpd{i}", "RRULE:FREQ=DAILY;COUNT=730")).ToArray();
        foreach (var vevent in series)
        {
            Assert.Equal(1, (await ImportAsync(calendarId, Vcalendar(vevent))).ImportedCount);
        }

        var rowsBefore = (await EventsAsync(calendarId)).Count;
        Assert.Equal(7 * 730, rowsBefore);

        // Re-import all 7 UIDs in one file: the combined update regeneration is 7*730 = 5110 > 5000.
        await Assert.ThrowsAsync<DomainValidationException>(() => ImportAsync(calendarId, Vcalendar(series)));

        Assert.Equal(rowsBefore, (await EventsAsync(calendarId)).Count);
    }

    [Theory]
    [InlineData("RRULE:FREQ=WEEKLY;COUNT=5;BYSETPOS=1", "Unsupported recurrence rule.")]
    [InlineData("RRULE:FREQ=MONTHLY;COUNT=5;BYDAY=1MO", "Unsupported BYDAY ordinal.")]
    [InlineData("RRULE:FREQ=DAILY", "A recurrence rule must set exactly one of COUNT or UNTIL.")]
    [InlineData("RRULE:FREQ=HOURLY;COUNT=5", "Unsupported recurrence frequency.")]
    public async Task Import_UnmappableRrule_IsSkippedWithReason(string rrule, string expectedReason)
    {
        var calendarId = await CreateCalendarAsync("SkipReasons");

        var result = await ImportAsync(calendarId, Vcalendar(Vevent("r1", "DTSTART:20300101T090000Z", "DTEND:20300101T100000Z", "SUMMARY:Rule", rrule)));

        Assert.Equal(0, result.ImportedCount);
        Assert.Equal(expectedReason, Assert.Single(result.Skipped).Reason);
    }

    // More recurrence than v1 models: a second RRULE, or an EXRULE, must skip the whole VEVENT rather than silently
    // reduce to the first rule. Neither is expressible through Ical.Net's singular RecurrenceRule, so the import reads
    // both off the raw property list.
    [Theory]
    [InlineData("RRULE:FREQ=DAILY;COUNT=5", "RRULE:FREQ=WEEKLY;COUNT=5")]
    [InlineData("RRULE:FREQ=DAILY;COUNT=5", "EXRULE:FREQ=WEEKLY;COUNT=1")]
    public async Task Import_MultipleOrExceptionRules_IsSkippedWithReason(string first, string second)
    {
        var calendarId = await CreateCalendarAsync("MultiRule");

        var result = await ImportAsync(calendarId, Vcalendar(Vevent(
            "m1", "DTSTART:20300101T090000Z", "DTEND:20300101T100000Z", "SUMMARY:Rules", first, second)));

        Assert.Equal(0, result.ImportedCount);
        Assert.Equal("Unsupported recurrence rule.", Assert.Single(result.Skipped).Reason);
    }

    [Fact]
    public async Task Import_TitleTooLong_IsSkippedNotTruncated()
    {
        var calendarId = await CreateCalendarAsync("Long");

        var result = await ImportAsync(calendarId, Vcalendar(Vevent("l1", "DTSTART:20300101T090000Z", "DTEND:20300101T100000Z", $"SUMMARY:{new string('x', 201)}")));

        Assert.Equal(0, result.ImportedCount);
        Assert.Contains("Title", Assert.Single(result.Skipped).Reason);
        Assert.Empty(await EventsAsync(calendarId));
    }

    // ------------------------------------------------------------------ helpers

    private static string Vcalendar(params string[] vevents) =>
        "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//test//EN\r\n" + string.Concat(vevents) + "END:VCALENDAR\r\n";

    private static string Vevent(string uid, params string[] lines) =>
        "BEGIN:VEVENT\r\nUID:" + uid + "\r\n" + string.Join("\r\n", lines) + "\r\nEND:VEVENT\r\n";

    private async Task<Guid> CreateCalendarAsync(string name) =>
        (await InScopeAsync(sp => sp.GetRequiredService<CalendarService>().Create(new NewCalendar { Name = name, Color = "#0369A1" }, ActorUserId)))
        .CalendarId;

    private Task<ExistingRecurrencePattern> CreatePatternAsync(NewRecurrencePattern request) =>
        InScopeAsync(sp => sp.GetRequiredService<RecurrencePatternService>().Create(request, ActorUserId));

    private async Task UpdateEventAsync(Guid id, NewCalendarEvent request) =>
        Assert.NotNull(await InScopeAsync(sp => sp.GetRequiredService<CalendarEventService>().Update(id, request, ActorUserId)));

    private async Task<IcsImportResult> ImportAsync(Guid calendarId, string ics, string contentType = "text/calendar")
    {
        await using var stream = Utf8(ics);
        return await InScopeAsync(sp => sp.GetRequiredService<CalendarIcsService>().ImportAsync(
            calendarId, stream, stream.Length, contentType, ActorUserId, canDelete: true));
    }

    private async Task<string> ExportAsync(Guid calendarId)
    {
        var output = new MemoryStream();
        Assert.True(await InScopeAsync(sp => sp.GetRequiredService<CalendarIcsService>().ExportStreamingAsync(
            CalendarIcsExportScope.ForCalendar(calendarId), output, (_, _) => { })));
        return Encoding.UTF8.GetString(output.ToArray());
    }

    private Task<List<Odyssey.Context.CalendarEvent>> EventsAsync(Guid calendarId) =>
        WithContextAsync(c => c.CalendarEvents.AsNoTracking().Where(e => e.CalendarId == calendarId).ToListAsync());
}
