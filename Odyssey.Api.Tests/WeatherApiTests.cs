using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Odyssey.Api.Tests.Infrastructure;
using Odyssey.Context;
using Odyssey.Context.Authorization;
using Odyssey.Core.Weather;
using Odyssey.Dtos;
using Odyssey.Dtos.Application;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos.Journal;
using Xunit;

namespace Odyssey.Api.Tests;

/// <summary>
/// The weather HTTP contract (issue #348 §5; weather points, issue #387 §5 and its ACs). The MET clients are
/// replaced by an in-process stub — the provider mapping is covered in <c>Odyssey.Core.Tests</c> against a
/// recorded response — so nothing here leaves the test host. The purge runs through the tracked purger the
/// Testing environment registers, so its whole success path runs here.
/// </summary>
public class WeatherApiTests
{
    private static readonly string[] Reader = [PermissionClaims.WeatherRead, PermissionClaims.WeatherPreferencesUpdate, PermissionClaims.LocationsRead];

    private static readonly string[] Admin =
    [
        .. Reader,
        PermissionClaims.LocationsCreate,
        PermissionClaims.LocationsUpdate,
        PermissionClaims.WeatherPointsRead,
        PermissionClaims.WeatherPointsUpdate,
        PermissionClaims.WeatherPointsDelete,
        PermissionClaims.SystemSettingsRead,
        PermissionClaims.SystemSettingsUpdate,
        PermissionClaims.CalendarRead,
        PermissionClaims.JournalRead,
    ];

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    private static NewLocation Home(string name = "Home", decimal latitude = 59.91273m) => new()
    {
        Name = name,
        Latitude = latitude,
        Longitude = 10.75027m,
        AltitudeMeters = 12,
        TimeZoneId = "Europe/Oslo",
        Source = LocationSource.Manual,
    };

    private static async Task<ExistingLocation> CreateLocationAsync(HttpClient client, string name = "Home", decimal latitude = 59.91273m)
    {
        var response = await client.PostAsJsonAsync("/api/locations", Home(name, latitude));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ExistingLocation>())!;
    }

    private static NewWeatherPoint NewPoint(string name = "Oslo", bool tracked = false, bool forecast = true, bool history = true) => new()
    {
        Name = name, Latitude = 59.91349m, Longitude = 10.73889m, AltitudeMeters = 23, TimeZoneId = "Europe/Oslo",
        ForecastEnabled = forecast, HistoryEnabled = history, IsTracked = tracked,
    };

    private static async Task<ExistingWeatherPoint> CreatePointAsync(
        HttpClient client, string name = "Oslo", bool tracked = false, bool forecast = true, bool history = true)
    {
        var response = await client.PostAsJsonAsync("/api/weather/points", NewPoint(name, tracked, forecast, history));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ExistingWeatherPoint>(Json))!;
    }

    private static UpdateWeatherPoint Update(ExistingWeatherPoint p, bool? forecast = null, bool? history = null, decimal? latitude = null) => new()
    {
        Name = p.Name, Latitude = latitude ?? p.Site.Latitude, Longitude = p.Site.Longitude, AltitudeMeters = p.Site.AltitudeMeters,
        TimeZoneId = p.Site.TimeZoneId, ForecastEnabled = forecast ?? p.ForecastEnabled, HistoryEnabled = history ?? p.HistoryEnabled,
        IsTracked = p.IsTracked, SettingsVersion = p.SettingsVersion,
    };

    private static Task<HttpResponseMessage> LinkAsync(HttpClient client, Guid locationId, Guid? pointId) =>
        client.PutAsJsonAsync($"/api/locations/{locationId}/weather-point", new SetLocationWeatherPoint { WeatherPointId = pointId });

    // ── Authorization (AC 9, 10) ──────────────────────────────────────────────────────────────────

    public static TheoryData<string, string> EveryEndpoint => new()
    {
        { "GET", "/api/weather/current" },
        { "GET", "/api/weather/hourly?fromUtc=2026-10-23T00:00:00Z&toUtc=2026-10-24T00:00:00Z" },
        { "GET", "/api/weather/hourly?date=2026-10-23" },
        { "GET", "/api/weather/daily?from=2026-10-23&to=2026-10-24" },
        { "GET", $"/api/calendar-events/{Guid.Empty}/weather" },
        { "GET", $"/api/journal-entries/{Guid.Empty}/weather" },
        { "GET", "/api/weather/points" },
        { "GET", $"/api/weather/points/{Guid.Empty}" },
        { "GET", $"/api/weather/points/{Guid.Empty}/linked-locations" },
        { "POST", "/api/weather/points" },
        { "PUT", $"/api/weather/points/{Guid.Empty}" },
        { "POST", $"/api/weather/points/{Guid.Empty}/refresh" },
        { "DELETE", $"/api/weather/points/{Guid.Empty}/history" },
        { "DELETE", $"/api/weather/points/{Guid.Empty}" },
        { "GET", "/api/weather/points/options" },
        { "GET", "/api/weather/preferences" },
        { "PUT", "/api/weather/preferences" },
        { "PUT", $"/api/locations/{Guid.Empty}/weather-point" },
    };

    [Theory]
    [MemberData(nameof(EveryEndpoint))]
    public async Task Every_endpoint_is_403_without_its_claim(string method, string path)
    {
        // Everything except the weather claims, so the 403 can only come from the weather gate.
        await using var factory = new WeatherFactory(
            [PermissionClaims.LocationsRead, PermissionClaims.LocationsUpdate, PermissionClaims.CalendarRead, PermissionClaims.JournalRead]);
        using var client = factory.CreateClient();

        var response = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), path)
        {
            Content = method is "PUT" or "POST" ? JsonContent.Create(new { weatherPointId = (Guid?)null }) : null,
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

    public static TheoryData<string, string> PointEndpoints => new()
    {
        { "GET", "/api/weather/points" },
        { "GET", $"/api/weather/points/{Guid.NewGuid()}" },
        { "GET", $"/api/weather/points/{Guid.NewGuid()}/linked-locations" },
        { "POST", "/api/weather/points" },
        { "PUT", $"/api/weather/points/{Guid.NewGuid()}" },
        { "POST", $"/api/weather/points/{Guid.NewGuid()}/refresh" },
        { "DELETE", $"/api/weather/points/{Guid.NewGuid()}/history" },
        { "DELETE", $"/api/weather/points/{Guid.NewGuid()}" },
    };

    [Theory]
    [MemberData(nameof(PointEndpoints))]
    public async Task Owner_user_and_guest_get_403_on_every_point_endpoint(string method, string path)
    {
        foreach (var role in new[] { RolePermissions.OwnerClaims, RolePermissions.UserClaims, RolePermissions.GuestClaims })
        {
            await using var factory = new WeatherFactory(role);
            var response = await factory.CreateClient().SendAsync(new HttpRequestMessage(new HttpMethod(method), path)
            {
                Content = method is "PUT" or "POST" ? JsonContent.Create(NewPoint()) : null,
            });
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }
    }

    [Fact]
    public async Task Admin_owner_and_user_can_link_and_guest_or_a_caller_without_weather_read_cannot()
    {
        await using var admin = new WeatherFactory(RolePermissions.AdminClaims);
        using var adminClient = admin.CreateClient();
        var point = await CreatePointAsync(adminClient);
        var place = await CreateLocationAsync(adminClient);

        foreach (var (claims, expected) in new[]
        {
            (RolePermissions.AdminClaims, HttpStatusCode.OK),
            (RolePermissions.OwnerClaims, HttpStatusCode.OK),
            (RolePermissions.UserClaims, HttpStatusCode.OK),
            (RolePermissions.GuestClaims, HttpStatusCode.Forbidden),
            ([PermissionClaims.LocationsRead, PermissionClaims.LocationsUpdate], HttpStatusCode.Forbidden),
        })
        {
            // Same in-memory store: the factories share nothing, so each links its own place.
            await using var factory = new WeatherFactory(claims);
            using var client = factory.CreateClient();
            Guid locationId = place.LocationId, pointId = point.WeatherPointId;
            if (expected == HttpStatusCode.OK)
            {
                using var scope = factory.Services.CreateScope();
                var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
                var now = DateTime.UtcNow;
                pointId = Guid.NewGuid();
                locationId = Guid.NewGuid();
                context.WeatherPoints.Add(new WeatherPoint
                {
                    WeatherPointId = pointId, Name = "P", ForecastEnabled = true, CurrentSiteVersion = 1, HistoryFrom = new DateOnly(2025, 1, 1),
                    SettingsVersion = Guid.NewGuid(), EnabledAt = now, CreatedAt = now, UpdatedAt = now,
                });
                context.WeatherPointSites.Add(new WeatherPointSite { WeatherPointId = pointId, SiteVersion = 1, Latitude = 59.9m, Longitude = 10.7m, TimeZoneId = "Europe/Oslo", AppliedAt = now });
                context.Locations.Add(new Location { LocationId = locationId, Name = "L", CreatedAt = now, UpdatedAt = now });
                await context.SaveChangesAsync();
            }

            Assert.Equal(expected, (await LinkAsync(client, locationId, pointId)).StatusCode);
        }
    }

    // ── Create (AC 1–5) ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Create_is_201_with_a_location_header_and_a_rounded_site_one()
    {
        await using var factory = new WeatherFactory(Admin);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/weather/points", NewPoint());

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = (await response.Content.ReadFromJsonAsync<ExistingWeatherPoint>(Json))!;
        Assert.EndsWith($"/api/weather/points/{created.WeatherPointId}", response.Headers.Location!.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal((1, 59.913m, 10.739m), (created.Site.Version, created.Site.Latitude, created.Site.Longitude));
        Assert.Equal(0, created.LinkedLocationCount);
        var page = await client.GetFromJsonAsync<PagedResult<ExistingWeatherPoint>>("/api/weather/points", Json);
        Assert.Equal("Oslo", Assert.Single(page!.Items).Name);
    }

    public static TheoryData<string> BadCreateBodies => new()
    {
        // both sources
        """{"name":"X","latitude":1,"longitude":1,"timeZoneId":"UTC","fromLocationId":"6f1c9b9e-6a7e-4d4c-9b1d-2a3b4c5d6e7f","expectedLocationFingerprint":"x","forecastEnabled":true,"historyEnabled":true,"isTracked":false}""",
        // neither
        """{"name":"X","forecastEnabled":true,"historyEnabled":true,"isTracked":false}""",
        // fromLocationId without a fingerprint
        """{"fromLocationId":"6f1c9b9e-6a7e-4d4c-9b1d-2a3b4c5d6e7f","forecastEnabled":true,"historyEnabled":true,"isTracked":false}""",
        // coordinates without a name
        """{"latitude":1,"longitude":1,"timeZoneId":"UTC","forecastEnabled":true,"historyEnabled":true,"isTracked":false}""",
        // out of range, and a zone that is not IANA
        """{"name":"X","latitude":91,"longitude":1,"timeZoneId":"UTC","forecastEnabled":true,"historyEnabled":true,"isTracked":false}""",
        """{"name":"X","latitude":1,"longitude":1,"timeZoneId":"Not/A_Zone","forecastEnabled":true,"historyEnabled":true,"isTracked":false}""",
    };

    [Theory]
    [MemberData(nameof(BadCreateBodies))]
    public async Task A_malformed_create_is_400(string body)
    {
        await using var factory = new WeatherFactory(Admin);
        var response = await factory.CreateClient().PostAsync("/api/weather/points",
            new StringContent(body, System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Create_from_a_location_copies_the_fingerprinted_position_and_links_nothing()
    {
        await using var factory = new WeatherFactory(Admin);
        using var client = factory.CreateClient();
        var location = await CreateLocationAsync(client, "Storgata 1");
        Assert.Equal(WeatherRules.Fingerprint(59.913m, 10.750m, 12, "Europe/Oslo"), location.WeatherFingerprint);

        var stale = await client.PostAsJsonAsync("/api/weather/points", new NewWeatherPoint
        {
            FromLocationId = location.LocationId, ExpectedLocationFingerprint = "v1:0.000:0.000:-:UTC",
            ForecastEnabled = true, HistoryEnabled = true, IsTracked = false,
        });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        var body = await stale.Content.ReadAsStringAsync();
        Assert.Contains(WeatherConflictException.LocationChanged, body, StringComparison.Ordinal);
        Assert.DoesNotContain("59.9", body, StringComparison.Ordinal);

        var created = await client.PostAsJsonAsync("/api/weather/points", new NewWeatherPoint
        {
            FromLocationId = location.LocationId, ExpectedLocationFingerprint = location.WeatherFingerprint,
            ForecastEnabled = true, HistoryEnabled = true, IsTracked = false,
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var point = (await created.Content.ReadFromJsonAsync<ExistingWeatherPoint>(Json))!;
        Assert.Equal(("Storgata 1", 59.913m, 12), (point.Name, point.Site.Latitude, point.Site.AltitudeMeters!.Value));
        Assert.Null((await client.GetFromJsonAsync<ExistingLocation>($"/api/locations/{location.LocationId}", Json))!.WeatherPoint);
    }

    [Fact]
    public async Task Create_from_a_location_needs_locations_read_and_a_place_with_coordinates()
    {
        await using var noRead = new WeatherFactory([PermissionClaims.WeatherPointsUpdate, PermissionClaims.WeatherPointsRead]);
        var forbidden = await noRead.CreateClient().PostAsJsonAsync("/api/weather/points", new NewWeatherPoint
        {
            FromLocationId = Guid.NewGuid(), ExpectedLocationFingerprint = "x", ForecastEnabled = true, HistoryEnabled = true, IsTracked = false,
        });
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);

        await using var factory = new WeatherFactory(Admin);
        using var client = factory.CreateClient();
        var bare = await client.PostAsJsonAsync("/api/locations", new NewLocation { Name = "Bare", Source = LocationSource.Manual });
        var place = (await bare.Content.ReadFromJsonAsync<ExistingLocation>())!;
        Assert.Null(place.WeatherFingerprint);

        var refused = await client.PostAsJsonAsync("/api/weather/points", new NewWeatherPoint
        {
            FromLocationId = place.LocationId, ExpectedLocationFingerprint = "v1:none", ForecastEnabled = true, HistoryEnabled = true, IsTracked = false,
        });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        var problem = await refused.Content.ReadFromJsonAsync<ValidationProblemDetails>();
        Assert.Contains("fromLocationId", problem!.Errors.Keys, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_duplicate_name_is_409_duplicate_name_and_a_stale_put_is_409_stale()
    {
        await using var factory = new WeatherFactory(Admin);
        using var client = factory.CreateClient();
        var oslo = await CreatePointAsync(client, "Oslo");
        var bergen = await CreatePointAsync(client, "Bergen");

        var duplicate = await client.PostAsJsonAsync("/api/weather/points", NewPoint("OSLO"));
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Contains(WeatherConflictException.DuplicateName, await duplicate.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        var renamed = Update(bergen);
        renamed.Name = "Oslo";
        var onPut = await client.PutAsJsonAsync($"/api/weather/points/{bergen.WeatherPointId}", renamed);
        Assert.Equal(HttpStatusCode.Conflict, onPut.StatusCode);
        Assert.Contains(WeatherConflictException.DuplicateName, await onPut.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"/api/weather/points/{oslo.WeatherPointId}", Update(oslo, history: false))).StatusCode);
        var stale = await client.PutAsJsonAsync($"/api/weather/points/{oslo.WeatherPointId}", Update(oslo, forecast: false));
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Contains("\"stale\"", await stale.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.True((await client.GetFromJsonAsync<ExistingWeatherPoint>($"/api/weather/points/{oslo.WeatherPointId}", Json))!.ForecastEnabled);

        Assert.Equal(HttpStatusCode.NotFound,
            (await client.PutAsJsonAsync($"/api/weather/points/{Guid.NewGuid()}", Update(oslo))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.PutAsJsonAsync($"/api/weather/points/{oslo.WeatherPointId}", new { name = "Oslo" })).StatusCode);
    }

    [Fact]
    public async Task A_move_with_linked_places_warns_and_reports_site_two()
    {
        await using var factory = new WeatherFactory(Admin);
        using var client = factory.CreateClient();
        var point = await CreatePointAsync(client);
        foreach (var name in new[] { "A", "B" })
        {
            var place = await CreateLocationAsync(client, name);
            Assert.Equal(HttpStatusCode.OK, (await LinkAsync(client, place.LocationId, point.WeatherPointId)).StatusCode);
        }

        var moved = await client.PutAsJsonAsync($"/api/weather/points/{point.WeatherPointId}", Update(point, latitude: 60.39299m));

        Assert.Equal(HttpStatusCode.OK, moved.StatusCode);
        using var raw = JsonDocument.Parse(await moved.Content.ReadAsStringAsync());
        Assert.Equal(2, raw.RootElement.GetProperty("point").GetProperty("site").GetProperty("version").GetInt32());
        var warning = Assert.Single(raw.RootElement.GetProperty("warnings").EnumerateArray());
        Assert.Equal("LinkedLocationsFollowMove", warning.GetProperty("code").GetString());
        Assert.Equal(2, warning.GetProperty("count").GetInt32());
    }

    // ── Linked count, linked places (AC 19, endpoint 3) ──────────────────────────────────────────

    [Fact]
    public async Task The_linked_count_and_its_sort_follow_locations_read()
    {
        await using var factory = new WeatherFactory(Admin);
        using var client = factory.CreateClient();
        var point = await CreatePointAsync(client);
        var place = await CreateLocationAsync(client, "Storgata 1", latitude: 59.92m);
        await LinkAsync(client, place.LocationId, point.WeatherPointId);

        var withRead = await client.GetFromJsonAsync<PagedResult<ExistingWeatherPoint>>("/api/weather/points?sortBy=LinkedLocationCount", Json);
        Assert.Equal(1, Assert.Single(withRead!.Items).LinkedLocationCount);
        using var linked = JsonDocument.Parse(await client.GetStringAsync($"/api/weather/points/{point.WeatherPointId}/linked-locations"));
        var only = Assert.Single(linked.RootElement.EnumerateArray());
        Assert.Equal(["locationId", "name", "distanceKm"], only.EnumerateObject().Select(p => p.Name));
        Assert.Equal(JsonValueKind.Number, only.GetProperty("distanceKm").ValueKind);
        Assert.Equal(1, only.GetProperty("distanceKm").GetInt32());

        await using var blind = new WeatherFactory([PermissionClaims.WeatherPointsRead]);
        using var blindClient = blind.CreateClient();
        Assert.Equal(HttpStatusCode.Forbidden, (await blindClient.GetAsync("/api/weather/points?sortBy=LinkedLocationCount")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await blindClient.GetAsync($"/api/weather/points/{point.WeatherPointId}/linked-locations")).StatusCode);
        using (var scope = blind.Services.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
            var now = DateTime.UtcNow;
            var id = Guid.NewGuid();
            context.WeatherPoints.Add(new WeatherPoint
            {
                WeatherPointId = id, Name = "P", CurrentSiteVersion = 1, HistoryFrom = new DateOnly(2025, 1, 1),
                SettingsVersion = Guid.NewGuid(), EnabledAt = now, CreatedAt = now, UpdatedAt = now,
            });
            context.WeatherPointSites.Add(new WeatherPointSite { WeatherPointId = id, SiteVersion = 1, Latitude = 1, Longitude = 1, TimeZoneId = "UTC", AppliedAt = now });
            await context.SaveChangesAsync();
        }

        using var page = JsonDocument.Parse(await blindClient.GetStringAsync("/api/weather/points"));
        Assert.Equal(JsonValueKind.Null, page.RootElement.GetProperty("items")[0].GetProperty("linkedLocationCount").ValueKind);
    }

    // ── Purge and delete (AC 11a, 20, 21) ────────────────────────────────────────────────────────

    [Fact]
    public async Task Purge_and_delete_keep_their_guards_then_204_and_the_places_are_unlinked()
    {
        await using var factory = new WeatherFactory(Admin);
        using var client = factory.CreateClient();
        var point = await CreatePointAsync(client);
        var place = await CreateLocationAsync(client);
        await LinkAsync(client, place.LocationId, point.WeatherPointId);
        Assert.Equal(HttpStatusCode.NoContent,
            (await client.PutAsJsonAsync("/api/weather/preferences", new UpdateWeatherPreferences { DefaultPointId = point.WeatherPointId })).StatusCode);

        var historyOn = await client.DeleteAsync($"/api/weather/points/{point.WeatherPointId}/history");
        Assert.Equal(HttpStatusCode.Conflict, historyOn.StatusCode);
        Assert.Contains(WeatherConflictException.StillEnabled, await historyOn.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        var stillOn = await client.DeleteAsync($"/api/weather/points/{point.WeatherPointId}");
        Assert.Contains(WeatherConflictException.StillEnabled, await stillOn.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync("/api/system-settings",
            new SystemSettingsUpdate { WeatherDefaultPointId = point.WeatherPointId.ToString() })).StatusCode);
        var off = (await (await client.PutAsJsonAsync($"/api/weather/points/{point.WeatherPointId}", Update(point, history: false)))
            .Content.ReadFromJsonAsync<WeatherPointUpdateResult>(Json))!.Point;
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/weather/points/{point.WeatherPointId}/history")).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync("/api/system-settings", new SystemSettingsUpdate { WeatherDefaultPointId = "" })).StatusCode);
        var current = (await client.GetFromJsonAsync<ExistingWeatherPoint>($"/api/weather/points/{point.WeatherPointId}", Json))!;
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"/api/weather/points/{point.WeatherPointId}", Update(current, forecast: false))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/weather/points/{point.WeatherPointId}")).StatusCode);

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/weather/points/{point.WeatherPointId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"/api/weather/points/{point.WeatherPointId}")).StatusCode);
        Assert.Null((await client.GetFromJsonAsync<ExistingLocation>($"/api/locations/{place.LocationId}", Json))!.WeatherPoint);
        Assert.Null((await client.GetFromJsonAsync<WeatherPreferences>("/api/weather/preferences", Json))!.DefaultPoint);
        Assert.NotNull(off);
    }

    [Fact]
    public async Task Deleting_the_household_default_is_409_household_default()
    {
        await using var factory = new WeatherFactory(Admin);
        using var client = factory.CreateClient();
        var point = await CreatePointAsync(client);
        await client.PutAsJsonAsync("/api/system-settings", new SystemSettingsUpdate { WeatherDefaultPointId = point.WeatherPointId.ToString() });
        using (var scope = factory.Services.CreateScope())
        {
            // Switched off behind the API's back: the default rule refuses that through PUT.
            var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
            var row = await context.WeatherPoints.SingleAsync();
            row.ForecastEnabled = false;
            row.HistoryEnabled = false;
            await context.SaveChangesAsync();
        }

        var response = await client.DeleteAsync($"/api/weather/points/{point.WeatherPointId}");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains(WeatherConflictException.HouseholdDefault, await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Refresh_without_a_contact_is_503_weather_not_configured()
    {
        await using var factory = new WeatherFactory(Admin, contact: null);
        using var client = factory.CreateClient();
        var point = await CreatePointAsync(client);

        var response = await client.PostAsync($"/api/weather/points/{point.WeatherPointId}/refresh", null);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains(WeatherNotConfiguredException.ErrorCode, await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(0, factory.Forecasts.Calls);
    }

    [Fact]
    public async Task Refresh_fetches_and_reports_the_issue_time()
    {
        await using var factory = new WeatherFactory(Admin);
        using var client = factory.CreateClient();
        var point = await CreatePointAsync(client, tracked: true);

        var result = await client.PostAsync($"/api/weather/points/{point.WeatherPointId}/refresh", null);

        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        var body = await result.Content.ReadFromJsonAsync<WeatherRefreshResult>(Json);
        Assert.True(body!.Fetched);
        Assert.NotNull(body.IssuedAtUtc);
        Assert.Equal(1, factory.Forecasts.Calls);
    }

    // ── The link (AC 11–14, 18) ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Linking_validates_its_target_and_reports_whole_kilometres()
    {
        await using var factory = new WeatherFactory(Admin);
        using var client = factory.CreateClient();
        var point = await CreatePointAsync(client);
        var far = await CreateLocationAsync(client, "Far", latitude: 64.4m);

        var linked = await LinkAsync(client, far.LocationId, point.WeatherPointId);
        Assert.Equal(HttpStatusCode.OK, linked.StatusCode);
        using (var raw = JsonDocument.Parse(await linked.Content.ReadAsStringAsync()))
        {
            Assert.Equal(JsonValueKind.Number, raw.RootElement.GetProperty("distanceKm").ValueKind);
            Assert.True(raw.RootElement.GetProperty("distanceKm").TryGetInt32(out var km) && km > 450);
            Assert.Equal(0, raw.RootElement.GetProperty("warnings").GetArrayLength());
            Assert.Equal(["weatherPointId", "name"], raw.RootElement.GetProperty("weatherPoint").EnumerateObject().Select(p => p.Name));
        }

        var unknown = await LinkAsync(client, far.LocationId, Guid.NewGuid());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, unknown.StatusCode);
        Assert.Contains("weatherPointId", (await unknown.Content.ReadFromJsonAsync<ValidationProblemDetails>())!.Errors.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(HttpStatusCode.NotFound, (await LinkAsync(client, Guid.NewGuid(), point.WeatherPointId)).StatusCode);

        // The key is required; its value may be null, which clears.
        var keyMissing = await client.PutAsJsonAsync($"/api/locations/{far.LocationId}/weather-point", new { });
        Assert.Equal(HttpStatusCode.BadRequest, keyMissing.StatusCode);
        var cleared = await LinkAsync(client, far.LocationId, null);
        Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);
        Assert.Null((await cleared.Content.ReadFromJsonAsync<LocationWeatherPointLink>(Json))!.WeatherPoint);
    }

    [Fact]
    public async Task A_nested_point_object_changes_no_point_and_a_location_put_changes_no_link()
    {
        await using var factory = new WeatherFactory(Admin);
        using var client = factory.CreateClient();
        var point = await CreatePointAsync(client);
        var other = await CreatePointAsync(client, "Bergen");
        var place = await CreateLocationAsync(client);

        var nested = await client.PutAsJsonAsync($"/api/locations/{place.LocationId}/weather-point", new
        {
            weatherPointId = point.WeatherPointId,
            weatherPoint = new { weatherPointId = point.WeatherPointId, name = "Hijacked", latitude = 1, longitude = 1 },
        });
        Assert.Equal(HttpStatusCode.OK, nested.StatusCode);
        var after = (await client.GetFromJsonAsync<ExistingWeatherPoint>($"/api/weather/points/{point.WeatherPointId}", Json))!;
        Assert.Equal(("Oslo", 59.913m, 1), (after.Name, after.Site.Latitude, after.Site.Version));

        var put = await client.PutAsJsonAsync($"/api/locations/{place.LocationId}", new
        {
            name = place.Name, latitude = place.Latitude, longitude = place.Longitude, altitudeMeters = place.AltitudeMeters,
            timeZoneId = place.TimeZoneId, source = (int)LocationSource.Manual, weatherPointId = other.WeatherPointId,
        });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        Assert.Equal(point.WeatherPointId,
            (await client.GetFromJsonAsync<ExistingLocation>($"/api/locations/{place.LocationId}", Json))!.WeatherPoint!.WeatherPointId);
    }

    [Fact]
    public async Task The_location_read_carries_the_link_only_for_a_weather_read_holder()
    {
        await using var factory = new WeatherFactory(Admin);
        using var client = factory.CreateClient();
        var point = await CreatePointAsync(client);
        var place = await CreateLocationAsync(client);
        await LinkAsync(client, place.LocationId, point.WeatherPointId);

        using var raw = JsonDocument.Parse(await client.GetStringAsync($"/api/locations/{place.LocationId}"));
        var link = raw.RootElement.GetProperty("weatherPoint");
        Assert.Equal(
            ["weatherPointId", "name", "forecastEnabled", "historyEnabled", "hasStoredHistory", "purgeInProgress"],
            link.EnumerateObject().Select(p => p.Name));
        Assert.Equal(JsonValueKind.Number, raw.RootElement.GetProperty("weatherPointDistanceKm").ValueKind);
        Assert.Equal(1, raw.RootElement.GetProperty("weatherPointDistanceKm").GetInt32());

        await using var blind = new WeatherFactory([PermissionClaims.LocationsRead, PermissionClaims.LocationsCreate, PermissionClaims.LocationsUpdate]);
        using var blindClient = blind.CreateClient();
        var mine = await CreateLocationAsync(blindClient);
        using (var scope = blind.Services.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
            var now = DateTime.UtcNow;
            var id = Guid.NewGuid();
            context.WeatherPoints.Add(new WeatherPoint
            {
                WeatherPointId = id, Name = "P", CurrentSiteVersion = 1, HistoryFrom = new DateOnly(2025, 1, 1),
                SettingsVersion = Guid.NewGuid(), EnabledAt = now, CreatedAt = now, UpdatedAt = now,
            });
            context.WeatherPointSites.Add(new WeatherPointSite { WeatherPointId = id, SiteVersion = 1, Latitude = 59.9m, Longitude = 10.7m, TimeZoneId = "UTC", AppliedAt = now });
            (await context.Locations.SingleAsync(l => l.LocationId == mine.LocationId)).WeatherPointId = id;
            await context.SaveChangesAsync();
        }

        using var hidden = JsonDocument.Parse(await blindClient.GetStringAsync($"/api/locations/{mine.LocationId}"));
        Assert.Equal(JsonValueKind.Null, hidden.RootElement.GetProperty("weatherPoint").ValueKind);
        Assert.Equal(JsonValueKind.Null, hidden.RootElement.GetProperty("weatherPointDistanceKm").ValueKind);
    }

    // ── Options (AC 17) ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Options_expose_no_coordinate_site_schedule_or_station_and_nearLocationId_needs_locations_read()
    {
        await using var factory = new WeatherFactory(Admin);
        using var client = factory.CreateClient();
        var point = await CreatePointAsync(client);
        var place = await CreateLocationAsync(client);

        using var plain = JsonDocument.Parse(await client.GetStringAsync("/api/weather/points/options"));
        Assert.Equal(["points", "householdDefaultPointId"], plain.RootElement.EnumerateObject().Select(p => p.Name));
        var entry = Assert.Single(plain.RootElement.GetProperty("points").EnumerateArray());
        Assert.Equal(["weatherPointId", "name", "forecastEnabled", "historyEnabled", "distanceKm"], entry.EnumerateObject().Select(p => p.Name));
        Assert.Equal(JsonValueKind.Null, entry.GetProperty("distanceKm").ValueKind);

        using var near = JsonDocument.Parse(await client.GetStringAsync($"/api/weather/points/options?nearLocationId={place.LocationId}"));
        var nearest = Assert.Single(near.RootElement.GetProperty("points").EnumerateArray());
        Assert.Equal(point.WeatherPointId, nearest.GetProperty("weatherPointId").GetGuid());
        Assert.Equal(JsonValueKind.Number, nearest.GetProperty("distanceKm").ValueKind);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/weather/points/options?nearLocationId={Guid.NewGuid()}")).StatusCode);

        await using var guest = new WeatherFactory([PermissionClaims.WeatherRead]);
        using var guestClient = guest.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await guestClient.GetAsync("/api/weather/points/options")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await guestClient.GetAsync($"/api/weather/points/options?nearLocationId={place.LocationId}")).StatusCode);
    }

    // ── Reads ─────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Current_with_no_place_is_NoPlace_and_an_unknown_point_is_404()
    {
        await using var factory = new WeatherFactory(Reader);
        using var client = factory.CreateClient();

        var current = await client.GetFromJsonAsync<WeatherCurrentResponse>("/api/weather/current", Json);
        Assert.Equal(WeatherStatus.NoPlace, current!.Status);
        Assert.Equal("Weather data from MET Norway", current.Attribution.Text);

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/weather/current?weatherPointId={Guid.NewGuid()}")).StatusCode);
    }

    [Fact]
    public async Task Current_for_an_untracked_point_is_fetched_on_demand()
    {
        await using var factory = new WeatherFactory(Admin);
        using var client = factory.CreateClient();
        var point = await CreatePointAsync(client);

        using var raw = JsonDocument.Parse(await client.GetStringAsync($"/api/weather/current?weatherPointId={point.WeatherPointId}"));

        Assert.Equal("Ok", raw.RootElement.GetProperty("status").GetString());
        Assert.Equal(["weatherPointId", "name"], raw.RootElement.GetProperty("point").EnumerateObject().Select(p => p.Name));
        Assert.Equal(point.WeatherPointId, raw.RootElement.GetProperty("point").GetProperty("weatherPointId").GetGuid());
        Assert.Equal(1, factory.Forecasts.Calls);
    }

    [Theory]
    [InlineData("/api/weather/hourly?fromUtc=2026-10-23T00:00:00Z&toUtc=2026-11-03T00:00:00Z")]
    [InlineData("/api/weather/hourly?fromUtc=2026-10-23T00:00:00Z&toUtc=2026-10-22T00:00:00Z")]
    [InlineData("/api/weather/hourly")]
    [InlineData("/api/weather/daily?from=2025-01-01&to=2026-01-02")]
    [InlineData("/api/weather/daily?from=2026-03-05&to=2026-03-01")]
    public async Task An_invalid_window_is_400(string path)
    {
        await using var factory = new WeatherFactory(Reader);
        Assert.Equal(HttpStatusCode.BadRequest, (await factory.CreateClient().GetAsync(path)).StatusCode);
    }

    [Fact]
    public async Task Preferences_accept_only_a_selectable_point_and_take_no_user_id()
    {
        await using var factory = new WeatherFactory(Admin);
        using var client = factory.CreateClient();
        var off = await CreatePointAsync(client, "Off", forecast: false);
        var home = await CreatePointAsync(client, "Home");

        var refused = await client.PutAsJsonAsync("/api/weather/preferences", new UpdateWeatherPreferences { DefaultPointId = off.WeatherPointId });
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        var problem = await refused.Content.ReadFromJsonAsync<ValidationProblemDetails>();
        Assert.Contains("defaultPointId", problem!.Errors.Keys, StringComparer.OrdinalIgnoreCase);

        Assert.Equal(HttpStatusCode.NoContent,
            (await client.PutAsJsonAsync("/api/weather/preferences", new UpdateWeatherPreferences { DefaultPointId = home.WeatherPointId })).StatusCode);
        using var raw = JsonDocument.Parse(await client.GetStringAsync("/api/weather/preferences"));
        Assert.Equal(["defaultPoint", "householdDefaultPoint"], raw.RootElement.EnumerateObject().Select(p => p.Name));
        Assert.Equal(home.WeatherPointId, raw.RootElement.GetProperty("defaultPoint").GetProperty("weatherPointId").GetGuid());
    }

    // ── Settings ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_household_default_must_be_a_point_with_its_forecast_on()
    {
        await using var factory = new WeatherFactory(Admin);
        using var client = factory.CreateClient();
        var off = await CreatePointAsync(client, "Off", forecast: false);
        var home = await CreatePointAsync(client, "Home");

        var refused = await client.PutAsJsonAsync("/api/system-settings", new SystemSettingsUpdate { WeatherDefaultPointId = off.WeatherPointId.ToString() });
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains(nameof(SystemSettingsUpdate.WeatherDefaultPointId), await refused.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);

        var accepted = await client.PutAsJsonAsync("/api/system-settings", new SystemSettingsUpdate { WeatherDefaultPointId = home.WeatherPointId.ToString() });
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);

        var preferences = await client.GetFromJsonAsync<WeatherPreferences>("/api/weather/preferences", Json);
        Assert.Equal(home.WeatherPointId, preferences!.HouseholdDefaultPoint!.WeatherPointId);
        var options = await client.GetFromJsonAsync<WeatherPointOptions>("/api/weather/points/options", Json);
        Assert.Equal(home.WeatherPointId, options!.HouseholdDefaultPointId);

        // …and its forecast cannot then be switched off.
        var disable = await client.PutAsJsonAsync($"/api/weather/points/{home.WeatherPointId}", Update(home, forecast: false));
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

    // ── Success paths ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Hourly_and_daily_return_the_fetched_forecast()
    {
        await using var factory = new WeatherFactory(Admin);
        using var client = factory.CreateClient();
        var point = await CreatePointAsync(client);
        var from = DateTime.UtcNow;

        var hourly = await client.GetFromJsonAsync<WeatherHourlyResponse>(
            $"/api/weather/hourly?weatherPointId={point.WeatherPointId}&fromUtc={from:yyyy-MM-ddTHH:mm:ssZ}&toUtc={from.AddHours(6):yyyy-MM-ddTHH:mm:ssZ}", Json);
        Assert.Equal(WeatherStatus.Ok, hourly!.Status);
        Assert.InRange(hourly.Steps.Count, 5, 7);
        Assert.All(hourly.Steps, step => Assert.Equal(7.5m, step.AirTemperatureC));

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var daily = await client.GetFromJsonAsync<WeatherDailyResponse>(
            $"/api/weather/daily?weatherPointId={point.WeatherPointId}&from={today.AddDays(-1):yyyy-MM-dd}&to={today.AddDays(3):yyyy-MM-dd}", Json);
        Assert.Equal(WeatherStatus.Ok, daily!.Status);
        Assert.NotEmpty(daily.Days);
        Assert.All(daily.Days, day => Assert.Equal(7.5m, day.Forecast!.MaxTempC));

        // The history bounds travel on every resolved daily response, under the names the client pages by.
        using var raw = JsonDocument.Parse(await client.GetStringAsync(
            $"/api/weather/daily?weatherPointId={point.WeatherPointId}&from={today.AddDays(-60):yyyy-MM-dd}&to={today.AddDays(-1):yyyy-MM-dd}"));
        var history = raw.RootElement.GetProperty("history");
        Assert.Equal(JsonValueKind.Null, history.GetProperty("measuredFrom").ValueKind);
        Assert.Equal(JsonValueKind.Null, history.GetProperty("measuredThrough").ValueKind);
        // Nothing has looked a station up yet, so none is reported missing; Frost is not configured here.
        Assert.Equal(JsonValueKind.True, history.GetProperty("hasObservationStation").ValueKind);
        Assert.Equal(JsonValueKind.False, history.GetProperty("observationsConfigured").ValueKind);
    }

    [Fact]
    public async Task Event_and_entry_weather_at_a_linked_place_name_the_place_and_the_distance()
    {
        await using var factory = new WeatherFactory(Admin);
        using var client = factory.CreateClient();
        var point = await CreatePointAsync(client);
        var place = await CreateLocationAsync(client, "Storgata 1", latitude: 59.92m);
        await LinkAsync(client, place.LocationId, point.WeatherPointId);

        var start = DateTime.UtcNow.AddHours(1);
        var eventId = Guid.NewGuid();
        var entryId = Guid.NewGuid();
        using (var scope = factory.Services.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
            context.CalendarEvents.Add(new CalendarEvent
            {
                CalendarEventId = eventId, CalendarId = Guid.NewGuid(), Title = "Walk", LocationId = place.LocationId,
                StartDateTime = start, EndDateTime = start.AddHours(2),
            });
            context.JournalEntries.Add(new JournalEntry
            {
                JournalEntryId = entryId, ExternalUid = "e1", Title = "Today", Content = "…", EntryDate = DateTime.UtcNow, LocationId = place.LocationId,
            });
            await context.SaveChangesAsync();
        }

        using var forEvent = JsonDocument.Parse(await client.GetStringAsync($"/api/calendar-events/{eventId}/weather"));
        Assert.Equal("Ok", forEvent.RootElement.GetProperty("status").GetString());
        Assert.Equal("Linked", forEvent.RootElement.GetProperty("resolvedFrom").GetString());
        Assert.Equal(point.WeatherPointId, forEvent.RootElement.GetProperty("point").GetProperty("weatherPointId").GetGuid());
        Assert.Equal(["locationId", "name"], forEvent.RootElement.GetProperty("linkedLocation").EnumerateObject().Select(p => p.Name));
        Assert.Equal(JsonValueKind.Number, forEvent.RootElement.GetProperty("distanceKm").ValueKind);
        Assert.Equal(1, forEvent.RootElement.GetProperty("distanceKm").GetInt32());

        var forEntry = await client.GetFromJsonAsync<JournalEntryWeatherResponse>($"/api/journal-entries/{entryId}/weather", Json);
        Assert.Equal(WeatherStatus.Ok, forEntry!.Status);
        Assert.Equal(WeatherResolvedFrom.Linked, forEntry.ResolvedFrom);
        Assert.Equal(place.LocationId, forEntry.LinkedLocation!.LocationId);
        Assert.NotNull(forEntry.Day);
    }

    // ── Rate limits ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Reads_are_limited_per_user()
    {
        await using var factory = new WeatherFactory(Reader, configuration: new Dictionary<string, string?>
        {
            ["RateLimiting:WeatherRead:PermitLimit"] = "2",
            ["RateLimiting:WeatherRead:WindowSeconds"] = "3600",
        });
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/weather/current")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/weather/points/options")).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.GetAsync("/api/weather/preferences")).StatusCode);
    }

    [Fact]
    public async Task Refresh_purge_and_delete_share_one_admin_budget()
    {
        await using var factory = new WeatherFactory(Admin, configuration: new Dictionary<string, string?>
        {
            ["RateLimiting:WeatherRefresh:PermitLimit"] = "2",
            ["RateLimiting:WeatherRefresh:WindowSeconds"] = "3600",
        });
        using var client = factory.CreateClient();
        var point = await CreatePointAsync(client, tracked: true);

        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync($"/api/weather/points/{point.WeatherPointId}/refresh", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.DeleteAsync($"/api/weather/points/{point.WeatherPointId}/history")).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.DeleteAsync($"/api/weather/points/{point.WeatherPointId}")).StatusCode);
    }

    // ── Scheduler ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_scheduler_fetches_only_the_first_places_within_the_cap()
    {
        await using var factory = new WeatherFactory(Admin);
        using var client = factory.CreateClient();
        foreach (var name in new[] { "One", "Two", "Three" })
        {
            await CreatePointAsync(client, name, tracked: true);
        }

        // Lowered below the three already scheduled: only the first two by enable order refresh.
        await factory.SetSystemSettingAsync(SystemSettingsKeys.WeatherMaxTrackedPoints, "2");
        Evict(factory);
        await Scheduler(factory).RunOnceAsync(CancellationToken.None);

        Assert.Equal(2, factory.Forecasts.Calls);
        var rows = (await client.GetFromJsonAsync<PagedResult<ExistingWeatherPoint>>("/api/weather/points", Json))!.Items;
        Assert.Equal(["Three"], rows.Where(r => r.TrackingSuspended).Select(r => r.Name));
    }

    [Fact]
    public async Task The_scheduler_sends_nothing_without_a_contact()
    {
        await using var factory = new WeatherFactory(Admin);
        using var client = factory.CreateClient();
        await CreatePointAsync(client, tracked: true);
        await factory.SetSystemSettingAsync(SystemSettingsKeys.WeatherUserAgentContact, "");
        Evict(factory);

        await Scheduler(factory).RunOnceAsync(CancellationToken.None);

        Assert.Equal(0, factory.Forecasts.Calls);
    }

    [Fact]
    public async Task The_scheduler_makes_no_frost_request_while_the_secret_is_not_set()
    {
        var observations = new CountingObservations();
        await using var factory = new WeatherFactory(Admin, observations: observations);
        using var client = factory.CreateClient();
        await CreatePointAsync(client, tracked: false);

        await Scheduler(factory).RunOnceAsync(CancellationToken.None);

        Assert.Equal(0, observations.Calls);
        Assert.Equal(0, factory.Forecasts.Calls);
    }

    /// <summary>A row written directly bypasses the save path's eviction; the lookup caches for 30 s.</summary>
    private static void Evict(WeatherFactory factory) =>
        factory.Services.GetRequiredService<Microsoft.Extensions.Caching.Memory.IMemoryCache>()
            .Remove(Odyssey.Core.SystemSettings.WeatherSettingsLookup.CacheKey);

    private static WeatherScheduleJob Scheduler(WeatherFactory factory) => new(
        factory.Services.GetRequiredService<IServiceScopeFactory>(),
        TimeProvider.System,
        Microsoft.Extensions.Logging.Abstractions.NullLogger<WeatherScheduleJob>.Instance);

    // ── Settings lookup ───────────────────────────────────────────────────────────────────────────

    public static TheoryData<string, string, string?, int, int> LookupRows => new()
    {
        // contact, interval, default → expected interval, expected cap
        { "ops@example.org", "120", null, 120, 5 },
        { "ops@example.org", "9999", null, 1440, 5 },
        { "ops@example.org", "not-a-number", null, 1440, 5 },
    };

    [Theory]
    [MemberData(nameof(LookupRows))]
    public async Task The_lookup_clamps_and_degrades_the_interval_toward_fewer_requests(
        string contact, string interval, string? unused, int expectedInterval, int expectedCap)
    {
        await using var factory = new WeatherFactory(Reader, contact);
        factory.CreateClient().Dispose();
        await factory.SetSystemSettingAsync(SystemSettingsKeys.WeatherRefreshIntervalMinutes, interval);
        Evict(factory);

        using var scope = factory.Services.CreateScope();
        var settings = await scope.ServiceProvider.GetRequiredService<IWeatherSettingsLookup>().GetAsync();

        Assert.Equal(expectedInterval, settings.RefreshIntervalMinutes);
        Assert.Equal(expectedCap, settings.MaxTrackedPoints);
        Assert.Equal(interval == "not-a-number", settings.IsDegraded);
        Assert.Null(unused);
    }

    [Theory]
    [InlineData("ops@example.org\r\nX-Injected: 1")]
    [InlineData("http://example.org")]
    [InlineData("not an address")]
    public async Task A_stored_contact_the_rule_rejects_reads_as_not_configured(string stored)
    {
        await using var factory = new WeatherFactory(Reader, contact: null);
        factory.CreateClient().Dispose();
        await factory.SetSystemSettingAsync(SystemSettingsKeys.WeatherUserAgentContact, stored);
        Evict(factory);

        using var scope = factory.Services.CreateScope();
        var settings = await scope.ServiceProvider.GetRequiredService<IWeatherSettingsLookup>().GetAsync();

        Assert.Null(settings.Contact);
        Assert.False(settings.IsConfigured);
        Assert.True(settings.IsDegraded);
    }

    [Fact]
    public async Task An_unparseable_household_default_is_none_and_degraded()
    {
        await using var factory = new WeatherFactory(Reader);
        factory.CreateClient().Dispose();
        await factory.SetSystemSettingAsync(SystemSettingsKeys.WeatherDefaultPointId, "not-a-guid");
        Evict(factory);

        using var scope = factory.Services.CreateScope();
        var settings = await scope.ServiceProvider.GetRequiredService<IWeatherSettingsLookup>().GetAsync();

        Assert.Null(settings.HouseholdDefaultPointId);
        Assert.True(settings.IsDegraded);
    }

    private sealed class CountingObservations : IWeatherObservationSource
    {
        public int Calls;

        public Task<StationLookupResult> FindStationAsync(WeatherSiteQuery site, WeatherMeasureGroup group, string contact, string clientId, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(new StationLookupResult(true, null));
        }

        public Task<ObservationFetchResult> FetchDailyAsync(
            IReadOnlyCollection<string> stationIds, IReadOnlyCollection<string> elements, DateOnly from, DateOnly to, string contact, string clientId, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(new ObservationFetchResult(true, []));
        }
    }

    // ── Hours of one day (issue #414) ─────────────────────────────────────────────────────────────

    private static DateOnly OsloToday() =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("Europe/Oslo")));

    [Theory]
    [InlineData("/api/weather/hourly?date=2026-10-23&fromUtc=2026-10-23T00:00:00Z", "date")]
    [InlineData("/api/weather/hourly", "fromUtc")]
    [InlineData("/api/weather/hourly?fromUtc=2026-10-23T00:00:00Z", "toUtc")]
    [InlineData("/api/weather/hourly?date=2026-13-40", "date")]
    [InlineData("/api/weather/hourly?date=2026-1-5", "date")]
    [InlineData("/api/weather/hourly?date=05.11.2026", "date")]
    [InlineData("/api/weather/hourly?date=2026-11-05%0A", "date")]
    [InlineData("/api/weather/hourly?date=%EF%BC%92%EF%BC%90%EF%BC%92%EF%BC%96-%EF%BC%91%EF%BC%91-%EF%BC%90%EF%BC%95", "date")]
    public async Task A_malformed_hourly_query_is_400_keyed_on_the_field_at_fault(string path, string key)
    {
        await using var factory = new WeatherFactory(Reader);
        var response = await factory.CreateClient().GetAsync(path);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ValidationProblemDetails>();
        Assert.Contains(problem!.Errors.Keys, k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Day_mode_returns_the_point_local_day_with_local_times_and_nothing_of_the_site()
    {
        await using var factory = new WeatherFactory(Admin);
        using var client = factory.CreateClient();
        var point = await CreatePointAsync(client);
        var tomorrow = OsloToday().AddDays(1);

        using var raw = JsonDocument.Parse(await client.GetStringAsync(
            $"/api/weather/hourly?weatherPointId={point.WeatherPointId}&date={tomorrow:yyyy-MM-dd}"));
        var root = raw.RootElement;

        Assert.Equal("Ok", root.GetProperty("status").GetString());
        var day = root.GetProperty("day");
        Assert.Equal(tomorrow.ToString("yyyy-MM-dd"), day.GetProperty("date").GetString());
        Assert.False(day.GetProperty("isToday").GetBoolean());
        var fromUtc = day.GetProperty("fromUtc").GetDateTime().ToUniversalTime();
        var toUtc = day.GetProperty("toUtc").GetDateTime().ToUniversalTime();
        Assert.Equal((decimal)(toUtc - fromUtc).TotalHours, day.GetProperty("lengthHours").GetDecimal());

        var steps = root.GetProperty("steps").EnumerateArray().ToList();
        Assert.NotEmpty(steps);
        DateTimeOffset? previousEnd = null;
        foreach (var step in steps)
        {
            var start = step.GetProperty("localStart").GetDateTimeOffset();
            var end = step.GetProperty("localEnd").GetDateTimeOffset();
            Assert.Equal(step.GetProperty("timeUtc").GetDateTime().ToUniversalTime(), start.UtcDateTime);
            Assert.True(end.UtcDateTime > fromUtc && start.UtcDateTime < toUtc);
            Assert.True(previousEnd is null || start >= previousEnd);
            previousEnd = end;
        }

        // The point's clock travels as an offset, never as a zone id or a position (#414 §7.3).
        var text = root.GetRawText();
        foreach (var hidden in new[] { "timeZoneId", "latitude", "longitude", "altitudeMeters", "siteVersion" })
        {
            Assert.DoesNotContain($"\"{hidden}\"", text, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task Day_mode_outside_the_window_is_OutOfRange_with_no_bounds_and_never_fetches()
    {
        await using var factory = new WeatherFactory(Admin);
        using var client = factory.CreateClient();
        var point = await CreatePointAsync(client);
        var today = OsloToday();

        foreach (var date in new[] { today.AddDays(-1), today.AddDays(WeatherRules.DayModeMaxDaysAhead + 1), DateOnly.MinValue, DateOnly.MaxValue })
        {
            var response = await client.GetFromJsonAsync<WeatherHourlyResponse>(
                $"/api/weather/hourly?weatherPointId={point.WeatherPointId}&date={date:yyyy-MM-dd}", Json);
            Assert.Equal(WeatherStatus.OutOfRange, response!.Status);
            Assert.Equal(date, response.Day!.Date);
            Assert.Null(response.Day.FromUtc);
            Assert.Null(response.Day.LengthHours);
            Assert.Empty(response.Steps);
        }

        Assert.Equal(0, factory.Forecasts.Calls);

        var last = await client.GetFromJsonAsync<WeatherHourlyResponse>(
            $"/api/weather/hourly?weatherPointId={point.WeatherPointId}&date={today.AddDays(WeatherRules.DayModeMaxDaysAhead):yyyy-MM-dd}", Json);
        Assert.NotEqual(WeatherStatus.OutOfRange, last!.Status);
    }

    [Fact]
    public async Task Every_other_weather_read_keeps_its_wire_shape()
    {
        await using var factory = new WeatherFactory(Admin);
        using var client = factory.CreateClient();
        var point = await CreatePointAsync(client);
        var place = await CreateLocationAsync(client, "Storgata 1", latitude: 59.92m);
        await LinkAsync(client, place.LocationId, point.WeatherPointId);
        var from = DateTime.UtcNow;

        var start = from.AddHours(1);
        var eventId = Guid.NewGuid();
        var entryId = Guid.NewGuid();
        using (var scope = factory.Services.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
            context.CalendarEvents.Add(new CalendarEvent
            {
                CalendarEventId = eventId, CalendarId = Guid.NewGuid(), Title = "Walk", LocationId = place.LocationId,
                StartDateTime = start, EndDateTime = start.AddHours(2),
            });
            context.JournalEntries.Add(new JournalEntry
            {
                JournalEntryId = entryId, ExternalUid = "e1", Title = "Today", Content = "…", EntryDate = from, LocationId = place.LocationId,
            });
            await context.SaveChangesAsync();
        }

        var range = await client.GetStringAsync(
            $"/api/weather/hourly?weatherPointId={point.WeatherPointId}&fromUtc={from:yyyy-MM-ddTHH:mm:ssZ}&toUtc={from.AddHours(6):yyyy-MM-ddTHH:mm:ssZ}");
        using (var doc = JsonDocument.Parse(range))
        {
            Assert.False(doc.RootElement.TryGetProperty("day", out _));
            Assert.NotEmpty(doc.RootElement.GetProperty("steps").EnumerateArray());
        }

        foreach (var body in new[]
        {
            range,
            await client.GetStringAsync($"/api/weather/current?weatherPointId={point.WeatherPointId}"),
            await client.GetStringAsync($"/api/calendar-events/{eventId}/weather"),
            await client.GetStringAsync($"/api/journal-entries/{entryId}/weather"),
        })
        {
            Assert.DoesNotContain("\"localStart\"", body, StringComparison.Ordinal);
            Assert.DoesNotContain("\"localEnd\"", body, StringComparison.Ordinal);
        }
    }

    private sealed class WeatherFactory : OdysseyApiFactory
    {
        public WeatherFactory(
            IReadOnlyCollection<string> permissions, string? contact = "ops@example.org",
            IReadOnlyDictionary<string, string?>? configuration = null, IWeatherObservationSource? observations = null)
            : this(permissions, contact, new StubForecasts(), configuration, observations ?? new NoObservations())
        {
        }

        private WeatherFactory(
            IReadOnlyCollection<string> permissions, string? contact, StubForecasts forecasts,
            IReadOnlyDictionary<string, string?>? configuration, IWeatherObservationSource observations)
            : base(permissions, "weather-actor", configuration, services =>
            {
                services.RemoveAll<IWeatherForecastSource>();
                services.AddSingleton<IWeatherForecastSource>(forecasts);
                services.RemoveAll<IWeatherObservationSource>();
                services.AddSingleton(observations);
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
        public Task<StationLookupResult> FindStationAsync(WeatherSiteQuery site, WeatherMeasureGroup group, string contact, string clientId, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("No read may call Frost.");

        public Task<ObservationFetchResult> FetchDailyAsync(
            IReadOnlyCollection<string> stationIds, IReadOnlyCollection<string> elements, DateOnly from, DateOnly to, string contact, string clientId, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("No read may call Frost.");
    }
}
