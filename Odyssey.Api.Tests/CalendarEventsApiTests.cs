using System.Net;
using System.Net.Http.Json;
using Odyssey.Dtos.Authorization;
using CalendarContext = Odyssey.Context.OdysseyContext;
using Odyssey.Dtos.Journal;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;
using Odyssey.Api.Tests.Infrastructure;

namespace Odyssey.Api.Tests;

public class CalendarEventsApiTests
{
    private const string ActorUserId = "calendar-events-actor-id";
    private const string CalendarsPath = "/api/calendars";
    private const string EventsPath = "/api/calendar-events";

    private static readonly string[] ReadWrite =
    [
        PermissionClaims.CalendarRead, PermissionClaims.CalendarCreate,
        PermissionClaims.CalendarUpdate, PermissionClaims.CalendarDelete,
    ];

    [Fact]
    public async Task List_Unauthenticated_ReturnsUnauthorized()
    {
        await using var factory = new ApiFactory(permissions: null);
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"{EventsPath}?from=2026-08-01T00:00:00Z&to=2026-08-08T00:00:00Z");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task List_WithoutCalendarClaims_ReturnsForbidden()
    {
        await using var factory = new ApiFactory(permissions: []);
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"{EventsPath}?from=2026-08-01T00:00:00Z&to=2026-08-08T00:00:00Z");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task List_MissingFromOrTo_ReturnsBadRequest()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"{EventsPath}?from=2026-08-01T00:00:00Z");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task List_WindowWiderThan92Days_ReturnsBadRequest()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"{EventsPath}?from=2026-01-01T00:00:00Z&to=2026-06-01T00:00:00Z");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task List_ReturnsEventOverlappingWindowStart()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();

        var calendar = await CreateCalendarAsync(client, "Personal");

        // Spans midnight across the window boundary: starts before From but is still in progress.
        var created = await CreateEventAsync(client, calendar.CalendarId, "Overnight shift",
            new DateTime(2026, 8, 4, 22, 0, 0, DateTimeKind.Utc), new DateTime(2026, 8, 5, 6, 0, 0, DateTimeKind.Utc));

        var items = await client.GetPagedItemsAsync<ExistingCalendarEvent>(
            $"{EventsPath}?from=2026-08-05T00:00:00Z&to=2026-08-06T00:00:00Z");

        Assert.Contains(items, e => e.CalendarEventId == created.CalendarEventId);
    }

    [Fact]
    public async Task Create_Then_Get()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();

        var calendar = await CreateCalendarAsync(client, "Personal");
        var created = await CreateEventAsync(client, calendar.CalendarId, "Dentist",
            new DateTime(2026, 8, 10, 9, 0, 0, DateTimeKind.Utc), new DateTime(2026, 8, 10, 9, 45, 0, DateTimeKind.Utc));

        var get = await client.GetAsync($"{EventsPath}/{created.CalendarEventId}");
        get.EnsureSuccessStatusCode();
        var fetched = await get.Content.ReadFromJsonAsync<ExistingCalendarEvent>();
        Assert.Equal("Dentist", fetched!.Title);
        Assert.Null(fetched.RecurrencePatternId);
    }

    [Fact]
    public async Task Create_EndBeforeStart_ReturnsBadRequest()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();

        var calendar = await CreateCalendarAsync(client, "Personal");

        var response = await client.PostAsJsonAsync(EventsPath, new NewCalendarEvent
        {
            CalendarId = calendar.CalendarId,
            Title = "Backwards",
            StartDateTime = new DateTime(2026, 8, 10, 10, 0, 0, DateTimeKind.Utc),
            EndDateTime = new DateTime(2026, 8, 10, 9, 0, 0, DateTimeKind.Utc),
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Create_DurationOver366Days_ReturnsBadRequest()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();

        var calendar = await CreateCalendarAsync(client, "Personal");

        var response = await client.PostAsJsonAsync(EventsPath, new NewCalendarEvent
        {
            CalendarId = calendar.CalendarId,
            Title = "Too long",
            StartDateTime = new DateTime(2026, 8, 10, 9, 0, 0, DateTimeKind.Utc),
            EndDateTime = new DateTime(2028, 8, 10, 9, 0, 0, DateTimeKind.Utc),
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Create_YearOutOfRange_ReturnsBadRequest()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();

        var calendar = await CreateCalendarAsync(client, "Personal");

        var response = await client.PostAsJsonAsync(EventsPath, new NewCalendarEvent
        {
            CalendarId = calendar.CalendarId,
            Title = "Out of range",
            StartDateTime = new DateTime(1, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            EndDateTime = new DateTime(1, 1, 2, 0, 0, 0, DateTimeKind.Utc),
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Create_MultiDayEvent_Succeeds()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();

        var calendar = await CreateCalendarAsync(client, "Personal");
        var created = await CreateEventAsync(client, calendar.CalendarId, "Holiday",
            new DateTime(2026, 8, 10, 9, 0, 0, DateTimeKind.Utc), new DateTime(2026, 8, 15, 9, 0, 0, DateTimeKind.Utc));

        Assert.Equal("Holiday", created.Title);
    }

    [Fact]
    public async Task Update_DurationOver366Days_ReturnsBadRequest()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();

        var calendar = await CreateCalendarAsync(client, "Personal");
        var created = await CreateEventAsync(client, calendar.CalendarId, "Standalone",
            new DateTime(2026, 8, 10, 9, 0, 0, DateTimeKind.Utc), new DateTime(2026, 8, 10, 10, 0, 0, DateTimeKind.Utc));

        var response = await client.PutAsJsonAsync($"{EventsPath}/{created.CalendarEventId}", new NewCalendarEvent
        {
            CalendarId = calendar.CalendarId,
            Title = "Now too long",
            StartDateTime = new DateTime(2026, 8, 10, 9, 0, 0, DateTimeKind.Utc),
            EndDateTime = new DateTime(2028, 8, 10, 9, 0, 0, DateTimeKind.Utc),
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Create_AllDay_NonMidnightBoundary_ReturnsBadRequest()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();

        var calendar = await CreateCalendarAsync(client, "Personal");

        var response = await client.PostAsJsonAsync(EventsPath, new NewCalendarEvent
        {
            CalendarId = calendar.CalendarId,
            Title = "Not midnight",
            StartDateTime = new DateTime(2026, 8, 10, 9, 0, 0, DateTimeKind.Utc),
            EndDateTime = new DateTime(2026, 8, 11, 0, 0, 0, DateTimeKind.Utc),
            IsAllDay = true,
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Create_SingleDayAllDay_Succeeds()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();

        var calendar = await CreateCalendarAsync(client, "Bills");
        var created = await CreateEventAsync(client, calendar.CalendarId, "Public holiday",
            new DateTime(2026, 8, 10, 0, 0, 0, DateTimeKind.Utc), new DateTime(2026, 8, 11, 0, 0, 0, DateTimeKind.Utc), isAllDay: true);

        Assert.True(created.IsAllDay);
    }

    [Fact]
    public async Task Create_UnknownCalendarId_ReturnsNotFound()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(EventsPath, new NewCalendarEvent
        {
            CalendarId = Guid.NewGuid(),
            Title = "Orphan",
            StartDateTime = new DateTime(2026, 8, 10, 9, 0, 0, DateTimeKind.Utc),
            EndDateTime = new DateTime(2026, 8, 10, 10, 0, 0, DateTimeKind.Utc),
        });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Update_CannotSetRecurrencePatternId()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();

        var calendar = await CreateCalendarAsync(client, "Personal");
        var created = await CreateEventAsync(client, calendar.CalendarId, "Standalone",
            new DateTime(2026, 8, 10, 9, 0, 0, DateTimeKind.Utc), new DateTime(2026, 8, 10, 10, 0, 0, DateTimeKind.Utc));

        var put = await client.PutAsJsonAsync($"{EventsPath}/{created.CalendarEventId}", new NewCalendarEvent
        {
            CalendarId = calendar.CalendarId,
            Title = "Still standalone",
            StartDateTime = new DateTime(2026, 8, 10, 9, 0, 0, DateTimeKind.Utc),
            EndDateTime = new DateTime(2026, 8, 10, 10, 0, 0, DateTimeKind.Utc),
        });
        put.EnsureSuccessStatusCode();

        var updated = await put.Content.ReadFromJsonAsync<ExistingCalendarEvent>();
        Assert.Null(updated!.RecurrencePatternId);
    }

    [Fact]
    public async Task Delete_ReturnsNoContent()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();

        var calendar = await CreateCalendarAsync(client, "Personal");
        var created = await CreateEventAsync(client, calendar.CalendarId, "Temp",
            new DateTime(2026, 8, 10, 9, 0, 0, DateTimeKind.Utc), new DateTime(2026, 8, 10, 10, 0, 0, DateTimeKind.Utc));

        var delete = await client.DeleteAsync($"{EventsPath}/{created.CalendarEventId}");
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
    }

    private static async Task<ExistingCalendar> CreateCalendarAsync(HttpClient client, string name)
    {
        var post = await client.PostAsJsonAsync(CalendarsPath, new NewCalendar { Name = name, Color = "#0369A1" });
        post.EnsureSuccessStatusCode();
        return (await post.Content.ReadFromJsonAsync<ExistingCalendar>())!;
    }

    private static async Task<ExistingCalendarEvent> CreateEventAsync(
        HttpClient client, Guid calendarId, string title, DateTime start, DateTime end, bool isAllDay = false)
    {
        var post = await client.PostAsJsonAsync(EventsPath, new NewCalendarEvent
        {
            CalendarId = calendarId,
            Title = title,
            StartDateTime = start,
            EndDateTime = end,
            IsAllDay = isAllDay,
        });
        post.EnsureSuccessStatusCode();
        return (await post.Content.ReadFromJsonAsync<ExistingCalendarEvent>())!;
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
