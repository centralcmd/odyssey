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

public class CalendarsApiTests
{
    private const string ActorUserId = "calendars-actor-id";
    private const string Path = "/api/calendars";

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

        var response = await client.GetAsync(Path);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task List_WithoutCalendarClaims_ReturnsForbidden()
    {
        await using var factory = new ApiFactory(permissions: []);
        using var client = factory.CreateClient();

        var response = await client.GetAsync(Path);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Create_Then_List_ReturnsCalendar()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();

        var created = await CreateAsync(client, "Personal", "#0369A1");
        Assert.Equal("Personal", created.Name);
        Assert.Equal("#0369A1", created.Color);

        var calendars = await client.GetPagedItemsAsync<ExistingCalendar>(Path);
        Assert.Contains(calendars, c => c.CalendarId == created.CalendarId && c.Name == "Personal");
    }

    [Fact]
    public async Task Create_DuplicateName_CaseInsensitive_ReturnsConflict()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();

        await CreateAsync(client, "Bills", "#F59E0B");

        var dup = await client.PostAsJsonAsync(Path, new NewCalendar { Name = "BILLS", Color = "#F59E0B" });
        Assert.Equal(HttpStatusCode.Conflict, dup.StatusCode);
    }

    [Fact]
    public async Task Create_ColorOutsidePalette_ReturnsBadRequest()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(Path, new NewCalendar { Name = "Custom", Color = "#123456" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Rename_ToExistingName_ReturnsConflict()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();

        await CreateAsync(client, "Personal", "#0369A1");
        var work = await CreateAsync(client, "Work", "#15803D");

        var put = await client.PutAsJsonAsync($"{Path}/{work.CalendarId}",
            new NewCalendar { Name = "personal", Color = "#15803D" });

        Assert.Equal(HttpStatusCode.Conflict, put.StatusCode);
    }

    [Fact]
    public async Task Delete_EmptyCalendar_ReturnsNoContent()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();

        var created = await CreateAsync(client, "Ephemeral", "#4A5670");

        var delete = await client.DeleteAsync($"{Path}/{created.CalendarId}");
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
    }

    [Fact]
    public async Task Delete_CalendarWithEvents_ReturnsConflict_NotServerError()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();

        var calendar = await CreateAsync(client, "InUse", "#B23B3B");

        var eventResponse = await client.PostAsJsonAsync("/api/calendar-events", new NewCalendarEvent
        {
            CalendarId = calendar.CalendarId,
            Title = "Meeting",
            StartDateTime = new DateTime(2026, 8, 10, 9, 0, 0, DateTimeKind.Utc),
            EndDateTime = new DateTime(2026, 8, 10, 10, 0, 0, DateTimeKind.Utc),
        });
        eventResponse.EnsureSuccessStatusCode();

        var delete = await client.DeleteAsync($"{Path}/{calendar.CalendarId}");
        Assert.Equal(HttpStatusCode.Conflict, delete.StatusCode);
    }

    private static async Task<ExistingCalendar> CreateAsync(HttpClient client, string name, string color)
    {
        var post = await client.PostAsJsonAsync(Path, new NewCalendar { Name = name, Color = color });
        post.EnsureSuccessStatusCode();
        return (await post.Content.ReadFromJsonAsync<ExistingCalendar>())!;
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
