using System.Net;
using System.Text;
using System.Text.Json;
using Odyssey.ApiClient.Resources;
using Odyssey.Dtos.Journal;
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

    private sealed class RecordingHandler(HttpStatusCode status, string? body) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastRequest = request;
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            var response = new HttpResponseMessage(status);
            if (body is not null)
                response.Content = new StringContent(body, Encoding.UTF8, "application/json");
            return response;
        }
    }

    private static (CalendarApiClient Client, RecordingHandler Handler) Create(HttpStatusCode status, string? body)
    {
        var handler = new RecordingHandler(status, body);
        var api = new OdysseyApi(new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") });
        return (new CalendarApiClient(api), handler);
    }

    private static NewRecurrencePattern Pattern() => new()
    {
        CalendarId = Id, Title = "Standup", StartDateTime = new DateTime(2026, 3, 16, 9, 0, 0),
        EndDateTime = new DateTime(2026, 3, 16, 10, 0, 0), Frequency = RecurrenceFrequency.Weekly,
        DaysOfWeek = DaysOfWeekFlags.Monday, OccurrenceCount = 4, TimeZoneId = "Europe/Oslo",
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
}
