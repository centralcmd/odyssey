using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Odyssey.Dtos.Authorization;
using CalendarContext = Odyssey.Context.OdysseyContext;
using Odyssey.Dtos.Journal;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;
using Odyssey.Api.Tests.Infrastructure;

namespace Odyssey.Api.Tests;

/// <summary>ICS import/export endpoints (issue #330).</summary>
public class CalendarIcsApiTests
{
    private const string ActorUserId = "calendar-ics-actor-id";
    private const string CalendarsPath = "/api/calendars";

    // The shipped default for CalendarIcsMaxImportEvents (issue #343 §6) — behavior-preserving with
    // the old hard-coded CalendarIcsService.MaxVEvents this replaces.
    private const int DefaultMaxImportEvents = 2000;

    private static readonly string[] ReadWrite =
    [
        PermissionClaims.CalendarRead, PermissionClaims.CalendarCreate,
        PermissionClaims.CalendarUpdate, PermissionClaims.CalendarDelete,
    ];

    // ------------------------------------------------------------------ export

    [Fact]
    public async Task Export_EmptyCalendar_ReturnsValidVCalendar_WithNoSniff()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();
        var calendar = await CreateCalendarAsync(client, "Empty");

        var response = await client.GetAsync($"{CalendarsPath}/{calendar.CalendarId}/ics");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/calendar", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("nosniff", response.Headers.TryGetValues("X-Content-Type-Options", out var v) ? string.Join("", v) : null);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("BEGIN:VCALENDAR", body);
        Assert.Contains("END:VCALENDAR", body);
        Assert.DoesNotContain("BEGIN:VEVENT", body);
    }

    [Fact]
    public async Task Export_FileName_IsDatePrefixedCalendarName()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();
        var calendar = await CreateCalendarAsync(client, "Trips");

        var response = await client.GetAsync($"{CalendarsPath}/{calendar.CalendarId}/ics");

        var fileName = response.Content.Headers.ContentDisposition?.FileNameStar
                       ?? response.Content.Headers.ContentDisposition?.FileName?.Trim('"');
        Assert.NotNull(fileName);
        Assert.Matches(@"^\d{8}_Trips\.ics$", fileName!);
        Assert.StartsWith($"{DateTime.UtcNow:yyyyMMdd}_", fileName!);
    }

    [Fact]
    public async Task Export_MissingCalendar_ReturnsNotFound()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"{CalendarsPath}/{Guid.NewGuid()}/ics");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Export_WithoutReadClaim_ReturnsForbidden()
    {
        await using var factory = new ApiFactory([]);
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"{CalendarsPath}/{Guid.NewGuid()}/ics");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Export_UnmodifiedSeries_EmitsSingleRRule()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();
        var calendar = await CreateCalendarAsync(client, "Series");

        var post = await client.PostAsJsonAsync("/api/recurrence-patterns", new NewRecurrencePattern
        {
            CalendarId = calendar.CalendarId,
            Title = "Standup",
            StartDateTime = new DateTime(2030, 1, 7, 9, 0, 0, DateTimeKind.Utc),
            EndDateTime = new DateTime(2030, 1, 7, 9, 30, 0, DateTimeKind.Utc),
            Frequency = RecurrenceFrequency.Weekly,
            DaysOfWeek = DaysOfWeekFlags.Monday,
            OccurrenceCount = 6,
        });
        post.EnsureSuccessStatusCode();

        var body = await (await client.GetAsync($"{CalendarsPath}/{calendar.CalendarId}/ics")).Content.ReadAsStringAsync();

        var vevents = CountOccurrences(body, "BEGIN:VEVENT");
        Assert.Equal(1, vevents);
        Assert.Contains("RRULE:", body);
        Assert.Contains("FREQ=WEEKLY", body);
    }

    [Fact]
    public async Task Export_UnclampedMonthlySeries_EmitsSingleRRule()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();
        var calendar = await CreateCalendarAsync(client, "Monthly");

        // DayOfMonth=15 never clamps (every month has a 15th), so the stored rows match the unclamped
        // RFC projection exactly → a single RRULE VEVENT.
        var post = await client.PostAsJsonAsync("/api/recurrence-patterns", new NewRecurrencePattern
        {
            CalendarId = calendar.CalendarId,
            Title = "Rent",
            StartDateTime = new DateTime(2030, 1, 15, 9, 0, 0, DateTimeKind.Utc),
            EndDateTime = new DateTime(2030, 1, 15, 10, 0, 0, DateTimeKind.Utc),
            Frequency = RecurrenceFrequency.Monthly,
            DayOfMonth = 15,
            OccurrenceCount = 4,
        });
        post.EnsureSuccessStatusCode();

        var body = await (await client.GetAsync($"{CalendarsPath}/{calendar.CalendarId}/ics")).Content.ReadAsStringAsync();

        Assert.Equal(1, CountOccurrences(body, "BEGIN:VEVENT"));
        Assert.Contains("FREQ=MONTHLY", body);
        Assert.Contains("BYMONTHDAY=15", body);
    }

    [Fact]
    public async Task Export_ClampedMonthlySeries_FlattensToStandaloneVEvents()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();
        var calendar = await CreateCalendarAsync(client, "Clamped");

        // DayOfMonth=31 starting Jan 31: the generator clamps Feb→28 and Apr→30, so the stored dates
        // differ from a strict RFC reading of the same rule (which would skip those months). Exporting
        // must NOT assert an RRULE an external reader would interpret differently — it flattens.
        var post = await client.PostAsJsonAsync("/api/recurrence-patterns", new NewRecurrencePattern
        {
            CalendarId = calendar.CalendarId,
            Title = "Month end",
            StartDateTime = new DateTime(2030, 1, 31, 9, 0, 0, DateTimeKind.Utc),
            EndDateTime = new DateTime(2030, 1, 31, 10, 0, 0, DateTimeKind.Utc),
            Frequency = RecurrenceFrequency.Monthly,
            DayOfMonth = 31,
            OccurrenceCount = 4,
        });
        post.EnsureSuccessStatusCode();

        var body = await (await client.GetAsync($"{CalendarsPath}/{calendar.CalendarId}/ics")).Content.ReadAsStringAsync();

        Assert.Equal(4, CountOccurrences(body, "BEGIN:VEVENT"));
        Assert.DoesNotContain("RRULE:", body);
    }

    [Fact]
    public async Task Export_AllDayRecurringSeries_UntilIsDateValued()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();
        var calendar = await CreateCalendarAsync(client, "AllDaySeries");

        // All-day weekly series bounded by RecurrenceEndDate → the exported RRULE UNTIL must be a DATE
        // (yyyyMMdd), not a DATE-TIME, to match the VALUE=DATE DTSTART (RFC 5545 §3.3.10).
        var post = await client.PostAsJsonAsync("/api/recurrence-patterns", new NewRecurrencePattern
        {
            CalendarId = calendar.CalendarId,
            Title = "Bin day",
            StartDateTime = new DateTime(2030, 1, 7, 0, 0, 0, DateTimeKind.Utc),
            EndDateTime = new DateTime(2030, 1, 8, 0, 0, 0, DateTimeKind.Utc),
            IsAllDay = true,
            Frequency = RecurrenceFrequency.Weekly,
            DaysOfWeek = DaysOfWeekFlags.Monday,
            RecurrenceEndDate = new DateTime(2030, 2, 25, 0, 0, 0, DateTimeKind.Utc),
        });
        post.EnsureSuccessStatusCode();

        var body = await (await client.GetAsync($"{CalendarsPath}/{calendar.CalendarId}/ics")).Content.ReadAsStringAsync();

        Assert.Contains("RRULE:", body);
        Assert.Matches(@"UNTIL=\d{8}(;|\r|\n)", body);      // DATE form
        Assert.DoesNotMatch(@"UNTIL=\d{8}T", body);          // never a DATE-TIME under an all-day DTSTART
    }

    [Fact]
    public async Task Export_EditedOccurrence_FlattensToStandaloneVEvents()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();
        var calendar = await CreateCalendarAsync(client, "Edited");

        var pattern = await (await client.PostAsJsonAsync("/api/recurrence-patterns", new NewRecurrencePattern
        {
            CalendarId = calendar.CalendarId,
            Title = "Standup",
            StartDateTime = new DateTime(2030, 1, 7, 9, 0, 0, DateTimeKind.Utc),
            EndDateTime = new DateTime(2030, 1, 7, 9, 30, 0, DateTimeKind.Utc),
            Frequency = RecurrenceFrequency.Weekly,
            DaysOfWeek = DaysOfWeekFlags.Monday,
            OccurrenceCount = 3,
        })).Content.ReadFromJsonAsync<ExistingRecurrencePattern>();

        // Individually edit one generated occurrence's title.
        Guid firstEventId;
        using (var scope = factory.Services.CreateScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<CalendarContext>();
            var row = ctx.CalendarEvents.Where(e => e.RecurrencePatternId == pattern!.RecurrencePatternId)
                .OrderBy(e => e.StartDateTime).First();
            firstEventId = row.CalendarEventId;
        }

        var edit = await client.PutAsJsonAsync($"/api/calendar-events/{firstEventId}", new NewCalendarEvent
        {
            CalendarId = calendar.CalendarId,
            Title = "Standup (special)",
            StartDateTime = new DateTime(2030, 1, 7, 9, 0, 0, DateTimeKind.Utc),
            EndDateTime = new DateTime(2030, 1, 7, 9, 30, 0, DateTimeKind.Utc),
        });
        edit.EnsureSuccessStatusCode();

        var body = await (await client.GetAsync($"{CalendarsPath}/{calendar.CalendarId}/ics")).Content.ReadAsStringAsync();

        Assert.Equal(3, CountOccurrences(body, "BEGIN:VEVENT"));
        Assert.DoesNotContain("RRULE:", body);
    }

    // ------------------------------------------------------------------ import

    [Fact]
    public async Task Import_ThreeStandaloneEvents_CreatesThreeRowsWithExternalUid()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();
        var calendar = await CreateCalendarAsync(client, "Import");

        var ics = Vcalendar(
            Vevent("a1", "DTSTART:20300101T090000Z", "DTEND:20300101T100000Z", "SUMMARY:One"),
            Vevent("a2", "DTSTART:20300102T090000Z", "DTEND:20300102T100000Z", "SUMMARY:Two"),
            Vevent("a3", "DTSTART:20300103T090000Z", "DTEND:20300103T100000Z", "SUMMARY:Three"));

        var result = await ImportAsync(client, calendar.CalendarId, ics);

        Assert.Equal(3, result.ImportedCount);
        Assert.Equal(0, result.UpdatedCount);
        Assert.Empty(result.Skipped);

        using var scope = factory.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<CalendarContext>();
        var rows = ctx.CalendarEvents.Where(e => e.CalendarId == calendar.CalendarId).ToList();
        Assert.Equal(3, rows.Count);
        Assert.All(rows, r => Assert.False(string.IsNullOrEmpty(r.ExternalUid)));
    }

    // Security review (PR #345, F1): a synthetic "{primary-key}@odyssey.local" UID must never be
    // persisted as ExternalUid, even when it doesn't belong to any row in the target calendar (e.g. it
    // arrived via an aggregate export's cross-calendar file). Persisting it would let a later export of
    // *this* row carry that foreign synthetic UID, and a re-import into the UID's true origin calendar
    // would then match and silently overwrite the unrelated original row in place.
    [Fact]
    public async Task Import_ForeignSyntheticUid_DoesNotPersistAsExternalUid()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();
        var calendar = await CreateCalendarAsync(client, "ForeignSynthetic");

        var foreignUid = $"{Guid.NewGuid()}@odyssey.local";
        var ics = Vcalendar(Vevent(foreignUid, "DTSTART:20300101T090000Z", "DTEND:20300101T100000Z", "SUMMARY:Foreign"));

        var result = await ImportAsync(client, calendar.CalendarId, ics);

        Assert.Equal(1, result.ImportedCount);

        using var scope = factory.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<CalendarContext>();
        var row = ctx.CalendarEvents.Single(e => e.CalendarId == calendar.CalendarId);
        Assert.Null(row.ExternalUid);
    }

    [Fact]
    public async Task Import_ExportedFile_IsIdempotent_CreatesZeroNewRows()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();
        var calendar = await CreateCalendarAsync(client, "RoundTrip");

        await client.PostAsJsonAsync("/api/calendar-events", new NewCalendarEvent
        {
            CalendarId = calendar.CalendarId,
            Title = "Lunch",
            StartDateTime = new DateTime(2030, 5, 1, 11, 0, 0, DateTimeKind.Utc),
            EndDateTime = new DateTime(2030, 5, 1, 12, 0, 0, DateTimeKind.Utc),
        });
        await client.PostAsJsonAsync("/api/recurrence-patterns", new NewRecurrencePattern
        {
            CalendarId = calendar.CalendarId,
            Title = "Weekly",
            StartDateTime = new DateTime(2030, 6, 2, 9, 0, 0, DateTimeKind.Utc),
            EndDateTime = new DateTime(2030, 6, 2, 10, 0, 0, DateTimeKind.Utc),
            Frequency = RecurrenceFrequency.Weekly,
            DaysOfWeek = DaysOfWeekFlags.Monday,
            OccurrenceCount = 5,
        });

        int patternsBefore, eventsBefore;
        using (var scope = factory.Services.CreateScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<CalendarContext>();
            patternsBefore = ctx.RecurrencePatterns.Count(p => p.CalendarId == calendar.CalendarId);
            eventsBefore = ctx.CalendarEvents.Count(e => e.CalendarId == calendar.CalendarId);
        }

        var exported = await (await client.GetAsync($"{CalendarsPath}/{calendar.CalendarId}/ics")).Content.ReadAsStringAsync();
        var result = await ImportAsync(client, calendar.CalendarId, exported);

        Assert.Equal(0, result.ImportedCount);
        Assert.Equal(2, result.UpdatedCount); // the standalone event + the recurring series (1 RRULE VEVENT)

        using (var scope = factory.Services.CreateScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<CalendarContext>();
            Assert.Equal(patternsBefore, ctx.RecurrencePatterns.Count(p => p.CalendarId == calendar.CalendarId));
            Assert.Equal(eventsBefore, ctx.CalendarEvents.Count(e => e.CalendarId == calendar.CalendarId));
        }
    }

    [Fact]
    public async Task Import_WeeklyCountNoByDay_Succeeds()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();
        var calendar = await CreateCalendarAsync(client, "WeeklyDefault");

        var ics = Vcalendar(Vevent("w1",
            "DTSTART:20300106T090000Z", "DTEND:20300106T100000Z", "SUMMARY:Weekly", "RRULE:FREQ=WEEKLY;COUNT=6"));

        var result = await ImportAsync(client, calendar.CalendarId, ics);

        Assert.Equal(1, result.ImportedCount);
        Assert.Empty(result.Skipped);
        using var scope = factory.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<CalendarContext>();
        Assert.Equal(6, ctx.CalendarEvents.Count(e => e.CalendarId == calendar.CalendarId));
    }

    [Fact]
    public async Task Import_WkstNonDefault_IsAccepted()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();
        var calendar = await CreateCalendarAsync(client, "Wkst");

        var ics = Vcalendar(Vevent("k1",
            "DTSTART:20300106T090000Z", "DTEND:20300106T100000Z", "SUMMARY:Wkst", "RRULE:FREQ=WEEKLY;COUNT=3;WKST=SU"));

        var result = await ImportAsync(client, calendar.CalendarId, ics);

        Assert.Equal(1, result.ImportedCount);
        Assert.Empty(result.Skipped);
    }

    [Fact]
    public async Task Import_ExdateOnSeries_IsRejectedWithReason()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();
        var calendar = await CreateCalendarAsync(client, "Exdate");

        var ics = Vcalendar(Vevent("e1",
            "DTSTART:20300101T090000Z", "DTEND:20300101T100000Z", "SUMMARY:Ex",
            "RRULE:FREQ=DAILY;COUNT=5", "EXDATE:20300103T090000Z"));

        var result = await ImportAsync(client, calendar.CalendarId, ics);

        Assert.Equal(0, result.ImportedCount);
        var group = Assert.Single(result.Skipped);
        Assert.Contains("EXDATE", group.Reason);
    }

    [Fact]
    public async Task Import_DuplicateUidInFile_ImportsFirstSkipsSecond()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();
        var calendar = await CreateCalendarAsync(client, "Dupe");

        var ics = Vcalendar(
            Vevent("same", "DTSTART:20300101T090000Z", "DTEND:20300101T100000Z", "SUMMARY:First"),
            Vevent("same", "DTSTART:20300102T090000Z", "DTEND:20300102T100000Z", "SUMMARY:Second"));

        var result = await ImportAsync(client, calendar.CalendarId, ics);

        Assert.Equal(1, result.ImportedCount);
        var group = Assert.Single(result.Skipped);
        Assert.Equal(1, group.Count);
        Assert.Contains("Duplicate UID", group.Reason);
    }

    [Fact]
    public async Task Import_TzidEvent_ConvertsToUtc()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();
        var calendar = await CreateCalendarAsync(client, "Tzid");

        var ics = Vcalendar(Vevent("t1",
            "DTSTART;TZID=America/New_York:20300101T090000",
            "DTEND;TZID=America/New_York:20300101T100000", "SUMMARY:Tz"));

        var result = await ImportAsync(client, calendar.CalendarId, ics);

        Assert.Equal(1, result.ImportedCount);
        using var scope = factory.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<CalendarContext>();
        var row = ctx.CalendarEvents.Single(e => e.CalendarId == calendar.CalendarId);
        Assert.Equal(new DateTime(2030, 1, 1, 14, 0, 0, DateTimeKind.Utc), DateTime.SpecifyKind(row.StartDateTime, DateTimeKind.Utc));
    }

    [Fact]
    public async Task Import_AllDayEvent_HasExclusiveEnd()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();
        var calendar = await CreateCalendarAsync(client, "AllDay");

        var ics = Vcalendar(Vevent("d1",
            "DTSTART;VALUE=DATE:20300704", "DTEND;VALUE=DATE:20300705", "SUMMARY:Holiday"));

        var result = await ImportAsync(client, calendar.CalendarId, ics);

        Assert.Equal(1, result.ImportedCount);
        using var scope = factory.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<CalendarContext>();
        var row = ctx.CalendarEvents.Single(e => e.CalendarId == calendar.CalendarId);
        Assert.True(row.IsAllDay);
        Assert.Equal(new DateTime(2030, 7, 4), row.StartDateTime.Date);
        Assert.Equal(new DateTime(2030, 7, 5), row.EndDateTime.Date);
    }

    [Fact]
    public async Task Import_MalformedFile_ReturnsBadRequest_NotServerError()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();
        var calendar = await CreateCalendarAsync(client, "Bad");

        var response = await PostIcsAsync(client, calendar.CalendarId, "this is not an ics file", "notes.ics");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Import_WrongContentType_ReturnsBadRequest()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();
        var calendar = await CreateCalendarAsync(client, "Ct");

        var ics = Vcalendar(Vevent("c1", "DTSTART:20300101T090000Z", "DTEND:20300101T100000Z", "SUMMARY:x"));
        var response = await PostIcsAsync(client, calendar.CalendarId, ics, "data.ics", contentType: "application/json");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Import_NonIcsExtension_ReturnsBadRequest()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();
        var calendar = await CreateCalendarAsync(client, "Ext");

        var ics = Vcalendar(Vevent("x1", "DTSTART:20300101T090000Z", "DTEND:20300101T100000Z", "SUMMARY:x"));
        var response = await PostIcsAsync(client, calendar.CalendarId, ics, "data.txt");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Import_MissingCalendar_ReturnsNotFound()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();

        var ics = Vcalendar(Vevent("m1", "DTSTART:20300101T090000Z", "DTEND:20300101T100000Z", "SUMMARY:x"));
        var response = await PostIcsAsync(client, Guid.NewGuid(), ics);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Import_WithOnlyCreateClaim_ReturnsForbidden()
    {
        await using var factory = new ApiFactory([PermissionClaims.CalendarCreate, PermissionClaims.CalendarRead]);
        using var client = factory.CreateClient();

        var ics = Vcalendar(Vevent("p1", "DTSTART:20300101T090000Z", "DTEND:20300101T100000Z", "SUMMARY:x"));
        var response = await PostIcsAsync(client, Guid.NewGuid(), ics);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Import_ReImportSeries_WithEditedFutureOccurrence_DiscardsEditAndFlagsWarning()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();
        var calendar = await CreateCalendarAsync(client, "Regen");

        // FREQ=WEEKLY on a Monday DTSTART, no BYDAY → the series' own weekday. Future dates so every
        // occurrence is regenerated on update.
        var ics = Vcalendar(Vevent("series-1",
            "DTSTART:20300107T090000Z", "DTEND:20300107T093000Z", "SUMMARY:Series", "RRULE:FREQ=WEEKLY;COUNT=4"));

        var first = await ImportAsync(client, calendar.CalendarId, ics);
        Assert.Equal(1, first.ImportedCount);

        // Individually edit ONE future occurrence's title, exactly as the calendar UI's per-occurrence
        // edit does.
        Guid editedId;
        using (var scope = factory.Services.CreateScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<CalendarContext>();
            var row = ctx.CalendarEvents.Where(e => e.CalendarId == calendar.CalendarId)
                .OrderBy(e => e.StartDateTime).First();
            editedId = row.CalendarEventId;
        }

        var edit = await client.PutAsJsonAsync($"/api/calendar-events/{editedId}", new NewCalendarEvent
        {
            CalendarId = calendar.CalendarId,
            Title = "EDITED-OCCURRENCE",
            StartDateTime = new DateTime(2030, 1, 7, 9, 0, 0, DateTimeKind.Utc),
            EndDateTime = new DateTime(2030, 1, 7, 9, 30, 0, DateTimeKind.Utc),
        });
        edit.EnsureSuccessStatusCode();

        var second = await ImportAsync(client, calendar.CalendarId, ics);

        Assert.Equal(0, second.ImportedCount);
        Assert.Equal(1, second.UpdatedCount);
        Assert.True(second.AnySeriesRegenerated);

        // The individual edit was discarded by the regenerate-future behaviour: no row keeps the edited
        // title, every occurrence is back to the series title, and the count is unchanged (still 4).
        using (var scope = factory.Services.CreateScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<CalendarContext>();
            var rows = ctx.CalendarEvents.Where(e => e.CalendarId == calendar.CalendarId).ToList();
            Assert.Equal(4, rows.Count);
            Assert.DoesNotContain(rows, r => r.Title == "EDITED-OCCURRENCE");
            Assert.All(rows, r => Assert.Equal("Series", r.Title));
        }
    }

    [Fact]
    public async Task Import_TooManyVEvents_ReturnsBadRequest()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();
        var calendar = await CreateCalendarAsync(client, "Flood");

        var events = Enumerable.Range(0, DefaultMaxImportEvents + 1)
            .Select(i => Vevent($"u{i}", "DTSTART:20300101T090000Z", "DTEND:20300101T100000Z", $"SUMMARY:E{i}"));
        var ics = Vcalendar(events.ToArray());

        var response = await PostIcsAsync(client, calendar.CalendarId, ics);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Import_ExceedsAggregateOccurrenceCap_ReturnsBadRequest()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();
        var calendar = await CreateCalendarAsync(client, "Aggregate");

        // 8 daily series of 730 occurrences = 5840 rows > 5000 aggregate cap.
        var events = Enumerable.Range(0, 8)
            .Select(i => Vevent($"agg{i}", "DTSTART:20300101T090000Z", "DTEND:20300101T093000Z",
                $"SUMMARY:Agg{i}", "RRULE:FREQ=DAILY;COUNT=730"));
        var ics = Vcalendar(events.ToArray());

        var response = await PostIcsAsync(client, calendar.CalendarId, ics);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        using var scope = factory.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<CalendarContext>();
        Assert.Equal(0, ctx.CalendarEvents.Count(e => e.CalendarId == calendar.CalendarId));
    }

    [Fact]
    public async Task Import_UpdatePathExceedsAggregateCap_ReturnsBadRequest_WithoutMutating()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();
        var calendar = await CreateCalendarAsync(client, "AggUpdate");

        // Seed 7 daily series of 730 occurrences via 7 SEPARATE imports (each 730 rows, under the 5000
        // create cap). This is the only way to accumulate patterns whose combined re-import regeneration
        // then exceeds the cap on the UPDATE path (the security-F9 fix — the cap must cover updates too).
        var series = Enumerable.Range(0, 7)
            .Select(i => Vevent($"agg-upd-{i}", "DTSTART:20300101T090000Z", "DTEND:20300101T093000Z",
                $"SUMMARY:AggUpd{i}", "RRULE:FREQ=DAILY;COUNT=730"))
            .ToArray();

        foreach (var vevent in series)
        {
            var seed = await ImportAsync(client, calendar.CalendarId, Vcalendar(vevent));
            Assert.Equal(1, seed.ImportedCount);
        }

        int rowsBefore;
        using (var scope = factory.Services.CreateScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<CalendarContext>();
            rowsBefore = ctx.CalendarEvents.Count(e => e.CalendarId == calendar.CalendarId);
        }
        Assert.Equal(7 * 730, rowsBefore);

        // Re-import all 7 UIDs in one file: the combined update regeneration is 7*730 = 5110 > 5000.
        var response = await PostIcsAsync(client, calendar.CalendarId, Vcalendar(series));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        using (var scope = factory.Services.CreateScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<CalendarContext>();
            Assert.Equal(rowsBefore, ctx.CalendarEvents.Count(e => e.CalendarId == calendar.CalendarId));
        }
    }

    [Theory]
    [InlineData("RRULE:FREQ=WEEKLY;COUNT=5;BYSETPOS=1", "Unsupported recurrence rule.")]
    [InlineData("RRULE:FREQ=MONTHLY;COUNT=5;BYDAY=1MO", "Unsupported BYDAY ordinal.")]
    [InlineData("RRULE:FREQ=DAILY", "A recurrence rule must set exactly one of COUNT or UNTIL.")]
    [InlineData("RRULE:FREQ=HOURLY;COUNT=5", "Unsupported recurrence frequency.")]
    public async Task Import_UnmappableRrule_IsSkippedWithReason(string rrule, string expectedReason)
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();
        var calendar = await CreateCalendarAsync(client, "SkipReasons");

        var ics = Vcalendar(Vevent("r1", "DTSTART:20300101T090000Z", "DTEND:20300101T100000Z", "SUMMARY:Rule", rrule));

        var result = await ImportAsync(client, calendar.CalendarId, ics);

        Assert.Equal(0, result.ImportedCount);
        var group = Assert.Single(result.Skipped);
        Assert.Equal(expectedReason, group.Reason);
    }

    [Theory]
    // More recurrence than v1 models: a second RRULE, or an EXRULE, must skip the whole VEVENT rather
    // than silently reduce to the first rule. Neither is expressible through Ical.Net's singular
    // RecurrenceRule, so the import reads both off the raw property list.
    [InlineData("RRULE:FREQ=DAILY;COUNT=5", "RRULE:FREQ=WEEKLY;COUNT=5")]
    [InlineData("RRULE:FREQ=DAILY;COUNT=5", "EXRULE:FREQ=WEEKLY;COUNT=1")]
    public async Task Import_MultipleOrExceptionRules_IsSkippedWithReason(string first, string second)
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();
        var calendar = await CreateCalendarAsync(client, "MultiRule");

        var ics = Vcalendar(Vevent(
            "m1", "DTSTART:20300101T090000Z", "DTEND:20300101T100000Z", "SUMMARY:Rules", first, second));

        var result = await ImportAsync(client, calendar.CalendarId, ics);

        Assert.Equal(0, result.ImportedCount);
        var group = Assert.Single(result.Skipped);
        Assert.Equal("Unsupported recurrence rule.", group.Reason);
    }

    [Fact]
    public async Task Import_OctetStreamContentType_IsAccepted()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();
        var calendar = await CreateCalendarAsync(client, "Octet");

        // Browsers/OSes routinely send application/octet-stream for .ics — it must be accepted.
        var ics = Vcalendar(Vevent("o1", "DTSTART:20300101T090000Z", "DTEND:20300101T100000Z", "SUMMARY:Octet"));
        var response = await PostIcsAsync(client, calendar.CalendarId, ics, "cal.ics", contentType: "application/octet-stream");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Import_TitleTooLong_IsSkippedNotTruncated()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();
        var calendar = await CreateCalendarAsync(client, "Long");

        var longTitle = new string('x', 201);
        var ics = Vcalendar(Vevent("l1", "DTSTART:20300101T090000Z", "DTEND:20300101T100000Z", $"SUMMARY:{longTitle}"));

        var result = await ImportAsync(client, calendar.CalendarId, ics);

        Assert.Equal(0, result.ImportedCount);
        var group = Assert.Single(result.Skipped);
        Assert.Contains("Title", group.Reason);
    }

    // ------------------------------------------------------------------ helpers

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }

    private static string Vcalendar(params string[] vevents) =>
        "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//test//EN\r\n" + string.Concat(vevents) + "END:VCALENDAR\r\n";

    private static string Vevent(string uid, params string[] lines) =>
        "BEGIN:VEVENT\r\nUID:" + uid + "\r\n" + string.Join("\r\n", lines) + "\r\nEND:VEVENT\r\n";

    private static async Task<ExistingCalendar> CreateCalendarAsync(HttpClient client, string name)
    {
        var post = await client.PostAsJsonAsync(CalendarsPath, new NewCalendar { Name = name, Color = "#0369A1" });
        post.EnsureSuccessStatusCode();
        return (await post.Content.ReadFromJsonAsync<ExistingCalendar>())!;
    }

    private static async Task<IcsImportResult> ImportAsync(HttpClient client, Guid calendarId, string ics)
    {
        var response = await PostIcsAsync(client, calendarId, ics);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<IcsImportResult>())!;
    }

    private static async Task<HttpResponseMessage> PostIcsAsync(
        HttpClient client, Guid calendarId, string ics, string fileName = "calendar.ics", string contentType = "text/calendar")
    {
        using var content = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(Encoding.UTF8.GetBytes(ics));
        fileContent.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        content.Add(fileContent, "file", fileName);
        return await client.PostAsync($"{CalendarsPath}/{calendarId}/ics", content);
    }

    private sealed class ApiFactory : OdysseyApiFactory
    {
        public ApiFactory(IReadOnlyCollection<string>? permissions)
            : base(permissions, ActorUserId, configureServices: IsolateCalendarContext)
        {
        }

        private static void IsolateCalendarContext(IServiceCollection services)
        {
            var calendarDatabaseName = $"calendar-{Guid.NewGuid()}";
            services.RemoveAll<DbContextOptions<CalendarContext>>();
            services.AddDbContext<CalendarContext>(options => options.UseInMemoryDatabase(calendarDatabaseName));
        }
    }
}
