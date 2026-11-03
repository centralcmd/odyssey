using Microsoft.EntityFrameworkCore;
using Odyssey.Core;
using Odyssey.Core.Journal;
using Odyssey.Core.Locations;
using Odyssey.Core.Weather;
using Odyssey.Dtos.Journal;
using Xunit;

namespace Odyssey.Core.Tests;

/// <summary>
/// Issue #387 — a place reads its weather from the weather POINT it links to. The read paths report where the
/// point came from (<c>ResolvedFrom</c>, and for a linked read the place and the whole-km distance), the link
/// service is the only writer of <c>Locations.WeatherPointId</c>, and the location rules no longer know weather.
/// </summary>
public class WeatherLinkedPlaceTests
{
    private static readonly DateTime Noon = new(2026, 10, 23, 12, 0, 0, DateTimeKind.Utc);

    // ── ResolvedFrom on the hourly and daily reads ───────────────────────────────────────────────

    [Fact]
    public async Task Hourly_and_daily_say_Explicit_for_a_named_point_and_Household_for_the_default()
    {
        using var h = new WeatherTestHarness();
        var named = await h.EnableAsync("Cabin");
        var household = await h.EnableAsync("Home");
        h.Settings.Value = h.Settings.Value with { HouseholdDefaultPointId = household.Id };

        var hourlyNamed = await h.Read(h.NewContext()).HourlyAsync(Hourly(named.Id), "u1", CancellationToken.None);
        var hourlyDefault = await h.Read(h.NewContext()).HourlyAsync(Hourly(null), "u1", CancellationToken.None);
        var dailyNamed = await h.Read(h.NewContext()).DailyAsync(Daily(named.Id), "u1", CancellationToken.None);
        var dailyDefault = await h.Read(h.NewContext()).DailyAsync(Daily(null), "u1", CancellationToken.None);

        Assert.Equal((WeatherResolvedFrom.Explicit, named.Id), (hourlyNamed.ResolvedFrom!.Value, hourlyNamed.Point!.WeatherPointId));
        Assert.Equal((WeatherResolvedFrom.Household, household.Id), (hourlyDefault.ResolvedFrom!.Value, hourlyDefault.Point!.WeatherPointId));
        Assert.Equal((WeatherResolvedFrom.Explicit, named.Id), (dailyNamed.ResolvedFrom!.Value, dailyNamed.Point!.WeatherPointId));
        Assert.Equal((WeatherResolvedFrom.Household, household.Id), (dailyDefault.ResolvedFrom!.Value, dailyDefault.Point!.WeatherPointId));
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

    // ── Event resolution through the link (AC 16) ────────────────────────────────────────────────

    [Fact]
    public async Task An_event_at_a_linked_place_resolves_Linked_with_the_place_and_the_distance()
    {
        using var h = new WeatherTestHarness();
        var oslo = await h.EnableAsync("Oslo", tracked: false);
        var street = await h.AddLocationAsync("Storgata 1", latitude: 59.91900m, longitude: 10.75027m);
        await h.LinkAsync(street.LocationId, oslo.Id);
        var eventId = await AddEventAsync(h, street.LocationId);

        var weather = (await h.Read(h.NewContext()).CalendarEventAsync(eventId, null, "u1", CancellationToken.None))!;

        Assert.Equal(WeatherResolvedFrom.Linked, weather.ResolvedFrom);
        Assert.Equal(oslo.Id, weather.Point!.WeatherPointId);
        Assert.Equal((street.LocationId, "Storgata 1"), (weather.LinkedLocation!.LocationId, weather.LinkedLocation.Name));
        Assert.Equal(1, weather.DistanceKm);
    }

    [Fact]
    public async Task An_event_at_an_unlinked_place_falls_through_to_the_preference_then_the_household_default()
    {
        using var h = new WeatherTestHarness();
        var home = await h.EnableAsync("Home");
        var cabin = await h.EnableAsync("Cabin");
        h.Settings.Value = h.Settings.Value with { HouseholdDefaultPointId = home.Id };
        var unlinked = await h.AddLocationAsync("Somewhere");
        var eventId = await AddEventAsync(h, unlinked.LocationId);

        var household = (await h.Read(h.NewContext()).CalendarEventAsync(eventId, null, "u1", CancellationToken.None))!;
        Assert.Equal((WeatherResolvedFrom.Household, home.Id), (household.ResolvedFrom!.Value, household.Point!.WeatherPointId));
        Assert.Null(household.LinkedLocation);
        Assert.Null(household.DistanceKm);

        await h.Read().SetPreferenceAsync("u1", new UpdateWeatherPreferences { DefaultPointId = cabin.Id }, CancellationToken.None);
        var preferred = (await h.Read(h.NewContext()).CalendarEventAsync(eventId, null, "u1", CancellationToken.None))!;
        Assert.Equal((WeatherResolvedFrom.Preferred, cabin.Id), (preferred.ResolvedFrom!.Value, preferred.Point!.WeatherPointId));
    }

    [Fact]
    public async Task An_event_linked_to_a_forecast_off_point_returns_its_stored_history_and_does_not_fall_back()
    {
        using var h = new WeatherTestHarness();
        var home = await h.EnableAsync("Home");
        h.Settings.Value = h.Settings.Value with { HouseholdDefaultPointId = home.Id };
        var boat = await h.EnableAsync("Boat", forecast: false);
        var eventId = await AddEventAsync(h, boat.LocationId);

        var weather = (await h.Read(h.NewContext()).CalendarEventAsync(eventId, null, "u1", CancellationToken.None))!;

        Assert.Equal((WeatherResolvedFrom.Linked, boat.Id), (weather.ResolvedFrom!.Value, weather.Point!.WeatherPointId));
        Assert.Equal(WeatherStatus.NoData, weather.Status);
        Assert.Equal(0, h.Forecasts.Calls);
    }

    [Fact]
    public async Task A_dangling_preference_resolves_as_absent()
    {
        using var h = new WeatherTestHarness();
        var home = await h.EnableAsync("Home");
        var cabin = await h.EnableAsync("Cabin", forecast: false, history: false);
        h.Settings.Value = h.Settings.Value with { HouseholdDefaultPointId = home.Id };
        // A stored preference naming a point that is later deleted (the write path would refuse it now).
        await new Odyssey.Core.Preferences.UserPreferencesService(h.Context, h.Time).UpsertServerOwnedAsync(
            "u1", Odyssey.Core.Preferences.UserPreferencesService.WeatherKey, $$"""{"defaultPointId":"{{cabin.Id}}"}""", CancellationToken.None);
        Assert.True(await h.Admin(h.NewContext()).DeleteAsync(cabin.Id, WeatherTestHarness.AdminId, CancellationToken.None));

        Assert.Null((await h.Read(h.NewContext()).PreferencesAsync("u1", CancellationToken.None)).DefaultPoint);
        Assert.Equal(home.Id, (await h.Read(h.NewContext()).ResolveAsync(null, "u1", CancellationToken.None)).Point!.WeatherPointId);
    }

    // ── The link (AC 11, 13) ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Linking_far_away_is_allowed_with_no_distance_warning_and_relinking_is_a_no_op()
    {
        using var h = new WeatherTestHarness();
        var oslo = await h.EnableAsync("Oslo");
        var far = await h.AddLocationAsync("Trondheim-ish", latitude: 64.4m, longitude: 10.75027m);

        var link = (await h.LinkAsync(far.LocationId, oslo.Id))!;
        Assert.Equal(oslo.Id, link.WeatherPoint!.WeatherPointId);
        Assert.True(link.DistanceKm > 450);
        Assert.Empty(link.Warnings);

        var updatedAt = (await h.NewContext().Locations.SingleAsync(l => l.LocationId == far.LocationId)).UpdatedAt;
        h.Time.Advance(TimeSpan.FromMinutes(5));
        var again = (await h.LinkAsync(far.LocationId, oslo.Id))!;
        Assert.Equal(link.DistanceKm, again.DistanceKm);
        Assert.Equal(updatedAt, (await h.NewContext().Locations.SingleAsync(l => l.LocationId == far.LocationId)).UpdatedAt);
    }

    [Fact]
    public async Task A_link_reports_a_time_zone_mismatch_missing_coordinates_and_a_forecast_off_point()
    {
        using var h = new WeatherTestHarness();
        var stockholm = await h.EnableAsync("Stockholm", zone: "Europe/Stockholm", forecast: false);
        var oslo = await h.AddLocationAsync("Oslo place");

        var mismatch = (await h.LinkAsync(oslo.LocationId, stockholm.Id))!;
        Assert.Equal([WeatherAdvisoryCode.TimeZoneMismatch, WeatherAdvisoryCode.PointNotSelectable], mismatch.Warnings.Select(w => w.Code));

        var now = h.Time.GetUtcNow().UtcDateTime;
        var bare = new Odyssey.Context.Location { LocationId = Guid.NewGuid(), Name = "No coordinates", CreatedAt = now, UpdatedAt = now };
        h.Context.Locations.Add(bare);
        await h.Context.SaveChangesAsync();
        var noCoordinates = (await h.LinkAsync(bare.LocationId, stockholm.Id))!;
        Assert.Null(noCoordinates.DistanceKm);
        Assert.Contains(noCoordinates.Warnings, w => w.Code == WeatherAdvisoryCode.NoLocationCoordinates);
        Assert.All(noCoordinates.Warnings.Concat(mismatch.Warnings), w =>
        {
            Assert.DoesNotContain("Stockholm", w.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("Oslo", w.Message, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Linking_to_an_unknown_or_purging_point_is_a_422_keyed_weatherPointId_and_a_forwarder_is_keyed_id()
    {
        using var h = new WeatherTestHarness();
        var point = await h.EnableAsync("Oslo", forecast: false, history: false);
        var place = await h.AddLocationAsync("Place");

        var unknown = await Assert.ThrowsAsync<DomainUnprocessableException>(() => h.LinkAsync(place.LocationId, Guid.NewGuid()));
        Assert.Equal(["weatherPointId"], unknown.Errors!.Keys);

        await using (var context = h.NewContext())
        {
            var row = await context.WeatherPoints.SingleAsync();
            row.PurgeStartedAt = Noon;
            row.PurgeMode = WeatherPurgeMode.Full;
            await context.SaveChangesAsync();
        }

        var purging = await Assert.ThrowsAsync<DomainUnprocessableException>(() => h.LinkAsync(place.LocationId, point.Id));
        Assert.Equal(["weatherPointId"], purging.Errors!.Keys);

        await using (var context = h.NewContext())
        {
            var forwarder = await context.Locations.SingleAsync(l => l.LocationId == place.LocationId);
            forwarder.MergedIntoLocationId = point.LocationId;
            await context.SaveChangesAsync();
        }

        var merged = await Assert.ThrowsAsync<DomainUnprocessableException>(() => h.LinkAsync(place.LocationId, null));
        Assert.Equal(["id"], merged.Errors!.Keys);
        Assert.Null(await h.LinkAsync(Guid.NewGuid(), null));
    }

    [Fact]
    public async Task An_archived_place_links_normally_and_null_clears_the_link()
    {
        using var h = new WeatherTestHarness();
        var point = await h.EnableAsync("Oslo");
        var archived = await h.AddLocationAsync("Old cabin", archived: true);

        Assert.Equal(point.Id, (await h.LinkAsync(archived.LocationId, point.Id))!.WeatherPoint!.WeatherPointId);
        var cleared = (await h.LinkAsync(archived.LocationId, null))!;

        Assert.Null(cleared.WeatherPoint);
        Assert.Null(cleared.DistanceKm);
        Assert.Null((await h.NewContext().Locations.SingleAsync(l => l.LocationId == archived.LocationId)).WeatherPointId);
    }

    // ── The location rules no longer know weather (AC 23) ────────────────────────────────────────

    [Fact]
    public async Task Clearing_a_linked_places_coordinates_and_merging_it_are_not_blocked_by_weather()
    {
        using var h = new WeatherTestHarness();
        var source = await h.EnableAsync("Source");
        var target = await h.AddLocationAsync("Target", latitude: 60m, longitude: 11m);
        var service = new LocationService(h.NewContext(), h.Time);

        var cleared = await service.Update(source.LocationId, new UpdateLocation
        {
            Name = "Source", Source = LocationSource.Manual,
        }, WeatherTestHarness.AdminId, CancellationToken.None, LocationClaims.Trusted);
        Assert.False(cleared!.HasCoordinates);

        // The survivor had no link, so it inherits the source's; the source's is cleared.
        await new LocationService(h.NewContext(), h.Time).MergeAsync(
            source.LocationId, target.LocationId, LocationClaims.Trusted, WeatherTestHarness.AdminId, CancellationToken.None);
        var check = h.NewContext();
        Assert.Equal(source.Id, (await check.Locations.SingleAsync(l => l.LocationId == target.LocationId)).WeatherPointId);
        Assert.Null((await check.Locations.SingleAsync(l => l.LocationId == source.LocationId)).WeatherPointId);
    }

    [Fact]
    public async Task A_merge_survivor_keeps_its_own_link()
    {
        using var h = new WeatherTestHarness();
        var source = await h.EnableAsync("Source");
        var target = await h.EnableAsync("Target");

        await new LocationService(h.NewContext(), h.Time).MergeAsync(
            source.LocationId, target.LocationId, LocationClaims.Trusted, WeatherTestHarness.AdminId, CancellationToken.None);

        Assert.Equal(target.Id, (await h.NewContext().Locations.SingleAsync(l => l.LocationId == target.LocationId)).WeatherPointId);
    }

    // ── ExistingLocation.weatherPoint (AC 2, 18) ─────────────────────────────────────────────────

    [Fact]
    public async Task The_location_read_carries_the_link_flags_and_distance_only_with_weather_read()
    {
        using var h = new WeatherTestHarness();
        var point = await h.EnableAsync("Oslo", forecast: false);
        var unlinked = await h.AddLocationAsync("Unlinked", latitude: 61m);
        h.Context.WeatherDays.Add(new Odyssey.Context.WeatherDay
        {
            WeatherPointId = point.Id, LocalDate = new DateOnly(2026, 9, 1), ObservedMeanTempC = 4, ObservedFetchedAtUtc = Noon, UpdatedAtUtc = Noon,
        });
        await h.Context.SaveChangesAsync();
        var service = new LocationService(h.NewContext(), h.Time);

        var withWeather = await service.ListAsync(new LocationsQueryParams(), CancellationToken.None, new LocationClaims { LocationsRead = true, WeatherRead = true });
        var linked = withWeather.Items.Single(l => l.LocationId == point.LocationId);
        Assert.Equal(new LinkedWeatherPointRef
        {
            WeatherPointId = point.Id, Name = "Oslo", ForecastEnabled = false, HistoryEnabled = true, HasStoredHistory = true, PurgeInProgress = false,
        }, linked.WeatherPoint);
        Assert.Equal(0, linked.WeatherPointDistanceKm);
        Assert.Equal(WeatherRules.Fingerprint(59.913m, 10.750m, 12, "Europe/Oslo"), linked.WeatherFingerprint);
        var other = withWeather.Items.Single(l => l.LocationId == unlinked.LocationId);
        Assert.Null(other.WeatherPoint);
        Assert.Null(other.WeatherPointDistanceKm);

        var without = await service.Get(point.LocationId, CancellationToken.None, new LocationClaims { LocationsRead = true });
        Assert.Null(without!.WeatherPoint);
        Assert.Null(without.WeatherPointDistanceKm);
        Assert.NotNull(without.WeatherFingerprint);
    }

    private static async Task<Guid> AddEventAsync(WeatherTestHarness h, Guid locationId)
    {
        var id = Guid.NewGuid();
        var calendarId = Guid.NewGuid();
        h.Context.Calendars.Add(new Odyssey.Context.Calendar { CalendarId = calendarId, Name = $"Calendar {id:N}" });
        h.Context.CalendarEvents.Add(new Odyssey.Context.CalendarEvent
        {
            CalendarEventId = id, CalendarId = calendarId, Title = "Walk", LocationId = locationId,
            StartDateTime = Noon.AddDays(-2), EndDateTime = Noon.AddDays(-2).AddHours(1),
        });
        await h.Context.SaveChangesAsync();
        h.Context.ChangeTracker.Clear();
        return id;
    }

    private static WeatherHourlyQuery Hourly(Guid? pointId) =>
        new() { WeatherPointId = pointId, FromUtc = Noon.AddHours(-6), ToUtc = Noon.AddHours(-1) };

    private static WeatherDailyQuery Daily(Guid? pointId) =>
        new() { WeatherPointId = pointId, From = new DateOnly(2026, 10, 1), To = new DateOnly(2026, 10, 10) };
}
