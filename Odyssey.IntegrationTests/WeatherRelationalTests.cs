// EF1002 flags interpolation into ExecuteSqlRawAsync. Every value interpolated below is a constant or a Guid
// this test generated itself — there is no external input.
#pragma warning disable EF1002

using Microsoft.EntityFrameworkCore;
using Odyssey.Context;
using Odyssey.Context.Secrets;
using Odyssey.Core.Weather;
using Odyssey.Dtos.Journal;
using Xunit;

namespace Odyssey.IntegrationTests;

/// <summary>
/// The relational half of issue #348 (AC 43, 44 and the N2 purge): the migration, the RESTRICT keys that
/// keep a location and its current site from being deleted under weather, the bulk purge in batches and
/// its resumption, and write fencing on the real concurrency token. None of it is observable on the EF
/// InMemory provider, which runs no migrations, enforces no key and cannot run <c>ExecuteDelete</c>.
/// </summary>
[Collection(MariaDbCollection.Name)]
public class WeatherRelationalTests(MariaDbFixture fixture)
{
    private const string Database = "odyssey_weather";
    private const string PreviousMigration = "_AddLocations";
    private const string ThisMigration = "_AddWeather";
    private const string SplitMigration = "_SplitWeatherForecastAndHistory";
    private const string AdminId = "weather-admin";

    private static readonly DateTime Noon = new(2026, 10, 23, 12, 0, 0, DateTimeKind.Utc);

    [SkippableFact]
    public async Task The_migration_creates_the_tables_and_seeds_the_four_settings()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await RecreateAsync();

        await using var context = NewContext();
        await MigrationSeam.MigrateToAsync(context, PreviousMigration);
        Assert.False(await MigrationSeam.TableExistsAsync(context, "WeatherLocations"));
        await MigrationSeam.MigrateToAsync(context, ThisMigration);

        foreach (var table in new[] { "WeatherLocations", "WeatherLocationSites", "WeatherLocationSchedules", "WeatherForecastRuns",
                     "WeatherForecastSteps", "WeatherObservations", "WeatherDays" })
        {
            Assert.True(await MigrationSeam.TableExistsAsync(context, table), table);
        }

        var keys = await context.SystemSettings.Where(s => s.Key.StartsWith("Weather")).Select(s => s.Key).ToListAsync();
        Assert.Equal(
            [SystemSettingsKeys.WeatherDefaultLocationId, SystemSettingsKeys.WeatherMaxTrackedLocations,
             SystemSettingsKeys.WeatherRefreshIntervalMinutes, SystemSettingsKeys.WeatherUserAgentContact],
            keys.Order(StringComparer.Ordinal));
    }

    [SkippableFact]
    public async Task The_split_migration_gives_both_halves_the_old_switch()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await RecreateAsync();

        await using var context = NewContext();
        await MigrationSeam.MigrateToAsync(context, ThisMigration);
        var on = Guid.NewGuid();
        var off = Guid.NewGuid();
        await context.Database.ExecuteSqlRawAsync($"""
            SET FOREIGN_KEY_CHECKS = 0;
            INSERT INTO `WeatherLocations` (`LocationId`, `Enabled`, `IsTracked`, `HistoryFrom`, `EnabledAt`, `CurrentSiteVersion`, `CreatedAt`, `UpdatedAt`)
            VALUES ('{on}', 1, 1, '2025-01-01', '2026-10-01', 1, '2026-10-01', '2026-10-01'),
                   ('{off}', 0, 0, '2025-01-01', '2026-10-01', 1, '2026-10-01', '2026-10-01');
            SET FOREIGN_KEY_CHECKS = 1;
            """);

        await MigrationSeam.MigrateToAsync(context, SplitMigration);

        var rows = await context.WeatherLocations.AsNoTracking().ToDictionaryAsync(w => w.LocationId);
        Assert.True(rows[on].ForecastEnabled);
        Assert.True(rows[on].HistoryEnabled);
        Assert.False(rows[off].ForecastEnabled);
        Assert.False(rows[off].HistoryEnabled);
    }

    [SkippableFact]
    public async Task With_the_forecast_on_the_purge_removes_only_the_measured_data()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await MigratedAsync();
        var locationId = await EnabledLocationAsync();
        await SeedHistoryAsync(locationId, steps: 50);
        await using (var context = NewContext())
        {
            // A measured-only day goes entirely; the forecast's day loses only its measured half.
            await context.Database.ExecuteSqlRawAsync($"""
                INSERT INTO `WeatherDays` (`LocationId`, `LocalDate`, `ObservedMeanTempC`, `ObservedFetchedAtUtc`, `UpdatedAtUtc`)
                VALUES ('{locationId}', '2026-10-22', 6.8, '2026-10-23 08:02:00', '2026-10-23 08:02:00')
                """);
            await context.Database.ExecuteSqlRawAsync(
                $"UPDATE `WeatherDays` SET `ObservedMeanTempC` = 4.1, `ObservedFetchedAtUtc` = '2026-10-23 08:02:00' WHERE `LocationId` = '{locationId}' AND `LocalDate` = '2026-10-23'");
            await context.Database.ExecuteSqlRawAsync(
                $"UPDATE `WeatherLocations` SET `FrostStationId` = 'SN18700', `StationResolvedAtUtc` = '2026-10-23 08:00:00' WHERE `LocationId` = '{locationId}'");
            await context.Database.ExecuteSqlRawAsync(
                $"UPDATE `WeatherLocationSchedules` SET `ObservationsCheckedThroughDate` = '2026-10-22', `LastObservationSuccessUtc` = '2026-10-23 08:02:00' WHERE `LocationId` = '{locationId}'");
            await Admin(context).UpsertAsync(locationId,
                new UpsertWeatherLocation { ForecastEnabled = true, HistoryEnabled = false, IsTracked = true }, AdminId, CancellationToken.None);
        }

        await using (var context = NewContext())
        {
            Assert.True(await Admin(context).PurgeAsync(locationId, AdminId, CancellationToken.None));
        }

        await using var verify = NewContext();
        var row = await verify.WeatherLocations.SingleAsync(w => w.LocationId == locationId);
        Assert.True(row.ForecastEnabled);
        Assert.Null(row.PurgeStartedAt);
        Assert.Null(row.FrostStationId);
        Assert.Null(row.StationResolvedAtUtc);
        Assert.Equal(50, await verify.WeatherForecastSteps.CountAsync(s => s.LocationId == locationId));
        Assert.True(await verify.WeatherForecastRuns.AnyAsync(r => r.LocationId == locationId));
        Assert.Equal(2, await verify.WeatherLocationSites.CountAsync(s => s.LocationId == locationId));
        Assert.False(await verify.WeatherObservations.AnyAsync(o => o.LocationId == locationId));
        var day = await verify.WeatherDays.SingleAsync(d => d.LocationId == locationId);
        Assert.Equal(new DateOnly(2026, 10, 23), day.LocalDate);
        Assert.Equal(24, day.ForecastCoverageHours);
        Assert.Null(day.ObservedMeanTempC);
        Assert.Null(day.ObservedFetchedAtUtc);
        var schedule = await verify.WeatherLocationSchedules.SingleAsync(s => s.LocationId == locationId);
        Assert.Null(schedule.ObservationsCheckedThroughDate);
        Assert.Null(schedule.LastObservationSuccessUtc);
        Assert.Null(schedule.LeaseUntilUtc);
    }

    [SkippableFact]
    public async Task A_location_with_weather_cannot_be_deleted_and_neither_can_its_current_site()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await MigratedAsync();
        var locationId = await EnabledLocationAsync();

        await using var context = NewContext();
        var location = await context.Locations.SingleAsync(l => l.LocationId == locationId);
        context.Locations.Remove(location);
        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());

        await using var other = NewContext();
        await Assert.ThrowsAnyAsync<Exception>(() => other.Database.ExecuteSqlRawAsync(
            $"DELETE FROM `WeatherLocationSites` WHERE `LocationId` = '{locationId}' AND `SiteVersion` = 1"));
    }

    [SkippableFact]
    public async Task The_purge_deletes_every_weather_row_in_batches_and_leaves_the_location()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await MigratedAsync();
        var locationId = await EnabledLocationAsync();
        var keep = await EnabledLocationAsync("Cabin");
        await SeedHistoryAsync(locationId, steps: RelationalWeatherHistoryPurger.BatchSize * 2 + 500);
        await SeedHistoryAsync(keep, steps: 10);

        await using (var context = NewContext())
        {
            await Admin(context).UpsertAsync(locationId, new UpsertWeatherLocation { ForecastEnabled = false, HistoryEnabled = false, IsTracked = true }, AdminId, CancellationToken.None);
        }

        await using (var context = NewContext())
        {
            Assert.True(await Admin(context).PurgeAsync(locationId, AdminId, CancellationToken.None));
        }

        await using var verify = NewContext();
        Assert.False(await verify.WeatherLocations.AnyAsync(w => w.LocationId == locationId));
        Assert.False(await verify.WeatherLocationSites.AnyAsync(s => s.LocationId == locationId));
        Assert.False(await verify.WeatherLocationSchedules.AnyAsync(s => s.LocationId == locationId));
        Assert.False(await verify.WeatherForecastRuns.AnyAsync(r => r.LocationId == locationId));
        Assert.False(await verify.WeatherForecastSteps.AnyAsync(s => s.LocationId == locationId));
        Assert.False(await verify.WeatherObservations.AnyAsync(o => o.LocationId == locationId));
        Assert.False(await verify.WeatherDays.AnyAsync(d => d.LocationId == locationId));
        Assert.True(await verify.Locations.AnyAsync(l => l.LocationId == locationId));

        Assert.Equal(10, await verify.WeatherForecastSteps.CountAsync(s => s.LocationId == keep));
        Assert.True(await verify.WeatherLocations.AnyAsync(w => w.LocationId == keep));
    }

    [SkippableFact]
    public async Task An_interrupted_purge_stays_fenced_and_a_repeat_completes_it()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await MigratedAsync();
        var locationId = await EnabledLocationAsync();
        await SeedHistoryAsync(locationId, steps: 50);

        // The state after step 1 and part of step 2: the fence is up, some rows are gone, the rest remain.
        await using (var context = NewContext())
        {
            await Admin(context).UpsertAsync(locationId, new UpsertWeatherLocation { ForecastEnabled = false, HistoryEnabled = false, IsTracked = true }, AdminId, CancellationToken.None);
            await context.Database.ExecuteSqlRawAsync(
                $"UPDATE `WeatherLocations` SET `PurgeStartedAt` = UTC_TIMESTAMP() WHERE `LocationId` = '{locationId}'");
            await context.Database.ExecuteSqlRawAsync(
                $"UPDATE `WeatherLocationSchedules` SET `LeaseUntilUtc` = '9999-12-31 23:59:59', `Version` = UUID() WHERE `LocationId` = '{locationId}'");
            await context.Database.ExecuteSqlRawAsync(
                $"DELETE FROM `WeatherForecastSteps` WHERE `LocationId` = '{locationId}' LIMIT 20");
        }

        await using (var context = NewContext())
        {
            var row = await Admin(context).GetAsync(locationId, CancellationToken.None);
            Assert.True(row!.PurgeInProgress);
            var refused = await Assert.ThrowsAsync<WeatherConflictException>(() => Admin(context).UpsertAsync(locationId,
                new UpsertWeatherLocation { ForecastEnabled = true, HistoryEnabled = true, IsTracked = true }, AdminId, CancellationToken.None));
            Assert.Equal(WeatherConflictException.PurgeInProgress, refused.Code);
        }

        await using (var context = NewContext())
        {
            // The lease is far future, yet a resumed purge is not refused for it.
            Assert.True(await Admin(context).PurgeAsync(locationId, AdminId, CancellationToken.None));
        }

        await using var verify = NewContext();
        Assert.False(await verify.WeatherLocations.AnyAsync(w => w.LocationId == locationId));
        Assert.False(await verify.WeatherForecastSteps.AnyAsync(s => s.LocationId == locationId));
        Assert.False(await verify.WeatherLocationSites.AnyAsync(s => s.LocationId == locationId));
    }

    [SkippableFact]
    public async Task A_fetch_in_flight_when_weather_is_turned_off_stores_nothing()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await MigratedAsync();
        var locationId = await EnabledLocationAsync();

        var source = new Forecasts(async () =>
        {
            await using var other = NewContext();
            await Admin(other).UpsertAsync(locationId, new UpsertWeatherLocation { ForecastEnabled = false, HistoryEnabled = false, IsTracked = true }, AdminId, CancellationToken.None);
        });

        await using (var context = NewContext())
        {
            var attempt = await new WeatherFetchService(context, source, new NoObservations(), new Settings(), new NoSecrets(), new FixedTime())
                .FetchForecastAsync(locationId, WeatherFetchMode.Scheduled, CancellationToken.None);
            Assert.Equal(WeatherAttemptStatus.Discarded, attempt.Status);
        }

        await using var verify = NewContext();
        Assert.False(await verify.WeatherForecastRuns.AnyAsync(r => r.LocationId == locationId));
        Assert.False(await verify.WeatherForecastSteps.AnyAsync(s => s.LocationId == locationId));
    }

    [SkippableFact]
    public async Task A_fetch_round_trips_decimal_values_and_utc_times()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await MigratedAsync();
        var locationId = await EnabledLocationAsync();

        await using (var context = NewContext())
        {
            var attempt = await new WeatherFetchService(context, new Forecasts(null), new NoObservations(), new Settings(), new NoSecrets(), new FixedTime())
                .FetchForecastAsync(locationId, WeatherFetchMode.Scheduled, CancellationToken.None);
            Assert.Equal(WeatherAttemptStatus.Completed, attempt.Status);
        }

        await using var verify = NewContext();
        var step = await verify.WeatherForecastSteps.OrderBy(s => s.TimeUtc).FirstAsync(s => s.LocationId == locationId);
        Assert.Equal(-7.5m, step.AirTemperatureC);
        Assert.Equal(1013.2m, step.AirPressureHpa);
        Assert.Equal(Noon, step.TimeUtc);
        var day = await verify.WeatherDays.SingleAsync(d => d.LocationId == locationId && d.LocalDate == new DateOnly(2026, 10, 23));
        Assert.Equal(-7.5m, day.ForecastMinTempC);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────────────────────────

    private async Task<Guid> EnabledLocationAsync(string name = "Home")
    {
        await using var context = NewContext();
        var location = new Location
        {
            LocationId = Guid.NewGuid(),
            Name = name + " " + Guid.NewGuid().ToString("N")[..6],
            Latitude = 59.91273m,
            Longitude = 10.75027m,
            AltitudeMeters = 12,
            TimeZoneId = "Europe/Oslo",
            Source = LocationSource.Manual,
            CreatedAt = Noon,
            UpdatedAt = Noon,
        };
        context.Locations.Add(location);
        await context.SaveChangesAsync();

        var result = await Admin(context).UpsertAsync(location.LocationId, new UpsertWeatherLocation
        {
            ForecastEnabled = true, HistoryEnabled = true,
            IsTracked = true,
            ExpectedLocationFingerprint = WeatherCoordinates.Fingerprint(location),
        }, AdminId, CancellationToken.None);
        Assert.NotNull(result);
        return location.LocationId;
    }

    /// <summary>One run with <paramref name="steps"/> steps, plus an observation, a day and a second site — written in bulk.</summary>
    private async Task SeedHistoryAsync(Guid locationId, int steps)
    {
        await using var context = NewContext();
        var runId = Guid.NewGuid();
        await context.Database.ExecuteSqlRawAsync($"""
            INSERT INTO `WeatherForecastRuns` (`WeatherForecastRunId`, `LocationId`, `SiteVersion`, `Provider`, `ProviderUpdatedAt`, `FetchedAtUtc`, `Latitude`, `Longitude`)
            VALUES ('{runId}', '{locationId}', 1, 1, '2026-10-23 11:30:00', '2026-10-23 11:31:00', 59.913, 10.750)
            """);
        await context.Database.ExecuteSqlRawAsync($"""
            INSERT INTO `WeatherForecastSteps` (`WeatherForecastRunId`, `LocationId`, `SiteVersion`, `RunProviderUpdatedAt`, `RunFetchedAtUtc`, `TimeUtc`, `PeriodHours`, `AirTemperatureC`)
            SELECT '{runId}', '{locationId}', 1, '2026-10-23 11:30:00', '2026-10-23 11:31:00', DATE_ADD('2026-01-01', INTERVAL seq MINUTE), 1, 5.0
            FROM seq_1_to_{steps}
            """);
        await context.Database.ExecuteSqlRawAsync($"""
            INSERT INTO `WeatherObservations` (`WeatherObservationId`, `LocationId`, `SiteVersion`, `Provider`, `StationId`, `LocalDate`, `MeanTempC`, `FetchedAtUtc`)
            VALUES ('{Guid.NewGuid()}', '{locationId}', 1, 1, 'SN18700', '2026-10-22', 6.8, '2026-10-23 08:02:00')
            """);
        await context.Database.ExecuteSqlRawAsync($"""
            INSERT INTO `WeatherDays` (`LocationId`, `LocalDate`, `ForecastCoverageHours`, `UpdatedAtUtc`)
            VALUES ('{locationId}', '2026-10-23', 24, '2026-10-23 11:31:00')
            """);
        await context.Database.ExecuteSqlRawAsync($"""
            INSERT INTO `WeatherLocationSites` (`LocationId`, `SiteVersion`, `Latitude`, `Longitude`, `TimeZoneId`, `AppliedAt`)
            VALUES ('{locationId}', 2, 60.393, 5.324, 'Europe/Oslo', '2026-10-23 11:40:00')
            """);
        await context.Database.ExecuteSqlRawAsync(
            $"UPDATE `WeatherLocations` SET `CurrentSiteVersion` = 2 WHERE `LocationId` = '{locationId}'");
    }

    private static WeatherLocationService Admin(OdysseyContext context) =>
        new(context, new WeatherFetchService(context, new Forecasts(null), new NoObservations(), new Settings(), new NoSecrets(), new FixedTime()),
            new Settings(), new RelationalWeatherHistoryPurger(context), new FixedTime());

    private async Task MigratedAsync()
    {
        await RecreateAsync();
        await using var context = NewContext();
        await context.Database.MigrateAsync();
        await AttributionUsers.EnsureAsync(context, AdminId);
    }

    private OdysseyContext NewContext() => new(new DbContextOptionsBuilder<OdysseyContext>()
        .UseMySql(fixture.ConnectionStringFor(Database), ServerVersion.AutoDetect(fixture.OdysseyConnectionString))
        .Options);

    private async Task RecreateAsync()
    {
        await using var server = new OdysseyContext(new DbContextOptionsBuilder<OdysseyContext>()
            .UseMySql(fixture.OdysseyConnectionString, ServerVersion.AutoDetect(fixture.OdysseyConnectionString))
            .Options);
        await server.Database.ExecuteSqlRawAsync($"DROP DATABASE IF EXISTS `{Database}`");
        await server.Database.ExecuteSqlRawAsync($"CREATE DATABASE `{Database}`");
    }

    private sealed class FixedTime : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(Noon);
    }

    private sealed class Settings : IWeatherSettingsLookup
    {
        public Task<WeatherSettings> GetAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new WeatherSettings("ops@example.org", 60, 5, null, false));
    }

    private sealed class NoSecrets : ISecretSettingsReader
    {
        public Task<SecretResult> GetAsync(string key, CancellationToken cancellationToken = default) => Task.FromResult(SecretResult.NotSet);
    }

    /// <summary>Answers with a 24-step run; runs <paramref name="duringRequest"/> while the request is "out".</summary>
    private sealed class Forecasts(Func<Task>? duringRequest) : IWeatherForecastSource
    {
        public async Task<ForecastFetchResult> FetchAsync(
            WeatherSiteQuery site, string contact, string? ifModifiedSince, TimeSpan budget, CancellationToken cancellationToken)
        {
            if (duringRequest is not null)
            {
                await duringRequest();
            }

            var steps = Enumerable.Range(0, 24).Select(h => new ForecastStepData(
                Noon.AddHours(h), 1, -7.5m, 70, 50, 1013.2m, 3, 6, 180, 0.5m, "cloudy", 0.2m, 20, null, null)).ToList();
            return new ForecastFetchResult(WeatherFetchOutcome.Ok, new ForecastPayload(Noon.AddMinutes(-30), steps), Noon.AddMinutes(30), "x");
        }
    }

    private sealed class NoObservations : IWeatherObservationSource
    {
        public Task<StationLookupResult> FindStationAsync(WeatherSiteQuery site, string contact, string clientId, CancellationToken cancellationToken) =>
            Task.FromResult(new StationLookupResult(true, null));

        public Task<ObservationFetchResult> FetchDailyAsync(
            string stationId, DateOnly from, DateOnly to, string contact, string clientId, CancellationToken cancellationToken) =>
            Task.FromResult(new ObservationFetchResult(true, []));
    }
}
