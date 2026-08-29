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

public class RecurrencePatternsApiTests
{
    private const string ActorUserId = "recurrence-patterns-actor-id";
    private const string CalendarsPath = "/api/calendars";
    private const string PatternsPath = "/api/recurrence-patterns";

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

        var response = await client.GetAsync(PatternsPath);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task List_WithoutCalendarClaims_ReturnsForbidden()
    {
        await using var factory = new ApiFactory(permissions: []);
        using var client = factory.CreateClient();

        var response = await client.GetAsync(PatternsPath);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Create_Weekly_GeneratesExactOccurrenceCount()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();

        var calendar = await CreateCalendarAsync(client, "Family");

        // Every 2 weeks on Monday and Thursday, 6 occurrences total.
        var created = await CreatePatternAsync(client, new NewRecurrencePattern
        {
            CalendarId = calendar.CalendarId,
            Title = "Standup",
            StartDateTime = new DateTime(2026, 8, 3, 10, 0, 0, DateTimeKind.Utc), // Monday
            EndDateTime = new DateTime(2026, 8, 3, 10, 30, 0, DateTimeKind.Utc),
            Frequency = RecurrenceFrequency.Weekly,
            Interval = 2,
            DaysOfWeek = DaysOfWeekFlags.Monday | DaysOfWeekFlags.Thursday,
            OccurrenceCount = 6,
        });

        Assert.Equal(6, created.GeneratedEventCount);

        var events = await client.GetPagedItemsAsync<ExistingCalendarEvent>($"{PatternsPath}/{created.RecurrencePatternId}/events");
        Assert.Equal(6, events.Count);
        Assert.All(events, e => Assert.Equal(created.RecurrencePatternId, e.RecurrencePatternId));

        var dates = events.OrderBy(e => e.StartDateTime).Select(e => e.StartDateTime.Date).ToList();
        List<DateTime> expected =
        [
            new(2026, 8, 3), new(2026, 8, 6),
            new(2026, 8, 17), new(2026, 8, 20),
            new(2026, 8, 31), new(2026, 9, 3),
        ];
        Assert.Equal(expected, dates);
    }

    [Fact]
    public async Task Create_MonthlyDayOfMonth31_ClampsToLastDayOfShorterMonth()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();

        var calendar = await CreateCalendarAsync(client, "Bills");

        var created = await CreatePatternAsync(client, new NewRecurrencePattern
        {
            CalendarId = calendar.CalendarId,
            Title = "Rent",
            StartDateTime = new DateTime(2026, 1, 31, 9, 0, 0, DateTimeKind.Utc),
            EndDateTime = new DateTime(2026, 1, 31, 10, 0, 0, DateTimeKind.Utc),
            Frequency = RecurrenceFrequency.Monthly,
            Interval = 1,
            DayOfMonth = 31,
            OccurrenceCount = 2,
        });

        var events = await client.GetPagedItemsAsync<ExistingCalendarEvent>($"{PatternsPath}/{created.RecurrencePatternId}/events");
        var second = events.OrderBy(e => e.StartDateTime).Skip(1).First();

        Assert.Equal(2, second.StartDateTime.Month);
        Assert.Equal(28, second.StartDateTime.Day); // 2026 is not a leap year
    }

    [Fact]
    public async Task Create_TemplateDurationOver366Days_ReturnsBadRequest()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();

        var calendar = await CreateCalendarAsync(client, "Personal");

        var response = await client.PostAsJsonAsync(PatternsPath, new NewRecurrencePattern
        {
            CalendarId = calendar.CalendarId,
            Title = "Too long",
            StartDateTime = new DateTime(2026, 8, 3, 9, 0, 0, DateTimeKind.Utc),
            EndDateTime = new DateTime(2028, 8, 3, 9, 0, 0, DateTimeKind.Utc),
            Frequency = RecurrenceFrequency.Daily,
            Interval = 1,
            OccurrenceCount = 3,
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Create_MissingBothEndConditions_ReturnsBadRequest()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();

        var calendar = await CreateCalendarAsync(client, "Personal");

        var response = await client.PostAsJsonAsync(PatternsPath, new NewRecurrencePattern
        {
            CalendarId = calendar.CalendarId,
            Title = "Never-ending",
            StartDateTime = new DateTime(2026, 8, 3, 9, 0, 0, DateTimeKind.Utc),
            EndDateTime = new DateTime(2026, 8, 3, 10, 0, 0, DateTimeKind.Utc),
            Frequency = RecurrenceFrequency.Daily,
            Interval = 1,
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Create_BothEndConditions_ReturnsBadRequest()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();

        var calendar = await CreateCalendarAsync(client, "Personal");

        var response = await client.PostAsJsonAsync(PatternsPath, new NewRecurrencePattern
        {
            CalendarId = calendar.CalendarId,
            Title = "Ambiguous",
            StartDateTime = new DateTime(2026, 8, 3, 9, 0, 0, DateTimeKind.Utc),
            EndDateTime = new DateTime(2026, 8, 3, 10, 0, 0, DateTimeKind.Utc),
            Frequency = RecurrenceFrequency.Daily,
            Interval = 1,
            OccurrenceCount = 5,
            RecurrenceEndDate = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc),
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Create_ProjectedOccurrencesExceedCap_ReturnsBadRequest()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();

        var calendar = await CreateCalendarAsync(client, "Personal");

        var response = await client.PostAsJsonAsync(PatternsPath, new NewRecurrencePattern
        {
            CalendarId = calendar.CalendarId,
            Title = "Daily forever-ish",
            StartDateTime = new DateTime(2026, 1, 1, 9, 0, 0, DateTimeKind.Utc),
            EndDateTime = new DateTime(2026, 1, 1, 9, 30, 0, DateTimeKind.Utc),
            Frequency = RecurrenceFrequency.Daily,
            Interval = 1,
            RecurrenceEndDate = new DateTime(2029, 6, 1, 0, 0, 0, DateTimeKind.Utc), // ~1200 days
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Update_RegeneratesFutureEventsOnly_LeavesPastUntouched()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();

        var calendar = await CreateCalendarAsync(client, "Family");
        var now = factory.TimeProvider.GetUtcNow().UtcDateTime;

        var created = await CreatePatternAsync(client, new NewRecurrencePattern
        {
            CalendarId = calendar.CalendarId,
            Title = "Weekly check-in",
            StartDateTime = now.AddDays(-24),
            EndDateTime = now.AddDays(-24).AddMinutes(30),
            Frequency = RecurrenceFrequency.Weekly,
            Interval = 1,
            DaysOfWeek = ToFlag(now.AddDays(-24).DayOfWeek),
            OccurrenceCount = 8, // spans well before and after "now"
        });

        var beforeUpdate = await client.GetPagedItemsAsync<ExistingCalendarEvent>($"{PatternsPath}/{created.RecurrencePatternId}/events");
        var pastEventIds = beforeUpdate.Where(e => e.StartDateTime < now).Select(e => e.CalendarEventId).ToHashSet();
        Assert.NotEmpty(pastEventIds);

        var put = await client.PutAsJsonAsync($"{PatternsPath}/{created.RecurrencePatternId}", new NewRecurrencePattern
        {
            CalendarId = calendar.CalendarId,
            Title = "Weekly check-in (renamed)",
            StartDateTime = now.AddDays(-24),
            EndDateTime = now.AddDays(-24).AddMinutes(30),
            Frequency = RecurrenceFrequency.Weekly,
            Interval = 1,
            DaysOfWeek = ToFlag(now.AddDays(-24).DayOfWeek),
            OccurrenceCount = 8,
        });
        put.EnsureSuccessStatusCode();

        var afterUpdate = await client.GetPagedItemsAsync<ExistingCalendarEvent>($"{PatternsPath}/{created.RecurrencePatternId}/events");

        // Past occurrences are untouched (same ids, same title).
        foreach (var pastId in pastEventIds)
        {
            var stillThere = afterUpdate.Single(e => e.CalendarEventId == pastId);
            Assert.Equal("Weekly check-in", stillThere.Title);
        }

        // Future occurrences reflect the new title.
        Assert.Contains(afterUpdate, e => e.StartDateTime >= now && e.Title == "Weekly check-in (renamed)");
    }

    [Fact]
    public async Task Delete_HardDeletesFutureEvents_DetachesPastEvents()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();

        var calendar = await CreateCalendarAsync(client, "Family");
        var now = factory.TimeProvider.GetUtcNow().UtcDateTime;

        var created = await CreatePatternAsync(client, new NewRecurrencePattern
        {
            CalendarId = calendar.CalendarId,
            Title = "Weekly check-in",
            StartDateTime = now.AddDays(-24),
            EndDateTime = now.AddDays(-24).AddMinutes(30),
            Frequency = RecurrenceFrequency.Weekly,
            Interval = 1,
            DaysOfWeek = ToFlag(now.AddDays(-24).DayOfWeek),
            OccurrenceCount = 8,
        });

        var beforeDelete = await client.GetPagedItemsAsync<ExistingCalendarEvent>($"{PatternsPath}/{created.RecurrencePatternId}/events");
        var pastIds = beforeDelete.Where(e => e.StartDateTime < now).Select(e => e.CalendarEventId).ToList();
        var futureIds = beforeDelete.Where(e => e.StartDateTime >= now).Select(e => e.CalendarEventId).ToList();
        Assert.NotEmpty(pastIds);
        Assert.NotEmpty(futureIds);

        var delete = await client.DeleteAsync($"{PatternsPath}/{created.RecurrencePatternId}");
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);

        foreach (var futureId in futureIds)
        {
            var response = await client.GetAsync($"/api/calendar-events/{futureId}");
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }

        foreach (var pastId in pastIds)
        {
            var response = await client.GetAsync($"/api/calendar-events/{pastId}");
            response.EnsureSuccessStatusCode();
            var stillThere = await response.Content.ReadFromJsonAsync<ExistingCalendarEvent>();
            Assert.Null(stillThere!.RecurrencePatternId);
        }
    }

    private static DaysOfWeekFlags ToFlag(DayOfWeek day) => day switch
    {
        DayOfWeek.Monday => DaysOfWeekFlags.Monday,
        DayOfWeek.Tuesday => DaysOfWeekFlags.Tuesday,
        DayOfWeek.Wednesday => DaysOfWeekFlags.Wednesday,
        DayOfWeek.Thursday => DaysOfWeekFlags.Thursday,
        DayOfWeek.Friday => DaysOfWeekFlags.Friday,
        DayOfWeek.Saturday => DaysOfWeekFlags.Saturday,
        _ => DaysOfWeekFlags.Sunday,
    };

    private static async Task<ExistingCalendar> CreateCalendarAsync(HttpClient client, string name)
    {
        var post = await client.PostAsJsonAsync(CalendarsPath, new NewCalendar { Name = name, Color = "#0369A1" });
        post.EnsureSuccessStatusCode();
        return (await post.Content.ReadFromJsonAsync<ExistingCalendar>())!;
    }

    private static async Task<ExistingRecurrencePattern> CreatePatternAsync(HttpClient client, NewRecurrencePattern request)
    {
        var post = await client.PostAsJsonAsync(PatternsPath, request);
        post.EnsureSuccessStatusCode();
        return (await post.Content.ReadFromJsonAsync<ExistingRecurrencePattern>())!;
    }

    private sealed class ApiFactory : OdysseyApiFactory
    {
        // The server resolves TimeProvider.System too (no override registered here), so this is the
        // same clock the RecurrencePatternService instances inside the test host use for "now".
        public readonly TimeProvider TimeProvider = TimeProvider.System;

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
