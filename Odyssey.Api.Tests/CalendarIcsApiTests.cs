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

/// <summary>
/// ICS import/export endpoints (issue #330): what only the HTTP surface shows — claim gates, status codes, headers and
/// the file name — plus a happy-path import and round trip. The import and export rules themselves are pinned at the
/// service tier, in <c>Odyssey.Core.Tests/Ics/CalendarIcsServiceTests</c>.
/// </summary>
public class CalendarIcsApiTests(CalendarIcsApiTests.Host host) : SharedApiHostTests<CalendarIcsApiTests.Host>(host)
{
    private const string ActorUserId = "calendar-ics-actor-id";
    private const string CalendarsPath = "/api/calendars";

    private static readonly string[] ReadWrite =
    [
        PermissionClaims.CalendarRead, PermissionClaims.CalendarCreate,
        PermissionClaims.CalendarUpdate, PermissionClaims.CalendarDelete,
    ];

    // ------------------------------------------------------------------ export

    [Fact]
    public async Task Export_EmptyCalendar_ReturnsValidVCalendar_WithNoSniff()
    {
        var factory = Factory;
        using var client = factory.ClientFor(ReadWrite);
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
        var factory = Factory;
        using var client = factory.ClientFor(ReadWrite);
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
        var factory = Factory;
        using var client = factory.ClientFor(ReadWrite);

        var response = await client.GetAsync($"{CalendarsPath}/{Guid.NewGuid()}/ics");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Export_WithoutReadClaim_ReturnsForbidden()
    {
        var factory = Factory;
        using var client = factory.ClientFor([]);

        var response = await client.GetAsync($"{CalendarsPath}/{Guid.NewGuid()}/ics");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ------------------------------------------------------------------ import

    [Fact]
    public async Task Import_ThreeStandaloneEvents_CreatesThreeRowsWithExternalUid()
    {
        var factory = Factory;
        using var client = factory.ClientFor(ReadWrite);
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

    [Fact]
    public async Task Import_ExportedFile_IsIdempotent_CreatesZeroNewRows()
    {
        var factory = Factory;
        using var client = factory.ClientFor(ReadWrite);
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
    public async Task Import_MalformedFile_ReturnsBadRequest_NotServerError()
    {
        var factory = Factory;
        using var client = factory.ClientFor(ReadWrite);
        var calendar = await CreateCalendarAsync(client, "Bad");

        var response = await PostIcsAsync(client, calendar.CalendarId, "this is not an ics file", "notes.ics");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Import_WrongContentType_ReturnsBadRequest()
    {
        var factory = Factory;
        using var client = factory.ClientFor(ReadWrite);
        var calendar = await CreateCalendarAsync(client, "Ct");

        var ics = Vcalendar(Vevent("c1", "DTSTART:20300101T090000Z", "DTEND:20300101T100000Z", "SUMMARY:x"));
        var response = await PostIcsAsync(client, calendar.CalendarId, ics, "data.ics", contentType: "application/json");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Import_NonIcsExtension_ReturnsBadRequest()
    {
        var factory = Factory;
        using var client = factory.ClientFor(ReadWrite);
        var calendar = await CreateCalendarAsync(client, "Ext");

        var ics = Vcalendar(Vevent("x1", "DTSTART:20300101T090000Z", "DTEND:20300101T100000Z", "SUMMARY:x"));
        var response = await PostIcsAsync(client, calendar.CalendarId, ics, "data.txt");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Import_MissingCalendar_ReturnsNotFound()
    {
        var factory = Factory;
        using var client = factory.ClientFor(ReadWrite);

        var ics = Vcalendar(Vevent("m1", "DTSTART:20300101T090000Z", "DTEND:20300101T100000Z", "SUMMARY:x"));
        var response = await PostIcsAsync(client, Guid.NewGuid(), ics);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Import_WithOnlyCreateClaim_ReturnsForbidden()
    {
        var factory = Factory;
        using var client = factory.ClientFor([PermissionClaims.CalendarCreate, PermissionClaims.CalendarRead]);

        var ics = Vcalendar(Vevent("p1", "DTSTART:20300101T090000Z", "DTEND:20300101T100000Z", "SUMMARY:x"));
        var response = await PostIcsAsync(client, Guid.NewGuid(), ics);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Import_OctetStreamContentType_IsAccepted()
    {
        var factory = Factory;
        using var client = factory.ClientFor(ReadWrite);
        var calendar = await CreateCalendarAsync(client, "Octet");

        // Browsers/OSes routinely send application/octet-stream for .ics — it must be accepted.
        var ics = Vcalendar(Vevent("o1", "DTSTART:20300101T090000Z", "DTEND:20300101T100000Z", "SUMMARY:Octet"));
        var response = await PostIcsAsync(client, calendar.CalendarId, ics, "cal.ics", contentType: "application/octet-stream");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
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

    public class ApiFactory : OdysseyApiFactory
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

    /// <summary>The class's shared host: every caller's claims travel per request (<see cref="SharedApiHostClients"/>).</summary>
    public sealed class Host() : ApiFactory(permissions: null);
}
