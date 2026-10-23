using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Odyssey.Api.Tests.Infrastructure;
using Odyssey.Context;
using Odyssey.Core.Weather;
using Odyssey.Dtos;
using Odyssey.Dtos.Application;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos.Journal;
using Xunit;

namespace Odyssey.Api.Tests;

/// <summary>
/// The weather HTTP contract (issue #348 §5, AC 10 and the endpoint ACs). The MET clients are replaced by
/// an in-process stub — the provider mapping is covered in <c>Odyssey.Core.Tests</c> against a recorded
/// response — so nothing here leaves the test host. The purge runs through the tracked purger the Testing
/// environment registers, so its whole success path runs here.
/// </summary>
public class WeatherApiTests
{
    private static readonly string[] Reader = [PermissionClaims.WeatherRead, PermissionClaims.WeatherPreferencesUpdate, PermissionClaims.LocationsRead];

    private static readonly string[] Admin =
    [
        .. Reader,
        PermissionClaims.LocationsCreate,
        PermissionClaims.LocationsUpdate,
        PermissionClaims.WeatherLocationsRead,
        PermissionClaims.WeatherLocationsUpdate,
        PermissionClaims.WeatherHistoryDelete,
        PermissionClaims.SystemSettingsRead,
        PermissionClaims.SystemSettingsUpdate,
        PermissionClaims.CalendarRead,
        PermissionClaims.JournalRead,
    ];

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    private static NewLocation Home(string name = "Home") => new()
    {
        Name = name,
        Latitude = 59.91273m,
        Longitude = 10.75027m,
        AltitudeMeters = 12,
        TimeZoneId = "Europe/Oslo",
        Source = LocationSource.Manual,
    };

    private static async Task<ExistingLocation> CreateLocationAsync(HttpClient client, string name = "Home")
    {
        var response = await client.PostAsJsonAsync("/api/locations", Home(name));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ExistingLocation>())!;
    }

    private static string Fingerprint(ExistingLocation location) => WeatherCoordinates.Fingerprint(new Location
    {
        LocationId = location.LocationId,
        Name = location.Name,
        Latitude = location.Latitude,
        Longitude = location.Longitude,
        AltitudeMeters = location.AltitudeMeters,
        TimeZoneId = location.TimeZoneId,
    });

    private static async Task<HttpResponseMessage> EnableAsync(HttpClient client, ExistingLocation location, bool tracked = false) =>
        await client.PutAsJsonAsync($"/api/weather/locations/{location.LocationId}", new UpsertWeatherLocation
        {
            Enabled = true,
            IsTracked = tracked,
            ExpectedLocationFingerprint = Fingerprint(location),
        });

    // ── Authorization ─────────────────────────────────────────────────────────────────────────────

    public static TheoryData<string, string> EveryEndpoint => new()
    {
        { "GET", "/api/weather/current" },
        { "GET", "/api/weather/hourly?fromUtc=2026-10-23T00:00:00Z&toUtc=2026-10-24T00:00:00Z" },
        { "GET", "/api/weather/daily?from=2026-10-23&to=2026-10-24" },
        { "GET", $"/api/calendar-events/{Guid.Empty}/weather" },
        { "GET", $"/api/journal-entries/{Guid.Empty}/weather" },
        { "GET", "/api/weather/locations" },
        { "PUT", $"/api/weather/locations/{Guid.Empty}" },
        { "POST", $"/api/weather/locations/{Guid.Empty}/apply-location" },
        { "POST", $"/api/weather/locations/{Guid.Empty}/refresh" },
        { "DELETE", $"/api/weather/locations/{Guid.Empty}" },
        { "GET", "/api/weather/locations/options" },
        { "GET", "/api/weather/preferences" },
        { "PUT", "/api/weather/preferences" },
    };

    [Theory]
    [MemberData(nameof(EveryEndpoint))]
    public async Task Every_endpoint_is_403_without_its_claim(string method, string path)
    {
        // Everything except the weather claims, so the 403 can only come from the weather gate.
        await using var factory = new WeatherFactory([PermissionClaims.LocationsRead, PermissionClaims.CalendarRead, PermissionClaims.JournalRead]);
        using var client = factory.CreateClient();

        var response = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), path)
        {
            Content = method is "PUT" or "POST" ? JsonContent.Create(new { }) : null,
        });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData("/api/calendar-events/{0}/weather", PermissionClaims.CalendarRead)]
    [InlineData("/api/journal-entries/{0}/weather", PermissionClaims.JournalRead)]
    public async Task Event_and_entry_weather_need_both_claims(string template, string moduleClaim)
    {
        await using var weatherOnly = new WeatherFactory([PermissionClaims.WeatherRead]);
        Assert.Equal(HttpStatusCode.Forbidden, (await weatherOnly.CreateClient().GetAsync(string.Format(template, Guid.NewGuid()))).StatusCode);

        await using var moduleOnly = new WeatherFactory([moduleClaim]);
        Assert.Equal(HttpStatusCode.Forbidden, (await moduleOnly.CreateClient().GetAsync(string.Format(template, Guid.NewGuid()))).StatusCode);

        await using var both = new WeatherFactory([moduleClaim, PermissionClaims.WeatherRead]);
        Assert.Equal(HttpStatusCode.NotFound, (await both.CreateClient().GetAsync(string.Format(template, Guid.NewGuid()))).StatusCode);
    }

    [Fact]
    public async Task A_reader_cannot_reach_the_admin_endpoints()
    {
        await using var factory = new WeatherFactory(Reader);
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/weather/locations")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.DeleteAsync($"/api/weather/locations/{Guid.NewGuid()}")).StatusCode);
    }

    // ── Admin ─────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Enable_is_201_then_200_and_lists_the_row_with_a_coarse_site()
    {
        await using var factory = new WeatherFactory(Admin);
        using var client = factory.CreateClient();
        var location = await CreateLocationAsync(client);

        var created = await EnableAsync(client, location);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var again = await EnableAsync(client, location);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);

        var page = await client.GetFromJsonAsync<PagedResult<ExistingWeatherLocation>>("/api/weather/locations", Json);
        var row = Assert.Single(page!.Items);
        Assert.Equal("Home", row.Name);
        Assert.Equal(59.913m, row.Site.Latitude);
        Assert.Equal(10.750m, row.Site.Longitude);
        Assert.False(row.LocationMoved);
        Assert.Equal(Fingerprint(location), row.LocationFingerprint);
    }

    [Fact]
    public async Task A_stale_review_is_a_409_carrying_its_code_and_no_coordinates()
    {
        await using var factory = new WeatherFactory(Admin);
        using var client = factory.CreateClient();
        var location = await CreateLocationAsync(client);

        var response = await client.PutAsJsonAsync($"/api/weather/locations/{location.LocationId}", new UpsertWeatherLocation
        {
            Enabled = true,
            IsTracked = false,
            ExpectedLocationFingerprint = "v1:0.000:0.000:-:UTC",
        });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains(WeatherConflictException.LocationChanged, body, StringComparison.Ordinal);
        Assert.DoesNotContain("59.9", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Enable_of_an_unknown_location_is_404_and_a_missing_flag_is_400()
    {
        await using var factory = new WeatherFactory(Admin);
        using var client = factory.CreateClient();

        var unknown = await client.PutAsJsonAsync($"/api/weather/locations/{Guid.NewGuid()}",
            new UpsertWeatherLocation { Enabled = true, IsTracked = false, ExpectedLocationFingerprint = "x" });
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);

        var location = await CreateLocationAsync(client);
        var missing = await client.PutAsJsonAsync($"/api/weather/locations/{location.LocationId}", new { isTracked = false });
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
    }

    [Fact]
    public async Task Purge_is_409_while_enabled_then_204_and_the_location_survives()
    {
        await using var factory = new WeatherFactory(Admin);
        using var client = factory.CreateClient();
        var location = await CreateLocationAsync(client);
        await EnableAsync(client, location);

        Assert.Equal(HttpStatusCode.Conflict, (await client.DeleteAsync($"/api/weather/locations/{location.LocationId}")).StatusCode);

        await client.PutAsJsonAsync($"/api/weather/locations/{location.LocationId}", new UpsertWeatherLocation { Enabled = false, IsTracked = false });
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/weather/locations/{location.LocationId}")).StatusCode);

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/weather/locations/{location.LocationId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"/api/weather/locations/{location.LocationId}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/locations/{location.LocationId}")).StatusCode);
    }

    [Fact]
    public async Task Refresh_without_a_contact_is_503_weather_not_configured()
    {
        await using var factory = new WeatherFactory(Admin, contact: null);
        using var client = factory.CreateClient();
        var location = await CreateLocationAsync(client);
        await EnableAsync(client, location);

        var response = await client.PostAsync($"/api/weather/locations/{location.LocationId}/refresh", null);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains(WeatherNotConfiguredException.ErrorCode, await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(0, factory.Forecasts.Calls);
    }

    [Fact]
    public async Task Refresh_fetches_and_reports_the_issue_time()
    {
        await using var factory = new WeatherFactory(Admin);
        using var client = factory.CreateClient();
        var location = await CreateLocationAsync(client);
        await EnableAsync(client, location, tracked: true);

        var result = await client.PostAsync($"/api/weather/locations/{location.LocationId}/refresh", null);

        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        var body = await result.Content.ReadFromJsonAsync<WeatherRefreshResult>(Json);
        Assert.True(body!.Fetched);
        Assert.NotNull(body.IssuedAtUtc);
        Assert.Equal(1, factory.Forecasts.Calls);
    }

    [Fact]
    public async Task Apply_with_nothing_to_apply_is_409()
    {
        await using var factory = new WeatherFactory(Admin);
        using var client = factory.CreateClient();
        var location = await CreateLocationAsync(client);
        await EnableAsync(client, location);

        var response = await client.PostAsJsonAsync($"/api/weather/locations/{location.LocationId}/apply-location",
            new ApplyWeatherLocation { ExpectedLocationFingerprint = Fingerprint(location) });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains(WeatherConflictException.NothingToApply, await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    // ── Reads ─────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Current_with_no_place_is_NoPlace_and_an_unknown_one_is_404()
    {
        await using var factory = new WeatherFactory(Reader);
        using var client = factory.CreateClient();

        var current = await client.GetFromJsonAsync<WeatherCurrentResponse>("/api/weather/current", Json);
        Assert.Equal(WeatherStatus.NoPlace, current!.Status);
        Assert.Equal("Weather data from MET Norway", current.Attribution.Text);

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/weather/current?locationId={Guid.NewGuid()}")).StatusCode);
    }

    [Fact]
    public async Task Current_for_an_untracked_place_is_fetched_on_demand()
    {
        await using var factory = new WeatherFactory(Admin);
        using var client = factory.CreateClient();
        var location = await CreateLocationAsync(client);
        await EnableAsync(client, location);

        var current = await client.GetFromJsonAsync<WeatherCurrentResponse>($"/api/weather/current?locationId={location.LocationId}", Json);

        Assert.Equal(WeatherStatus.Ok, current!.Status);
        Assert.Equal(location.LocationId, current.Location!.LocationId);
        Assert.NotNull(current.Step);
        Assert.Equal(1, factory.Forecasts.Calls);
    }

    [Theory]
    [InlineData("/api/weather/hourly?fromUtc=2026-10-23T00:00:00Z&toUtc=2026-11-03T00:00:00Z")]
    [InlineData("/api/weather/hourly?fromUtc=2026-10-23T00:00:00Z&toUtc=2026-10-22T00:00:00Z")]
    [InlineData("/api/weather/hourly")]
    [InlineData("/api/weather/daily?from=2026-01-01&to=2026-03-05")]
    [InlineData("/api/weather/daily?from=2026-03-05&to=2026-03-01")]
    public async Task An_invalid_window_is_400(string path)
    {
        await using var factory = new WeatherFactory(Reader);
        Assert.Equal(HttpStatusCode.BadRequest, (await factory.CreateClient().GetAsync(path)).StatusCode);
    }

    [Fact]
    public async Task Preferences_accept_only_a_selectable_place_and_take_no_user_id()
    {
        await using var factory = new WeatherFactory(Admin);
        using var client = factory.CreateClient();
        var bare = await CreateLocationAsync(client, "Bare");
        var home = await CreateLocationAsync(client, "Home");
        await EnableAsync(client, home);

        var refused = await client.PutAsJsonAsync("/api/weather/preferences", new UpdateWeatherPreferences { DefaultLocationId = bare.LocationId });
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        var problem = await refused.Content.ReadFromJsonAsync<ValidationProblemDetails>();
        Assert.Contains("defaultLocationId", problem!.Errors.Keys, StringComparer.OrdinalIgnoreCase);

        Assert.Equal(HttpStatusCode.NoContent,
            (await client.PutAsJsonAsync("/api/weather/preferences", new UpdateWeatherPreferences { DefaultLocationId = home.LocationId })).StatusCode);
        var preferences = await client.GetFromJsonAsync<WeatherPreferences>("/api/weather/preferences", Json);
        Assert.Equal(home.LocationId, preferences!.DefaultLocation!.LocationId);

        var options = await client.GetFromJsonAsync<WeatherLocationOptions>("/api/weather/locations/options", Json);
        Assert.Equal([home.LocationId], options!.Locations.Select(l => l.LocationId));
    }

    // ── Settings ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_household_default_must_have_weather_on()
    {
        await using var factory = new WeatherFactory(Admin);
        using var client = factory.CreateClient();
        var bare = await CreateLocationAsync(client, "Bare");
        var home = await CreateLocationAsync(client, "Home");
        await EnableAsync(client, home);

        var refused = await client.PutAsJsonAsync("/api/system-settings", new SystemSettingsUpdate { WeatherDefaultLocationId = bare.LocationId.ToString() });
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains(nameof(SystemSettingsUpdate.WeatherDefaultLocationId), await refused.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);

        var accepted = await client.PutAsJsonAsync("/api/system-settings", new SystemSettingsUpdate { WeatherDefaultLocationId = home.LocationId.ToString() });
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);

        var preferences = await client.GetFromJsonAsync<WeatherPreferences>("/api/weather/preferences", Json);
        Assert.Equal(home.LocationId, preferences!.HouseholdDefaultLocation!.LocationId);

        // …and it cannot then be switched off.
        var disable = await client.PutAsJsonAsync($"/api/weather/locations/{home.LocationId}", new UpsertWeatherLocation { Enabled = false, IsTracked = false });
        Assert.Equal(HttpStatusCode.Conflict, disable.StatusCode);
    }

    [Theory]
    [InlineData("not an address")]
    [InlineData("http://example.org/contact")]
    public async Task An_unusable_contact_is_refused(string contact)
    {
        await using var factory = new WeatherFactory(Admin);
        var response = await factory.CreateClient().PutAsJsonAsync("/api/system-settings", new SystemSettingsUpdate { WeatherUserAgentContact = contact });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private sealed class WeatherFactory : OdysseyApiFactory
    {
        public WeatherFactory(IReadOnlyCollection<string> permissions, string? contact = "ops@example.org")
            : this(permissions, contact, new StubForecasts())
        {
        }

        private WeatherFactory(IReadOnlyCollection<string> permissions, string? contact, StubForecasts forecasts)
            : base(permissions, "weather-actor", configureServices: services =>
            {
                services.RemoveAll<IWeatherForecastSource>();
                services.AddSingleton<IWeatherForecastSource>(forecasts);
                services.RemoveAll<IWeatherObservationSource>();
                services.AddSingleton<IWeatherObservationSource, NoObservations>();
            })
        {
            Forecasts = forecasts;
            Contact = contact;
        }

        public StubForecasts Forecasts { get; }

        private string? Contact { get; }

        public new HttpClient CreateClient()
        {
            var client = base.CreateClient();
            if (Contact is not null && !contactSet)
            {
                SetSystemSettingAsync(SystemSettingsKeys.WeatherUserAgentContact, Contact).GetAwaiter().GetResult();
                contactSet = true;
            }

            return client;
        }

        private bool contactSet;
    }

    private sealed class StubForecasts : IWeatherForecastSource
    {
        private int calls;

        public int Calls => calls;

        public Task<ForecastFetchResult> FetchAsync(
            WeatherSiteQuery site, string contact, string? ifModifiedSince, TimeSpan budget, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref calls);
            var now = DateTime.UtcNow;
            var from = new DateTime(now.Year, now.Month, now.Day, now.Hour, 0, 0, DateTimeKind.Utc).AddHours(-1);
            var steps = Enumerable.Range(0, 48).Select(h => new ForecastStepData(
                from.AddHours(h), 1, 7.5m, 70, 50, 1010, 3, 6, 180, 0.5m, "cloudy", 0.2m, 20, null, null)).ToList();
            return Task.FromResult(new ForecastFetchResult(WeatherFetchOutcome.Ok, new ForecastPayload(now.AddMinutes(-10), steps),
                ExpiresUtc: now.AddMinutes(30), LastModified: "Fri, 23 Oct 2026 11:30:00 GMT"));
        }
    }

    private sealed class NoObservations : IWeatherObservationSource
    {
        public Task<StationLookupResult> FindStationAsync(WeatherSiteQuery site, string contact, string clientId, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("No read may call Frost.");

        public Task<ObservationFetchResult> FetchDailyAsync(
            string stationId, DateOnly from, DateOnly to, string contact, string clientId, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("No read may call Frost.");
    }
}
