using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Odyssey.TestData;
using Xunit;

namespace Odyssey.E2ETests.Api;

/// <summary>
/// History windows (issues #436, #437) over real HTTP against the seeded roles: the windows are travel dates, so only
/// the Admin (<c>weather.points.read</c>/<c>.update</c>) reaches them, and the <c>weather.read</c> surfaces every role
/// shares carry none of their data. Also the write limit, which the in-process test host cannot enforce: Kestrel
/// refuses an oversized body with <c>413</c>, and an unauthorized caller is refused before the body is read.
/// </summary>
[Collection(ApiStackCollection.Name)]
public class WeatherHistoryWindowPermissionTests(ApiStackFixture fixture)
{
    private static DemoUser UserIn(string role) => DemoUsers.All.First(user => user.Role == role);

    [SkippableTheory]
    [InlineData("Owner")]
    [InlineData("User")]
    [InlineData("Guest")]
    public async Task A_non_admin_role_gets_403_on_every_point_route(string role)
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        var user = UserIn(role);
        var client = await fixture.CreateAuthenticatedClientAsync(user.Email, user.Password);

        // A random id: the claim gate answers before the point is looked up, so a 404 here would mean no gate.
        var id = Guid.NewGuid();
        var windowed = new
        {
            name = "x", latitude = 59.9, longitude = 10.7, timeZoneId = "Europe/Oslo", forecastEnabled = true,
            historyEnabled = true, isTracked = false, settingsVersion = Guid.NewGuid(),
            historyWindows = new[] { new { from = "2026-06-10", to = "2026-06-17" } },
        };

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/weather/points")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/api/weather/points/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await fixture.PostWithAntiforgeryAsync(client, "/api/weather/points", windowed)).StatusCode);
        // The client pipeline attaches the antiforgery token to every unsafe method, PUT included, so a 403 is the
        // claim gate and not the antiforgery 400.
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PutAsJsonAsync($"/api/weather/points/{id}", windowed)).StatusCode);
    }

    [SkippableFact]
    public async Task A_guests_weather_read_surfaces_carry_no_window_data_for_a_windowed_point()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);

        // The Admin seeds the exposure under test: a point whose windows are distinctive dates.
        var admin = UserIn("Admin");
        var adminClient = await fixture.CreateAuthenticatedClientAsync(admin.Email, admin.Password);
        var created = await fixture.PostWithAntiforgeryAsync(adminClient, "/api/weather/points", new
        {
            name = $"E2E windows {Guid.NewGuid():N}"[..40],
            latitude = 61.517, longitude = 10.165, timeZoneId = "Europe/Oslo",
            forecastEnabled = true, historyEnabled = true, isTracked = false,
            historyWindows = new object[]
            {
                new { from = "2025-07-01", to = "2025-07-07" },
                new { from = "2026-06-10", to = "2026-06-17" },
            },
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var point = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var pointId = point.RootElement.GetProperty("weatherPointId").GetGuid();
        Assert.Equal(2, point.RootElement.GetProperty("historyWindows").GetArrayLength());

        try
        {
            var guest = UserIn("Guest");
            var client = await fixture.CreateAuthenticatedClientAsync(guest.Email, guest.Password);
            var now = DateTime.UtcNow;
            var hourFrom = new DateTime(now.Year, now.Month, now.Day, now.Hour, 0, 0, DateTimeKind.Utc);

            var surfaces = new[]
            {
                "/api/weather/points/options",
                $"/api/weather/current?weatherPointId={pointId}",
                $"/api/weather/hourly?weatherPointId={pointId}&fromUtc={hourFrom:yyyy-MM-ddTHH:mm:ssZ}&toUtc={hourFrom.AddHours(6):yyyy-MM-ddTHH:mm:ssZ}",
                $"/api/weather/hourly?weatherPointId={pointId}&date={DateOnly.FromDateTime(now):yyyy-MM-dd}",
                // The Locations history chart's read, which also carries the history bounds.
                $"/api/weather/daily?weatherPointId={pointId}&from=2025-07-01&to=2026-06-30",
            };

            foreach (var path in surfaces)
            {
                var response = await client.GetAsync(path);
                Assert.True(response.StatusCode == HttpStatusCode.OK, $"{path} answered {(int)response.StatusCode} for the Guest.");
                var body = await response.Content.ReadAsStringAsync();
                foreach (var word in new[] { "historyWindow", "coveredDays", "fetchableDays", "earliestFrom", "latestTo", "hasOpenEnd" })
                {
                    Assert.DoesNotContain(word, body, StringComparison.OrdinalIgnoreCase);
                }

                // The window ends the Admin chose are not echoed (measured days could be; the seeded point has none yet).
                foreach (var date in new[] { "2025-07-07", "2026-06-17" })
                {
                    Assert.DoesNotContain(date, body, StringComparison.Ordinal);
                }
            }

            // Not vacuous: the reads resolved the windowed point, not the household default or nothing.
            using var daily = JsonDocument.Parse(await client.GetStringAsync(surfaces[4]));
            Assert.Equal(pointId, daily.RootElement.GetProperty("point").GetProperty("weatherPointId").GetGuid());
            Assert.Equal(JsonValueKind.Object, daily.RootElement.GetProperty("history").ValueKind);
        }
        finally
        {
            await fixture.DeleteWithAntiforgeryAsync(adminClient, $"/api/weather/points/{pointId}");
        }
    }

    [SkippableFact]
    public async Task A_body_over_the_write_limit_is_413_for_the_admin_and_403_for_anyone_else()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        var padding = new string(' ', (4 * 1024 * 1024) + 1);
        StringContent Oversized() => new($"{{\"name\":\"Oslo\"{padding}}}", Encoding.UTF8, "application/json");

        var admin = UserIn("Admin");
        var adminClient = await fixture.CreateAuthenticatedClientAsync(admin.Email, admin.Password);
        var refused = await fixture.PostContentWithAntiforgeryAsync(adminClient, "/api/weather/points", Oversized());
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, refused.StatusCode);
        Assert.DoesNotContain("Exception", await refused.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        var guest = UserIn("Guest");
        var guestClient = await fixture.CreateAuthenticatedClientAsync(guest.Email, guest.Password);
        var forbidden = await fixture.PostContentWithAntiforgeryAsync(guestClient, "/api/weather/points", Oversized());
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
    }
}
