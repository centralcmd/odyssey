using System.Net;
using System.Text;
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

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/weather/points")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/api/weather/points/{Guid.NewGuid()}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await fixture.PostWithAntiforgeryAsync(client, "/api/weather/points", new { name = "x" })).StatusCode);
    }

    [SkippableFact]
    public async Task A_guests_weather_read_surfaces_carry_no_window_data()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        var guest = UserIn("Guest");
        var client = await fixture.CreateAuthenticatedClientAsync(guest.Email, guest.Password);

        var options = await client.GetStringAsync("/api/weather/points/options");
        Assert.DoesNotContain("historyWindow", options, StringComparison.OrdinalIgnoreCase);

        var daily = await client.GetAsync("/api/weather/daily?from=2026-01-01&to=2026-01-31");
        Assert.DoesNotContain("historyWindow", await daily.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
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
