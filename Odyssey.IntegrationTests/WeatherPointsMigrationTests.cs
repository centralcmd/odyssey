// EF1002 flags interpolation into ExecuteSqlRawAsync. Every value interpolated below is a Guid or a literal
// this test wrote itself — there is no external input.
#pragma warning disable EF1002

using Microsoft.EntityFrameworkCore;
using Odyssey.Context;
using Odyssey.Context.Secrets;
using Odyssey.Core.Weather;
using Odyssey.Dtos.Journal;
using Xunit;

namespace Odyssey.IntegrationTests;

/// <summary>
/// Issue #387 §11, AC 24 — the <c>WeatherPointsExpand</c> / <c>WeatherPointsContract</c> pair against real MariaDB:
/// stop before the expand, seed the old per-location shape, stop between the two to write the rows an old API or
/// worker could still write there, then complete the run. Every "before" row is written with raw SQL: the entities
/// map the weather-points schema.
/// </summary>
[Collection(MariaDbCollection.Name)]
public class WeatherPointsMigrationTests(MariaDbFixture fixture)
{
    private const string Database = "odyssey_weather_points";
    private const string Baseline = "_UnifyLocationsContract";
    private const string Expand = "_WeatherPointsExpand";
    private const string AdminId = "weather-points-admin";
    private const string ReaderId = "weather-points-reader";
    private const string MalformedReaderId = "weather-points-malformed";

    private static readonly Guid Both = Guid.Parse("10000000-0000-0000-0000-000000000001");
    private static readonly Guid HistoryOnly = Guid.Parse("10000000-0000-0000-0000-000000000002");
    private static readonly Guid MidPurge = Guid.Parse("10000000-0000-0000-0000-000000000003");
    private static readonly Guid Orphaned = Guid.Parse("10000000-0000-0000-0000-000000000004");
    private static readonly Guid Plain = Guid.Parse("10000000-0000-0000-0000-000000000005");

    [SkippableFact]
    public async Task The_pair_converts_every_weather_location_into_a_point_under_the_same_id()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await RecreateAsync();

        List<DateOnly> daysBefore;
        await using (var context = NewContext())
        {
            await MigrationSeam.MigrateToAsync(context, Baseline);
            await AttributionUsers.EnsureAsync(context, AdminId, ReaderId, MalformedReaderId);
            await SeedOldShapeAsync(context);
            daysBefore = await ReadDatesAsync(context, $"SELECT `LocalDate` FROM `WeatherDays` WHERE `LocationId` = '{Both}' ORDER BY `LocalDate`");
        }

        // Between the two migrations an old API or worker can still write rows that know nothing of points.
        await using (var context = NewContext())
        {
            await MigrationSeam.MigrateToAsync(context, Expand);
            Assert.Equal(3, await MigrationSeam.CountAsync(context, "SELECT COUNT(*) FROM `WeatherPoints`"));
            await context.Database.ExecuteSqlRawAsync($"""
                INSERT INTO `WeatherDays` (`LocationId`, `LocalDate`, `ObservedMeanTempC`, `ObservedFetchedAtUtc`, `UpdatedAtUtc`)
                VALUES ('{Both}', '2026-10-24', 3.3, '2026-10-25 08:00:00', '2026-10-25 08:00:00');
                INSERT INTO `WeatherObservations` (`WeatherObservationId`, `LocationId`, `SiteVersion`, `Provider`, `TemperatureStationId`, `LocalDate`, `MeanTempC`, `FetchedAtUtc`)
                VALUES ('{Guid.NewGuid()}', '{Both}', 1, 1, 'SN18700', '2026-10-24', 3.3, '2026-10-25 08:00:00');
                """);
            daysBefore.Add(new DateOnly(2026, 10, 24));
        }

        await using (var context = NewContext())
        {
            await context.Database.MigrateAsync();
            Assert.Empty(await context.Database.GetPendingMigrationsAsync());
        }

        await using var verify = NewContext();

        // One point per former weather location, under the same GUID, named after the place.
        var points = await verify.WeatherPoints.AsNoTracking().OrderBy(p => p.WeatherPointId).ToListAsync();
        Assert.Equal([Both, HistoryOnly, MidPurge], points.Select(p => p.WeatherPointId));
        Assert.Equal(["Both halves", "History only", "Mid purge"], points.Select(p => p.Name));
        Assert.Equal((true, true, true), (points[0].ForecastEnabled, points[0].HistoryEnabled, points[0].IsTracked));
        Assert.Equal((false, true), (points[1].ForecastEnabled, points[1].HistoryEnabled));
        Assert.Equal(WeatherPurgeMode.HistoryOnly, points[2].PurgeMode);
        Assert.NotNull(points[2].PurgeStartedAt);
        Assert.Null(points[0].PurgeMode);
        Assert.All(points, p => Assert.NotEqual(Guid.Empty, p.SettingsVersion));
        Assert.Equal(AdminId, points[0].CreatedByUserId);
        Assert.Equal(new DateTime(2026, 10, 23, 8, 0, 0), points[0].StationResolvedAtUtc);
        Assert.Equal(2, points[0].CurrentSiteVersion);

        // Every data row keyed on its point, the late writers re-backfilled, the orphans gone.
        Assert.Equal(2, await verify.WeatherPointSites.CountAsync(s => s.WeatherPointId == Both));
        Assert.Equal(3, await verify.WeatherPointSchedules.CountAsync());
        Assert.Equal(1, await verify.WeatherStations.CountAsync(s => s.WeatherPointId == Both));
        Assert.Equal(1, await verify.WeatherForecastRuns.CountAsync(r => r.WeatherPointId == Both));
        Assert.Equal(3, await verify.WeatherForecastSteps.CountAsync(s => s.WeatherPointId == Both));
        Assert.Equal(1, await verify.WeatherObservations.CountAsync(o => o.WeatherPointId == HistoryOnly));
        Assert.Equal(1, await verify.WeatherObservations.CountAsync(o => o.WeatherPointId == Both && o.LocalDate == new DateOnly(2026, 10, 24)));
        Assert.Equal(0, await MigrationSeam.CountAsync(verify, $"""
            SELECT (SELECT COUNT(*) FROM `WeatherDays` WHERE `WeatherPointId` = '{Orphaned}')
                 + (SELECT COUNT(*) FROM `WeatherObservations` WHERE `WeatherPointId` = '{Orphaned}')
                 + (SELECT COUNT(*) FROM `WeatherForecastRuns` WHERE `WeatherPointId` = '{Orphaned}')
            """));

        // Each converted place linked to its point; the others unlinked.
        var links = await verify.Locations.AsNoTracking().ToDictionaryAsync(l => l.LocationId, l => l.WeatherPointId);
        Assert.Equal(Both, links[Both]);
        Assert.Equal(HistoryOnly, links[HistoryOnly]);
        Assert.Equal(MidPurge, links[MidPurge]);
        Assert.Null(links[Orphaned]);
        Assert.Null(links[Plain]);

        // The settings keys renamed in place, value and UpdatedBy intact; the old keys gone.
        var settings = await verify.SystemSettings.AsNoTracking().Where(s => s.Key.StartsWith("Weather")).ToDictionaryAsync(s => s.Key);
        Assert.Equal((Both.ToString(), AdminId), (settings[SystemSettingsKeys.WeatherDefaultPointId].Value, settings[SystemSettingsKeys.WeatherDefaultPointId].UpdatedBy));
        Assert.Equal(("7", AdminId), (settings[SystemSettingsKeys.WeatherMaxTrackedPoints].Value, settings[SystemSettingsKeys.WeatherMaxTrackedPoints].UpdatedBy));
        Assert.DoesNotContain("WeatherDefaultLocationId", settings.Keys);
        Assert.DoesNotContain("WeatherMaxTrackedLocations", settings.Keys);

        // The valid preference rewritten, the malformed one untouched.
        var preferences = await verify.UserPreferences.AsNoTracking()
            .Where(p => p.Key == "weather-preferences").ToDictionaryAsync(p => p.UserId, p => p.PreferencesJson);
        using (var json = System.Text.Json.JsonDocument.Parse(preferences[ReaderId]!))
        {
            Assert.Equal(HistoryOnly, json.RootElement.GetProperty("defaultPointId").GetGuid());
            Assert.False(json.RootElement.TryGetProperty("defaultLocationId", out _));
        }

        Assert.Equal("{not json", preferences[MalformedReaderId]);

        // The read path serves the same days for the point as it did for the location.
        var read = new WeatherReadService(verify, new WeatherFetchService(verify, new NoForecasts(), new NoObservations(), new Settings(), new NoSecrets()),
            new Settings());
        var daily = await read.DailyAsync(new WeatherDailyQuery
        {
            WeatherPointId = Both, From = new DateOnly(2026, 1, 1), To = new DateOnly(2026, 10, 24),
        }, ReaderId, CancellationToken.None);
        Assert.Equal(daysBefore, daily.Days.Select(d => d.Date));
        Assert.Equal(WeatherResolvedFrom.Explicit, daily.ResolvedFrom);
    }

    [SkippableFact]
    public async Task Down_restores_the_old_schema()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await RecreateAsync();
        await using var context = NewContext();
        await context.Database.MigrateAsync();

        await MigrationSeam.MigrateToAsync(context, Baseline);

        Assert.True(await MigrationSeam.TableExistsAsync(context, "WeatherLocations"));
        Assert.True(await MigrationSeam.TableExistsAsync(context, "WeatherLocationSites"));
        Assert.False(await MigrationSeam.TableExistsAsync(context, "WeatherPoints"));
        Assert.Equal(0, await MigrationSeam.CountAsync(context, """
            SELECT COUNT(*) FROM information_schema.COLUMNS
            WHERE TABLE_SCHEMA = DATABASE() AND COLUMN_NAME = 'WeatherPointId'
            """));
        Assert.Equal(2, await MigrationSeam.CountAsync(context,
            "SELECT COUNT(*) FROM `SystemSettings` WHERE `Key` IN ('WeatherDefaultLocationId', 'WeatherMaxTrackedLocations')"));

        // And forward again, cleanly.
        await context.Database.MigrateAsync();
        Assert.True(await MigrationSeam.TableExistsAsync(context, "WeatherPoints"));
    }

    /// <summary>
    /// §11's fixture: a place with both halves on (two sites, a run, steps, a station, a day), one with history
    /// only (an observation and a day), one mid-purge, an orphaned data row on a place with no weather row, a
    /// household default, a user preference and a malformed preference row.
    /// </summary>
    private static async Task SeedOldShapeAsync(OdysseyContext context)
    {
        foreach (var (id, name) in new[] { (Both, "Both halves"), (HistoryOnly, "History only"), (MidPurge, "Mid purge"), (Orphaned, "Orphaned"), (Plain, "Plain") })
        {
            await context.Database.ExecuteSqlRawAsync($"""
                INSERT INTO `Locations` (`LocationId`, `Name`, `Source`, `Latitude`, `Longitude`, `AltitudeMeters`, `TimeZoneId`, `CreatedAt`, `UpdatedAt`)
                VALUES ('{id}', '{name}', 1, 59.91273, 10.75027, 12, 'Europe/Oslo', '2026-10-01', '2026-10-01')
                """);
        }

        await context.Database.ExecuteSqlRawAsync($"""
            SET FOREIGN_KEY_CHECKS = 0;
            INSERT INTO `WeatherLocations` (`LocationId`, `ForecastEnabled`, `HistoryEnabled`, `IsTracked`, `HistoryFrom`, `EnabledAt`, `CurrentSiteVersion`, `PurgeStartedAt`, `StationResolvedAtUtc`, `CreatedByUserId`, `UpdatedByUserId`, `CreatedAt`, `UpdatedAt`)
            VALUES ('{Both}', 1, 1, 1, '2025-10-01', '2026-10-01', 2, NULL, '2026-10-23 08:00:00', '{AdminId}', '{AdminId}', '2026-10-01', '2026-10-02'),
                   ('{HistoryOnly}', 0, 1, 0, '2025-10-01', '2026-10-01', 1, NULL, NULL, '{AdminId}', '{AdminId}', '2026-10-01', '2026-10-01'),
                   ('{MidPurge}', 1, 0, 0, '2025-10-01', '2026-10-01', 1, '2026-10-20 10:00:00', NULL, '{AdminId}', '{AdminId}', '2026-10-01', '2026-10-01');
            SET FOREIGN_KEY_CHECKS = 1;
            INSERT INTO `WeatherLocationSites` (`LocationId`, `SiteVersion`, `Latitude`, `Longitude`, `AltitudeMeters`, `TimeZoneId`, `AppliedAt`, `AppliedByUserId`)
            VALUES ('{Both}', 1, 59.913, 10.750, 12, 'Europe/Oslo', '2026-10-01', '{AdminId}'),
                   ('{Both}', 2, 59.950, 10.750, 12, 'Europe/Oslo', '2026-10-10', '{AdminId}'),
                   ('{HistoryOnly}', 1, 59.913, 10.750, 12, 'Europe/Oslo', '2026-10-01', '{AdminId}'),
                   ('{MidPurge}', 1, 59.913, 10.750, 12, 'Europe/Oslo', '2026-10-01', '{AdminId}');
            INSERT INTO `WeatherLocationSchedules` (`LocationId`, `Version`, `ForecastConsecutiveFailures`, `ObservationConsecutiveFailures`, `ArchivedSkipLogged`, `LeaseUntilUtc`)
            VALUES ('{Both}', UUID(), 0, 0, 0, NULL),
                   ('{HistoryOnly}', UUID(), 0, 0, 0, NULL),
                   ('{MidPurge}', UUID(), 0, 0, 0, '9999-12-31 23:59:59');
            INSERT INTO `WeatherStations` (`LocationId`, `Group`, `StationId`, `Name`, `DistanceKm`, `ResolvedAtUtc`)
            VALUES ('{Both}', 0, 'SN18700', 'OSLO - BLINDERN', 3.2, '2026-10-23 08:00:00');
            """);

        var run = Guid.NewGuid();
        var orphanRun = Guid.NewGuid();
        await context.Database.ExecuteSqlRawAsync($"""
            INSERT INTO `WeatherForecastRuns` (`WeatherForecastRunId`, `LocationId`, `SiteVersion`, `Provider`, `ProviderUpdatedAt`, `FetchedAtUtc`, `Latitude`, `Longitude`)
            VALUES ('{run}', '{Both}', 2, 1, '2026-10-23 11:30:00', '2026-10-23 11:31:00', 59.950, 10.750),
                   ('{orphanRun}', '{Orphaned}', 1, 1, '2026-10-23 11:30:00', '2026-10-23 11:31:00', 59.913, 10.750);
            INSERT INTO `WeatherForecastSteps` (`WeatherForecastRunId`, `LocationId`, `SiteVersion`, `RunProviderUpdatedAt`, `RunFetchedAtUtc`, `TimeUtc`, `PeriodHours`, `AirTemperatureC`)
            VALUES ('{run}', '{Both}', 2, '2026-10-23 11:30:00', '2026-10-23 11:31:00', '2026-10-23 12:00:00', 1, 5.0),
                   ('{run}', '{Both}', 2, '2026-10-23 11:30:00', '2026-10-23 11:31:00', '2026-10-23 13:00:00', 1, 5.5),
                   ('{run}', '{Both}', 2, '2026-10-23 11:30:00', '2026-10-23 11:31:00', '2026-10-23 14:00:00', 1, 6.0),
                   ('{orphanRun}', '{Orphaned}', 1, '2026-10-23 11:30:00', '2026-10-23 11:31:00', '2026-10-23 12:00:00', 1, 5.0);
            INSERT INTO `WeatherObservations` (`WeatherObservationId`, `LocationId`, `SiteVersion`, `Provider`, `TemperatureStationId`, `LocalDate`, `MeanTempC`, `FetchedAtUtc`)
            VALUES ('{Guid.NewGuid()}', '{HistoryOnly}', 1, 1, 'SN18700', '2026-10-22', 6.8, '2026-10-23 08:02:00'),
                   ('{Guid.NewGuid()}', '{Orphaned}', 1, 1, 'SN18700', '2026-10-22', 6.8, '2026-10-23 08:02:00');
            INSERT INTO `WeatherDays` (`LocationId`, `LocalDate`, `ForecastCoverageHours`, `ForecastMaxTempC`, `ObservedMeanTempC`, `ObservedFetchedAtUtc`, `UpdatedAtUtc`)
            VALUES ('{Both}', '2026-10-22', NULL, NULL, 6.8, '2026-10-23 08:02:00', '2026-10-23 08:02:00'),
                   ('{Both}', '2026-10-23', 24, 9.0, NULL, NULL, '2026-10-23 11:31:00'),
                   ('{HistoryOnly}', '2026-10-22', NULL, NULL, 6.8, '2026-10-23 08:02:00', '2026-10-23 08:02:00'),
                   ('{Orphaned}', '2026-10-22', NULL, NULL, 6.8, '2026-10-23 08:02:00', '2026-10-23 08:02:00');
            """);

        await context.Database.ExecuteSqlRawAsync($"""
            UPDATE `SystemSettings` SET `Value` = '{Both}', `UpdatedBy` = '{AdminId}' WHERE `Key` = 'WeatherDefaultLocationId';
            UPDATE `SystemSettings` SET `Value` = '7', `UpdatedBy` = '{AdminId}' WHERE `Key` = 'WeatherMaxTrackedLocations';
            """);

        // Parameterised: the JSON's braces would otherwise read as format placeholders.
        var valid = $$"""{"defaultLocationId":"{{HistoryOnly}}"}""";
        const string malformed = "{not json";
        await context.Database.ExecuteSqlAsync($"""
            INSERT INTO `UserPreferences` (`UserPreferenceId`, `UserId`, `Key`, `PreferencesJson`, `UpdatedAt`)
            VALUES ({Guid.NewGuid()}, {ReaderId}, 'weather-preferences', {valid}, '2026-10-01'),
                   ({Guid.NewGuid()}, {MalformedReaderId}, 'weather-preferences', {malformed}, '2026-10-01')
            """);
    }

    private static async Task<List<DateOnly>> ReadDatesAsync(OdysseyContext context, string sql)
    {
        await using var command = context.Database.GetDbConnection().CreateCommand();
        command.CommandText = sql;
        await context.Database.OpenConnectionAsync();
        try
        {
            var dates = new List<DateOnly>();
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                dates.Add(DateOnly.FromDateTime(reader.GetDateTime(0)));
            }

            return dates;
        }
        finally
        {
            await context.Database.CloseConnectionAsync();
        }
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

    private sealed class Settings : IWeatherSettingsLookup
    {
        public Task<WeatherSettings> GetAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new WeatherSettings("ops@example.org", 60, 5, null, false));
    }

    private sealed class NoSecrets : ISecretSettingsReader
    {
        public Task<SecretResult> GetAsync(string key, CancellationToken cancellationToken = default) => Task.FromResult(SecretResult.NotSet);
    }

    private sealed class NoForecasts : IWeatherForecastSource
    {
        public Task<ForecastFetchResult> FetchAsync(
            WeatherSiteQuery site, string contact, string? ifModifiedSince, TimeSpan budget, CancellationToken cancellationToken) =>
            Task.FromResult(new ForecastFetchResult(WeatherFetchOutcome.NotModified));
    }

    private sealed class NoObservations : IWeatherObservationSource
    {
        public Task<StationLookupResult> FindStationAsync(WeatherSiteQuery site, WeatherMeasureGroup group, string contact, string clientId, CancellationToken cancellationToken) =>
            Task.FromResult(new StationLookupResult(true, null));

        public Task<ObservationFetchResult> FetchDailyAsync(
            IReadOnlyCollection<string> stationIds, IReadOnlyCollection<string> elements, DateOnly from, DateOnly to, string contact, string clientId, CancellationToken cancellationToken) =>
            Task.FromResult(new ObservationFetchResult(true, []));
    }
}
