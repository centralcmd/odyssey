using System.Net;
using System.Text;
using System.Text.Json;
using Odyssey.ApiClient.Resources;
using Odyssey.Dtos.Journal;
using Odyssey.Testing.Http;
using Xunit;

namespace Odyssey.ApiClient.Tests;

/// <summary>
/// The typed client's issue #519 surface: the view zone on the windowed list and the aggregate export, the series
/// update and preview with their counts, the occurrence delete that may report detached rows, and the import's
/// default zone.
/// </summary>
public class CalendarRecurrenceApiClientTests
{
    private static readonly Guid Id = Guid.Parse("7c1d0000-0000-0000-0000-000000000001");

    private static (CalendarApiClient Client, RecordingHttpHandler Handler) Create(HttpStatusCode status, string? body)
    {
        var handler = new RecordingHttpHandler(status, body);
        var api = new OdysseyApi(new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") });
        return (new CalendarApiClient(api), handler);
    }

    private static NewRecurrencePattern Pattern() => new()
    {
        CalendarId = Id,
        Title = "Standup",
        StartDateTime = new DateTime(2026, 3, 16, 9, 0, 0),
        EndDateTime = new DateTime(2026, 3, 16, 10, 0, 0),
        Frequency = RecurrenceFrequency.Weekly,
        DaysOfWeek = DaysOfWeekFlags.Monday,
        OccurrenceCount = 4,
        TimeZoneId = "Europe/Oslo",
        ExpectedVersion = Guid.Parse("00000000-0000-0000-0000-0000000000aa"),
        ConfirmedDrops = new RecurrenceConfirmedDrops { Overrides = 1, Exclusions = 0 },
    };

    [Fact]
    public async Task ListEventsAsync_with_a_view_zone_puts_it_on_the_query_string()
    {
        var (client, handler) = Create(HttpStatusCode.OK, """{"items":[],"totalCount":0,"offset":0,"limit":200}""");

        var result = await client.ListEventsAsync(
            new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc), new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc), "Europe/Oslo");

        Assert.True(result.IsSuccess);
        Assert.Contains("viewTimeZoneId=Europe%2FOslo", handler.LastRequest!.RequestUri!.Query);
    }

    [Fact]
    public async Task UpdatePatternWithResultAsync_sends_the_version_and_consent_and_reads_the_counts()
    {
        var (client, handler) = Create(HttpStatusCode.OK, $$"""
            {"pattern":{"recurrencePatternId":"{{Id}}","calendarId":"{{Id}}","title":"Standup","version":"{{Id}}","exclusionCount":2,"isKeyed":true},
             "droppedOverrideCount":1,"droppedExclusionCount":0,"detachedCount":3,"adjustedCount":0}
            """);

        var result = await client.UpdatePatternWithResultAsync(Id, Pattern());

        Assert.Equal(HttpMethod.Put, handler.LastRequest!.Method);
        Assert.Equal($"/api/recurrence-patterns/{Id}", handler.LastRequest.RequestUri!.AbsolutePath);
        using var sent = JsonDocument.Parse(handler.LastBody!);
        Assert.Equal("00000000-0000-0000-0000-0000000000aa", sent.RootElement.GetProperty("expectedVersion").GetString());
        Assert.Equal(1, sent.RootElement.GetProperty("confirmedDrops").GetProperty("overrides").GetInt32());
        Assert.Equal("Europe/Oslo", sent.RootElement.GetProperty("timeZoneId").GetString());
        Assert.Equal(1, result.Value!.DroppedOverrideCount);
        Assert.Equal(3, result.Value.DetachedCount);
        Assert.Equal(2, result.Value.Pattern.ExclusionCount);
    }

    [Fact]
    public async Task PreviewPatternAsync_posts_to_the_preview_route()
    {
        var (client, handler) = Create(HttpStatusCode.OK, $$"""
            {"droppedOverrideCount":2,"droppedExclusionCount":1,"detachedCount":0,"adjustedCount":0,"version":"{{Id}}"}
            """);

        var result = await client.PreviewPatternAsync(Id, Pattern());

        Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
        Assert.Equal($"/api/recurrence-patterns/{Id}/preview", handler.LastRequest.RequestUri!.AbsolutePath);
        Assert.Equal((2, 1, Id), (result.Value!.DroppedOverrideCount, result.Value.DroppedExclusionCount, result.Value.Version));
    }

    [Fact]
    public async Task DeleteEventWithResultAsync_reads_no_body_on_204_and_the_detached_report_on_200()
    {
        var (plain, _) = Create(HttpStatusCode.NoContent, null);
        var none = await plain.DeleteEventWithResultAsync(Id);
        Assert.True(none.IsSuccess);
        Assert.Null(none.Value);

        var (reporting, handler) = Create(HttpStatusCode.OK, """{"detached":true,"detachedCount":2}""");
        var report = await reporting.DeleteEventWithResultAsync(Id);
        Assert.Equal(HttpMethod.Delete, handler.LastRequest!.Method);
        Assert.True(report.Value!.Detached);
        Assert.Equal(2, report.Value.DetachedCount);
    }

    [Fact]
    public async Task A_series_conflict_comes_back_as_a_failure_carrying_its_code()
    {
        var (client, _) = Create(HttpStatusCode.Conflict,
            """{"status":409,"title":"Conflict","detail":"Changed.","code":"calendar-series-drops-changed","droppedOverrideCount":2}""");

        var result = await client.UpdatePatternWithResultAsync(Id, Pattern());

        Assert.False(result.IsSuccess);
        Assert.Equal(HttpStatusCode.Conflict, result.Status);
    }

    [Fact]
    public async Task ImportAsync_with_a_default_zone_sends_it_as_a_form_field()
    {
        var (client, handler) = Create(HttpStatusCode.OK, """{"importedCount":1,"gatedSeriesCount":2,"overridesApplied":3}""");

        var result = await client.ImportAsync(Id, new ApiUpload("c.ics", "text/calendar", 15, () => new MemoryStream("BEGIN:VCALENDAR"u8.ToArray())),
            allowRemovals: true, defaultTimeZoneId: "Europe/Oslo");

        Assert.Contains("name=defaultTimeZoneId", handler.LastBody);
        Assert.Contains("Europe/Oslo", handler.LastBody);
        Assert.Contains("name=allowRemovals", handler.LastBody);
        Assert.Equal((2, 3), (result.Value!.GatedSeriesCount, result.Value.OverridesApplied));
    }

    [Fact]
    public void The_aggregate_export_url_carries_the_view_zone()
    {
        var (client, _) = Create(HttpStatusCode.OK, null);

        var url = client.ExportAggregateUrl(
            new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc), new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc), null, null,
            "America/New_York");

        Assert.Contains("viewTimeZoneId=America%2FNew_York", url);
    }

    [Fact]
    public async Task The_windowed_list_sends_its_instants_as_utc_with_a_z_and_no_plus_offset()
    {
        // #520 v5 N3: an instant never reaches the query string with a '+' offset, whatever Kind the caller held.
        var (client, handler) = Create(HttpStatusCode.OK, """{"items":[],"totalCount":0,"offset":0,"limit":200}""");

        await client.ListEventsAsync(
            new DateTime(2026, 2, 28, 23, 0, 0, DateTimeKind.Utc), new DateTime(2026, 3, 31, 22, 0, 0), "Europe/Oslo");

        var query = Uri.UnescapeDataString(handler.LastRequest!.RequestUri!.Query);
        Assert.Contains("from=2026-02-28T23:00:00.0000000Z", query);
        Assert.Contains("to=2026-03-31T22:00:00.0000000Z", query);
        Assert.DoesNotContain("+", query);
    }

    [Fact]
    public async Task The_aggregate_summary_sends_its_window_as_utc_with_the_view_zone()
    {
        var (client, handler) = Create(HttpStatusCode.OK, """{"eventCount":0,"seriesCount":0,"occurrenceCount":0}""");

        await client.SummarizeAggregateExportAsync(
            new DateTime(2026, 3, 1, 5, 0, 0), new DateTime(2026, 4, 1, 4, 0, 0, DateTimeKind.Utc), null, null, "America/New_York");

        var query = Uri.UnescapeDataString(handler.LastRequest!.RequestUri!.Query);
        Assert.Contains("viewTimeZoneId=America/New_York", query);
        Assert.Contains("from=2026-03-01T05:00:00.0000000Z", query);
        Assert.Contains("to=2026-04-01T04:00:00.0000000Z", query);
    }

    [Fact]
    public async Task UpdateEventWithResultAsync_sends_the_wall_clock_and_zone_and_reads_the_stored_event()
    {
        var (client, handler) = Create(HttpStatusCode.OK, $$"""
            {"calendarEventId":"{{Id}}","calendarId":"{{Id}}","title":"Standup","startDateTime":"2026-03-29T03:30:00",
             "endDateTime":"2026-03-29T04:30:00","timeZoneId":"Europe/Oslo","isOverride":true,"detached":false,"detachedCount":2}
            """);
        var body = new NewCalendarEvent
        {
            CalendarId = Id,
            Title = "Standup",
            StartDateTime = new DateTime(2026, 3, 29, 2, 30, 0),
            EndDateTime = new DateTime(2026, 3, 29, 3, 30, 0),
            TimeZoneId = "Europe/Oslo",
        };

        var result = await client.UpdateEventWithResultAsync(Id, body);

        Assert.Equal(HttpMethod.Put, handler.LastRequest!.Method);
        Assert.Equal($"/api/calendar-events/{Id}", handler.LastRequest.RequestUri!.AbsolutePath);
        using var sent = JsonDocument.Parse(handler.LastBody!);
        Assert.Equal("2026-03-29T02:30:00", sent.RootElement.GetProperty("startDateTime").GetString());
        Assert.Equal("Europe/Oslo", sent.RootElement.GetProperty("timeZoneId").GetString());
        Assert.Equal(new DateTime(2026, 3, 29, 3, 30, 0), result.Value!.StartDateTime);
        Assert.True(result.Value.IsOverride);
        Assert.Equal(2, result.Value.DetachedCount);
    }

    [Fact]
    public async Task CreateEventWithResultAsync_and_CreatePatternWithResultAsync_post_and_read_the_body()
    {
        var (events, eventHandler) = Create(HttpStatusCode.Created, $$"""{"calendarEventId":"{{Id}}","calendarId":"{{Id}}","title":"A","startDateTime":"2026-06-04T09:00:00"}""");
        var created = await events.CreateEventWithResultAsync(new NewCalendarEvent
        {
            CalendarId = Id,
            Title = "A",
            StartDateTime = new DateTime(2026, 6, 4, 9, 0, 0),
            EndDateTime = new DateTime(2026, 6, 4, 10, 0, 0),
        });
        Assert.Equal(HttpMethod.Post, eventHandler.LastRequest!.Method);
        Assert.Equal(Id, created.Value!.CalendarEventId);

        var (patterns, patternHandler) = Create(HttpStatusCode.Created, $$"""{"recurrencePatternId":"{{Id}}","calendarId":"{{Id}}","title":"S","startDateTime":"2026-06-04T09:00:00"}""");
        var series = await patterns.CreatePatternWithResultAsync(Pattern());
        Assert.Equal("/api/recurrence-patterns", patternHandler.LastRequest!.RequestUri!.AbsolutePath);
        Assert.Equal(new DateTime(2026, 6, 4, 9, 0, 0), series.Value!.StartDateTime);
    }

    [Fact]
    public async Task GetPatternResultAsync_tells_a_404_from_other_failures()
    {
        var (gone, handler) = Create(HttpStatusCode.NotFound, """{"status":404,"title":"Not Found","detail":"Recurrence pattern not found."}""");
        var missing = await gone.GetPatternResultAsync(Id);
        Assert.Equal($"/api/recurrence-patterns/{Id}", handler.LastRequest!.RequestUri!.AbsolutePath);
        Assert.Equal((false, HttpStatusCode.NotFound), (missing.IsSuccess, missing.Status));

        var (broken, _) = Create(HttpStatusCode.InternalServerError, """{"status":500,"title":"Error"}""");
        Assert.Equal(HttpStatusCode.InternalServerError, (await broken.GetPatternResultAsync(Id)).Status);
    }
}
