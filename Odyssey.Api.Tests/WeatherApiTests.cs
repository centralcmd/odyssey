using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
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
        { "GET", $"/api/calendar-events/weather?ids={Guid.Empty}" },
        { "GET", $"/api/journal-entries/weather?ids={Guid.Empty}" },
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
                    WeatherPointId = pointId, Name = "P", ForecastEnabled = true, CurrentSiteVersion = 1,
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
                WeatherPointId = id, Name = "P", CurrentSiteVersion = 1,
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
                WeatherPointId = id, Name = "P", CurrentSiteVersion = 1,
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

    // ── Event and entry batches (issue #430) ──────────────────────────────────────────────────────

    [Theory]
    [InlineData("/api/calendar-events/weather?ids={0}", PermissionClaims.CalendarRead)]
    [InlineData("/api/journal-entries/weather?ids={0}", PermissionClaims.JournalRead)]
    public async Task Batch_reads_need_both_claims_and_reach_the_batch_action_not_the_record(string template, string moduleClaim)
    {
        await using var weatherOnly = new WeatherFactory([PermissionClaims.WeatherRead]);
        Assert.Equal(HttpStatusCode.Forbidden, (await weatherOnly.CreateClient().GetAsync(string.Format(template, Guid.NewGuid()))).StatusCode);

        await using var moduleOnly = new WeatherFactory([moduleClaim]);
        Assert.Equal(HttpStatusCode.Forbidden, (await moduleOnly.CreateClient().GetAsync(string.Format(template, Guid.NewGuid()))).StatusCode);

        await using var both = new WeatherFactory([moduleClaim, PermissionClaims.WeatherRead]);
        var response = await both.CreateClient().GetAsync(string.Format(template, Guid.NewGuid()));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(0, body.RootElement.GetProperty("items").GetArrayLength());
        Assert.True(body.RootElement.TryGetProperty("attribution", out _));
    }

    public static TheoryData<string> MalformedIds => new()
    {
        "",
        "?ids=",
        "?ids=not-a-guid-SECRET",
        "?ids={6f1c4a8e-0000-4000-8000-000000000001}",
        "?ids=6f1c4a8e000040008000000000000001",
        "?" + string.Join("&", Enumerable.Range(0, WeatherRules.BatchMaxIds + 1).Select(_ => $"ids={Guid.NewGuid()}")),
    };

    [Theory]
    [MemberData(nameof(MalformedIds))]
    public async Task A_malformed_ids_list_is_one_400_keyed_ids_that_echoes_nothing(string query)
    {
        await using var factory = new WeatherFactory([PermissionClaims.JournalRead, PermissionClaims.WeatherRead]);

        var response = await factory.CreateClient().GetAsync($"/api/journal-entries/weather{query}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        using var body = JsonDocument.Parse(text);
        var errors = body.RootElement.GetProperty("errors").EnumerateObject().ToList();
        var error = Assert.Single(errors);
        Assert.Equal("ids", error.Name);
        Assert.Equal([WeatherRules.BatchIdsMessage], error.Value.EnumerateArray().Select(e => e.GetString()));
        Assert.DoesNotContain("SECRET", text);
        Assert.DoesNotContain("6f1c4a8e", text);
    }

    [Fact]
    public async Task Fifty_ids_and_two_spellings_of_one_id_are_accepted()
    {
        await using var factory = new WeatherFactory([PermissionClaims.CalendarRead, PermissionClaims.WeatherRead]);
        using var client = factory.CreateClient();
        var fifty = string.Join("&", Enumerable.Range(0, WeatherRules.BatchMaxIds).Select(_ => $"ids={Guid.NewGuid()}"));
        var id = Guid.NewGuid();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/calendar-events/weather?{fifty}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(
            $"/api/calendar-events/weather?ids={id.ToString("D").ToLowerInvariant()}&ids={id.ToString("D").ToUpperInvariant()}")).StatusCode);
    }

    [Fact]
    public async Task The_four_event_and_entry_reads_send_private_no_store()
    {
        await using var factory = new WeatherFactory(Admin);
        using var client = factory.CreateClient();
        var eventId = Guid.NewGuid();
        var entryId = Guid.NewGuid();
        using (var scope = factory.Services.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
            context.CalendarEvents.Add(new CalendarEvent
            {
                CalendarEventId = eventId, CalendarId = Guid.NewGuid(), Title = "Walk",
                StartDateTime = DateTime.UtcNow.AddDays(-3), EndDateTime = DateTime.UtcNow.AddDays(-3).AddHours(1),
            });
            context.JournalEntries.Add(new JournalEntry
            {
                JournalEntryId = entryId, ExternalUid = "e-cache", Title = "Today", Content = "…", EntryDate = DateTime.UtcNow.AddDays(-3),
            });
            await context.SaveChangesAsync();
        }

        foreach (var path in new[]
        {
            $"/api/calendar-events/{eventId}/weather", $"/api/journal-entries/{entryId}/weather",
            $"/api/calendar-events/weather?ids={eventId}", $"/api/journal-entries/weather?ids={entryId}",
        })
        {
            var response = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            // HttpClient parses the header (and prints it back reordered), so assert its meaning: exactly
            // private + no-store, and no directive that would let any cache keep the response.
            var cache = response.Headers.CacheControl!;
            Assert.True(cache.Private && cache.NoStore);
            Assert.False(cache.Public || cache.NoCache || cache.MaxAge is not null || cache.SharedMaxAge is not null);
            Assert.Empty(cache.Extensions);
        }
    }

    [Fact]
    public async Task Batch_reads_have_their_own_budget_and_do_not_spend_the_single_reads()
    {
        await using var factory = new WeatherFactory(Admin, configuration: new Dictionary<string, string?>
        {
            ["RateLimiting:WeatherReadBatch:PermitLimit"] = "1",
            ["RateLimiting:WeatherReadBatch:WindowSeconds"] = "3600",
        });
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/journal-entries/weather?ids={Guid.NewGuid()}")).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.GetAsync($"/api/calendar-events/weather?ids={Guid.NewGuid()}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/weather/current")).StatusCode);
    }

    [Fact]
    public async Task The_shipped_batch_budget_is_twenty_a_minute_and_leaves_the_single_reads_alone()
    {
        // AC 18 with the shipped configuration — no override — so a changed default fails here.
        Assert.Equal((20, 60), (new WeatherReadBatchRateLimitOptions().PermitLimit, new WeatherReadBatchRateLimitOptions().WindowSeconds));
        await using var factory = new WeatherFactory(Admin);
        using var client = factory.CreateClient();

        for (var i = 0; i < 20; i++)
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/journal-entries/weather?ids={Guid.NewGuid()}")).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.GetAsync($"/api/calendar-events/weather?ids={Guid.NewGuid()}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/weather/current")).StatusCode);
    }

    [Fact]
    public async Task A_batch_answers_a_linked_place_with_its_point_in_request_order()
    {
        await using var factory = new WeatherFactory(Admin);
        using var client = factory.CreateClient();
        var point = await CreatePointAsync(client);
        var place = await CreateLocationAsync(client, "Storgata 1", latitude: 59.92m);
        await LinkAsync(client, place.LocationId, point.WeatherPointId);
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        using (var scope = factory.Services.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
            foreach (var id in new[] { first, second })
            {
                context.JournalEntries.Add(new JournalEntry
                {
                    JournalEntryId = id, ExternalUid = $"e-{id}", Title = "Day", Content = "…", EntryDate = DateTime.UtcNow, LocationId = place.LocationId,
                });
            }

            await context.SaveChangesAsync();
        }

        var batch = await client.GetFromJsonAsync<JournalEntryWeatherBatch>($"/api/journal-entries/weather?ids={second}&ids={first}", Json);

        Assert.Equal([second, first], batch!.Items.Select(i => i.JournalEntryId));
        Assert.All(batch.Items, i =>
        {
            Assert.Equal(WeatherResolvedFrom.Linked, i.ResolvedFrom);
            Assert.Equal(point.WeatherPointId, i.Point!.WeatherPointId);
            Assert.Equal(place.LocationId, i.LinkedLocation!.LocationId);
        });
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

    /// <summary>The point's today as the SERVER computes it, so a run that straddles Oslo midnight cannot disagree with it.</summary>
    private static async Task<DateOnly> PointTodayAsync(HttpClient client, Guid pointId)
    {
        // The UTC date is the point's today or one day either side; the day block's isToday says which.
        var utc = DateOnly.FromDateTime(DateTime.UtcNow);
        foreach (var candidate in new[] { utc, utc.AddDays(1), utc.AddDays(-1) })
        {
            var response = await client.GetFromJsonAsync<WeatherHourlyResponse>(
                $"/api/weather/hourly?weatherPointId={pointId}&date={candidate:yyyy-MM-dd}", Json);
            if (response!.Day!.IsToday)
                return candidate;
        }

        throw new InvalidOperationException("No date within a day of UTC is the point's today.");
    }

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
        var tomorrow = (await PointTodayAsync(client, point.WeatherPointId)).AddDays(1);

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

        Assert.DoesNotContain("Europe/Oslo", text, StringComparison.Ordinal);
        Assert.DoesNotContain("59.91", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Day_mode_outside_the_window_is_OutOfRange_with_no_bounds_and_never_fetches()
    {
        await using var factory = new WeatherFactory(Admin);
        using var client = factory.CreateClient();
        var point = await CreatePointAsync(client);
        var today = await PointTodayAsync(client, point.WeatherPointId);
        var callsBefore = factory.Forecasts.Calls;

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

        Assert.Equal(callsBefore, factory.Forecasts.Calls);

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

        // (body, carries steps): the journal-entry read carries a day summary and no step, so for it the absence of the
        // local times is trivially true; for the others it is a fact about a real step.
        foreach (var (body, carriesSteps) in new[]
        {
            (range, true),
            (await client.GetStringAsync($"/api/weather/current?weatherPointId={point.WeatherPointId}"), true),
            (await client.GetStringAsync($"/api/calendar-events/{eventId}/weather"), true),
            (await client.GetStringAsync($"/api/journal-entries/{entryId}/weather"), false),
        })
        {
            if (carriesSteps)
                Assert.Contains("\"timeUtc\"", body, StringComparison.Ordinal);
            Assert.DoesNotContain("\"localStart\"", body, StringComparison.Ordinal);
            Assert.DoesNotContain("\"localEnd\"", body, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Day_mode_shares_the_weather_read_limit()
    {
        await using var factory = new WeatherFactory(Reader, configuration: new Dictionary<string, string?>
        {
            ["RateLimiting:WeatherRead:PermitLimit"] = "1",
            ["RateLimiting:WeatherRead:WindowSeconds"] = "3600",
        });
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/weather/hourly?date=2026-10-23")).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.GetAsync("/api/weather/hourly?date=2026-10-24")).StatusCode);
    }

    [Fact]
    public async Task The_openapi_document_lists_the_hourly_parameters_and_requires_none()
    {
        await using var factory = new WeatherFactory(Reader, configuration: new Dictionary<string, string?> { ["Swagger:Enabled"] = "true" });
        using var document = JsonDocument.Parse(await factory.CreateClient().GetStringAsync("/swagger/v1/swagger.json"));

        var parameters = document.RootElement.GetProperty("paths").GetProperty("/api/weather/hourly").GetProperty("get")
            .GetProperty("parameters").EnumerateArray().ToList();
        Assert.Equal(["date", "fromutc", "toutc", "weatherpointid"],
            parameters.Select(p => p.GetProperty("name").GetString()!.ToLowerInvariant()).Order(StringComparer.Ordinal).ToList());
        Assert.All(parameters, p => Assert.False(p.TryGetProperty("required", out var required) && required.GetBoolean()));
    }

    // ── History windows (issue #436) ───────────────────────────────────────────────────────────────

    private static WeatherHistoryWindowDto Window(string from, string? to = null) =>
        new() { From = DateOnly.Parse(from), To = to is null ? null : DateOnly.Parse(to) };

    [Fact]
    public async Task Windows_round_trip_with_a_status_and_the_list_carries_only_the_summary()
    {
        await using var factory = new WeatherFactory(Admin);
        using var client = factory.CreateClient();
        var request = NewPoint();
        request.HistoryWindows = [Window("2026-06-10", "2026-06-17"), Window("2025-07-01", "2025-07-07")];
        var created = await client.PostAsJsonAsync("/api/weather/points", request);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var point = (await created.Content.ReadFromJsonAsync<ExistingWeatherPoint>(Json))!;

        var single = (await client.GetFromJsonAsync<ExistingWeatherPoint>($"/api/weather/points/{point.WeatherPointId}", Json))!;
        Assert.Equal([new DateOnly(2025, 7, 1), new DateOnly(2026, 6, 10)], single.HistoryWindows!.Select(w => w.From));
        Assert.All(single.HistoryWindows!, w => Assert.True(Enum.IsDefined(w.Status)));

        var list = await client.GetStringAsync("/api/weather/points");
        Assert.DoesNotContain("\"historyWindows\"", list, StringComparison.Ordinal);
        Assert.Contains("\"historyWindowSummary\"", list, StringComparison.Ordinal);
        Assert.Contains("\"count\":2", list, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Put_without_windows_keeps_them_and_with_an_array_replaces_them()
    {
        await using var factory = new WeatherFactory(Admin);
        using var client = factory.CreateClient();
        var request = NewPoint();
        request.HistoryWindows = [Window("2026-06-10", "2026-06-17")];
        var point = (await (await client.PostAsJsonAsync("/api/weather/points", request)).Content.ReadFromJsonAsync<ExistingWeatherPoint>(Json))!;

        var keep = await client.PutAsJsonAsync($"/api/weather/points/{point.WeatherPointId}", Update(point));
        Assert.Equal(HttpStatusCode.OK, keep.StatusCode);
        var kept = (await keep.Content.ReadFromJsonAsync<WeatherPointUpdateResult>(Json))!.Point;
        Assert.Equal(new DateOnly(2026, 6, 10), Assert.Single(kept.HistoryWindows!).From);

        var replace = Update(kept);
        replace.HistoryWindows = [Window("2026-08-01", "2026-08-07"), Window("2026-09-01")];
        var replaced = (await (await client.PutAsJsonAsync($"/api/weather/points/{point.WeatherPointId}", replace)).Content.ReadFromJsonAsync<WeatherPointUpdateResult>(Json))!.Point;
        Assert.Equal([new DateOnly(2026, 8, 1), new DateOnly(2026, 9, 1)], replaced.HistoryWindows!.Select(w => w.From));
    }

    private static string PointBody(string windowsJson) =>
        $$"""{"name":"Oslo","latitude":59.9,"longitude":10.7,"timeZoneId":"Europe/Oslo","forecastEnabled":true,"historyEnabled":true,"isTracked":false,"historyWindows":{{windowsJson}}}""";

    private static StringContent JsonBody(string json) => new(json, System.Text.Encoding.UTF8, "application/json");

    /// <summary>The server's today (UTC, as <c>WeatherPointValidation</c> takes it) plus a number of days.</summary>
    private static string ServerToday(int plusDays) => DateOnly.FromDateTime(DateTime.UtcNow).AddDays(plusDays).ToString("yyyy-MM-dd");

    // AC 6: each case names the ONE key it must produce, and the test compares the whole key set, so a stray second key
    // (a per-element attribute error, a duplicate under another casing) fails as surely as a missing one.
    public static TheoryData<string, string> InvalidWindowSets => new()
    {
        { """[{"from":"2026-06-10","to":"2026-06-17"},{"from":"2026-06-17","to":"2026-06-20"}]""", "HistoryWindows[1]" },
        { """[{"from":"2025-01-01"},{"from":"2026-06-10","to":"2026-06-17"}]""", "HistoryWindows[0]" },
        { """[{"from":"2025-01-01"},{"from":"2026-01-01"}]""", "HistoryWindows[0]" },
        { """[{"from":"2026-06-10","to":"2026-06-09"}]""", "HistoryWindows[0].To" },
        { """[{"from":"1949-12-31","to":"2026-06-09"}]""", "HistoryWindows[0].From" },
        { $$"""[{"from":"{{ServerToday(731)}}"}]""", "HistoryWindows[0].From" },
        { $$"""[{"from":"2026-06-10","to":"{{ServerToday(731)}}"}]""", "HistoryWindows[0].To" },
        { """[{"to":"2026-06-09"}]""", "HistoryWindows[0].From" },
        { """[]""", "HistoryWindows" },
    };

    [Theory]
    [MemberData(nameof(InvalidWindowSets))]
    public async Task An_invalid_window_set_is_a_400_keyed_on_exactly_the_member_path(string windowsJson, string key)
    {
        await using var factory = new WeatherFactory(Admin);
        using var client = factory.CreateClient();

        var response = await client.PostAsync("/api/weather/points", JsonBody(PointBody(windowsJson)));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = (await response.Content.ReadFromJsonAsync<ValidationProblemDetails>())!;
        Assert.Equal([key], problem.Errors.Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task The_last_allowed_day_and_adjacent_windows_are_accepted()
    {
        await using var factory = new WeatherFactory(Admin);
        using var client = factory.CreateClient();

        var response = await client.PostAsync("/api/weather/points", JsonBody(PointBody(
            $$"""[{"from":"2026-06-10","to":"2026-06-17"},{"from":"2026-06-18","to":"2026-06-20"},{"from":"{{ServerToday(730)}}"}]""")));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task An_impossibly_large_set_is_one_list_error_with_no_per_element_errors()
    {
        // AC 24: the spec's shape — 700 000 empty elements, under the 4 MB write limit, far above D. Every element is
        // individually invalid (no From), so any per-element work would surface as a per-element key. No wall-clock
        // bound in the fast tier: the single key is the proof that nothing ran per element.
        await using var factory = new WeatherFactory(Admin);
        using var client = factory.CreateClient();
        const int count = 700_000;
        Assert.True(count > WeatherHistoryWindowRules.MaxValidCount(DateOnly.FromDateTime(DateTime.UtcNow)));
        var impossible = PointBody($"[{string.Join(",", Enumerable.Repeat("{}", count))}]");
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(impossible) < 4 * 1024 * 1024);

        var refused = await client.PostAsync("/api/weather/points", JsonBody(impossible));

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        var problem = (await refused.Content.ReadFromJsonAsync<ValidationProblemDetails>())!;
        var error = Assert.Single(problem.Errors);
        Assert.Equal("HistoryWindows", error.Key);
        Assert.Equal("There are more history windows than there are days they could cover.", Assert.Single(error.Value));
    }

    [Fact]
    public async Task An_unknown_member_on_a_window_element_is_ignored_and_sets_no_id()
    {
        // AC 14 / §7.4: unknown members are ignored by the binder; a window id is always server-generated.
        await using var factory = new WeatherFactory(Admin);
        using var client = factory.CreateClient();
        var forged = Guid.NewGuid();
        var response = await client.PostAsync("/api/weather/points", JsonBody(PointBody(
            $$"""[{"from":"2026-06-10","to":"2026-06-17","id":"{{forged}}","weatherPointHistoryWindowId":"{{forged}}","weatherPointId":"{{forged}}","status":"Complete","extra":[1,"x"]}]""")));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var point = (await response.Content.ReadFromJsonAsync<ExistingWeatherPoint>(Json))!;
        using (var scope = factory.Services.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
            var stored = await context.WeatherPointHistoryWindows.AsNoTracking().SingleAsync();
            Assert.NotEqual(forged, stored.WeatherPointHistoryWindowId);
            Assert.NotEqual(Guid.Empty, stored.WeatherPointHistoryWindowId);
            Assert.Equal(point.WeatherPointId, stored.WeatherPointId);
            Assert.Equal((new DateOnly(2026, 6, 10), (DateOnly?)new DateOnly(2026, 6, 17)), (stored.FromDate, stored.ToDate));
        }

        var single = await client.GetStringAsync($"/api/weather/points/{point.WeatherPointId}");
        Assert.DoesNotContain(forged.ToString(), single, StringComparison.OrdinalIgnoreCase);

        // Nothing on the element could capture them: no [JsonExtensionData] bag, no id member.
        Assert.DoesNotContain(typeof(WeatherHistoryWindowDto).GetProperties(), p =>
            p.IsDefined(typeof(System.Text.Json.Serialization.JsonExtensionDataAttribute), true)
            || p.Name.Contains("Id", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_oversized_body_maps_to_a_generic_413()
    {
        // The in-process host cannot enforce [RequestSizeLimit] (and the Testing environment refuses to run on Kestrel),
        // so the transport half is Odyssey.E2ETests.Api's; this pins the handler half: status 413 only, no exception text.
        var context = new Microsoft.AspNetCore.Http.DefaultHttpContext
        {
            RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider(),
            Response = { Body = new MemoryStream() },
        };
        context.Features.Set<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerFeature>(new Microsoft.AspNetCore.Diagnostics.ExceptionHandlerFeature
        {
            Error = new Microsoft.AspNetCore.Http.BadHttpRequestException("Request body too large. The max request body size is 4194304 bytes.", 413),
        });

        await Odyssey.Api.GlobalExceptionHandler.HandleAsync(context);

        Assert.Equal(StatusCodes.Status413PayloadTooLarge, context.Response.StatusCode);
        context.Response.Body.Position = 0;
        var body = await new StreamReader(context.Response.Body).ReadToEndAsync();
        Assert.DoesNotContain("4194304", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Both_point_writes_carry_the_4_mb_limit()
    {
        // Read off the routed endpoints' metadata — the public IRequestSizeLimitMetadata surface the request-size
        // middleware consults — rather than the attribute's private field.
        await using var factory = new WeatherFactory(Admin);
        using var client = factory.CreateClient();
        var endpoints = factory.Services.GetServices<Microsoft.AspNetCore.Routing.EndpointDataSource>()
            .SelectMany(source => source.Endpoints).ToList();

        foreach (var name in new[] { "PostWeatherPoint", "PutWeatherPoint" })
        {
            var endpoint = Assert.Single(endpoints,
                e => e.Metadata.GetMetadata<Microsoft.AspNetCore.Routing.IRouteNameMetadata>()?.RouteName == name);
            var limit = endpoint.Metadata.GetMetadata<Microsoft.AspNetCore.Http.Metadata.IRequestSizeLimitMetadata>();
            Assert.Equal(4L * 1024 * 1024, limit?.MaxRequestBodySize);
        }
    }

    [Fact]
    public async Task A_window_only_edit_with_a_stale_token_is_a_409_and_changes_nothing()
    {
        await using var factory = new WeatherFactory(Admin);
        using var client = factory.CreateClient();
        var request = NewPoint();
        request.HistoryWindows = [Window("2026-06-10", "2026-06-17")];
        var point = (await (await client.PostAsJsonAsync("/api/weather/points", request)).Content.ReadFromJsonAsync<ExistingWeatherPoint>(Json))!;
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"/api/weather/points/{point.WeatherPointId}", Update(point))).StatusCode);

        var stale = Update(point);
        stale.HistoryWindows = [Window("2026-07-01", "2026-07-07")];
        Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsJsonAsync($"/api/weather/points/{point.WeatherPointId}", stale)).StatusCode);

        var after = (await client.GetFromJsonAsync<ExistingWeatherPoint>($"/api/weather/points/{point.WeatherPointId}", Json))!;
        Assert.Equal(new DateOnly(2026, 6, 10), Assert.Single(after.HistoryWindows!).From);
    }

    /// <summary>
    /// A windowed point with measured days stored inside its trip, a place linked to it, and a calendar event and a
    /// journal entry on a trip day at that place — so every <c>weather.read</c> surface (AC 9a) answers for this point
    /// with real data rather than vacuously.
    /// </summary>
    private static async Task<(ExistingWeatherPoint Point, string[] Paths)> WindowedPointWithEveryReadAsync(WeatherFactory factory, HttpClient client)
    {
        var request = NewPoint();
        request.HistoryWindows = [Window("2026-06-10", "2026-06-17"), Window("2025-07-01", "2025-07-07")];
        var point = (await (await client.PostAsJsonAsync("/api/weather/points", request)).Content.ReadFromJsonAsync<ExistingWeatherPoint>(Json))!;
        var place = await CreateLocationAsync(client, "Storgata 1", latitude: 59.92m);
        Assert.Equal(HttpStatusCode.OK, (await LinkAsync(client, place.LocationId, point.WeatherPointId)).StatusCode);

        var eventId = Guid.NewGuid();
        var entryId = Guid.NewGuid();
        var tripDay = new DateTime(2026, 6, 12, 10, 0, 0, DateTimeKind.Utc);
        using (var scope = factory.Services.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
            for (var day = new DateOnly(2026, 6, 10); day <= new DateOnly(2026, 6, 17); day = day.AddDays(1))
            {
                context.WeatherDays.Add(new WeatherDay
                {
                    WeatherPointId = point.WeatherPointId, LocalDate = day, ObservedMinTempC = 9.5m, ObservedMaxTempC = 18.5m,
                    ObservedMeanTempC = 14m, ObservedPrecipitationMm = 1.2m, ObservedFetchedAtUtc = new DateTime(2026, 6, 20, 7, 0, 0, DateTimeKind.Utc),
                    ObservedSiteVersion = 1, UpdatedAtUtc = new DateTime(2026, 6, 20, 7, 0, 0, DateTimeKind.Utc),
                });
            }

            context.CalendarEvents.Add(new CalendarEvent
            {
                CalendarEventId = eventId, CalendarId = Guid.NewGuid(), Title = "Hike", LocationId = place.LocationId,
                StartDateTime = tripDay, EndDateTime = tripDay.AddHours(3),
            });
            context.JournalEntries.Add(new JournalEntry
            {
                JournalEntryId = entryId, ExternalUid = "trip", Title = "Trip", Content = "…", EntryDate = tripDay, LocationId = place.LocationId,
            });
            await context.SaveChangesAsync();
        }

        var now = DateTime.UtcNow;
        var hourFrom = new DateTime(now.Year, now.Month, now.Day, now.Hour, 0, 0, DateTimeKind.Utc);
        var pointToday = await PointTodayAsync(client, point.WeatherPointId);
        var id = point.WeatherPointId;
        return (point,
        [
            "/api/weather/points/options",
            $"/api/weather/current?weatherPointId={id}",
            $"/api/weather/hourly?weatherPointId={id}&fromUtc={hourFrom:yyyy-MM-ddTHH:mm:ssZ}&toUtc={hourFrom.AddHours(6):yyyy-MM-ddTHH:mm:ssZ}",
            $"/api/weather/hourly?weatherPointId={id}&date={pointToday.AddDays(1):yyyy-MM-dd}",
            // The daily read carries the history bounds (WeatherDailyResponse.History), the "history bounds" of AC 9a.
            $"/api/weather/daily?weatherPointId={id}&from=2026-06-01&to=2026-06-30",
            $"/api/calendar-events/{eventId}/weather",
            $"/api/journal-entries/{entryId}/weather",
        ]);
    }

    /// <summary>Every member name a window, its summary or its status travels under, plus the status values.</summary>
    private static readonly string[] WindowVocabulary =
    [
        "historyWindow", "coveredDays", "fetchableDays", "earliestFrom", "latestTo", "hasOpenEnd",
        "\"Upcoming\"", "\"Collecting\"", "\"Backfilling\"", "\"Complete\"", "\"Unavailable\"",
    ];

    [Fact]
    public async Task Weather_read_surfaces_carry_no_window_data()
    {
        await using var factory = new WeatherFactory(Admin);
        using var client = factory.CreateClient();
        var (point, paths) = await WindowedPointWithEveryReadAsync(factory, client);

        foreach (var path in paths)
        {
            var response = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await response.Content.ReadAsStringAsync();
            foreach (var word in WindowVocabulary)
            {
                Assert.DoesNotContain(word, body, StringComparison.OrdinalIgnoreCase);
            }
        }

        // Not vacuous: the bounds read answers with the stored trip days, and the event/entry reads resolve this point.
        using var daily = JsonDocument.Parse(await client.GetStringAsync(paths[4]));
        var history = daily.RootElement.GetProperty("history");
        Assert.Equal("2026-06-10", history.GetProperty("measuredFrom").GetString());
        Assert.Equal("2026-06-17", history.GetProperty("measuredThrough").GetString());
        Assert.Equal(8, daily.RootElement.GetProperty("days").GetArrayLength());
        foreach (var path in paths[5..])
        {
            using var doc = JsonDocument.Parse(await client.GetStringAsync(path));
            Assert.Equal(point.WeatherPointId, doc.RootElement.GetProperty("point").GetProperty("weatherPointId").GetGuid());
        }
    }

    [Fact]
    public async Task Weather_read_responses_are_byte_identical_when_only_the_windows_change()
    {
        // AC 9b: same stored data, different windows → the same bytes on every weather.read surface, so the accepted
        // exposure (§7.3: the measured days themselves) is exactly what is described and no wider.
        await using var factory = new WeatherFactory(Admin);
        using var client = factory.CreateClient();
        var (point, paths) = await WindowedPointWithEveryReadAsync(factory, client);

        async Task<List<string>> ReadAllAsync()
        {
            var bodies = new List<string>();
            foreach (var path in paths)
            {
                var response = await client.GetAsync(path);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                bodies.Add(await response.Content.ReadAsStringAsync());
            }

            return bodies;
        }

        // A first pass settles the on-demand forecast fetch, so the comparison is about windows and nothing else.
        await ReadAllAsync();
        var before = await ReadAllAsync();

        var current = (await client.GetFromJsonAsync<ExistingWeatherPoint>($"/api/weather/points/{point.WeatherPointId}", Json))!;
        var edit = Update(current);
        edit.HistoryWindows = [Window("2024-03-01", "2024-03-05"), Window("2026-06-12", "2026-06-13"), Window("2026-09-01")];
        var edited = await client.PutAsJsonAsync($"/api/weather/points/{point.WeatherPointId}", edit);
        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);
        var changed = (await edited.Content.ReadFromJsonAsync<WeatherPointUpdateResult>(Json))!.Point;
        Assert.Equal(3, changed.HistoryWindowSummary.Count);
        Assert.NotEqual(current.SettingsVersion, changed.SettingsVersion);

        var after = await ReadAllAsync();

        for (var i = 0; i < paths.Length; i++)
        {
            Assert.True(before[i] == after[i], $"{paths[i]} changed when only the windows did:\n{before[i]}\n---\n{after[i]}");
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
