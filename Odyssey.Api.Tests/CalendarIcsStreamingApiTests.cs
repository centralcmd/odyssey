using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Odyssey.Api.Controllers;
using Odyssey.Api.Tests.Infrastructure;
using Odyssey.Context;
using Odyssey.Core.Journal;
using Odyssey.Dtos;
using Odyssey.Dtos.Application;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos.Journal;
using Odyssey.Testing.Logging;
using Xunit;
using ContextCalendar = Odyssey.Context.Calendar;
using ContextCalendarEvent = Odyssey.Context.CalendarEvent;

namespace Odyssey.Api.Tests;

/// <summary>
/// Calendar <c>.ics</c> export and import: streaming, limits and round-trip fixes (issue #516). The fast tier: EF
/// InMemory, so it covers what every export and import produces; the real-engine behaviour (no export transaction,
/// the batch transactions, the query counts) is in <c>Odyssey.IntegrationTests</c>.
/// </summary>
public class CalendarIcsStreamingApiTests(CalendarIcsStreamingApiTests.Host host)
    : SharedApiHostTests<CalendarIcsStreamingApiTests.Host>(host)
{
    private const string ActorUserId = "calendar-ics-streaming-actor";
    private const string CalendarsPath = "/api/calendars";
    private const string EventsPath = "/api/calendar-events";
    private const string PatternsPath = "/api/recurrence-patterns";

    private static readonly string[] ReadWrite =
    [
        PermissionClaims.CalendarRead, PermissionClaims.CalendarCreate, PermissionClaims.CalendarUpdate,
        PermissionClaims.CalendarDelete,
    ];

    private static readonly string[] ReadWriteNoDelete =
    [
        PermissionClaims.CalendarRead, PermissionClaims.CalendarCreate, PermissionClaims.CalendarUpdate,
    ];

    private static readonly string[] WithSettings =
    [
        .. ReadWrite, PermissionClaims.SystemSettingsRead, PermissionClaims.SystemSettingsUpdate,
        PermissionClaims.SystemSettingsSecurityUpdate,
    ];

    private static readonly string[] WithPlaces =
    [
        .. ReadWrite, PermissionClaims.LocationsRead, PermissionClaims.LocationsCreate,
    ];

    // ------------------------------------------------------------------ AC 2: throttling on all eight

    public static TheoryData<Type, string> ThrottledActions() => new()
    {
        { typeof(CalendarIcsController), nameof(CalendarIcsController.Export) },
        { typeof(CalendarIcsController), nameof(CalendarIcsController.SummarizeExport) },
        { typeof(CalendarEventsController), nameof(CalendarEventsController.ExportIcs) },
        { typeof(CalendarEventsController), nameof(CalendarEventsController.SummarizeIcsExport) },
        { typeof(CalendarEventsController), nameof(CalendarEventsController.ExportEventIcs) },
        { typeof(CalendarEventsController), nameof(CalendarEventsController.SummarizeEventIcsExport) },
        { typeof(RecurrencePatternsController), nameof(RecurrencePatternsController.ExportIcs) },
        { typeof(RecurrencePatternsController), nameof(RecurrencePatternsController.SummarizeIcsExport) },
    };

    [Theory]
    [MemberData(nameof(ThrottledActions))]
    public void Every_calendar_export_and_summary_carries_the_export_limiter_and_the_concurrency_filter(Type controller, string action)
    {
        var method = controller.GetMethod(action)!;

        Assert.Equal(
            ImportExportRateLimiting.ExportConcurrencyPolicy,
            method.GetCustomAttribute<EnableRateLimitingAttribute>()?.PolicyName);
        Assert.Equal(typeof(ExportConcurrencyFilter), method.GetCustomAttribute<TypeFilterAttribute>()?.ImplementationType);
    }

    // ------------------------------------------------------------------ AC 3, 4, 5: caps and summaries

    [Fact]
    public async Task Summaries_match_their_exports_and_the_rows_header()
    {
        var factory = Factory;
        using var client = factory.CreateClient();
        var calendar = await CreateCalendarAsync(client, "Counts");
        var series = await CreateSeriesAsync(client, calendar.CalendarId, "Standup", count: 3);
        var one = await CreateEventAsync(client, calendar.CalendarId, "One");
        await CreateEventAsync(client, calendar.CalendarId, "Two");

        foreach (var (export, summary, events, rules, occurrences) in new[]
        {
            ($"{EventsPath}/{one.CalendarEventId}/ics", $"{EventsPath}/{one.CalendarEventId}/ics/summary", 1, 0, 1),
            ($"{CalendarsPath}/{calendar.CalendarId}/ics", $"{CalendarsPath}/{calendar.CalendarId}/ics/summary", 3, 1, 5),
            ($"{PatternsPath}/{series.RecurrencePatternId}/ics", $"{PatternsPath}/{series.RecurrencePatternId}/ics/summary", 1, 1, 3),
            ($"{EventsPath}/ics", $"{EventsPath}/ics/summary", 3, 1, 5),
            ($"{EventsPath}/ics?search=One", $"{EventsPath}/ics/summary?search=One", 1, 0, 1),
        })
        {
            var planned = (await (await client.GetAsync(summary)).Content.ReadFromJsonAsync<CalendarIcsExportSummary>())!;
            var response = await client.GetAsync(export);
            var body = await response.Content.ReadAsStringAsync();

            Assert.Equal(events, planned.EventCount);
            Assert.Equal(rules, planned.SeriesCount);
            Assert.Equal(occurrences, planned.OccurrenceCount);
            Assert.Equal(planned.EventCount, Count(body, "BEGIN:VEVENT"));
            Assert.Equal(planned.SeriesCount, Count(body, "RRULE:"));
            Assert.Equal(planned.EventCount.ToString(), response.Headers.GetValues("X-Odyssey-Export-Rows").Single());
        }
    }

    [Fact]
    public async Task Every_export_carries_the_download_headers()
    {
        var factory = Factory;
        using var client = factory.CreateClient();
        var calendar = await CreateCalendarAsync(client, "Headers");
        var series = await CreateSeriesAsync(client, calendar.CalendarId, "Weekly", count: 2);
        var single = await CreateEventAsync(client, calendar.CalendarId, "Single");

        foreach (var url in new[]
        {
            $"{CalendarsPath}/{calendar.CalendarId}/ics", $"{EventsPath}/{single.CalendarEventId}/ics",
            $"{PatternsPath}/{series.RecurrencePatternId}/ics", $"{EventsPath}/ics",
        })
        {
            var response = await client.GetAsync(url);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("text/calendar", response.Content.Headers.ContentType!.MediaType);
            Assert.Equal("attachment", response.Content.Headers.ContentDisposition!.DispositionType);
            Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
            Assert.True(response.Headers.CacheControl!.NoStore);
            Assert.True(response.Headers.Contains("X-Odyssey-Export-Rows"));
        }
    }

    [Fact]
    public async Task Count_cap_refuses_each_export_and_summary_with_the_calendar_code_and_unlimited_admits_them()
    {
        var factory = Factory.ForCaller(WithSettings);
        using var client = factory.CreateClient();
        var calendar = await CreateCalendarAsync(client, "Capped");
        var series = await CreateSeriesAsync(client, calendar.CalendarId, "Six", count: 6);
        // An edited occurrence is a RECURRENCE-ID component since issue #519: the calendar export carries the master, the
        // override and the single event (3), the series export the master and the override (2), and the unfiltered
        // aggregate — flat for a series with any exception — the six rows and the single event (7).
        await EditFirstOccurrenceAsync(factory, series.RecurrencePatternId);
        var single = await CreateEventAsync(client, calendar.CalendarId, "Single");
        // The single-event export plans exactly one VEVENT, and the cap's floor is 1, so no cap can refuse it: it is
        // asserted to pass under the cap rather than left out (tester finding 3).
        var singleUrl = $"{EventsPath}/{single.CalendarEventId}/ics";

        var urls = new[]
        {
            $"{CalendarsPath}/{calendar.CalendarId}/ics", $"{PatternsPath}/{series.RecurrencePatternId}/ics", $"{EventsPath}/ics",
        };

        await SetAsync(client, new SystemSettingsUpdate { CalendarIcsMaxExportEvents = new CapacityLimit { Value = 1 } });
        foreach (var url in urls)
        {
            foreach (var target in new[] { url, Summary(url) })
            {
                var response = await client.GetAsync(target);
                Assert.True(HttpStatusCode.BadRequest == response.StatusCode, target);
                var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
                Assert.Equal(ExportLimitExceededException.CalendarEventsCode, problem!.Extensions["code"]?.ToString());
            }
        }

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(singleUrl)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(Summary(singleUrl))).StatusCode);

        await SetAsync(client, new SystemSettingsUpdate { CalendarIcsMaxExportEvents = new CapacityLimit { Unlimited = true } });
        foreach (var url in urls.Append(singleUrl))
        {
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(url)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(Summary(url))).StatusCode);
        }
    }

    [Theory]
    [InlineData("calendar")]
    [InlineData("pattern")]
    public async Task Byte_cap_aborts_the_export_while_its_summary_still_answers(string kind)
    {
        // AC 3 on the per-calendar and the series export (the aggregate paths are in CalendarEventsIcsApiTests). A
        // single event cannot reach the 1 MB floor of the cap, so its writer is covered in CalendarIcsExportByteCapTests.
        var factory = Factory.ForCaller(WithSettings);
        using var client = factory.CreateClient();
        await SetAsync(client, new SystemSettingsUpdate { CalendarIcsMaxExportMegabytes = 1 });
        var calendar = await CreateCalendarAsync(client, "Bulky");
        var patternId = await SeedBulkySeriesAsync(factory, calendar.CalendarId, 1000);
        var url = kind == "calendar" ? $"{CalendarsPath}/{calendar.CalendarId}/ics" : $"{PatternsPath}/{patternId}/ics";

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(Summary(url))).StatusCode);
        await AssertAbortedMidStreamAsync(client, url, expectedRows: 1000);
    }

    // The headers arrive as a 200 with the planned count, and only the body read fails: any other status, or a
    // complete body, fails the test (tester finding 2).
    internal static async Task AssertAbortedMidStreamAsync(HttpClient client, string url, int expectedRows)
    {
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(expectedRows.ToString(), response.Headers.GetValues("X-Odyssey-Export-Rows").Single());

        // TestServer surfaces HttpContext.Abort() on the body as this chain; any other failure is not the abort.
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => response.Content.ReadAsStringAsync());
        var io = Assert.IsType<IOException>(error.InnerException);
        Assert.Equal("The application aborted the request.", Assert.IsType<OperationCanceledException>(io.InnerException).Message);
    }

    [Fact]
    public async Task Summaries_return_404_exactly_where_their_exports_do()
    {
        var factory = Factory;
        using var client = factory.CreateClient();
        var unknown = Guid.NewGuid();

        foreach (var url in new[] { $"{CalendarsPath}/{unknown}/ics", $"{EventsPath}/{unknown}/ics", $"{PatternsPath}/{unknown}/ics" })
        {
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(url)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(Summary(url))).StatusCode);
        }
    }

    [Fact]
    public async Task A_summary_followed_at_once_by_its_export_succeeds()
    {
        // AC 18b: the summary holds its permits only while it plans, so the download never 429s its own user.
        var factory = Factory;
        using var client = factory.CreateClient();
        var calendar = await CreateCalendarAsync(client, "Sequence");

        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"{CalendarsPath}/{calendar.CalendarId}/ics/summary")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"{CalendarsPath}/{calendar.CalendarId}/ics")).StatusCode);
        }
    }

    // ------------------------------------------------------------------ AC 12: the moved-occurrence rule

    [Fact]
    public async Task An_occurrence_moved_to_another_calendar_is_an_exdate_here_and_a_standalone_event_there()
    {
        var factory = Factory;
        using var client = factory.CreateClient();
        var a = await CreateCalendarAsync(client, "A");
        var b = await CreateCalendarAsync(client, "B");
        var series = await CreateSeriesAsync(client, a.CalendarId, "Moving", count: 3);
        var movedId = await MoveFirstOccurrenceAsync(factory, series.RecurrencePatternId, b.CalendarId);
        var movedUid = $"{movedId}@odyssey.local";

        var aBody = await (await client.GetAsync($"{CalendarsPath}/{a.CalendarId}/ics")).Content.ReadAsStringAsync();
        var bBody = await (await client.GetAsync($"{CalendarsPath}/{b.CalendarId}/ics")).Content.ReadAsStringAsync();
        var seriesBody = await (await client.GetAsync($"{PatternsPath}/{series.RecurrencePatternId}/ics")).Content.ReadAsStringAsync();

        // Issue #519 AC 17: the series keeps its rule in its own calendar with the moved key as an EXDATE, the moved row
        // is a standalone event under its own synthetic UID in B, and the series export carries it as a RECURRENCE-ID.
        Assert.Equal(1, Count(aBody, "RRULE:"));
        Assert.Equal(1, Count(aBody, "BEGIN:VEVENT"));
        Assert.Contains("EXDATE:20300101T090000", aBody);
        Assert.DoesNotContain(movedUid, aBody);
        Assert.Equal(1, Count(bBody, "BEGIN:VEVENT"));
        Assert.Contains(movedUid, bBody);
        Assert.DoesNotContain("RRULE:", bBody);
        Assert.Equal(1, Count(seriesBody, "RRULE:"));
        Assert.Equal(2, Count(seriesBody, "BEGIN:VEVENT"));
        Assert.Contains("RECURRENCE-ID:20300101T090000", seriesBody);
    }

    [Fact]
    public async Task An_intact_series_still_collapses_to_one_rule_in_its_own_calendar()
    {
        var factory = Factory;
        using var client = factory.CreateClient();
        var a = await CreateCalendarAsync(client, "Intact");
        await CreateSeriesAsync(client, a.CalendarId, "Intact", count: 4);

        var body = await (await client.GetAsync($"{CalendarsPath}/{a.CalendarId}/ics")).Content.ReadAsStringAsync();

        Assert.Equal(1, Count(body, "BEGIN:VEVENT"));
        Assert.Equal(1, Count(body, "RRULE:"));
    }

    // ------------------------------------------------------------------ AC 14: STATUS and stamps

    [Fact]
    public async Task Every_exported_vevent_carries_status_and_its_stamps()
    {
        var factory = Factory;
        using var client = factory.CreateClient();
        var calendar = await CreateCalendarAsync(client, "Stamps");
        await CreateSeriesAsync(client, calendar.CalendarId, "Rule", count: 2);
        await CreateEventAsync(client, calendar.CalendarId, "Single");
        var created = new DateTime(2029, 3, 4, 5, 6, 7, DateTimeKind.Utc);
        var updated = new DateTime(2029, 4, 5, 6, 7, 8, DateTimeKind.Utc);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
            foreach (var row in db.CalendarEvents.Where(e => e.CalendarId == calendar.CalendarId))
            {
                row.CreatedAt = created;
                row.UpdatedAt = updated;
            }

            foreach (var pattern in db.RecurrencePatterns.Where(p => p.CalendarId == calendar.CalendarId))
            {
                pattern.CreatedAt = created;
                pattern.UpdatedAt = updated;
            }

            await db.SaveChangesAsync();
        }

        var body = await (await client.GetAsync($"{CalendarsPath}/{calendar.CalendarId}/ics")).Content.ReadAsStringAsync();

        Assert.Equal(2, Count(body, "BEGIN:VEVENT"));
        Assert.Equal(2, Count(body, "STATUS:CONFIRMED"));
        Assert.Equal(2, Count(body, "CREATED:20290304T050607Z"));
        Assert.Equal(2, Count(body, "LAST-MODIFIED:20290405T060708Z"));
        Assert.Equal(2, Count(body, "DTSTAMP:"));
    }

    // ------------------------------------------------------------------ AC 10, 11: UIDs on import

    [Fact]
    public async Task An_aggregate_export_imported_back_keeps_the_stored_external_uid()
    {
        var factory = Factory;
        using var client = factory.CreateClient();
        var calendar = await CreateCalendarAsync(client, "Google");
        var id = await SeedRowAsync(factory, calendar.CalendarId, "From Google", externalUid: "abc@google.com");

        var file = await (await client.GetAsync($"{EventsPath}/ics")).Content.ReadAsStringAsync();
        Assert.Contains($"{id}@odyssey.local", file);
        var result = await ImportAsync(client, calendar.CalendarId, file);

        Assert.Equal(1, result.UpdatedCount);
        Assert.Equal(0, result.ImportedCount);
        var row = await RowAsync(factory, id);
        Assert.Equal("abc@google.com", row.ExternalUid);
    }

    [Fact]
    public async Task Importing_one_export_into_another_calendar_twice_updates_the_second_time()
    {
        var factory = Factory;
        using var client = factory.CreateClient();
        var a = await CreateCalendarAsync(client, "Source");
        var b = await CreateCalendarAsync(client, "Copy");
        await CreateEventAsync(client, a.CalendarId, "One");
        await CreateEventAsync(client, a.CalendarId, "Two");
        await CreateSeriesAsync(client, a.CalendarId, "Rule", count: 3);
        var file = await (await client.GetAsync($"{CalendarsPath}/{a.CalendarId}/ics")).Content.ReadAsStringAsync();

        var first = await ImportAsync(client, b.CalendarId, file);
        var second = await ImportAsync(client, b.CalendarId, file);

        Assert.Equal(3, first.ImportedCount);
        Assert.Equal(0, second.ImportedCount);
        Assert.Equal(3, second.UpdatedCount);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        Assert.Equal(5, await db.CalendarEvents.CountAsync(e => e.CalendarId == b.CalendarId));
        Assert.All(await db.CalendarEvents.Where(e => e.CalendarId == b.CalendarId && e.RecurrencePatternId == null).ToListAsync(),
            e => Assert.Null(e.ExternalUid));
    }

    [Fact]
    public async Task The_345_F1_chain_cannot_overwrite_a_row_of_the_originating_calendar()
    {
        var factory = Factory;
        using var client = factory.CreateClient();
        var a = await CreateCalendarAsync(client, "A");
        var b = await CreateCalendarAsync(client, "B");
        var x = await CreateEventAsync(client, a.CalendarId, "X");
        var before = await RowAsync(factory, x.CalendarEventId);

        var fromA = await (await client.GetAsync($"{CalendarsPath}/{a.CalendarId}/ics")).Content.ReadAsStringAsync();
        await ImportAsync(client, b.CalendarId, fromA);
        var fromB = await (await client.GetAsync($"{CalendarsPath}/{b.CalendarId}/ics")).Content.ReadAsStringAsync();
        // B's file names only B's own rows.
        Assert.DoesNotContain($"{x.CalendarEventId}@odyssey.local", fromB);
        // B's copy is edited before it travels back (tester finding 5): an overwrite of X would now change its title,
        // where an identical copy would have left the title equal either way.
        Assert.Contains("SUMMARY:X\r\n", fromB);
        fromB = fromB.Replace("SUMMARY:X\r\n", "SUMMARY:Edited in B\r\n", StringComparison.Ordinal);
        var back = await ImportAsync(client, a.CalendarId, fromB);

        var after = await RowAsync(factory, x.CalendarEventId);
        Assert.Equal("X", after.Title);
        Assert.Equal(before.UpdatedAt, after.UpdatedAt);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        var aRows = await db.CalendarEvents.Where(e => e.CalendarId == a.CalendarId).ToListAsync();
        // B's UID names nothing in A, so rule 4 creates B's copy beside X; it never lands on X.
        Assert.Equal((1, 0), (back.ImportedCount, back.UpdatedCount));
        Assert.Equal(["Edited in B", "X"], aRows.Select(r => r.Title).Order(StringComparer.Ordinal));
        var aIds = aRows.Select(r => $"{r.CalendarEventId}@odyssey.local").ToHashSet();
        Assert.All(aRows, r => Assert.False(r.ExternalUid?.EndsWith("@odyssey.local") ?? false));
        // A copy imported back carries B's synthetic UID as its match key — never one naming a row of A.
        Assert.All(aRows, r => Assert.DoesNotContain(r.ImportSourceUid ?? string.Empty, aIds));
    }

    // ------------------------------------------------------------------ AC 13: place detail

    [Fact]
    public async Task Place_detail_round_trips_without_growing_through_an_importer_without_locations_read()
    {
        var factory = Factory;
        using var client = factory.CreateClient();
        var calendar = await CreateCalendarAsync(client, "Places");
        var cafe = await SeedLocationAsync(factory, "Café");
        var id = await SeedRowAsync(factory, calendar.CalendarId, "Coffee", location: "Back room", locationId: cafe);

        // What a locations.read holder's export carries for the row.
        var holderFile = Vcalendar(Vevent($"{id}@odyssey.local", "DTSTART:20300101T090000Z", "DTEND:20300101T100000Z",
            "SUMMARY:Coffee", "LOCATION:Café — Back room", "X-ODYSSEY-LOCATION-DETAIL:Back room"));
        for (var i = 0; i < 3; i++)
        {
            await ImportAsync(client, calendar.CalendarId, holderFile);
            var row = await RowAsync(factory, id);
            Assert.Equal("Back room", row.Location);
            Assert.Equal(cafe, row.LocationId);
        }

        // This caller's own export writes the detail alone, and re-imports unchanged.
        var ownFile = await (await client.GetAsync($"{CalendarsPath}/{calendar.CalendarId}/ics")).Content.ReadAsStringAsync();
        Assert.Contains("X-ODYSSEY-LOCATION-DETAIL:Back room", ownFile);
        await ImportAsync(client, calendar.CalendarId, ownFile);
        Assert.Equal("Back room", (await RowAsync(factory, id)).Location);
    }

    [Fact]
    public async Task A_file_exported_without_locations_read_never_turns_its_detail_into_a_place()
    {
        var factory = Factory.ForCaller(WithPlaces);
        using var client = factory.CreateClient();
        var calendar = await CreateCalendarAsync(client, "Holder");
        var file = Vcalendar(Vevent("detail-only@example.com", "DTSTART:20300101T090000Z", "DTEND:20300101T100000Z",
            "SUMMARY:Coffee", "LOCATION:Back room", "X-ODYSSEY-LOCATION-DETAIL:Back room"));

        var result = await ImportAsync(client, calendar.CalendarId, file);

        Assert.Equal(0, result.PlacesCreated);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        Assert.False(await db.Locations.AnyAsync(l => l.Name == "Back room"));
        var row = await db.CalendarEvents.SingleAsync(e => e.CalendarId == calendar.CalendarId);
        Assert.Equal("Back room", row.Location);
        Assert.Null(row.LocationId);
    }

    // ------------------------------------------------------------------ AC 15: cancellation

    [Fact]
    public async Task A_cancelled_uid_the_calendar_does_not_hold_is_skipped()
    {
        var factory = Factory;
        using var client = factory.CreateClient();
        var calendar = await CreateCalendarAsync(client, "Unknown");

        var result = await ImportAsync(client, calendar.CalendarId, Vcalendar(Cancelled("nobody@example.com", "Gone")));

        var group = Assert.Single(result.Skipped);
        Assert.Equal("Event is cancelled.", group.Reason);
    }

    [Fact]
    public async Task A_matching_cancelled_event_is_kept_unless_removals_are_allowed()
    {
        var factory = Factory;
        using var client = factory.CreateClient();
        var calendar = await CreateCalendarAsync(client, "Kept");
        var id = await SeedRowAsync(factory, calendar.CalendarId, "Keep me", externalUid: "keep@example.com");

        var result = await ImportAsync(client, calendar.CalendarId, Vcalendar(Cancelled("keep@example.com", "Keep me")));

        Assert.Contains("kept, because removing cancelled events was not requested", Assert.Single(result.Skipped).Reason);
        Assert.Equal(0, result.RemovedCount);
        Assert.NotNull(await FindRowAsync(factory, id));
    }

    [Fact]
    public async Task With_allow_removals_and_calendar_delete_a_cancelled_event_is_removed_and_audited_by_id()
    {
        var factory = Factory;
        var logs = factory.Logs!;
        using var client = factory.CreateClient();
        var calendar = await CreateCalendarAsync(client, "Removed");
        var id = await SeedRowAsync(factory, calendar.CalendarId, "Secret title", externalUid: "gone@example.com");

        var result = await ImportAsync(client, calendar.CalendarId, Vcalendar(Cancelled("gone@example.com", "Secret title")), allowRemovals: true);

        Assert.Equal(1, result.RemovedCount);
        Assert.Equal(["Secret title"], result.RemovedSampleTitles);
        Assert.Null(await FindRowAsync(factory, id));
        var audit = Assert.Single(logs.ForCategory(nameof(CalendarIcsService)), e => e.Message.Contains("removed"));
        Assert.Equal(LogLevel.Information, audit.Level);
        Assert.Contains(id.ToString(), audit.Message);
        Assert.Contains(ActorUserId, audit.Message);
        Assert.DoesNotContain("Secret title", audit.Message);
    }

    [Fact]
    public async Task Allow_removals_without_calendar_delete_is_a_403_and_nothing_is_applied()
    {
        var factory = Factory.ForCaller(ReadWriteNoDelete);
        using var client = factory.CreateClient();
        var calendar = await CreateCalendarAsync(client, "NoDelete");
        var file = Vcalendar(
            Vevent("new@example.com", "DTSTART:20300101T090000Z", "DTEND:20300101T100000Z", "SUMMARY:New"),
            Cancelled("other@example.com", "Other"));

        var response = await PostIcsAsync(client, calendar.CalendarId, file, allowRemovals: true);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await using var scope = factory.Services.CreateAsyncScope();
        Assert.False(await scope.ServiceProvider.GetRequiredService<OdysseyContext>().CalendarEvents
            .AnyAsync(e => e.CalendarId == calendar.CalendarId));
    }

    [Fact]
    public async Task A_cancelled_series_is_removed_with_delete_semantics()
    {
        // A fixed clock, so which occurrences are past is decided by the test and never by when it runs.
        var now = new DateTime(2031, 3, 1, 12, 0, 0, DateTimeKind.Utc);
        // A host of its own: the clock is a replaced service.
        await using var factory = new ApiFactory(ReadWrite, clock: new FakeTimeProvider(now));
        using var client = factory.CreateClient();
        var calendar = await CreateCalendarAsync(client, "SeriesGone");
        var series = await CreateSeriesAsync(client, calendar.CalendarId, "Daily", count: 4, start: now.AddHours(-36));
        var file = Vcalendar(Vevent($"{series.RecurrencePatternId}@odyssey.local", "STATUS:CANCELLED", "SUMMARY:Daily",
            "DTSTART:20300101T090000Z", "RRULE:FREQ=DAILY;COUNT=4"));

        var result = await ImportAsync(client, calendar.CalendarId, file, allowRemovals: true);

        Assert.Equal(1, result.RemovedCount);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        Assert.False(await db.RecurrencePatterns.AnyAsync(p => p.RecurrencePatternId == series.RecurrencePatternId));
        var left = await db.CalendarEvents.Where(e => e.CalendarId == calendar.CalendarId).ToListAsync();
        Assert.Equal(2, left.Count); // the two past occurrences, detached and kept
        Assert.All(left, e => Assert.Null(e.RecurrencePatternId));
        Assert.All(left, e => Assert.True(e.StartDateTime < now));
    }

    [Fact]
    public async Task A_cancelled_recurrence_id_component_excludes_that_occurrence_and_keeps_the_series()
    {
        // Issue #519 supersedes this test's Spec A form (S6): a cancelled override is an exclusion, gated like a removal.
        var factory = Factory;
        using var client = factory.CreateClient();
        var calendar = await CreateCalendarAsync(client, "Exception");
        var series = await CreateSeriesAsync(client, calendar.CalendarId, "Weekly", count: 3);
        var file = Vcalendar(Vevent($"{series.RecurrencePatternId}@odyssey.local", "STATUS:CANCELLED", "SUMMARY:Weekly",
            "RECURRENCE-ID:20300102T090000Z", "DTSTART:20300102T090000Z"));

        var gated = await ImportAsync(client, calendar.CalendarId, file);
        Assert.Equal(1, gated.GatedSeriesCount);
        Assert.Equal(CalendarIcsService.GatedReason, Assert.Single(gated.Skipped).Reason);

        var result = await ImportAsync(client, calendar.CalendarId, file, allowRemovals: true);

        Assert.Equal(1, result.ExclusionsApplied);
        Assert.Empty(result.Skipped);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        Assert.True(await db.RecurrencePatterns.AnyAsync(p => p.RecurrencePatternId == series.RecurrencePatternId));
        Assert.Equal(2, await db.CalendarEvents.CountAsync(e => e.RecurrencePatternId == series.RecurrencePatternId));
        Assert.Equal(new DateTime(2030, 1, 2, 9, 0, 0),
            Assert.Single(await db.RecurrenceExclusions.Where(x => x.RecurrencePatternId == series.RecurrencePatternId).ToListAsync()).OccurrenceKey);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task For_one_uid_the_last_component_wins_between_live_and_cancelled(bool liveFirst)
    {
        // AC 17c: live then cancelled removes the event; cancelled then live leaves it live.
        var factory = Factory;
        using var client = factory.CreateClient();
        var calendar = await CreateCalendarAsync(client, "Order");
        await SeedRowAsync(factory, calendar.CalendarId, "Stored", externalUid: "same@example.com");
        var live = Vevent("same@example.com", "DTSTART:20300101T090000Z", "DTEND:20300101T100000Z", "SUMMARY:Live");
        var cancelled = Cancelled("same@example.com", "Live");

        await ImportAsync(client, calendar.CalendarId, liveFirst ? Vcalendar(live, cancelled) : Vcalendar(cancelled, live), allowRemovals: true);

        await using var scope = factory.Services.CreateAsyncScope();
        var rows = await scope.ServiceProvider.GetRequiredService<OdysseyContext>().CalendarEvents
            .Where(e => e.CalendarId == calendar.CalendarId).ToListAsync();
        if (liveFirst)
        {
            Assert.Empty(rows);
        }
        else
        {
            Assert.Equal("Live", Assert.Single(rows).Title);
        }
    }

    [Fact]
    public async Task An_event_created_and_cancelled_in_one_batch_counts_as_neither_imported_nor_removed()
    {
        // Architect nit 9: the row never reached the database, so the result must not report a removal.
        var factory = Factory;
        using var client = factory.CreateClient();
        var calendar = await CreateCalendarAsync(client, "Net zero");
        var live = Vevent("fresh@example.com", "DTSTART:20300101T090000Z", "DTEND:20300101T100000Z", "SUMMARY:Fresh");

        var result = await ImportAsync(
            client, calendar.CalendarId, Vcalendar(live, Cancelled("fresh@example.com", "Fresh")), allowRemovals: true);

        Assert.Equal((0, 0), (result.ImportedCount, result.RemovedCount));
        Assert.Empty(result.RemovedSampleTitles);
        await using var scope = factory.Services.CreateAsyncScope();
        Assert.False(await scope.ServiceProvider.GetRequiredService<OdysseyContext>()
            .CalendarEvents.AnyAsync(e => e.CalendarId == calendar.CalendarId));
    }

    [Fact]
    public async Task A_uid_over_255_characters_skips_its_event_and_the_batch_still_commits()
    {
        // Architect finding 4: under strict mode it used to fail the whole batch's save, so the file never completed.
        var factory = Factory;
        using var client = factory.CreateClient();
        var calendar = await CreateCalendarAsync(client, "Long uid");

        var result = await ImportAsync(client, calendar.CalendarId, Vcalendar(
            Standalone(new string('u', 256) + "@example.com", "Too long"), Standalone("fine@example.com", "Fine")));

        Assert.Equal(1, result.ImportedCount);
        var group = Assert.Single(result.Skipped);
        Assert.Equal(CalendarIcsService.UidTooLongReason, group.Reason);
        Assert.Equal(["Too long"], group.SampleTitles);
    }

    // ------------------------------------------------------------------ AC 16b, 17: streaming caps and partial failure

    [Theory]
    [InlineData("line")]
    [InlineData("component")]
    [InlineData("unterminated")]
    public async Task An_oversized_or_unclosed_component_refuses_the_whole_file_before_anything_is_applied(string shape)
    {
        var factory = Factory;
        using var client = factory.CreateClient();
        var calendar = await CreateCalendarAsync(client, "Broken");
        var bad = shape switch
        {
            // One unfolded line of 2 MiB, folded on the wire as RFC 5545 allows.
            "line" => "BEGIN:VEVENT\r\nUID:bad\r\nDESCRIPTION:" + string.Join("\r\n ", Enumerable.Repeat(new string('a', 70), 30_000)) + "\r\nEND:VEVENT\r\n",
            // A 5 MiB VEVENT of ordinary lines.
            "component" => "BEGIN:VEVENT\r\nUID:bad\r\n" + string.Concat(Enumerable.Repeat("X-PAD:" + new string('b', 1000) + "\r\n", 5200)) + "END:VEVENT\r\n",
            _ => "BEGIN:VEVENT\r\nUID:bad\r\nSUMMARY:Never closed\r\n",
        };
        var file = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//test//EN\r\n"
            + string.Concat(Enumerable.Range(0, CalendarIcsService.ImportBatchSize).Select(i => Standalone($"ok-{i}@example.com", $"Ok {i}")))
            + bad + (shape == "unterminated" ? string.Empty : "END:VCALENDAR\r\n");

        var response = await PostIcsAsync(client, calendar.CalendarId, file);

        // Issue #519 §3 component 6: pass 1 reads the whole file before any batch, so a structural fault anywhere in it
        // is a plain 400 and the batches before it are never applied.
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = (await response.Content.ReadFromJsonAsync<ProblemDetails>())!;
        Assert.False(problem.Extensions.ContainsKey("code"));
        Assert.Contains("too large or not closed", problem.Detail);
        await using var scope = factory.Services.CreateAsyncScope();
        Assert.Equal(0, await scope.ServiceProvider.GetRequiredService<OdysseyContext>()
            .CalendarEvents.CountAsync(e => e.CalendarId == calendar.CalendarId));
    }

    [Fact]
    public async Task Standalone_events_beyond_the_occurrence_budget_import_under_the_shipped_limits()
    {
        // Architect finding 1: CalendarIcsMaxAggregateOccurrences bounds series expansion only (§3 component 5,
        // Non-Goal 4). The factory's real ImportExportLimitsLookup serves the shipped defaults.
        var factory = Factory;
        using var client = factory.CreateClient();
        var calendar = await CreateCalendarAsync(client, "Many standalone");
        var count = SystemSettingsDefaults.CalendarIcsMaxAggregateOccurrences + 1;
        var file = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//test//EN\r\n"
            + string.Concat(Enumerable.Range(0, count).Select(i => Standalone($"many-{i}@example.com", $"Row {i}")))
            + "END:VCALENDAR\r\n";

        var result = await ImportAsync(client, calendar.CalendarId, file);

        Assert.Equal(count, result.ImportedCount);
        Assert.Empty(result.Skipped);
    }

    [Fact]
    public async Task Generated_series_occurrences_still_count_against_the_budget_across_the_whole_import()
    {
        var factory = Factory.ForCaller(WithSettings);
        using var client = factory.CreateClient();
        var calendar = await CreateCalendarAsync(client, "Budgeted");
        await SetAsync(client, new SystemSettingsUpdate { CalendarIcsMaxAggregateOccurrences = 15 });
        var series = Enumerable.Range(0, 2).Select(i => Vevent(
            $"series-{i}@example.com", "DTSTART:20300101T090000Z", "DTEND:20300101T100000Z", $"SUMMARY:Series {i}",
            "RRULE:FREQ=DAILY;COUNT=10"));
        // Twenty standalone events first: none of them spends the budget, so only the second series crosses it.
        var file = Vcalendar([
            .. Enumerable.Range(0, 20).Select(i => Standalone($"plain-{i}@example.com", $"Plain {i}")), .. series]);

        var response = await PostIcsAsync(client, calendar.CalendarId, file);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = (await response.Content.ReadFromJsonAsync<ProblemDetails>())!;
        Assert.Contains("more than 15 series occurrences", problem.Detail);
    }

    [Fact]
    public async Task Near_cap_components_flush_a_batch_by_size_before_it_reaches_its_event_count()
    {
        // Security finding 1: a batch is applied once its components reach ImportBatchCharBudget, so 500 near-cap
        // components can never be held at once. Each component here is ~3 Mi characters, so 16 Mi is crossed by the
        // sixth: twelve components commit as two batches of six, which the per-batch log line shows.
        var factory = Factory;
        var logs = factory.Logs!;
        using var client = factory.CreateClient();
        var calendar = await CreateCalendarAsync(client, "Sized");
        var padding = string.Concat(Enumerable.Repeat("X-PAD:" + new string('p', 1018) + "\r\n", 3 * 1024));
        var big = Enumerable.Range(0, 12).Select(i => "BEGIN:VEVENT\r\nUID:big-" + i + "@example.com\r\n"
            + "DTSTART:20300101T090000Z\r\nDTEND:20300101T100000Z\r\nSUMMARY:Big " + i + "\r\n" + padding + "END:VEVENT\r\n");
        var file = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//test//EN\r\n" + string.Concat(big) + "END:VCALENDAR\r\n";
        Assert.InRange(padding.Length * 6, CalendarIcsService.ImportBatchCharBudget, int.MaxValue);
        Assert.InRange(padding.Length * 5, 0, CalendarIcsService.ImportBatchCharBudget - 64 * 1024);

        var result = await ImportAsync(client, calendar.CalendarId, file);

        Assert.Equal(12, result.ImportedCount);
        Assert.Equal(2, logs.ForCategory(nameof(CalendarIcsService)).Count(e => e.Message.Contains("batch committed")));
    }

    [Fact]
    public async Task A_file_whose_first_component_is_unclosed_is_a_plain_400()
    {
        var factory = Factory;
        using var client = factory.CreateClient();
        var calendar = await CreateCalendarAsync(client, "Unclosed");

        var response = await PostIcsAsync(client, calendar.CalendarId, "BEGIN:VCALENDAR\r\nBEGIN:VEVENT\r\nUID:x\r\n");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = (await response.Content.ReadFromJsonAsync<ProblemDetails>())!;
        Assert.False(problem.Extensions.ContainsKey("code"));
    }

    [Fact]
    public async Task A_failure_in_batch_three_keeps_batches_one_and_two_and_a_rerun_completes_the_file()
    {
        // Groups commit in UID-hash order since issue #519 (AR3), so the failure is one every group spends alike: three
        // batches' worth of single-occurrence series against an occurrence budget of two batches fail in batch three
        // whatever the order. This copy pins the HTTP error shape and the re-run on EF InMemory, so it lowers the batch
        // size to keep the file small; the committed-batch semantics at the production batch size are
        // CalendarIcsStreamingIntegrationTests' twin on MariaDB.
        const int batchSize = 5;
        // A host of its own: the batch size is a replaced service.
        await using var factory = new ApiFactory(WithSettings, importBatchGroups: batchSize);
        using var client = factory.CreateClient();
        var calendar = await CreateCalendarAsync(client, "Partial");
        await SetAsync(client, new SystemSettingsUpdate { CalendarIcsMaxAggregateOccurrences = 2 * batchSize });
        var file = Vcalendar([.. Enumerable.Range(0, 3 * batchSize).Select(i => Vevent(
            $"{SyntheticGuid(i)}@odyssey.local", "DTSTART:20300101T090000Z", "DTEND:20300101T100000Z", $"SUMMARY:Row {i}",
            "RRULE:FREQ=DAILY;COUNT=1"))]);

        var failed = await PostIcsAsync(client, calendar.CalendarId, file);

        Assert.Equal(HttpStatusCode.BadRequest, failed.StatusCode);
        var problem = (await failed.Content.ReadFromJsonAsync<ProblemDetails>())!;
        Assert.Equal(CalendarIcsImportPartiallyAppliedException.DiscriminatorCode, problem.Extensions["code"]?.ToString());
        Assert.Equal("2", problem.Extensions["committedBatches"]?.ToString());
        Assert.Equal($"{2 * batchSize}", problem.Extensions["committedEvents"]?.ToString());
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
            var series = await db.RecurrencePatterns.Where(p => p.CalendarId == calendar.CalendarId).ToListAsync();
            Assert.Equal(2 * batchSize, series.Count);
            // The match keys were written in the same batch as their rows, so the re-run finds them.
            Assert.All(series, p => Assert.NotNull(p.ImportSourceUid));
        }

        await SetAsync(client, new SystemSettingsUpdate { CalendarIcsMaxAggregateOccurrences = 20000 });
        var rerun = await ImportAsync(client, calendar.CalendarId, file);

        Assert.Equal(2 * batchSize, rerun.UpdatedCount);
        Assert.Equal(batchSize, rerun.ImportedCount);
    }

    [Fact]
    public async Task The_buffered_upload_is_deleted_after_a_failed_import()
    {
        // AC 16 (tester finding 6): ASP.NET Core spools a multipart body over 64 KB to ASPNETCORE_TEMP. The probe
        // proves the spool existed while the request ran, so its absence afterwards is the deletion, not a body that
        // was never written to disk.
        var probe = new SpoolProbe($"spool-{Guid.NewGuid():N}");
        // A host of its own: the probe is a startup filter.
        await using var factory = new ApiFactory(ReadWrite, startup: probe);
        using var client = factory.CreateClient();
        var calendar = await CreateCalendarAsync(client, "Spooled");
        var file = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//test//EN\r\nX-MARKER:" + probe.Marker + "\r\nX-PAD:" + new string('p', 32 * 1024) + "\r\n"
            + string.Concat(Enumerable.Range(0, CalendarIcsService.ImportBatchSize).Select(i => Standalone($"spool-{i}@example.com", $"Spool {i}")))
            + "BEGIN:VEVENT\r\nUID:never\r\n";
        Assert.True(Encoding.UTF8.GetByteCount(file) > 64 * 1024);

        var response = await PostIcsAsync(client, calendar.CalendarId, file);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True(probe.SeenDuringRequest, "The upload was never spooled to disk, so this test proves nothing.");
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (probe.SpoolFiles().Count > 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }

        Assert.Empty(probe.SpoolFiles());
    }

    // ------------------------------------------------------------------ helpers

    // Looks for the request's spool file (ASPNETCORE_*.tmp holding the marker) as the response starts, which is
    // before the framework disposes it.
    private sealed class SpoolProbe(string marker) : IStartupFilter
    {
        public string Marker { get; } = marker;

        public bool SeenDuringRequest { get; private set; }

        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, inner) =>
            {
                // OnStarting runs before the client can see any response, so the check never races the test, and
                // after model binding has spooled the form.
                if (HttpMethods.IsPost(context.Request.Method) && context.Request.Path.Value!.EndsWith("/ics", StringComparison.Ordinal))
                {
                    context.Response.OnStarting(() =>
                    {
                        SeenDuringRequest |= SpoolFiles().Count > 0;
                        return Task.CompletedTask;
                    });
                }

                await inner(context);
            });
            next(app);
        };

        public List<string> SpoolFiles()
        {
            var directory = Environment.GetEnvironmentVariable("ASPNETCORE_TEMP") is { Length: > 0 } configured
                ? configured
                : Path.GetTempPath();
            var found = new List<string>();
            foreach (var path in Directory.EnumerateFiles(directory, "ASPNETCORE_*.tmp"))
            {
                try
                {
                    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    var head = new byte[4096];
                    var read = stream.Read(head, 0, head.Length);
                    if (Encoding.UTF8.GetString(head, 0, read).Contains(Marker, StringComparison.Ordinal))
                    {
                        found.Add(path);
                    }
                }
                catch (IOException)
                {
                    // Deleted, or another test's spool held exclusively, between the listing and the open.
                }
            }

            return found;
        }
    }

    private static string Summary(string url)
    {
        var at = url.IndexOf('?');
        return at < 0 ? url + "/summary" : url[..at] + "/summary" + url[at..];
    }

    private static Guid SyntheticGuid(int i) => new($"00000000-0000-0000-0000-{i:D12}");

    private static string Standalone(string uid, string title) =>
        Vevent(uid, "DTSTART:20300101T090000Z", "DTEND:20300101T100000Z", $"SUMMARY:{title}");

    private static string Cancelled(string uid, string title) =>
        Vevent(uid, "STATUS:CANCELLED", "DTSTART:20300101T090000Z", "DTEND:20300101T100000Z", $"SUMMARY:{title}");

    private static int Count(string haystack, string needle)
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

    private static async Task SetAsync(HttpClient client, SystemSettingsUpdate update) =>
        (await client.PutAsJsonAsync("/api/system-settings", update)).EnsureSuccessStatusCode();

    private static async Task<ExistingCalendar> CreateCalendarAsync(HttpClient client, string name)
    {
        var post = await client.PostAsJsonAsync(CalendarsPath, new NewCalendar { Name = name, Color = "#0369A1" });
        post.EnsureSuccessStatusCode();
        return (await post.Content.ReadFromJsonAsync<ExistingCalendar>())!;
    }

    private static async Task<ExistingCalendarEvent> CreateEventAsync(HttpClient client, Guid calendarId, string title)
    {
        var start = new DateTime(2030, 6, 1, 9, 0, 0, DateTimeKind.Utc);
        var post = await client.PostAsJsonAsync(EventsPath, new NewCalendarEvent
        {
            CalendarId = calendarId, Title = title, StartDateTime = start, EndDateTime = start.AddHours(1),
        });
        post.EnsureSuccessStatusCode();
        return (await post.Content.ReadFromJsonAsync<ExistingCalendarEvent>())!;
    }

    private static async Task<ExistingRecurrencePattern> CreateSeriesAsync(
        HttpClient client, Guid calendarId, string title, int count, DateTime? start = null)
    {
        var first = start ?? new DateTime(2030, 1, 1, 9, 0, 0, DateTimeKind.Utc);
        var post = await client.PostAsJsonAsync(PatternsPath, new NewRecurrencePattern
        {
            CalendarId = calendarId,
            Title = title,
            StartDateTime = first,
            EndDateTime = first.AddHours(1),
            Frequency = Odyssey.Dtos.Journal.RecurrenceFrequency.Daily,
            Interval = 1,
            OccurrenceCount = count,
        });
        post.EnsureSuccessStatusCode();
        return (await post.Content.ReadFromJsonAsync<ExistingRecurrencePattern>())!;
    }

    private static async Task EditFirstOccurrenceAsync(ApiFactory factory, Guid patternId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        var first = await db.CalendarEvents.Where(e => e.RecurrencePatternId == patternId).OrderBy(e => e.StartDateTime).FirstAsync();
        first.Title = "Edited";
        await db.SaveChangesAsync();
    }

    private static async Task<Guid> MoveFirstOccurrenceAsync(ApiFactory factory, Guid patternId, Guid toCalendarId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        var first = await db.CalendarEvents.Where(e => e.RecurrencePatternId == patternId).OrderBy(e => e.StartDateTime).FirstAsync();
        first.CalendarId = toCalendarId;
        await db.SaveChangesAsync();
        return first.CalendarEventId;
    }

    private static async Task<Guid> SeedRowAsync(
        ApiFactory factory, Guid calendarId, string title, string? externalUid = null, string? location = null, Guid? locationId = null)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        var start = new DateTime(2030, 1, 1, 9, 0, 0, DateTimeKind.Utc);
        var row = new ContextCalendarEvent
        {
            CalendarId = calendarId, Title = title, StartDateTime = start, EndDateTime = start.AddHours(1),
            ExternalUid = externalUid, Location = location, LocationId = locationId, CreatedByUserId = ActorUserId,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        db.CalendarEvents.Add(row);
        await db.SaveChangesAsync();
        return row.CalendarEventId;
    }

    private static async Task SeedRowsAsync(ApiFactory factory, Guid calendarId, int count, Func<int, string> title)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        var start = new DateTime(2030, 1, 1, 9, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < count; i++)
        {
            db.CalendarEvents.Add(new ContextCalendarEvent
            {
                CalendarId = calendarId, Title = title(i), StartDateTime = start.AddMinutes(i), EndDateTime = start.AddMinutes(i + 30),
                CreatedByUserId = ActorUserId, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            });
        }

        await db.SaveChangesAsync();
    }

    // A series of `rows` daily occurrences, each with a 2,000-character description (~2 KB a VEVENT); the first is
    // edited, so every export of it writes row by row.
    private static async Task<Guid> SeedBulkySeriesAsync(ApiFactory factory, Guid calendarId, int rows)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        var start = new DateTime(2030, 1, 1, 9, 0, 0, DateTimeKind.Utc);
        var stamp = new DateTime(2029, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var pattern = new RecurrencePattern
        {
            CalendarId = calendarId, Title = "Bulky", Description = new string('x', 2000), StartDateTime = start,
            EndDateTime = start.AddHours(1), Frequency = Odyssey.Context.RecurrenceFrequency.Daily, Interval = 1, OccurrenceCount = rows,
            CreatedByUserId = ActorUserId, CreatedAt = stamp, UpdatedAt = stamp,
        };
        db.RecurrencePatterns.Add(pattern);
        for (var i = 0; i < rows; i++)
        {
            db.CalendarEvents.Add(new ContextCalendarEvent
            {
                CalendarId = calendarId, Title = i == 0 ? "Edited" : "Bulky", Description = new string('x', 2000),
                StartDateTime = start.AddDays(i), EndDateTime = start.AddDays(i).AddHours(1), RecurrencePattern = pattern,
                CreatedByUserId = ActorUserId, CreatedAt = stamp, UpdatedAt = stamp,
            });
        }

        await db.SaveChangesAsync();
        return pattern.RecurrencePatternId;
    }

    private static async Task<Guid> SeedLocationAsync(ApiFactory factory, string name)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        var location = new Location { Name = name, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        db.Locations.Add(location);
        await db.SaveChangesAsync();
        return location.LocationId;
    }

    private static async Task<ContextCalendarEvent?> FindRowAsync(ApiFactory factory, Guid id)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<OdysseyContext>().CalendarEvents.AsNoTracking()
            .FirstOrDefaultAsync(e => e.CalendarEventId == id);
    }

    private static async Task<ContextCalendarEvent> RowAsync(ApiFactory factory, Guid id) => (await FindRowAsync(factory, id))!;

    private static async Task<IcsImportResult> ImportAsync(HttpClient client, Guid calendarId, string ics, bool allowRemovals = false)
    {
        var response = await PostIcsAsync(client, calendarId, ics, allowRemovals);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<IcsImportResult>())!;
    }

    private static async Task<HttpResponseMessage> PostIcsAsync(HttpClient client, Guid calendarId, string ics, bool allowRemovals = false)
    {
        using var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(ics));
        file.Headers.ContentType = new MediaTypeHeaderValue("text/calendar");
        content.Add(file, "file", "calendar.ics");
        if (allowRemovals)
        {
            content.Add(new StringContent("true"), "allowRemovals");
        }

        return await client.PostAsync($"{CalendarsPath}/{calendarId}/ics", content);
    }

    /// <summary>The class's shared host: a calendar editor, with logs captured and cleared per test.</summary>
    public sealed class Host() : ApiFactory(ReadWrite, new RecordingLoggerProvider());

    public class ApiFactory : OdysseyApiFactory
    {
        public ApiFactory(
            IReadOnlyCollection<string> permissions, RecordingLoggerProvider? logs = null, TimeProvider? clock = null,
            IStartupFilter? startup = null, int? importBatchGroups = null)
            : base(permissions, ActorUserId, configureServices: services =>
            {
                if (startup is not null)
                {
                    services.AddSingleton(startup);
                }

                if (importBatchGroups is { } groups)
                {
                    services.RemoveAll<CalendarIcsService>();
                    services.AddScoped(provider =>
                    {
                        var service = ActivatorUtilities.CreateInstance<CalendarIcsService>(provider);
                        service.ImportBatchGroups = groups;
                        return service;
                    });
                }

                if (clock is not null)
                {
                    services.RemoveAll<TimeProvider>();
                    services.AddSingleton(clock);
                }

                var name = $"calendar-ics-streaming-{Guid.NewGuid()}";
                services.RemoveAll<DbContextOptions<OdysseyContext>>();
                services.AddDbContext<OdysseyContext>(options => options.UseInMemoryDatabase(name));
                if (logs is not null)
                {
                    services.AddSingleton<Microsoft.Extensions.Logging.ILoggerProvider>(logs);
                }
            }) =>
            Logs = logs;

        public RecordingLoggerProvider? Logs { get; }

        public override async Task ResetStoreAsync()
        {
            await base.ResetStoreAsync();
            Logs?.Clear();
        }
    }
}
