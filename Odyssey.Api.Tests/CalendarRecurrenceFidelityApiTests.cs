using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using Odyssey.Api.Tests.Infrastructure;
using Odyssey.Context;
using Odyssey.Core.Journal;
using Odyssey.Dtos;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos.Journal;
using Odyssey.Testing.Logging;
using Xunit;
using ContextCalendarEvent = Odyssey.Context.CalendarEvent;
using ContextRecurrencePattern = Odyssey.Context.RecurrencePattern;
using DtoFrequency = Odyssey.Dtos.Journal.RecurrenceFrequency;
using DtoDays = Odyssey.Dtos.Journal.DaysOfWeekFlags;

namespace Odyssey.Api.Tests;

/// <summary>
/// Calendar recurrence fidelity and time zones (issue #519) over HTTP, on EF InMemory: wall-clock plus zone writes,
/// occurrence keys, overrides and exclusions surviving series edits and re-imports, the preview and the version and
/// consent checks, and the import's too-many-components refusal code. The zoned export and the grouped import with its
/// removal gate are pinned at the service tier in <c>Odyssey.Core.Tests/Ics/CalendarRecurrenceIcsServiceTests</c>. The
/// series lock, the SQL window and the races are covered against MariaDB in <c>Odyssey.IntegrationTests</c>.
/// </summary>
public class CalendarRecurrenceFidelityApiTests(CalendarRecurrenceFidelityApiTests.Host host) : SharedApiHostTests<CalendarRecurrenceFidelityApiTests.Host>(host)
{
    private const string ActorUserId = "calendar-recurrence-actor";
    private const string CalendarsPath = "/api/calendars";
    private const string EventsPath = "/api/calendar-events";
    private const string PatternsPath = "/api/recurrence-patterns";

    // Every test runs at this instant, so "future" is decided by the test and not by the wall clock.
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static readonly string[] ReadWrite =
    [
        PermissionClaims.CalendarRead, PermissionClaims.CalendarCreate, PermissionClaims.CalendarUpdate,
        PermissionClaims.CalendarDelete,
    ];

    // ------------------------------------------------------------------ AC 1, 2: zones and DST

    [Fact]
    public async Task A_weekly_0900_oslo_series_keeps_its_local_time_and_its_instants_move_at_the_march_change()
    {
        var factory = Factory;
        using var client = factory.ClientFor(ReadWrite);
        var calendar = await CreateCalendarAsync(client, "Oslo");

        var series = await CreatePatternAsync(client, new NewRecurrencePattern
        {
            CalendarId = calendar.CalendarId, Title = "Standup",
            StartDateTime = Wall(2026, 3, 16, 9), EndDateTime = Wall(2026, 3, 16, 10), TimeZoneId = "Europe/Oslo",
            Frequency = DtoFrequency.Weekly, DaysOfWeek = DtoDays.Monday, OccurrenceCount = 4,
        });
        var rows = await SeriesRowsAsync(client, series.RecurrencePatternId);

        Assert.All(rows, r => Assert.Equal(new TimeSpan(9, 0, 0), r.StartDateTime.TimeOfDay));
        Assert.All(rows, r => Assert.Equal("Europe/Oslo", r.TimeZoneId));
        Assert.Equal(
            [Utc(2026, 3, 16, 8), Utc(2026, 3, 23, 8), Utc(2026, 3, 30, 7), Utc(2026, 4, 6, 7)],
            rows.Select(r => r.StartUtc!.Value));
        Assert.All(rows, r => Assert.Equal(r.StartDateTime, r.OccurrenceKey));
        Assert.Equal(Utc(2026, 3, 16, 8), series.StartUtc);
        Assert.Equal("Europe/Oslo", series.TimeZoneId);
        Assert.True(series.IsKeyed);
    }

    [Fact]
    public async Task A_weekly_monday_2330_los_angeles_series_falls_on_local_mondays()
    {
        var factory = Factory;
        using var client = factory.ClientFor(ReadWrite);
        var calendar = await CreateCalendarAsync(client, "LA");

        var series = await CreatePatternAsync(client, new NewRecurrencePattern
        {
            CalendarId = calendar.CalendarId, Title = "Late call",
            StartDateTime = Wall(2026, 3, 2, 23, 30), EndDateTime = Wall(2026, 3, 3, 0, 15), TimeZoneId = "America/Los_Angeles",
            Frequency = DtoFrequency.Weekly, DaysOfWeek = DtoDays.Monday, OccurrenceCount = 4,
        });
        var rows = await SeriesRowsAsync(client, series.RecurrencePatternId);

        Assert.Equal(4, rows.Count);
        Assert.All(rows, r => Assert.Equal(DayOfWeek.Monday, r.StartDateTime.DayOfWeek));
        Assert.All(rows, r => Assert.Equal(DayOfWeek.Tuesday, r.StartUtc!.Value.DayOfWeek));
    }

    [Fact]
    public async Task A_wall_clock_in_a_gap_moves_forward_and_an_ambiguous_one_takes_the_earlier_instant()
    {
        var factory = Factory;
        using var client = factory.ClientFor(ReadWrite);
        var calendar = await CreateCalendarAsync(client, "Gaps");

        var gap = await CreateEventAsync(client, calendar.CalendarId, "Gap", Wall(2026, 3, 29, 2, 30), Wall(2026, 3, 29, 4, 30), "Europe/Oslo");
        var overlap = await CreateEventAsync(client, calendar.CalendarId, "Overlap", Wall(2026, 10, 25, 2, 30), Wall(2026, 10, 25, 3, 30), "Europe/Oslo");

        // §8.1, #520 F2: the response shows what was stored.
        Assert.Equal(Wall(2026, 3, 29, 3, 30), gap.StartDateTime);
        Assert.Equal(Utc(2026, 3, 29, 1, 30), gap.StartUtc);
        Assert.Equal(Utc(2026, 10, 25, 0, 30), overlap.StartUtc);
    }

    // ------------------------------------------------------------------ AC 3: floating

    [Fact]
    public async Task A_floating_event_reads_back_unconverted_and_exports_without_z()
    {
        var factory = Factory;
        using var client = factory.ClientFor(ReadWrite);
        var calendar = await CreateCalendarAsync(client, "Floating");

        var created = await CreateEventAsync(client, calendar.CalendarId, "Legacy", Wall(2030, 6, 1, 9), Wall(2030, 6, 1, 10), null);
        var read = (await client.GetFromJsonAsync<ExistingCalendarEvent>($"{EventsPath}/{created.CalendarEventId}"))!;

        Assert.True(read.IsFloating);
        Assert.Null(read.StartUtc);
        Assert.Null(read.TimeZoneId);
        Assert.Equal(new TimeSpan(9, 0, 0), read.StartDateTime.TimeOfDay);
        foreach (var zone in new[] { "UTC", "Europe/Oslo", "Asia/Tokyo" })
        {
            var listed = await ListAsync(client, calendar.CalendarId, Utc(2030, 5, 31, 0), Utc(2030, 6, 3, 0), zone);
            Assert.Equal(new TimeSpan(9, 0, 0), Assert.Single(listed.Items).StartDateTime.TimeOfDay);
        }

        var body = await client.GetStringAsync($"{EventsPath}/{created.CalendarEventId}/ics");
        Assert.Contains("DTSTART:20300601T090000\r\n", body);
        Assert.DoesNotContain("BEGIN:VTIMEZONE", body);
    }

    // ------------------------------------------------------------------ AC 4, 5, 6: keys survive series edits

    [Fact]
    public async Task A_description_edit_keeps_an_overridden_title_and_a_deleted_occurrence()
    {
        var factory = Factory;
        using var client = factory.ClientFor(ReadWrite);
        var calendar = await CreateCalendarAsync(client, "Template");
        var series = await WeeklyMondaysAsync(client, calendar.CalendarId, count: 5);
        var rows = await SeriesRowsAsync(client, series.RecurrencePatternId);

        await EditTitleAsync(client, rows[1], "Special");
        await DeleteAsync(client, rows[2].CalendarEventId);
        var current = (await client.GetFromJsonAsync<ExistingRecurrencePattern>($"{PatternsPath}/{series.RecurrencePatternId}"))!;
        Assert.Equal(1, current.ExclusionCount);
        Assert.Equal(series.Version, current.Version); // occurrence writes never rotate it (v4 §3)

        var result = await UpdatePatternAsync(client, series.RecurrencePatternId, Rule(calendar.CalendarId, count: 5) with
        {
            Description = "New agenda", ExpectedVersion = current.Version,
            ConfirmedDrops = new RecurrenceConfirmedDrops(),
        });

        Assert.Equal(0, result.DroppedOverrideCount);
        Assert.Equal(0, result.DroppedExclusionCount);
        Assert.NotEqual(current.Version, result.Pattern.Version);
        var after = await SeriesRowsAsync(client, series.RecurrencePatternId);
        Assert.Equal(4, after.Count);
        var special = Assert.Single(after, r => r.IsOverride);
        Assert.Equal("Special", special.Title);
        Assert.Null(special.Description);
        Assert.All(after.Where(r => !r.IsOverride), r => Assert.Equal("New agenda", r.Description));
        Assert.DoesNotContain(after, r => r.OccurrenceKey == rows[2].OccurrenceKey);
    }

    [Fact]
    public async Task Moving_a_weekly_series_from_mondays_to_tuesdays_drops_its_future_exceptions_and_counts_them()
    {
        var factory = Factory;
        using var client = factory.ClientFor(ReadWrite);
        var calendar = await CreateCalendarAsync(client, "Days");
        var series = await WeeklyMondaysAsync(client, calendar.CalendarId, count: 4);
        var rows = await SeriesRowsAsync(client, series.RecurrencePatternId);
        await EditTitleAsync(client, rows[1], "Edited");
        await DeleteAsync(client, rows[2].CalendarEventId);

        var result = await UpdatePatternAsync(client, series.RecurrencePatternId,
            Rule(calendar.CalendarId, count: 4) with { DaysOfWeek = DtoDays.Tuesday });

        Assert.Equal(1, result.DroppedOverrideCount);
        Assert.Equal(1, result.DroppedExclusionCount);
        var after = await SeriesRowsAsync(client, series.RecurrencePatternId);
        Assert.Equal(4, after.Count);
        Assert.All(after, r => Assert.Equal(DayOfWeek.Tuesday, r.StartDateTime.DayOfWeek));
        Assert.DoesNotContain(after, r => r.IsOverride);
        Assert.Equal(0, result.Pattern.ExclusionCount);
    }

    [Fact]
    public async Task Changing_the_interval_from_one_to_two_drops_exactly_the_odd_week_exceptions()
    {
        var factory = Factory;
        using var client = factory.ClientFor(ReadWrite);
        var calendar = await CreateCalendarAsync(client, "Interval");
        var until = Wall(2026, 3, 9, 9);
        var series = await CreatePatternAsync(client, Rule(calendar.CalendarId, count: null) with { RecurrenceEndDate = until });
        var rows = await SeriesRowsAsync(client, series.RecurrencePatternId); // weeks 0..5
        Assert.Equal(6, rows.Count);
        await EditTitleAsync(client, rows[1], "Odd override");
        await EditTitleAsync(client, rows[2], "Even override");
        await DeleteAsync(client, rows[3].CalendarEventId);
        await DeleteAsync(client, rows[4].CalendarEventId);

        var result = await UpdatePatternAsync(client, series.RecurrencePatternId,
            Rule(calendar.CalendarId, count: null) with { RecurrenceEndDate = until, Interval = 2 });

        Assert.Equal(1, result.DroppedOverrideCount);
        Assert.Equal(1, result.DroppedExclusionCount);
        var after = await SeriesRowsAsync(client, series.RecurrencePatternId);
        Assert.Equal(["Standup", "Even override"], after.Select(r => r.Title));
        Assert.Equal(1, result.Pattern.ExclusionCount);
    }

    [Fact]
    public async Task Giving_a_floating_series_a_zone_keeps_every_key_and_drops_nothing()
    {
        var factory = Factory;
        using var client = factory.ClientFor(ReadWrite);
        var calendar = await CreateCalendarAsync(client, "Zone only");
        var series = await WeeklyMondaysAsync(client, calendar.CalendarId, count: 4);
        var rows = await SeriesRowsAsync(client, series.RecurrencePatternId);
        await EditTitleAsync(client, rows[1], "Edited");
        await DeleteAsync(client, rows[2].CalendarEventId);

        var result = await UpdatePatternAsync(client, series.RecurrencePatternId,
            Rule(calendar.CalendarId, count: 4) with { TimeZoneId = "Europe/Oslo" });

        Assert.Equal(0, result.DroppedOverrideCount);
        Assert.Equal(0, result.DroppedExclusionCount);
        var after = await SeriesRowsAsync(client, series.RecurrencePatternId);
        Assert.Equal(3, after.Count);
        Assert.All(after, r => Assert.Equal("Europe/Oslo", r.TimeZoneId));
        Assert.All(after, r => Assert.Equal(new TimeSpan(9, 0, 0), r.StartDateTime.TimeOfDay));
        Assert.Equal(rows.Where((_, i) => i != 2).Select(r => r.OccurrenceKey), after.Select(r => r.OccurrenceKey));
        Assert.Equal("Edited", after[1].Title);
    }

    [Fact]
    public async Task The_preview_reports_the_counts_the_put_then_applies_and_saves_nothing()
    {
        var factory = Factory;
        using var client = factory.ClientFor(ReadWrite);
        var calendar = await CreateCalendarAsync(client, "Preview");
        var series = await WeeklyMondaysAsync(client, calendar.CalendarId, count: 4);
        var rows = await SeriesRowsAsync(client, series.RecurrencePatternId);
        await EditTitleAsync(client, rows[1], "Edited");
        await DeleteAsync(client, rows[3].CalendarEventId);
        var body = Rule(calendar.CalendarId, count: 4) with { DaysOfWeek = DtoDays.Wednesday };

        var previewResponse = await client.PostAsJsonAsync($"{PatternsPath}/{series.RecurrencePatternId}/preview", body);
        previewResponse.EnsureSuccessStatusCode();
        var preview = (await previewResponse.Content.ReadFromJsonAsync<RecurrencePatternPreview>())!;
        Assert.Equal("Edited", Assert.Single(await SeriesRowsAsync(client, series.RecurrencePatternId), r => r.IsOverride).Title);

        var result = await UpdatePatternAsync(client, series.RecurrencePatternId, body with
        {
            ExpectedVersion = preview.Version,
            ConfirmedDrops = new RecurrenceConfirmedDrops { Overrides = preview.DroppedOverrideCount, Exclusions = preview.DroppedExclusionCount },
        });

        Assert.Equal((1, 1), (preview.DroppedOverrideCount, preview.DroppedExclusionCount));
        Assert.Equal((preview.DroppedOverrideCount, preview.DroppedExclusionCount), (result.DroppedOverrideCount, result.DroppedExclusionCount));
        Assert.Equal(series.Version, preview.Version);
    }

    [Fact]
    public async Task A_stale_expected_version_is_a_409_and_changes_nothing()
    {
        var factory = Factory;
        using var client = factory.ClientFor(ReadWrite);
        var calendar = await CreateCalendarAsync(client, "Stale");
        var series = await WeeklyMondaysAsync(client, calendar.CalendarId, count: 3);
        await UpdatePatternAsync(client, series.RecurrencePatternId, Rule(calendar.CalendarId, count: 3) with { Title = "Moved on" });

        var response = await client.PutAsJsonAsync($"{PatternsPath}/{series.RecurrencePatternId}",
            Rule(calendar.CalendarId, count: 3) with { Title = "Mine", ExpectedVersion = series.Version });

        await AssertConflictAsync(response, CalendarSeriesConflictException.VersionConflict);
        var stored = (await client.GetFromJsonAsync<ExistingRecurrencePattern>($"{PatternsPath}/{series.RecurrencePatternId}"))!;
        Assert.Equal("Moved on", stored.Title);
    }

    [Fact]
    public async Task Drops_beyond_the_confirmed_counts_are_a_409_carrying_the_actual_counts_and_change_nothing()
    {
        var factory = Factory;
        using var client = factory.ClientFor(ReadWrite);
        var calendar = await CreateCalendarAsync(client, "Consent");
        var series = await WeeklyMondaysAsync(client, calendar.CalendarId, count: 3);
        var rows = await SeriesRowsAsync(client, series.RecurrencePatternId);
        await EditTitleAsync(client, rows[1], "Edited");

        var response = await client.PutAsJsonAsync($"{PatternsPath}/{series.RecurrencePatternId}",
            Rule(calendar.CalendarId, count: 3) with
            {
                DaysOfWeek = DtoDays.Friday, ExpectedVersion = series.Version, ConfirmedDrops = new RecurrenceConfirmedDrops(),
            });

        var problem = await AssertConflictAsync(response, CalendarSeriesConflictException.DropsChanged);
        Assert.Equal("1", problem.Extensions["droppedOverrideCount"]?.ToString());
        Assert.Equal("0", problem.Extensions["droppedExclusionCount"]?.ToString());
        Assert.Contains(await SeriesRowsAsync(client, series.RecurrencePatternId), r => r.Title == "Edited");
    }

    // ------------------------------------------------------------------ Authorization (PR #528 review T1)

    [Fact]
    public async Task The_preview_needs_calendar_update()
    {
        using var client = Factory.ClientFor(ReadWrite);
        var calendarId = (await CreateCalendarAsync(client, "Preview auth")).CalendarId;
        var patternId = (await WeeklyMondaysAsync(client, calendarId, count: 3)).RecurrencePatternId;

        using var readOnly = Factory.ClientFor(
            [PermissionClaims.CalendarRead, PermissionClaims.CalendarCreate, PermissionClaims.CalendarDelete]);
        var refused = await readOnly.PostAsJsonAsync($"{PatternsPath}/{patternId}/preview", Rule(calendarId, count: 3));

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync($"{PatternsPath}/{patternId}/preview", Rule(calendarId, count: 3))).StatusCode);
    }

    [Fact]
    public async Task A_series_put_that_drops_exceptions_needs_calendar_update_alone()
    {
        // v3 §B.6: an interactive series PUT discards exceptions under calendar.update, because the user previewed and
        // confirmed them; only an import's discards join removals under allowRemovals + calendar.delete.
        var factory = Factory;
        using var client = factory.ClientFor([PermissionClaims.CalendarRead, PermissionClaims.CalendarCreate, PermissionClaims.CalendarUpdate]);
        var calendar = await CreateCalendarAsync(client, "Update alone");
        var series = await WeeklyMondaysAsync(client, calendar.CalendarId, count: 3);
        var rows = await SeriesRowsAsync(client, series.RecurrencePatternId);
        await EditTitleAsync(client, rows[1], "Edited");

        var result = await UpdatePatternAsync(client, series.RecurrencePatternId,
            Rule(calendar.CalendarId, count: 3) with { DaysOfWeek = DtoDays.Thursday });

        Assert.Equal(1, result.DroppedOverrideCount);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.DeleteAsync($"{EventsPath}/{rows[0].CalendarEventId}")).StatusCode);

        using var other = factory.ClientFor([PermissionClaims.CalendarRead, PermissionClaims.CalendarDelete]);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await other.PutAsJsonAsync($"{PatternsPath}/{series.RecurrencePatternId}", Rule(calendar.CalendarId, count: 3))).StatusCode);
    }

    // ------------------------------------------------------------------ AC 14: server-set fields

    [Fact]
    public async Task A_body_carrying_server_set_fields_changes_none_of_them()
    {
        var factory = Factory;
        using var client = factory.ClientFor(ReadWrite);
        var calendar = await CreateCalendarAsync(client, "Server set");
        var created = await CreateEventAsync(client, calendar.CalendarId, "Plain", Wall(2030, 6, 1, 9), Wall(2030, 6, 1, 10), null);
        var series = await WeeklyMondaysAsync(client, calendar.CalendarId, count: 2);
        var json = $$"""
            {"calendarId":"{{calendar.CalendarId}}","title":"Plain","startDateTime":"2030-06-01T09:00:00Z","endDateTime":"2030-06-01T10:00:00Z",
             "occurrenceKey":"2030-06-01T09:00:00","isOverride":true,"recurrencePatternId":"{{series.RecurrencePatternId}}","version":"{{Guid.NewGuid()}}"}
            """;

        (await client.PutAsync($"{EventsPath}/{created.CalendarEventId}", new StringContent(json, Encoding.UTF8, "application/json")))
            .EnsureSuccessStatusCode();
        var seriesJson = $$"""
            {"calendarId":"{{calendar.CalendarId}}","title":"Standup","startDateTime":"2026-02-02T09:00:00","endDateTime":"2026-02-02T09:30:00",
             "frequency":1,"daysOfWeek":1,"occurrenceCount":2,"version":"{{Guid.NewGuid()}}","keysBackfilledAt":null}
            """;
        (await client.PutAsync($"{PatternsPath}/{series.RecurrencePatternId}", new StringContent(seriesJson, Encoding.UTF8, "application/json")))
            .EnsureSuccessStatusCode();

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        var row = await db.CalendarEvents.SingleAsync(e => e.CalendarEventId == created.CalendarEventId);
        Assert.Null(row.OccurrenceKey);
        Assert.False(row.IsOverride);
        Assert.Null(row.RecurrencePatternId);
        Assert.NotNull((await db.RecurrencePatterns.SingleAsync(p => p.RecurrencePatternId == series.RecurrencePatternId)).KeysBackfilledAt);
    }

    // ------------------------------------------------------------------ AC 13: zone validation

    [Theory]
    [InlineData("Mars/Olympus")]
    [InlineData("../x")]
    [InlineData("posix/Europe/Oslo")]
    [InlineData("localtime")]
    [InlineData("W. Europe Standard Time")]
    public async Task A_zone_that_is_not_an_iana_id_is_a_400_on_every_write(string zone)
    {
        var factory = Factory;
        using var client = factory.ClientFor(ReadWrite);
        var calendar = await CreateCalendarAsync(client, "Zones");
        var series = await WeeklyMondaysAsync(client, calendar.CalendarId, count: 2);
        var created = await CreateEventAsync(client, calendar.CalendarId, "E", Wall(2030, 6, 1, 9), Wall(2030, 6, 1, 10), null);
        var evt = new NewCalendarEvent
        {
            CalendarId = calendar.CalendarId, Title = "E", StartDateTime = Wall(2030, 6, 1, 9), EndDateTime = Wall(2030, 6, 1, 10),
            TimeZoneId = zone,
        };

        foreach (var response in new[]
        {
            await client.PostAsJsonAsync(EventsPath, evt),
            await client.PutAsJsonAsync($"{EventsPath}/{created.CalendarEventId}", evt),
            await client.PostAsJsonAsync(PatternsPath, Rule(calendar.CalendarId, count: 2) with { TimeZoneId = zone }),
            await client.PutAsJsonAsync($"{PatternsPath}/{series.RecurrencePatternId}", Rule(calendar.CalendarId, count: 2) with { TimeZoneId = zone }),
            await client.GetAsync($"{EventsPath}?from=2030-06-01T00:00:00Z&to=2030-06-02T00:00:00Z&viewTimeZoneId={Uri.EscapeDataString(zone)}"),
            await PostIcsAsync(client, calendar.CalendarId, Vcalendar(), defaultTimeZoneId: zone),
        })
        {
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var problem = (await response.Content.ReadFromJsonAsync<ValidationProblemDetails>())!;
            Assert.Contains(problem.Errors.Keys, key => key.EndsWith("TimeZoneId", StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public async Task With_a_zone_sent_a_z_time_is_the_same_wall_clock_and_an_offset_is_a_400_on_its_field()
    {
        // §5.1 "Their Kind is ignored": Z and no designator name the same wall-clock. An offset cannot be ignored — binding
        // has already turned it into server-local time — so it is refused rather than read two ways (PR #528 review A3).
        var factory = Factory;
        using var client = factory.ClientFor(ReadWrite);
        var calendar = await CreateCalendarAsync(client, "Designators");
        HttpResponseMessage Post(string start, string end) => client.PostAsync(EventsPath, new StringContent(
            $$"""{"calendarId":"{{calendar.CalendarId}}","title":"T","startDateTime":"{{start}}","endDateTime":"{{end}}","timeZoneId":"Europe/Oslo"}""",
            Encoding.UTF8, "application/json")).GetAwaiter().GetResult();

        var plain = (await Post("2030-06-01T09:00:00", "2030-06-01T10:00:00").Content.ReadFromJsonAsync<ExistingCalendarEvent>())!;
        var zulu = (await Post("2030-06-01T09:00:00Z", "2030-06-01T10:00:00Z").Content.ReadFromJsonAsync<ExistingCalendarEvent>())!;
        var offset = Post("2030-06-01T09:00:00+00:00", "2030-06-01T10:00:00");

        Assert.Equal(Utc(2030, 6, 1, 7), plain.StartUtc);
        Assert.Equal(plain.StartUtc, zulu.StartUtc);
        Assert.Equal(HttpStatusCode.BadRequest, offset.StatusCode);
        var problem = (await offset.Content.ReadFromJsonAsync<ValidationProblemDetails>())!;
        Assert.Contains("startDateTime", problem.Errors.Keys);
    }

    [Fact]
    public async Task An_all_day_zone_is_stored_as_null_and_a_put_without_a_zone_keeps_the_stored_one()
    {
        var factory = Factory;
        using var client = factory.ClientFor(ReadWrite);
        var calendar = await CreateCalendarAsync(client, "Keep zone");

        var allDay = (await (await client.PostAsJsonAsync(EventsPath, new NewCalendarEvent
        {
            CalendarId = calendar.CalendarId, Title = "Holiday", StartDateTime = Utc(2030, 6, 1, 0), EndDateTime = Utc(2030, 6, 2, 0),
            IsAllDay = true, TimeZoneId = "Europe/Oslo",
        })).Content.ReadFromJsonAsync<ExistingCalendarEvent>())!;
        Assert.Null(allDay.TimeZoneId);

        var zoned = await CreateEventAsync(client, calendar.CalendarId, "Zoned", Wall(2030, 6, 1, 9), Wall(2030, 6, 1, 10), "Asia/Tokyo");
        var put = await client.PutAsJsonAsync($"{EventsPath}/{zoned.CalendarEventId}", new NewCalendarEvent
        {
            CalendarId = calendar.CalendarId, Title = "Zoned", StartDateTime = Wall(2030, 6, 1, 11), EndDateTime = Wall(2030, 6, 1, 12),
        });
        var updated = (await put.Content.ReadFromJsonAsync<ExistingCalendarEvent>())!;

        Assert.Equal("Asia/Tokyo", updated.TimeZoneId);
        Assert.Equal(Utc(2030, 6, 1, 2), updated.StartUtc);
    }

    // ------------------------------------------------------------------ Occurrence writes

    [Fact]
    public async Task Deleting_an_occurrence_records_an_exclusion_that_a_series_edit_does_not_bring_back()
    {
        var factory = Factory;
        using var client = factory.ClientFor(ReadWrite);
        var calendar = await CreateCalendarAsync(client, "Stays deleted");
        var series = await WeeklyMondaysAsync(client, calendar.CalendarId, count: 3);
        var rows = await SeriesRowsAsync(client, series.RecurrencePatternId);

        var delete = await client.DeleteAsync($"{EventsPath}/{rows[1].CalendarEventId}");
        Assert.Equal(HttpStatusCode.OK, delete.StatusCode);
        Assert.False((await delete.Content.ReadFromJsonAsync<CalendarEventDeleteResult>())!.Detached);
        await UpdatePatternAsync(client, series.RecurrencePatternId, Rule(calendar.CalendarId, count: 3) with { Title = "Renamed" });

        var after = await SeriesRowsAsync(client, series.RecurrencePatternId);
        Assert.Equal(2, after.Count);
        Assert.DoesNotContain(after, r => r.OccurrenceKey == rows[1].OccurrenceKey);
    }

    [Fact]
    public async Task An_occurrence_write_keys_a_legacy_series_inline_and_reports_a_detached_row()
    {
        var factory = Factory;
        using var client = factory.ClientFor(ReadWrite);
        var calendar = await CreateCalendarAsync(client, "Legacy");
        var (patternId, rowIds) = await SeedLegacySeriesAsync(factory, calendar.CalendarId, rescheduleThird: true);

        // The rescheduled third row matches no key, so keying detaches it; its own delete then applies to it alone.
        var response = await client.DeleteAsync($"{EventsPath}/{rowIds[2]}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = (await response.Content.ReadFromJsonAsync<CalendarEventDeleteResult>())!;
        Assert.True(result.Detached);
        Assert.Equal(1, result.DetachedCount);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        var pattern = await db.RecurrencePatterns.SingleAsync(p => p.RecurrencePatternId == patternId);
        Assert.NotNull(pattern.KeysBackfilledAt);
        // The third occurrence's own key is now an exclusion (§11 step 4): the slot is remembered as deleted.
        Assert.Single(await db.RecurrenceExclusions.Where(x => x.RecurrencePatternId == patternId).ToListAsync());
        Assert.Equal(2, await db.CalendarEvents.CountAsync(e => e.RecurrencePatternId == patternId && e.OccurrenceKey != null));
    }

    // ------------------------------------------------------------------ AC 16 (InMemory half): series delete

    [Fact]
    public async Task A_series_delete_removes_its_exclusions_and_clears_keys_on_the_rows_it_detaches()
    {
        var clock = new FakeTimeProvider(Now);
        await using var factory = new ApiFactory(ReadWrite, clock);
        using var client = factory.CreateClient();
        var calendar = await CreateCalendarAsync(client, "Delete series");
        var series = await WeeklyMondaysAsync(client, calendar.CalendarId, count: 4);
        var rows = await SeriesRowsAsync(client, series.RecurrencePatternId);
        await EditTitleAsync(client, rows[0], "Past override");
        await DeleteAsync(client, rows[3].CalendarEventId);
        clock.SetUtcNow(new DateTimeOffset(2026, 2, 12, 0, 0, 0, TimeSpan.Zero)); // rows 0 and 1 are past now

        (await client.DeleteAsync($"{PatternsPath}/{series.RecurrencePatternId}")).EnsureSuccessStatusCode();

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        Assert.Empty(await db.RecurrenceExclusions.ToListAsync());
        var kept = await db.CalendarEvents.Where(e => e.CalendarId == calendar.CalendarId).ToListAsync();
        Assert.Equal(2, kept.Count);
        Assert.All(kept, r => Assert.Null(r.RecurrencePatternId));
        Assert.All(kept, r => Assert.Null(r.OccurrenceKey));
        Assert.All(kept, r => Assert.False(r.IsOverride));
    }

    // ------------------------------------------------------------------ Import shape

    [Fact]
    public async Task A_file_with_too_many_components_for_its_size_is_refused_before_anything_is_applied()
    {
        var factory = Factory;
        using var client = factory.ClientFor(ReadWrite);
        var calendar = await CreateCalendarAsync(client, "Tiny components");
        var file = "BEGIN:VCALENDAR\r\n" + string.Concat(Enumerable.Repeat("BEGIN:VEVENT\r\nEND:VEVENT\r\n", 2000)) + "END:VCALENDAR\r\n";

        var response = await PostIcsAsync(client, calendar.CalendarId, file);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = (await response.Content.ReadFromJsonAsync<ProblemDetails>())!;
        Assert.Equal(CalendarIcsService.TooManyComponentsCode, problem.Extensions["code"]?.ToString());
    }

    // ------------------------------------------------------------------ AC 26 (InMemory half): the window

    [Fact]
    public async Task A_mix_of_floating_and_zoned_rows_pages_identically_under_every_view_zone()
    {
        var factory = Factory;
        using var client = factory.ClientFor(ReadWrite);
        var calendar = await CreateCalendarAsync(client, "Mixed");
        await SeedMixedRowsAsync(factory, calendar.CalendarId, 60);
        var from = Utc(2026, 3, 20, 0);
        var to = Utc(2026, 4, 10, 0);

        var totals = new List<int>();
        foreach (var zone in new[] { "UTC", "Europe/Oslo", "America/New_York" })
        {
            var whole = await ListAsync(client, calendar.CalendarId, from, to, zone, limit: 200);
            var paged = new List<ExistingCalendarEvent>();
            for (var offset = 0; offset < whole.TotalCount; offset += 7)
            {
                var page = await ListAsync(client, calendar.CalendarId, from, to, zone, offset: offset, limit: 7);
                Assert.Equal(whole.TotalCount, page.TotalCount);
                paged.AddRange(page.Items);
            }

            Assert.Equal(whole.Items.Select(i => i.CalendarEventId), paged.Select(i => i.CalendarEventId));
            var view = TimeZoneInfo.FindSystemTimeZoneById(zone);
            var instants = paged.Select(i => i.StartUtc ?? TimeZoneInfo.ConvertTimeToUtc(
                DateTime.SpecifyKind(i.StartDateTime, DateTimeKind.Unspecified), view)).ToList();
            Assert.Equal(instants.Order(), instants);
            totals.Add(whole.TotalCount);
        }

        Assert.All(totals, total => Assert.Equal(60, total));
    }

    // ------------------------------------------------------------------ helpers

    private static DateTime Wall(int year, int month, int day, int hour, int minute = 0) =>
        new(year, month, day, hour, minute, 0, DateTimeKind.Unspecified);

    private static DateTime Utc(int year, int month, int day, int hour, int minute = 0) =>
        new(year, month, day, hour, minute, 0, DateTimeKind.Utc);

    private static int Count(string text, string value) =>
        (text.Length - text.Replace(value, string.Empty, StringComparison.Ordinal).Length) / value.Length;

    // A floating weekly Monday 09:00 series from 2 February 2026: every occurrence is after the fixed clock.
    private static NewRecurrencePattern Rule(Guid calendarId, int? count) => new()
    {
        CalendarId = calendarId, Title = "Standup",
        StartDateTime = Wall(2026, 2, 2, 9), EndDateTime = Wall(2026, 2, 2, 9, 30),
        Frequency = DtoFrequency.Weekly, DaysOfWeek = DtoDays.Monday, OccurrenceCount = count,
    };

    private static Task<ExistingRecurrencePattern> WeeklyMondaysAsync(HttpClient client, Guid calendarId, int count) =>
        CreatePatternAsync(client, Rule(calendarId, count));

    private static async Task<ExistingRecurrencePattern> CreatePatternAsync(HttpClient client, NewRecurrencePattern pattern)
    {
        var post = await client.PostAsJsonAsync(PatternsPath, pattern);
        Assert.True(post.IsSuccessStatusCode, await post.Content.ReadAsStringAsync());
        return (await post.Content.ReadFromJsonAsync<ExistingRecurrencePattern>())!;
    }

    private static async Task<RecurrencePatternUpdateResult> UpdatePatternAsync(HttpClient client, Guid id, NewRecurrencePattern pattern)
    {
        var put = await client.PutAsJsonAsync($"{PatternsPath}/{id}", pattern);
        Assert.True(put.IsSuccessStatusCode, await put.Content.ReadAsStringAsync());
        return (await put.Content.ReadFromJsonAsync<RecurrencePatternUpdateResult>())!;
    }

    private static async Task<List<ExistingCalendarEvent>> SeriesRowsAsync(HttpClient client, Guid patternId) =>
        (await client.GetFromJsonAsync<PagedResult<ExistingCalendarEvent>>($"{PatternsPath}/{patternId}/events?limit=200"))!
        .Items.OrderBy(r => r.OccurrenceKey).ToList();

    private static async Task<PagedResult<ExistingCalendarEvent>> ListAsync(
        HttpClient client, Guid calendarId, DateTime from, DateTime to, string? zone, int offset = 0, int limit = 200)
    {
        var url = $"{EventsPath}?from={from:o}&to={to:o}&calendarIds={calendarId}&offset={offset}&limit={limit}"
            + (zone is null ? string.Empty : $"&viewTimeZoneId={Uri.EscapeDataString(zone)}");
        var response = await client.GetAsync(url);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<PagedResult<ExistingCalendarEvent>>())!;
    }

    private static async Task EditTitleAsync(HttpClient client, ExistingCalendarEvent row, string title)
    {
        var put = await client.PutAsJsonAsync($"{EventsPath}/{row.CalendarEventId}", new NewCalendarEvent
        {
            CalendarId = row.CalendarId, Title = title, StartDateTime = row.StartDateTime, EndDateTime = row.EndDateTime,
            IsAllDay = row.IsAllDay, TimeZoneId = row.TimeZoneId,
        });
        Assert.True(put.IsSuccessStatusCode, await put.Content.ReadAsStringAsync());
        Assert.True((await put.Content.ReadFromJsonAsync<ExistingCalendarEvent>())!.IsOverride);
    }

    private static async Task DeleteAsync(HttpClient client, Guid eventId) =>
        Assert.True((await client.DeleteAsync($"{EventsPath}/{eventId}")).IsSuccessStatusCode);

    private static async Task<ProblemDetails> AssertConflictAsync(HttpResponseMessage response, string code)
    {
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var problem = (await response.Content.ReadFromJsonAsync<ProblemDetails>())!;
        Assert.Equal(code, problem.Extensions["code"]?.ToString());
        return problem;
    }

    private static async Task<ExistingCalendar> CreateCalendarAsync(HttpClient client, string name)
    {
        var post = await client.PostAsJsonAsync(CalendarsPath, new NewCalendar { Name = name, Color = "#0369A1" });
        post.EnsureSuccessStatusCode();
        return (await post.Content.ReadFromJsonAsync<ExistingCalendar>())!;
    }

    private static async Task<ExistingCalendarEvent> CreateEventAsync(
        HttpClient client, Guid calendarId, string title, DateTime start, DateTime end, string? zone)
    {
        var post = await client.PostAsJsonAsync(EventsPath, new NewCalendarEvent
        {
            CalendarId = calendarId, Title = title, StartDateTime = start, EndDateTime = end, TimeZoneId = zone,
        });
        Assert.True(post.IsSuccessStatusCode, await post.Content.ReadAsStringAsync());
        return (await post.Content.ReadFromJsonAsync<ExistingCalendarEvent>())!;
    }

    // A series as written before #519: un-keyed rows, one of them rescheduled.
    private static async Task<(Guid PatternId, List<Guid> RowIds)> SeedLegacySeriesAsync(
        ApiFactory factory, Guid calendarId, bool rescheduleThird)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        var pattern = new ContextRecurrencePattern
        {
            RecurrencePatternId = Guid.NewGuid(), CalendarId = calendarId, Title = "Legacy",
            StartDateTime = Wall(2026, 2, 2, 9), EndDateTime = Wall(2026, 2, 2, 10),
            Frequency = Odyssey.Context.RecurrenceFrequency.Weekly, DaysOfWeek = Odyssey.Context.DaysOfWeekFlags.Monday,
            OccurrenceCount = 3, CreatedAt = Now.UtcDateTime, UpdatedAt = Now.UtcDateTime, Version = Guid.NewGuid(),
        };
        db.RecurrencePatterns.Add(pattern);
        var ids = new List<Guid>();
        for (var i = 0; i < 3; i++)
        {
            var start = Wall(2026, 2, 2, 9).AddDays(7 * i).AddHours(rescheduleThird && i == 2 ? 3 : 0);
            var row = new ContextCalendarEvent
            {
                CalendarEventId = Guid.NewGuid(), CalendarId = calendarId, Title = "Legacy", StartDateTime = start,
                EndDateTime = start.AddHours(1), RecurrencePatternId = pattern.RecurrencePatternId,
                CreatedAt = Now.UtcDateTime.AddMinutes(i), UpdatedAt = Now.UtcDateTime,
            };
            db.CalendarEvents.Add(row);
            ids.Add(row.CalendarEventId);
        }

        await db.SaveChangesAsync();
        return (pattern.RecurrencePatternId, ids);
    }

    // Alternating floating and zoned rows, hourly-ish, across the Oslo change on 29 March 2026.
    private static async Task SeedMixedRowsAsync(ApiFactory factory, Guid calendarId, int count)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        var oslo = TimeZoneInfo.FindSystemTimeZoneById("Europe/Oslo");
        for (var i = 0; i < count; i++)
        {
            var wall = Wall(2026, 3, 27, 6).AddHours(i * 3.5);
            var zoned = i % 2 == 0;
            var start = zoned ? CalendarTimeZones.ToUtc(wall, oslo) : wall;
            db.CalendarEvents.Add(new ContextCalendarEvent
            {
                CalendarId = calendarId, Title = $"Row {i}", StartDateTime = start, EndDateTime = start.AddMinutes(30),
                TimeZoneId = zoned ? "Europe/Oslo" : null, CreatedAt = Now.UtcDateTime, UpdatedAt = Now.UtcDateTime,
            });
        }

        await db.SaveChangesAsync();
    }

    private static string Vevent(string uid, params string[] lines) =>
        "BEGIN:VEVENT\r\nUID:" + uid + "\r\n" + string.Join("\r\n", lines) + "\r\nEND:VEVENT\r\n";

    private static string Vcalendar(params string[] vevents) =>
        "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//test//EN\r\n" + string.Concat(vevents) + "END:VCALENDAR\r\n";

    private static async Task<HttpResponseMessage> PostIcsAsync(
        HttpClient client, Guid calendarId, string ics, bool allowRemovals = false, string? defaultTimeZoneId = null)
    {
        using var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(ics));
        file.Headers.ContentType = new MediaTypeHeaderValue("text/calendar");
        content.Add(file, "file", "calendar.ics");
        if (allowRemovals)
        {
            content.Add(new StringContent("true"), "allowRemovals");
        }

        if (defaultTimeZoneId is not null)
        {
            content.Add(new StringContent(defaultTimeZoneId), "defaultTimeZoneId");
        }

        return await client.PostAsync($"{CalendarsPath}/{calendarId}/ics", content);
    }

    public class ApiFactory : OdysseyApiFactory
    {
        // One root, as the base factory keeps: a fresh root per factory would build EF a service provider per factory.
        private static readonly Microsoft.EntityFrameworkCore.Storage.InMemoryDatabaseRoot StoreRoot = new();

        public ApiFactory(IReadOnlyCollection<string> permissions, TimeProvider? clock = null, RecordingLoggerProvider? logs = null)
            : this(permissions, clock, logs, $"calendar-recurrence-{Guid.NewGuid()}")
        {
        }

        private ApiFactory(IReadOnlyCollection<string> permissions, TimeProvider? clock, RecordingLoggerProvider? logs, string name)
            : base(permissions, ActorUserId, configureServices: services =>
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton(clock ?? new FakeTimeProvider(Now));
                services.RemoveAll<DbContextOptions<OdysseyContext>>();
                services.AddDbContext<OdysseyContext>(options => options.UseInMemoryDatabase(name, StoreRoot));
                if (logs is not null)
                {
                    services.AddSingleton<Microsoft.Extensions.Logging.ILoggerProvider>(logs);
                }
            })
        {
        }
    }

    /// <summary>The class's shared host: every caller's claims travel per request (<see cref="SharedApiHostClients"/>).</summary>
    public sealed class Host() : ApiFactory([]);
}
