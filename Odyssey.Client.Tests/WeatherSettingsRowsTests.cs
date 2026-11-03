using Bunit;
using Odyssey.Client.Pages;
using Odyssey.Dtos.Application;
using Xunit;
using static Odyssey.Client.Tests.SettingsPageHarness;

namespace Odyssey.Client.Tests;

/// <summary>
/// Issue #388 AC 17: System settings → Weather names points, not locations, and the scheduled-points cap is the
/// value the server serves.
/// </summary>
[Collection(SettingsPageCollection.Name)]
public class WeatherSettingsRowsTests : IDisposable
{
    public void Dispose() => Settings.InteractiveCheck = static () => OperatingSystem.IsBrowser();

    [Fact]
    public async Task The_weather_card_shows_the_renamed_rows_with_the_cap_read_from_settings()
    {
        await using var ctx = NewPageContext(SettingsClient(new SystemSettingsDto { WeatherMaxTrackedPoints = 7 }).Object);

        var page = ctx.Render<Settings>();

        Assert.Equal("Points on a schedule", page.Find("#ss-ttl-weatherMaxTrackedPoints").TextContent.Trim());
        Assert.Equal("Household default weather point", page.Find("#ss-ttl-weatherDefaultPointId").TextContent.Trim());
        Assert.Equal("7", page.Find("#ss-in-weatherMaxTrackedPoints").GetAttribute("value"));
        Assert.Equal(
            ["weatherMaxTrackedPoints", "weatherDefaultPointId"],
            Settings.Sections.Single(s => s.Group == "Weather").Items
                .Where(i => i.Title is "Points on a schedule" or "Household default weather point").Select(i => i.Key));
        Assert.DoesNotContain("weather location", page.Markup, StringComparison.OrdinalIgnoreCase);
    }
}
