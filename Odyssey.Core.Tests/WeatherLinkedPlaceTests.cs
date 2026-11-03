using Microsoft.EntityFrameworkCore;
using Odyssey.Core;
using Odyssey.Core.Weather;
using Odyssey.Dtos.Journal;
using Xunit;

namespace Odyssey.Core.Tests;

/// <summary>
/// Issue #380 §5.8 (AC 16, 20) — weather now consumes a location whose coordinates are nullable. The read paths
/// report where their place came from (<c>ResolvedFrom</c>), and the admin paths refuse to build or apply a
/// site from a place with no position rather than throwing on a null (senior-tester M2).
/// </summary>
public class WeatherLinkedPlaceTests
{
    private static readonly DateTime Noon = new(2026, 10, 23, 12, 0, 0, DateTimeKind.Utc);

    // ── ResolvedFrom on the hourly and daily reads ───────────────────────────────────────────────

    [Fact]
    public async Task Hourly_and_daily_say_Explicit_for_a_named_place_and_Household_for_the_default()
    {
        using var h = new WeatherTestHarness();
        var named = await h.EnableAsync("Cabin");
        var household = await h.EnableAsync("Home");
        h.Settings.Value = h.Settings.Value with { HouseholdDefaultLocationId = household.LocationId };

        var hourlyNamed = await h.Read(h.NewContext()).HourlyAsync(Hourly(named.LocationId), "u1", CancellationToken.None);
        var hourlyDefault = await h.Read(h.NewContext()).HourlyAsync(Hourly(null), "u1", CancellationToken.None);
        var dailyNamed = await h.Read(h.NewContext()).DailyAsync(Daily(named.LocationId), "u1", CancellationToken.None);
        var dailyDefault = await h.Read(h.NewContext()).DailyAsync(Daily(null), "u1", CancellationToken.None);

        Assert.Equal((WeatherResolvedFrom.Explicit, named.LocationId), (hourlyNamed.ResolvedFrom!.Value, hourlyNamed.Location!.LocationId));
        Assert.Equal((WeatherResolvedFrom.Household, household.LocationId), (hourlyDefault.ResolvedFrom!.Value, hourlyDefault.Location!.LocationId));
        Assert.Equal((WeatherResolvedFrom.Explicit, named.LocationId), (dailyNamed.ResolvedFrom!.Value, dailyNamed.Location!.LocationId));
        Assert.Equal((WeatherResolvedFrom.Household, household.LocationId), (dailyDefault.ResolvedFrom!.Value, dailyDefault.Location!.LocationId));
    }

    [Fact]
    public async Task Hourly_and_daily_with_nothing_to_resolve_are_NoPlace_with_no_ResolvedFrom()
    {
        using var h = new WeatherTestHarness();

        var hourly = await h.Read().HourlyAsync(Hourly(null), "u1", CancellationToken.None);
        var daily = await h.Read().DailyAsync(Daily(null), "u1", CancellationToken.None);

        Assert.Equal((WeatherStatus.NoPlace, (WeatherResolvedFrom?)null), (hourly.Status, hourly.ResolvedFrom));
        Assert.Equal((WeatherStatus.NoPlace, (WeatherResolvedFrom?)null), (daily.Status, daily.ResolvedFrom));
    }

    // ── A weather place whose coordinates were removed ───────────────────────────────────────────

    [Fact]
    public async Task A_place_whose_coordinates_were_removed_reads_as_moved()
    {
        using var h = new WeatherTestHarness();
        var location = await h.EnableAsync();
        var live = await RemoveCoordinatesAsync(h, location.LocationId);
        var site = await h.NewContext().WeatherLocationSites.AsNoTracking().SingleAsync();

        Assert.True(WeatherCoordinates.Moved(live, site));
        Assert.True((await h.Admin(h.NewContext()).GetAsync(location.LocationId, CancellationToken.None))!.LocationMoved);
    }

    [Fact]
    public async Task Applying_the_position_of_a_place_with_none_is_a_422_keyed_locationId_and_writes_no_site()
    {
        using var h = new WeatherTestHarness();
        var location = await h.EnableAsync();
        await RemoveCoordinatesAsync(h, location.LocationId);

        var refused = await Assert.ThrowsAsync<DomainUnprocessableException>(() => h.Admin(h.NewContext()).ApplyAsync(
            location.LocationId,
            new ApplyWeatherLocation { ExpectedLocationFingerprint = WeatherCoordinates.NoCoordinatesFingerprint },
            WeatherTestHarness.AdminId, CancellationToken.None));

        Assert.Equal(["locationId"], refused.Errors!.Keys);
        Assert.Equal(1, await h.NewContext().WeatherLocationSites.CountAsync());
    }

    [Fact]
    public async Task Turning_weather_on_for_a_place_with_no_position_is_a_422_keyed_locationId()
    {
        using var h = new WeatherTestHarness();
        var location = await h.AddLocationAsync();
        await RemoveCoordinatesAsync(h, location.LocationId);

        var refused = await Assert.ThrowsAsync<DomainUnprocessableException>(() => h.Admin(h.NewContext()).UpsertAsync(
            location.LocationId,
            new UpsertWeatherLocation
            {
                ForecastEnabled = true, HistoryEnabled = false, IsTracked = false,
                ExpectedLocationFingerprint = WeatherCoordinates.NoCoordinatesFingerprint,
            },
            WeatherTestHarness.AdminId, CancellationToken.None));

        Assert.Equal(["locationId"], refused.Errors!.Keys);
        Assert.Empty(await h.NewContext().WeatherLocations.ToListAsync());
    }

    /// <summary>
    /// What a hand edit or a restore can leave — the API refuses to clear a weather place's coordinates, and
    /// the EF InMemory provider enforces no CHECK, so this is the only way to reach the shape.
    /// </summary>
    private static async Task<Odyssey.Context.Location> RemoveCoordinatesAsync(WeatherTestHarness h, Guid locationId)
    {
        var context = h.NewContext();
        var live = await context.Locations.SingleAsync(l => l.LocationId == locationId);
        live.Latitude = null;
        live.Longitude = null;
        live.TimeZoneId = null;
        await context.SaveChangesAsync();
        return live;
    }

    private static WeatherHourlyQuery Hourly(Guid? locationId) =>
        new() { LocationId = locationId, FromUtc = Noon.AddHours(-6), ToUtc = Noon.AddHours(-1) };

    private static WeatherDailyQuery Daily(Guid? locationId) =>
        new() { LocationId = locationId, From = new DateOnly(2026, 10, 1), To = new DateOnly(2026, 10, 10) };
}
