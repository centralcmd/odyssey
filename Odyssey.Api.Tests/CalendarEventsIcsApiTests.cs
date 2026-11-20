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

/// <summary>
/// Single event / series / aggregate .ics export endpoints (issue #340): what only the HTTP surface shows — headers,
/// file names, 404s, claim gates, the 400 and its problem-details code, and the aborted over-cap stream — plus a
/// happy-path export of each scope. Which VEVENTs each scope writes, the aggregate's filters and its caps are pinned at
/// the service tier, in <c>Odyssey.Core.Tests/Ics/CalendarEventsIcsServiceTests</c>.
/// </summary>
public class CalendarEventsIcsApiTests(CalendarEventsIcsApiTests.Host host) : SharedApiHostTests<CalendarEventsIcsApiTests.Host>(host)
{
    private const string ActorUserId = "calendar-events-ics-actor-id";
    private const string CalendarsPath = "/api/calendars";
    private const string CalendarEventsPath = "/api/calendar-events";
    private const string RecurrencePatternsPath = "/api/recurrence-patterns";

    // The event cap these tests set through /api/system-settings: it ships unlimited since issue #516.
    private const int TestMaxExportEvents = 5;

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
        PermissionClaims.SystemSettingsRead, PermissionClaims.SystemSettingsUpdate, PermissionClaims.SystemSettingsSecurityUpdate,
    ];

    // ------------------------------------------------------------------ single event (AC 1)

    [Fact]
    public async Task ExportEvent_NonRecurring_ReturnsStandaloneVEvent()
    {
        var factory = Factory;
        using var client = factory.ClientFor(ReadWrite);
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

    [Fact]
    public async Task ExportEvent_UnknownId_ReturnsNotFound()
    {
        var factory = Factory;
        using var client = factory.ClientFor(ReadWrite);

        var response = await client.GetAsync($"{CalendarEventsPath}/{Guid.NewGuid()}/ics");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ExportEvent_WithoutReadClaim_ReturnsForbidden()
    {
        var factory = Factory;
        using var client = factory.ClientFor([]);

        var response = await client.GetAsync($"{CalendarEventsPath}/{Guid.NewGuid()}/ics");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ------------------------------------------------------------------ series (AC 2, 19)

    [Fact]
    public async Task ExportPattern_IntactSeries_EmitsSingleRRule()
    {
        var factory = Factory;
        using var client = factory.ClientFor(ReadWrite);
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
    public async Task ExportPattern_UnknownId_ReturnsNotFound()
    {
        var factory = Factory;
        using var client = factory.ClientFor(ReadWrite);

        var response = await client.GetAsync($"{RecurrencePatternsPath}/{Guid.NewGuid()}/ics");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ------------------------------------------------------------------ aggregate: no filter (AC 3, 12)

    [Fact]
    public async Task ExportAggregate_NoFilter_IncludesEventsFromEveryCalendar_AndCollapsesIntactSeries()
    {
        var factory = Factory;
        using var client = factory.ClientFor(ReadWrite);
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

    [Fact]
    public async Task ExportAggregate_FileName_IsFixedName()
    {
        var factory = Factory;
        using var client = factory.ClientFor(ReadWrite);

        var response = await client.GetAsync($"{CalendarEventsPath}/ics");

        var fileName = response.Content.Headers.ContentDisposition?.FileNameStar
                       ?? response.Content.Headers.ContentDisposition?.FileName?.Trim('"');
        Assert.Matches(@"^\d{8}_calendar-events\.ics$", fileName!);
    }

    // ------------------------------------------------------------------ aggregate: validation (AC 7)

    [Fact]
    public async Task ExportAggregate_OnlyFromSet_ReturnsBadRequest()
    {
        var factory = Factory;
        using var client = factory.ClientFor(ReadWrite);

        var response = await client.GetAsync($"{CalendarEventsPath}/ics?from={Uri.EscapeDataString(DateTime.UtcNow.ToString("O"))}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ------------------------------------------------------------------ aggregate: caps (AC 8, 9; issue #516 AC 4)

    private static async Task CapExportEventsAsync(HttpClient client, int max) =>
        (await client.PutAsJsonAsync("/api/system-settings", new SystemSettingsUpdate
        {
            CalendarIcsMaxExportEvents = new CapacityLimit { Value = max },
        })).EnsureSuccessStatusCode();

    private static async Task AssertExportLimitAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.Equal("calendar_events_export_limit_exceeded", problem!.Extensions["code"]?.ToString());
    }

    [Fact]
    public async Task ExportAggregate_FilteredMatchExceedsMaxVEvents_ReturnsBadRequest()
    {
        var factory = Factory;
        using var client = factory.ClientFor(ReadWriteWithSizeCapControl);
        await CapExportEventsAsync(client, TestMaxExportEvents);
        var calendar = await CreateCalendarAsync(client, "TooManyFiltered");
        await SeedStandaloneRowsAsync(factory, calendar.CalendarId, TestMaxExportEvents + 1, "Match");

        await AssertExportLimitAsync(await client.GetAsync($"{CalendarEventsPath}/ics?search=Match"));
    }

    // ------------------------------------------------------------------ aggregate: export byte-size cap (issue #516 AC 3)

    // Seeds standalone rows with a long, fixed-length title so a modest row count produces a
    // multi-hundred-KB export — enough to reliably cross a 1 MB CalendarIcsMaxExportMegabytes cap.
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

    // Both aggregate paths abort at the byte cap (issue #516 Goal 2): the filtered one used to truncate below the
    // promised row count and the unfiltered one to refuse late with a 400. The client sees a failed transfer, never a
    // complete 200 body.
    [Theory]
    [InlineData("?search=Bulky")]
    [InlineData("")]
    public async Task ExportAggregate_OverByteCap_AbortsTheConnection(string query)
    {
        var factory = Factory;
        using var client = factory.ClientFor(ReadWriteWithSizeCapControl);

        (await client.PutAsJsonAsync("/api/system-settings", new SystemSettingsUpdate
        {
            CalendarIcsMaxExportMegabytes = 1,
        })).EnsureSuccessStatusCode();

        var calendar = await CreateCalendarAsync(client, "Bulky");
        await SeedBulkyStandaloneRowsAsync(factory, calendar.CalendarId, 2000, "Bulky");

        await CalendarIcsStreamingApiTests.AssertAbortedMidStreamAsync(client, $"{CalendarEventsPath}/ics{query}", expectedRows: 2000);
    }

    // ------------------------------------------------------------------ claims (AC 15)

    [Fact]
    public async Task ExportAggregate_WithoutReadClaim_ReturnsForbidden()
    {
        var factory = Factory;
        using var client = factory.ClientFor([]);

        var response = await client.GetAsync($"{CalendarEventsPath}/ics");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task ExportPattern_WithoutReadClaim_ReturnsForbidden()
    {
        var factory = Factory;
        using var client = factory.ClientFor([]);

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

    public class ApiFactory : OdysseyApiFactory
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

    /// <summary>The class's shared host: every caller's claims travel per request (<see cref="SharedApiHostClients"/>).</summary>
    public sealed class Host() : ApiFactory(permissions: null);
}
