using System.Net;
using System.Net.Http.Json;
using Odyssey.Dtos.Application;
using Odyssey.Dtos.Authorization;
using CalendarContext = Odyssey.Context.OdysseyContext;
using ContextCalendarEvent = Odyssey.Context.CalendarEvent;
using Odyssey.Dtos.Journal;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;
using Odyssey.Api.Tests.Infrastructure;

namespace Odyssey.Api.Tests;

/// <summary>Single event / series / aggregate .ics export endpoints (issue #340).</summary>
public class CalendarEventsIcsApiTests
{
    private const string ActorUserId = "calendar-events-ics-actor-id";
    private const string CalendarsPath = "/api/calendars";
    private const string CalendarEventsPath = "/api/calendar-events";
    private const string RecurrencePatternsPath = "/api/recurrence-patterns";

    // The shipped default for CalendarIcsMaxExportEvents (issue #343 §6) — behavior-preserving with
    // the old hard-coded CalendarIcsService.MaxVEvents this replaces.
    private const int DefaultMaxExportEvents = 2000;

    private static readonly string[] ReadWrite =
    [
        PermissionClaims.CalendarRead, PermissionClaims.CalendarCreate,
        PermissionClaims.CalendarUpdate, PermissionClaims.CalendarDelete,
    ];

    // Adds the claims needed to also PUT CalendarIcsMaxExportMegabytes via /api/system-settings
    // (issue #343 follow-up: the export byte-size cap tests below).
    private static readonly string[] ReadWriteWithSizeCapControl =
    [
        PermissionClaims.CalendarRead, PermissionClaims.CalendarCreate,
        PermissionClaims.CalendarUpdate, PermissionClaims.CalendarDelete,
        PermissionClaims.SystemSettingsRead, PermissionClaims.SystemSettingsSecurityUpdate,
    ];

    // ------------------------------------------------------------------ single event (AC 1)

    [Fact]
    public async Task ExportEvent_NonRecurring_ReturnsStandaloneVEvent()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();
        var calendar = await CreateCalendarAsync(client, "Solo");
        var ev = await CreateEventAsync(client, calendar.CalendarId, "Dentist", new DateTime(2030, 3, 1, 9, 0, 0, DateTimeKind.Utc));

        var response = await client.GetAsync($"{CalendarEventsPath}/{ev.CalendarEventId}/ics");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/calendar", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("nosniff", response.Headers.TryGetValues("X-Content-Type-Options", out var v) ? string.Join("", v) : null);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(1, CountOccurrences(body, "BEGIN:VEVENT"));
        Assert.Contains("SUMMARY:Dentist", body);
        Assert.DoesNotContain("RRULE:", body);
    }

    // The PR's headline single-event flow — exporting one dated occurrence that belongs to a series,
    // as opposed to the standalone-event case above — was previously untested at any tier. GET
    // /calendar-events/{id}/ics doesn't know or care about RecurrencePatternId; it always emits a
    // standalone VEVENT for that one row (the occurrence-vs-series choice lives client-side, in
    // ExportEventScopeDialog — the series path is /recurrence-patterns/{id}/ics instead).
    [Fact]
    public async Task ExportEvent_OccurrenceOfSeries_ReturnsStandaloneVEvent()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();
        var calendar = await CreateCalendarAsync(client, "OccurrenceExport");
        var pattern = await CreatePatternAsync(client, calendar.CalendarId, "Standup",
            new DateTime(2030, 1, 7, 9, 0, 0, DateTimeKind.Utc), occurrenceCount: 4,
            frequency: RecurrenceFrequency.Weekly, daysOfWeek: DaysOfWeekFlags.Monday);
        var occurrenceId = await FirstGeneratedEventAsync(factory, pattern.RecurrencePatternId);

        var response = await client.GetAsync($"{CalendarEventsPath}/{occurrenceId}/ics");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(1, CountOccurrences(body, "BEGIN:VEVENT"));
        Assert.Contains("SUMMARY:Standup", body);
        Assert.DoesNotContain("RRULE:", body);
    }

    [Fact]
    public async Task ExportEvent_UnknownId_ReturnsNotFound()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"{CalendarEventsPath}/{Guid.NewGuid()}/ics");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ExportEvent_WithoutReadClaim_ReturnsForbidden()
    {
        await using var factory = new ApiFactory([]);
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"{CalendarEventsPath}/{Guid.NewGuid()}/ics");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ------------------------------------------------------------------ series (AC 2, 19)

    [Fact]
    public async Task ExportPattern_IntactSeries_EmitsSingleRRule()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();
        var calendar = await CreateCalendarAsync(client, "Series");
        var pattern = await CreatePatternAsync(client, calendar.CalendarId, "Standup",
            new DateTime(2030, 1, 7, 9, 0, 0, DateTimeKind.Utc), occurrenceCount: 5,
            frequency: RecurrenceFrequency.Weekly, daysOfWeek: DaysOfWeekFlags.Monday);

        var response = await client.GetAsync($"{RecurrencePatternsPath}/{pattern.RecurrencePatternId}/ics");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/calendar", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("nosniff", response.Headers.TryGetValues("X-Content-Type-Options", out var v) ? string.Join("", v) : null);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        var fileName = response.Content.Headers.ContentDisposition?.FileNameStar
                       ?? response.Content.Headers.ContentDisposition?.FileName?.Trim('"');
        Assert.Matches(@"^\d{8}_Standup\.ics$", fileName!);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(1, CountOccurrences(body, "BEGIN:VEVENT"));
        Assert.Contains("RRULE:", body);
    }

    [Fact]
    public async Task ExportPattern_ClampedSeries_FlattensToStandaloneVEvents()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();
        var calendar = await CreateCalendarAsync(client, "Clamped");
        var pattern = await CreatePatternAsync(client, calendar.CalendarId, "Month end",
            new DateTime(2030, 1, 31, 9, 0, 0, DateTimeKind.Utc), occurrenceCount: 4,
            frequency: RecurrenceFrequency.Monthly, dayOfMonth: 31);

        var body = await (await client.GetAsync($"{RecurrencePatternsPath}/{pattern.RecurrencePatternId}/ics")).Content.ReadAsStringAsync();

        Assert.Equal(4, CountOccurrences(body, "BEGIN:VEVENT"));
        Assert.DoesNotContain("RRULE:", body);
    }

    [Fact]
    public async Task ExportPattern_UnknownId_ReturnsNotFound()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"{RecurrencePatternsPath}/{Guid.NewGuid()}/ics");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ------------------------------------------------------------------ aggregate: no filter (AC 3, 12)

    [Fact]
    public async Task ExportAggregate_NoFilter_IncludesEventsFromEveryCalendar_AndCollapsesIntactSeries()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();
        var calendarA = await CreateCalendarAsync(client, "Work");
        var calendarB = await CreateCalendarAsync(client, "Home");
        await CreateEventAsync(client, calendarA.CalendarId, "Solo A", new DateTime(2030, 2, 1, 9, 0, 0, DateTimeKind.Utc));
        await CreateEventAsync(client, calendarB.CalendarId, "Solo B", new DateTime(2030, 2, 2, 9, 0, 0, DateTimeKind.Utc));
        await CreatePatternAsync(client, calendarA.CalendarId, "Standup",
            new DateTime(2030, 1, 7, 9, 0, 0, DateTimeKind.Utc), occurrenceCount: 4,
            frequency: RecurrenceFrequency.Weekly, daysOfWeek: DaysOfWeekFlags.Monday);

        var response = await client.GetAsync($"{CalendarEventsPath}/ics");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/calendar", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("nosniff", response.Headers.TryGetValues("X-Content-Type-Options", out var v) ? string.Join("", v) : null);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        var body = await response.Content.ReadAsStringAsync();
        // 2 standalone + 1 collapsed RRULE series = 3 VEVENTs.
        Assert.Equal(3, CountOccurrences(body, "BEGIN:VEVENT"));
        Assert.Contains("RRULE:", body);
    }

    // Security review (PR #345 F1): a degenerate rule whose requested day never exists (Feb 30 every
    // year) used to make GenerateRfcLiteral's strict-RFC projection climb years without ever hitting
    // its own step guard, eventually throwing ArgumentOutOfRangeException out of CanExportAsRule —
    // and because the unfiltered aggregate export runs CanExportAsRule over every pattern in every
    // calendar, one such rule anywhere 500'd this endpoint for every caller. The pattern itself is
    // buildable via the API (NewRecurrencePattern has no DayOfMonth/MonthOfYear existence
    // cross-check), so this only needs a plain POST + GET, no direct DB manipulation.
    [Fact]
    public async Task ExportAggregate_NoFilter_DegenerateYearlyRule_FlattensInsteadOf500()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();
        var calendar = await CreateCalendarAsync(client, "Degenerate");
        await CreatePatternAsync(client, calendar.CalendarId, "Never happens",
            new DateTime(2030, 2, 28, 9, 0, 0, DateTimeKind.Utc), occurrenceCount: 3,
            frequency: RecurrenceFrequency.Yearly, dayOfMonth: 30);

        var response = await client.GetAsync($"{CalendarEventsPath}/ics");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("RRULE:", body);
        Assert.True(CountOccurrences(body, "BEGIN:VEVENT") > 0);
    }

    // Same degenerate rule via the single-series export endpoint, whose CanExportAsRule call is the
    // same shared method — proves the fix isn't aggregate-path-specific.
    [Fact]
    public async Task ExportPattern_DegenerateYearlyRule_FlattensInsteadOf500()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();
        var calendar = await CreateCalendarAsync(client, "DegeneratePattern");
        var pattern = await CreatePatternAsync(client, calendar.CalendarId, "Never happens",
            new DateTime(2030, 2, 28, 9, 0, 0, DateTimeKind.Utc), occurrenceCount: 3,
            frequency: RecurrenceFrequency.Yearly, dayOfMonth: 30);

        var response = await client.GetAsync($"{RecurrencePatternsPath}/{pattern.RecurrencePatternId}/ics");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("RRULE:", body);
    }

    [Fact]
    public async Task ExportAggregate_ZeroMatches_ReturnsOkWithEmptyValidIcs()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"{CalendarEventsPath}/ics");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("BEGIN:VCALENDAR", body);
        Assert.DoesNotContain("BEGIN:VEVENT", body);
    }

    [Fact]
    public async Task ExportAggregate_FileName_IsFixedName()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"{CalendarEventsPath}/ics");

        var fileName = response.Content.Headers.ContentDisposition?.FileNameStar
                       ?? response.Content.Headers.ContentDisposition?.FileName?.Trim('"');
        Assert.Matches(@"^\d{8}_calendar-events\.ics$", fileName!);
    }

    [Fact]
    public async Task ExportAggregate_EachVEvent_CarriesSourceCalendarCategory()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();
        var calendar = await CreateCalendarAsync(client, "Categorized");
        await CreateEventAsync(client, calendar.CalendarId, "Tagged", new DateTime(2030, 2, 1, 9, 0, 0, DateTimeKind.Utc));

        var body = await (await client.GetAsync($"{CalendarEventsPath}/ics")).Content.ReadAsStringAsync();

        Assert.Contains("CATEGORIES:Categorized", body);
    }

    [Fact]
    public async Task ExportAggregate_DuplicateExternalUidAcrossCalendars_EmitsDistinctUids()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();
        var calendarA = await CreateCalendarAsync(client, "ImportA");
        var calendarB = await CreateCalendarAsync(client, "ImportB");

        var ics = Vcalendar(Vevent("shared-uid", "DTSTART:20300101T090000Z", "DTEND:20300101T100000Z", "SUMMARY:Shared"));
        (await PostIcsAsync(client, calendarA.CalendarId, ics)).EnsureSuccessStatusCode();
        (await PostIcsAsync(client, calendarB.CalendarId, ics)).EnsureSuccessStatusCode();

        var body = await (await client.GetAsync($"{CalendarEventsPath}/ics")).Content.ReadAsStringAsync();

        var uids = ExtractUidLines(body);
        Assert.Equal(2, uids.Count);
        Assert.Equal(2, uids.Distinct().Count());
        Assert.DoesNotContain(uids, u => u == "UID:shared-uid");
    }

    // ------------------------------------------------------------------ aggregate: cross-calendar (AC 4, 5)

    [Fact]
    public async Task ExportAggregate_CalendarFilterNoDate_MatchesWholeCalendarExport_WhenNothingMoved()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();
        var calendar = await CreateCalendarAsync(client, "Equivalence");
        await CreatePatternAsync(client, calendar.CalendarId, "Standup",
            new DateTime(2030, 1, 7, 9, 0, 0, DateTimeKind.Utc), occurrenceCount: 4,
            frequency: RecurrenceFrequency.Weekly, daysOfWeek: DaysOfWeekFlags.Monday);
        await CreateEventAsync(client, calendar.CalendarId, "Solo", new DateTime(2030, 2, 1, 9, 0, 0, DateTimeKind.Utc));

        var wholeCalendarBody = await (await client.GetAsync($"{CalendarsPath}/{calendar.CalendarId}/ics")).Content.ReadAsStringAsync();
        var aggregateBody = await (await client.GetAsync($"{CalendarEventsPath}/ics?calendarIds={calendar.CalendarId}")).Content.ReadAsStringAsync();

        Assert.Equal(CountOccurrences(wholeCalendarBody, "BEGIN:VEVENT"), CountOccurrences(aggregateBody, "BEGIN:VEVENT"));
        Assert.Equal(CountOccurrences(wholeCalendarBody, "RRULE:"), CountOccurrences(aggregateBody, "RRULE:"));
    }

    [Fact]
    public async Task ExportAggregate_OccurrenceMovedOutOfFilteredCalendar_FlattensAndExcludesMovedRow()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();
        var calendarA = await CreateCalendarAsync(client, "MoveOutA");
        var calendarB = await CreateCalendarAsync(client, "MoveOutB");
        var pattern = await CreatePatternAsync(client, calendarA.CalendarId, "Standup",
            new DateTime(2030, 1, 7, 9, 0, 0, DateTimeKind.Utc), occurrenceCount: 3,
            frequency: RecurrenceFrequency.Weekly, daysOfWeek: DaysOfWeekFlags.Monday);

        var movedRow = await FirstGeneratedEventAsync(factory, pattern.RecurrencePatternId);
        await MoveEventToCalendarAsync(client, movedRow, calendarB.CalendarId, "Standup",
            new DateTime(2030, 1, 7, 9, 0, 0, DateTimeKind.Utc), new DateTime(2030, 1, 7, 9, 30, 0, DateTimeKind.Utc));

        var body = await (await client.GetAsync($"{CalendarEventsPath}/ics?calendarIds={calendarA.CalendarId}")).Content.ReadAsStringAsync();

        // Row-count mismatch (2 of 3 fetched) forces the flattened fallback; the moved row (now in B) is excluded.
        Assert.Equal(2, CountOccurrences(body, "BEGIN:VEVENT"));
        Assert.DoesNotContain("RRULE:", body);
    }

    [Fact]
    public async Task ExportAggregate_OccurrenceMovedIntoFilteredCalendar_IncludesItAsStandaloneVEvent()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();
        var calendarA = await CreateCalendarAsync(client, "MoveInA");
        var calendarB = await CreateCalendarAsync(client, "MoveInB");
        var pattern = await CreatePatternAsync(client, calendarB.CalendarId, "Standup",
            new DateTime(2030, 1, 7, 9, 0, 0, DateTimeKind.Utc), occurrenceCount: 3,
            frequency: RecurrenceFrequency.Weekly, daysOfWeek: DaysOfWeekFlags.Monday);

        var movedRow = await FirstGeneratedEventAsync(factory, pattern.RecurrencePatternId);
        await MoveEventToCalendarAsync(client, movedRow, calendarA.CalendarId, "Standup",
            new DateTime(2030, 1, 7, 9, 0, 0, DateTimeKind.Utc), new DateTime(2030, 1, 7, 9, 30, 0, DateTimeKind.Utc));

        var body = await (await client.GetAsync($"{CalendarEventsPath}/ics?calendarIds={calendarA.CalendarId}")).Content.ReadAsStringAsync();

        Assert.Equal(1, CountOccurrences(body, "BEGIN:VEVENT"));
        Assert.DoesNotContain("RRULE:", body);
    }

    [Fact]
    public async Task ExportAggregate_EntirePatternRelocated_CollapsesUnderNewCalendar()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();
        var calendarA = await CreateCalendarAsync(client, "RelocateA");
        var calendarB = await CreateCalendarAsync(client, "RelocateB");
        var pattern = await CreatePatternAsync(client, calendarA.CalendarId, "Standup",
            new DateTime(2030, 1, 7, 9, 0, 0, DateTimeKind.Utc), occurrenceCount: 3,
            frequency: RecurrenceFrequency.Weekly, daysOfWeek: DaysOfWeekFlags.Monday);

        // Reassign every generated row to calendar B, individually, preserving each row's own
        // Start/End — CanExportAsRule alone would still pass (no compared field changed), but the
        // CalendarId-uniformity guard now agrees on B.
        List<(Guid Id, DateTime Start, DateTime End)> generatedRows;
        using (var scope = factory.Services.CreateScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<CalendarContext>();
            generatedRows = ctx.CalendarEvents.Where(e => e.RecurrencePatternId == pattern.RecurrencePatternId)
                .OrderBy(e => e.StartDateTime)
                .Select(e => new { e.CalendarEventId, e.StartDateTime, e.EndDateTime })
                .AsEnumerable()
                .Select(e => (e.CalendarEventId, e.StartDateTime, e.EndDateTime))
                .ToList();
        }

        foreach (var row in generatedRows)
        {
            await MoveEventToCalendarAsync(client, row.Id, calendarB.CalendarId, "Standup", row.Start, row.End);
        }

        var body = await (await client.GetAsync($"{CalendarEventsPath}/ics?calendarIds={calendarB.CalendarId}")).Content.ReadAsStringAsync();

        Assert.Equal(1, CountOccurrences(body, "BEGIN:VEVENT"));
        Assert.Contains("RRULE:", body);
        Assert.Contains("CATEGORIES:RelocateB", body);
    }

    [Fact]
    public async Task ExportAggregate_PatternSplitAcrossCalendars_FlattensDespiteCanExportAsRulePassing()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();
        var calendarA = await CreateCalendarAsync(client, "SplitA");
        var calendarB = await CreateCalendarAsync(client, "SplitB");
        var pattern = await CreatePatternAsync(client, calendarA.CalendarId, "Standup",
            new DateTime(2030, 1, 7, 9, 0, 0, DateTimeKind.Utc), occurrenceCount: 4,
            frequency: RecurrenceFrequency.Weekly, daysOfWeek: DaysOfWeekFlags.Monday);

        List<Guid> rowIds;
        using (var scope = factory.Services.CreateScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<CalendarContext>();
            rowIds = ctx.CalendarEvents.Where(e => e.RecurrencePatternId == pattern.RecurrencePatternId)
                .OrderBy(e => e.StartDateTime).Select(e => e.CalendarEventId).ToList();
        }

        // Move only the first row to B; the rest stay in A. All 4 still individually pass
        // CanExportAsRule's field comparison — only the uniformity guard catches the split.
        var starts = new[]
        {
            new DateTime(2030, 1, 7, 9, 0, 0, DateTimeKind.Utc), new DateTime(2030, 1, 14, 9, 0, 0, DateTimeKind.Utc),
            new DateTime(2030, 1, 21, 9, 0, 0, DateTimeKind.Utc), new DateTime(2030, 1, 28, 9, 0, 0, DateTimeKind.Utc),
        };
        await MoveEventToCalendarAsync(client, rowIds[0], calendarB.CalendarId, "Standup", starts[0], starts[0].AddMinutes(30));

        // No calendar filter: both calendars' rows are fetched, so the whole (still-linked) group is
        // considered together and must flatten because it disagrees on CalendarId.
        var body = await (await client.GetAsync($"{CalendarEventsPath}/ics")).Content.ReadAsStringAsync();

        Assert.Equal(4, CountOccurrences(body, "BEGIN:VEVENT"));
        Assert.DoesNotContain("RRULE:", body);
    }

    // ------------------------------------------------------------------ aggregate: date/search filter (AC 6)

    [Fact]
    public async Task ExportAggregate_DateRangeFilter_NeverCollapsesEvenAnIntactSeries()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();
        var calendar = await CreateCalendarAsync(client, "Windowed");
        await CreatePatternAsync(client, calendar.CalendarId, "Standup",
            new DateTime(2030, 1, 7, 9, 0, 0, DateTimeKind.Utc), occurrenceCount: 4,
            frequency: RecurrenceFrequency.Weekly, daysOfWeek: DaysOfWeekFlags.Monday);

        var from = new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var to = new DateTime(2030, 1, 31, 0, 0, 0, DateTimeKind.Utc);
        var body = await (await client.GetAsync(
            $"{CalendarEventsPath}/ics?from={Uri.EscapeDataString(from.ToString("O"))}&to={Uri.EscapeDataString(to.ToString("O"))}"))
            .Content.ReadAsStringAsync();

        Assert.Equal(4, CountOccurrences(body, "BEGIN:VEVENT"));
        Assert.DoesNotContain("RRULE:", body);
    }

    // Mirrors CalendarEventsApiTests' List_ReturnsEventOverlappingWindowStart — the aggregate export's
    // date filter documents the same overlap (not start-only) semantics as the plain list endpoint
    // (CalendarIcsService.cs: "Overlap, not start-only — matches ListAsync's semantics (AC 6)"), but
    // every prior date-filter test here seeded events entirely inside the window, so a start-only
    // mutant would have gone undetected.
    [Fact]
    public async Task ExportAggregate_DateFilter_IncludesEventOverlappingWindowStart()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();
        var calendar = await CreateCalendarAsync(client, "Overlap");

        // Spans midnight across the window boundary: starts before From but is still in progress.
        var overnight = await CreateEventAsync(client, calendar.CalendarId, "Overnight shift",
            new DateTime(2030, 8, 4, 22, 0, 0, DateTimeKind.Utc));
        var updated = await client.PutAsJsonAsync($"{CalendarEventsPath}/{overnight.CalendarEventId}", new NewCalendarEvent
        {
            CalendarId = calendar.CalendarId,
            Title = "Overnight shift",
            StartDateTime = new DateTime(2030, 8, 4, 22, 0, 0, DateTimeKind.Utc),
            EndDateTime = new DateTime(2030, 8, 5, 6, 0, 0, DateTimeKind.Utc),
        });
        updated.EnsureSuccessStatusCode();

        var from = new DateTime(2030, 8, 5, 0, 0, 0, DateTimeKind.Utc);
        var to = new DateTime(2030, 8, 6, 0, 0, 0, DateTimeKind.Utc);
        var body = await (await client.GetAsync(
            $"{CalendarEventsPath}/ics?from={Uri.EscapeDataString(from.ToString("O"))}&to={Uri.EscapeDataString(to.ToString("O"))}"))
            .Content.ReadAsStringAsync();

        Assert.Contains("SUMMARY:Overnight shift", body);
    }

    [Fact]
    public async Task ExportAggregate_SearchFilter_MatchesTitleOnly()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();
        var calendar = await CreateCalendarAsync(client, "Search");
        await CreateEventAsync(client, calendar.CalendarId, "Team Standup", new DateTime(2030, 2, 1, 9, 0, 0, DateTimeKind.Utc));
        await CreateEventAsync(client, calendar.CalendarId, "Dentist", new DateTime(2030, 2, 2, 9, 0, 0, DateTimeKind.Utc));
        // Negative case: "standup" appears in Location, not Title — must NOT match (the aggregate
        // export's search is Title-only, unlike the Calendar page's own client-side live filter).
        await client.PostAsJsonAsync(CalendarEventsPath, new NewCalendarEvent
        {
            CalendarId = calendar.CalendarId,
            Title = "Team sync",
            Location = "Standup room",
            StartDateTime = new DateTime(2030, 2, 3, 9, 0, 0, DateTimeKind.Utc),
            EndDateTime = new DateTime(2030, 2, 3, 10, 0, 0, DateTimeKind.Utc),
        });

        var body = await (await client.GetAsync($"{CalendarEventsPath}/ics?search=standup")).Content.ReadAsStringAsync();

        Assert.Equal(1, CountOccurrences(body, "BEGIN:VEVENT"));
        Assert.Contains("SUMMARY:Team Standup", body);
        Assert.DoesNotContain("SUMMARY:Team sync", body);
    }

    // Filters were previously only ever exercised one at a time; the actual bulk-export dialog applies
    // calendarIds + date range + search together, so the query conjunction itself (AND, not OR) was
    // untested — a regression here (e.g. one filter silently overriding another) would pass every
    // existing single-filter test.
    [Fact]
    public async Task ExportAggregate_CombinedFilters_AppliesAllConjunctively()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();
        var calendarA = await CreateCalendarAsync(client, "CombinedA");
        var calendarB = await CreateCalendarAsync(client, "CombinedB");

        // Matches calendar + date + search (should be included).
        await CreateEventAsync(client, calendarA.CalendarId, "Standup", new DateTime(2030, 3, 10, 9, 0, 0, DateTimeKind.Utc));
        // Right calendar + search, wrong date (outside the window).
        await CreateEventAsync(client, calendarA.CalendarId, "Standup", new DateTime(2030, 4, 10, 9, 0, 0, DateTimeKind.Utc));
        // Right date + search, wrong calendar.
        await CreateEventAsync(client, calendarB.CalendarId, "Standup", new DateTime(2030, 3, 11, 9, 0, 0, DateTimeKind.Utc));
        // Right calendar + date, wrong search term.
        await CreateEventAsync(client, calendarA.CalendarId, "Dentist", new DateTime(2030, 3, 12, 9, 0, 0, DateTimeKind.Utc));

        var from = new DateTime(2030, 3, 1, 0, 0, 0, DateTimeKind.Utc);
        var to = new DateTime(2030, 3, 31, 0, 0, 0, DateTimeKind.Utc);
        var body = await (await client.GetAsync(
            $"{CalendarEventsPath}/ics?calendarIds={calendarA.CalendarId}" +
            $"&from={Uri.EscapeDataString(from.ToString("O"))}&to={Uri.EscapeDataString(to.ToString("O"))}&search=standup"))
            .Content.ReadAsStringAsync();

        Assert.Equal(1, CountOccurrences(body, "BEGIN:VEVENT"));
    }

    // ------------------------------------------------------------------ aggregate: validation (AC 7)

    [Fact]
    public async Task ExportAggregate_OnlyFromSet_ReturnsBadRequest()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"{CalendarEventsPath}/ics?from={Uri.EscapeDataString(DateTime.UtcNow.ToString("O"))}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ExportAggregate_ToBeforeFrom_ReturnsBadRequest()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();
        var from = new DateTime(2030, 2, 1, 0, 0, 0, DateTimeKind.Utc);
        var to = new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        var response = await client.GetAsync(
            $"{CalendarEventsPath}/ics?from={Uri.EscapeDataString(from.ToString("O"))}&to={Uri.EscapeDataString(to.ToString("O"))}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ExportAggregate_SpanOver92Days_ReturnsBadRequest()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();
        var from = new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var to = from.AddDays(93);

        var response = await client.GetAsync(
            $"{CalendarEventsPath}/ics?from={Uri.EscapeDataString(from.ToString("O"))}&to={Uri.EscapeDataString(to.ToString("O"))}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // Boundary-pass companions to ToBeforeFrom/SpanOver92Days above: the rejecting side alone can't
    // catch a `>` accidentally loosened to `>=` (or `<` to `<=`) — only a value exactly AT the boundary
    // that must still succeed can.
    [Fact]
    public async Task ExportAggregate_ToEqualsFrom_ReturnsOk()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();
        var same = new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        var response = await client.GetAsync(
            $"{CalendarEventsPath}/ics?from={Uri.EscapeDataString(same.ToString("O"))}&to={Uri.EscapeDataString(same.ToString("O"))}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task ExportAggregate_SpanExactly92Days_ReturnsOk()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();
        var calendar = await CreateCalendarAsync(client, "ExactSpan");
        var from = new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var to = from.AddDays(92);
        await CreateEventAsync(client, calendar.CalendarId, "Edge event", to.AddHours(-1));

        var response = await client.GetAsync(
            $"{CalendarEventsPath}/ics?from={Uri.EscapeDataString(from.ToString("O"))}&to={Uri.EscapeDataString(to.ToString("O"))}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("SUMMARY:Edge event", body);
    }

    // ------------------------------------------------------------------ aggregate: caps (AC 8, 9)

    [Fact]
    public async Task ExportAggregate_FilteredMatchExceedsMaxVEvents_ReturnsBadRequest()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();
        var calendar = await CreateCalendarAsync(client, "TooManyFiltered");
        await SeedStandaloneRowsAsync(factory, calendar.CalendarId, DefaultMaxExportEvents + 1, "Match");

        var response = await client.GetAsync($"{CalendarEventsPath}/ics?search=Match");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.Contains("would exceed", problem!.Detail);
    }

    // Boundary-pass companion: exactly MaxVEvents matches must succeed (the filtered path has a single
    // cap — matched rows and output VEVENTs are 1:1, so this also proves the count is exact, not off
    // by one in either direction).
    [Fact]
    public async Task ExportAggregate_FilteredMatchExactlyMaxVEvents_ReturnsOk()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();
        var calendar = await CreateCalendarAsync(client, "ExactlyMaxFiltered");
        await SeedStandaloneRowsAsync(factory, calendar.CalendarId, DefaultMaxExportEvents, "Match");

        var response = await client.GetAsync($"{CalendarEventsPath}/ics?search=Match");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(DefaultMaxExportEvents, CountOccurrences(body, "BEGIN:VEVENT"));
    }

    [Fact]
    public async Task ExportAggregate_NoFilterRawRowsExceedFetchCap_ReturnsBadRequest()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();
        var calendar = await CreateCalendarAsync(client, "TooManyRaw");
        await SeedStandaloneRowsAsync(factory, calendar.CalendarId, Odyssey.Dtos.SystemSettingsDefaults.CalendarIcsMaxAggregateExportRows + 1, "Row");

        var response = await client.GetAsync($"{CalendarEventsPath}/ics");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.Contains("would need to fetch", problem!.Detail);
    }

    // Boundary-pass companion: exactly the shipped fetch cap in raw rows must NOT trip it.
    // They're all standalone, so the output cap (2,000) rejects the request next — but the distinct
    // "would produce" message (vs. "would need to fetch" above) proves it was rejected by the OUTPUT
    // check, meaning the fetch boundary itself correctly let exactly-cap rows through. A `>` loosened
    // to `>=` on the fetch check would instead surface the "would need to fetch" message here.
    [Fact]
    public async Task ExportAggregate_NoFilterRawRowsExactlyFetchCap_PassesFetchCheck()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();
        var calendar = await CreateCalendarAsync(client, "ExactlyFetchCap");
        await SeedStandaloneRowsAsync(factory, calendar.CalendarId, Odyssey.Dtos.SystemSettingsDefaults.CalendarIcsMaxAggregateExportRows, "Row");

        var response = await client.GetAsync($"{CalendarEventsPath}/ics");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.Contains("would produce", problem!.Detail);
    }

    [Fact]
    public async Task ExportAggregate_NoFilterPostCollapseCountExceedsOutputCap_ReturnsBadRequest()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();
        var calendar = await CreateCalendarAsync(client, "TooManyOutput");
        // Under the fetch cap (20,000) but over the output cap (2,000) — all standalone, so nothing
        // collapses and the raw count IS the eventual VEVENT count.
        await SeedStandaloneRowsAsync(factory, calendar.CalendarId, DefaultMaxExportEvents + 1, "Row");

        var response = await client.GetAsync($"{CalendarEventsPath}/ics");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.Contains("would produce", problem!.Detail);
    }

    // ------------------------------------------------------------------ aggregate: export byte-size cap (follow-up to #343)

    // Seeds standalone rows with a long, fixed-length title so a modest row count produces a
    // multi-hundred-KB export — enough to reliably cross a 1 MB CalendarIcsMaxExportMegabytes cap
    // well before the (much higher) default row-count cap would ever kick in.
    private static async Task SeedBulkyStandaloneRowsAsync(ApiFactory factory, Guid calendarId, int count, string titlePrefix)
    {
        using var scope = factory.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<CalendarContext>();
        var now = DateTime.UtcNow;
        var start = new DateTime(2030, 1, 1, 9, 0, 0, DateTimeKind.Utc);
        var padding = new string('x', 700);
        var rows = new List<ContextCalendarEvent>(count);
        for (var i = 0; i < count; i++)
        {
            rows.Add(new ContextCalendarEvent
            {
                CalendarId = calendarId,
                Title = $"{titlePrefix} {i} {padding}",
                StartDateTime = start.AddMinutes(i),
                EndDateTime = start.AddMinutes(i).AddMinutes(30),
                CreatedByUserId = ActorUserId,
                CreatedAt = now,
                UpdatedAt = now,
            });
        }

        ctx.CalendarEvents.AddRange(rows);
        await ctx.SaveChangesAsync();
    }

    [Fact]
    public async Task ExportAggregate_FilteredExportOverByteCap_TruncatesBelowThePromisedRowCount()
    {
        // The byte-size cap can't be rejected up front like the row-count caps above (total output
        // size isn't knowable until it's generated) — it truncates the stream instead, leaving the
        // body with fewer VEVENTs than X-Odyssey-Export-Rows already promised.
        await using var factory = new ApiFactory(ReadWriteWithSizeCapControl);
        using var client = factory.CreateClient();

        (await client.PutAsJsonAsync("/api/system-settings", new SystemSettingsUpdate
        {
            CalendarIcsMaxExportMegabytes = 1,
        })).EnsureSuccessStatusCode();

        var calendar = await CreateCalendarAsync(client, "Bulky");
        await SeedBulkyStandaloneRowsAsync(factory, calendar.CalendarId, 2000, "Bulky");

        // A search filter forces the filtered/chunked path (never the no-filter aggregate one).
        var response = await client.GetAsync($"{CalendarEventsPath}/ics?search=Bulky");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var promisedRows = int.Parse(response.Headers.GetValues("X-Odyssey-Export-Rows").Single());
        Assert.Equal(2000, promisedRows);

        var body = await response.Content.ReadAsStringAsync();
        var deliveredRows = CountOccurrences(body, "BEGIN:VEVENT");
        Assert.True(deliveredRows < promisedRows,
            $"Expected the export to be truncated below {promisedRows} rows, but delivered {deliveredRows}.");
    }

    [Fact]
    public async Task ExportAggregate_NoFilterExportOverByteCap_ReturnsBadRequest_NotTruncated()
    {
        // Unlike the filtered/chunked path above, the no-filter aggregate export isn't streamed — the
        // whole document is already built in memory, and no header has been sent yet, so a too-large
        // result is rejected cleanly with a 400 instead of truncated.
        await using var factory = new ApiFactory(ReadWriteWithSizeCapControl);
        using var client = factory.CreateClient();

        (await client.PutAsJsonAsync("/api/system-settings", new SystemSettingsUpdate
        {
            CalendarIcsMaxExportMegabytes = 1,
        })).EnsureSuccessStatusCode();

        var calendar = await CreateCalendarAsync(client, "BulkyNoFilter");
        await SeedBulkyStandaloneRowsAsync(factory, calendar.CalendarId, 2000, "Row");

        var response = await client.GetAsync($"{CalendarEventsPath}/ics");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.Contains("exceeds the configured maximum", problem!.Detail);
    }

    // ------------------------------------------------------------------ claims (AC 15)

    [Fact]
    public async Task ExportAggregate_WithoutReadClaim_ReturnsForbidden()
    {
        await using var factory = new ApiFactory([]);
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"{CalendarEventsPath}/ics");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task ExportPattern_WithoutReadClaim_ReturnsForbidden()
    {
        await using var factory = new ApiFactory([]);
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"{RecurrencePatternsPath}/{Guid.NewGuid()}/ics");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
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

    private static List<string> ExtractUidLines(string body) => body
        .Split("\r\n", StringSplitOptions.RemoveEmptyEntries)
        .Where(line => line.StartsWith("UID:", StringComparison.Ordinal))
        .ToList();

    private static string Vcalendar(params string[] vevents) =>
        "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//test//EN\r\n" + string.Concat(vevents) + "END:VCALENDAR\r\n";

    private static string Vevent(string uid, params string[] lines) =>
        "BEGIN:VEVENT\r\nUID:" + uid + "\r\n" + string.Join("\r\n", lines) + "\r\nEND:VEVENT\r\n";

    private static async Task<HttpResponseMessage> PostIcsAsync(HttpClient client, Guid calendarId, string ics)
    {
        using var content = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes(ics));
        fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/calendar");
        content.Add(fileContent, "file", "calendar.ics");
        return await client.PostAsync($"{CalendarsPath}/{calendarId}/ics", content);
    }

    private static async Task<ExistingCalendar> CreateCalendarAsync(HttpClient client, string name)
    {
        var post = await client.PostAsJsonAsync(CalendarsPath, new NewCalendar { Name = name, Color = "#0369A1" });
        post.EnsureSuccessStatusCode();
        return (await post.Content.ReadFromJsonAsync<ExistingCalendar>())!;
    }

    private static async Task<ExistingCalendarEvent> CreateEventAsync(HttpClient client, Guid calendarId, string title, DateTime start)
    {
        var post = await client.PostAsJsonAsync(CalendarEventsPath, new NewCalendarEvent
        {
            CalendarId = calendarId,
            Title = title,
            StartDateTime = start,
            EndDateTime = start.AddHours(1),
        });
        post.EnsureSuccessStatusCode();
        return (await post.Content.ReadFromJsonAsync<ExistingCalendarEvent>())!;
    }

    private static async Task<ExistingRecurrencePattern> CreatePatternAsync(
        HttpClient client, Guid calendarId, string title, DateTime start, int occurrenceCount,
        RecurrenceFrequency frequency, DaysOfWeekFlags? daysOfWeek = null, int? dayOfMonth = null, int? monthOfYear = null)
    {
        var post = await client.PostAsJsonAsync(RecurrencePatternsPath, new NewRecurrencePattern
        {
            CalendarId = calendarId,
            Title = title,
            StartDateTime = start,
            EndDateTime = start.AddMinutes(30),
            Frequency = frequency,
            DaysOfWeek = daysOfWeek,
            DayOfMonth = dayOfMonth,
            MonthOfYear = monthOfYear ?? (frequency == RecurrenceFrequency.Yearly ? start.Month : null),
            OccurrenceCount = occurrenceCount,
        });
        post.EnsureSuccessStatusCode();
        return (await post.Content.ReadFromJsonAsync<ExistingRecurrencePattern>())!;
    }

    private static async Task<Guid> FirstGeneratedEventAsync(ApiFactory factory, Guid patternId)
    {
        using var scope = factory.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<CalendarContext>();
        return ctx.CalendarEvents.Where(e => e.RecurrencePatternId == patternId)
            .OrderBy(e => e.StartDateTime).First().CalendarEventId;
    }

    private static async Task MoveEventToCalendarAsync(
        HttpClient client, Guid eventId, Guid newCalendarId, string title, DateTime start, DateTime end)
    {
        var response = await client.PutAsJsonAsync($"{CalendarEventsPath}/{eventId}", new NewCalendarEvent
        {
            CalendarId = newCalendarId,
            Title = title,
            StartDateTime = start,
            EndDateTime = end,
        });
        response.EnsureSuccessStatusCode();
    }

    // Seeds standalone CalendarEvent rows directly through the DbContext — bulk-volume cap tests need
    // thousands of rows, which would be prohibitively slow one HTTP POST at a time.
    private static async Task SeedStandaloneRowsAsync(ApiFactory factory, Guid calendarId, int count, string titlePrefix)
    {
        using var scope = factory.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<CalendarContext>();
        var now = DateTime.UtcNow;
        var start = new DateTime(2030, 1, 1, 9, 0, 0, DateTimeKind.Utc);
        var rows = new List<ContextCalendarEvent>(count);
        for (var i = 0; i < count; i++)
        {
            rows.Add(new ContextCalendarEvent
            {
                CalendarId = calendarId,
                Title = $"{titlePrefix} {i}",
                StartDateTime = start.AddMinutes(i),
                EndDateTime = start.AddMinutes(i).AddMinutes(30),
                CreatedByUserId = ActorUserId,
                CreatedAt = now,
                UpdatedAt = now,
            });
        }

        ctx.CalendarEvents.AddRange(rows);
        await ctx.SaveChangesAsync();
    }

    private sealed class ApiFactory : OdysseyApiFactory
    {
        public ApiFactory(IReadOnlyCollection<string>? permissions)
            : base(permissions, ActorUserId, configureServices: IsolateCalendarContext)
        {
        }

        private static void IsolateCalendarContext(IServiceCollection services)
        {
            var calendarDatabaseName = $"calendar-events-ics-{Guid.NewGuid()}";
            services.RemoveAll<DbContextOptions<CalendarContext>>();
            services.AddDbContext<CalendarContext>(options => options.UseInMemoryDatabase(calendarDatabaseName));
        }
    }
}
