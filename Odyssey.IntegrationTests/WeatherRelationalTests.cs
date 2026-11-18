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
/// The relational half of issue #348 (AC 43, 44 and the N2 purge) and of the weather points of issue #387
/// (AC 11, 21, 22, 25): the historical migrations, the point keys and their on-delete behaviour, the bulk purge
/// in batches and its resumption, the link races, and write fencing on the real concurrency token. None of it is observable on the EF
/// InMemory provider, which runs no migrations, enforces no key and cannot run <c>ExecuteDelete</c>.
/// </summary>
[Collection(MariaDbCollection.Name)]
public class WeatherRelationalTests(MariaDbFixture fixture)
{
    private const string Database = "odyssey_weather";
    private const string PreviousMigration = "_AddLocations";
    private const string ThisMigration = "_AddWeather";
    private const string SplitMigration = "_SplitWeatherForecastAndHistory";
    private const string StationMigration = "_WeatherStationPerMeasure";
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
        // The keys as this migration seeded them; WeatherPointsContract renames the last two (issue #387).
        Assert.Equal(
            ["WeatherDefaultLocationId", "WeatherMaxTrackedLocations",
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

        // The entity maps the weather-points schema now (issue #387), so this pre-#387 shape is read raw.
        Assert.Equal(2L, await MigrationSeam.CountAsync(context,
            $"SELECT COUNT(*) FROM `WeatherLocations` WHERE `LocationId` = '{on}' AND `ForecastEnabled` = 1 AND `HistoryEnabled` = 1")
            + await MigrationSeam.CountAsync(context,
            $"SELECT COUNT(*) FROM `WeatherLocations` WHERE `LocationId` = '{off}' AND `ForecastEnabled` = 0 AND `HistoryEnabled` = 0"));
    }

    [SkippableFact]
    public async Task The_station_migration_starts_measured_history_over_and_keeps_the_forecast()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await RecreateAsync();

        await using var context = NewContext();
        await MigrationSeam.MigrateToAsync(context, SplitMigration);
        var id = Guid.NewGuid();
        await context.Database.ExecuteSqlRawAsync($"""
            SET FOREIGN_KEY_CHECKS = 0;
            INSERT INTO `WeatherLocations` (`LocationId`, `ForecastEnabled`, `HistoryEnabled`, `IsTracked`, `HistoryFrom`, `EnabledAt`, `CurrentSiteVersion`, `CreatedAt`, `UpdatedAt`, `FrostStationId`, `StationResolvedAtUtc`)
            VALUES ('{id}', 1, 1, 1, '2025-01-01', '2026-10-01', 1, '2026-10-01', '2026-10-01', 'SN18700', '2026-10-01');
            INSERT INTO `WeatherLocationSchedules` (`LocationId`, `Version`, `ObservationsCheckedThroughDate`, `ForecastConsecutiveFailures`, `ObservationConsecutiveFailures`, `ArchivedSkipLogged`)
            VALUES ('{id}', '{Guid.NewGuid()}', '2026-10-22', 0, 2, 0);
            INSERT INTO `WeatherObservations` (`WeatherObservationId`, `LocationId`, `SiteVersion`, `Provider`, `StationId`, `LocalDate`, `MeanTempC`, `FetchedAtUtc`)
            VALUES ('{Guid.NewGuid()}', '{id}', 1, 1, 'SN18700', '2026-10-22', 6.8, '2026-10-23 08:02:00');
            INSERT INTO `WeatherDays` (`LocationId`, `LocalDate`, `ObservedMeanTempC`, `ObservedFetchedAtUtc`, `UpdatedAtUtc`)
            VALUES ('{id}', '2026-10-22', 6.8, '2026-10-23 08:02:00', '2026-10-23 08:02:00');
            INSERT INTO `WeatherDays` (`LocationId`, `LocalDate`, `ForecastCoverageHours`, `ForecastMaxTempC`, `ObservedMeanTempC`, `ObservedFetchedAtUtc`, `UpdatedAtUtc`)
            VALUES ('{id}', '2026-10-23', 24, 9.0, 4.1, '2026-10-23 08:02:00', '2026-10-23 08:02:00');
            SET FOREIGN_KEY_CHECKS = 1;
            """);

        await MigrationSeam.MigrateToAsync(context, StationMigration);

        // Read raw: the entities map the weather-points schema now (issue #387).
        Assert.True(await MigrationSeam.TableExistsAsync(context, "WeatherStations"));
        Assert.Equal(0, await MigrationSeam.CountAsync(context, "SELECT COUNT(*) FROM `WeatherObservations`"));
        Assert.Equal(1, await MigrationSeam.CountAsync(context, "SELECT COUNT(*) FROM `WeatherLocations` WHERE `StationResolvedAtUtc` IS NULL"));
        var schedule = (await MigrationSeam.RowAsync(context, "SELECT * FROM `WeatherLocationSchedules`"))!;
        Assert.Null(schedule["ObservationsCheckedThroughDate"]);
        Assert.Equal(0, Convert.ToInt32(schedule["ObservationConsecutiveFailures"]));
        // The measured-only day goes; the forecast's day keeps its forecast and loses only the measured half.
        Assert.Equal(1, await MigrationSeam.CountAsync(context, "SELECT COUNT(*) FROM `WeatherDays`"));
        var day = (await MigrationSeam.RowAsync(context, "SELECT * FROM `WeatherDays`"))!;
        Assert.Equal(new DateTime(2026, 10, 23), Convert.ToDateTime(day["LocalDate"]));
        Assert.Equal(9.0m, Convert.ToDecimal(day["ForecastMaxTempC"]));
        Assert.Null(day["ObservedMeanTempC"]);
        Assert.Null(day["ObservedFetchedAtUtc"]);
    }

    [SkippableFact]
    public async Task With_the_forecast_on_a_history_purge_removes_only_the_measured_data()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await MigratedAsync();
        var pointId = await EnabledPointAsync();
        await SeedHistoryAsync(pointId, steps: 50);
        await using (var context = NewContext())
        {
            // A measured-only day goes entirely; the forecast's day loses only its measured half.
            await context.Database.ExecuteSqlRawAsync($"""
                INSERT INTO `WeatherDays` (`WeatherPointId`, `LocalDate`, `ObservedMeanTempC`, `ObservedFetchedAtUtc`, `UpdatedAtUtc`)
                VALUES ('{pointId}', '2026-10-22', 6.8, '2026-10-23 08:02:00', '2026-10-23 08:02:00')
                """);
            await context.Database.ExecuteSqlRawAsync(
                $"UPDATE `WeatherDays` SET `ObservedMeanTempC` = 4.1, `ObservedFetchedAtUtc` = '2026-10-23 08:02:00' WHERE `WeatherPointId` = '{pointId}' AND `LocalDate` = '2026-10-23'");
            await context.Database.ExecuteSqlRawAsync(
                $"UPDATE `WeatherPoints` SET `StationResolvedAtUtc` = '2026-10-23 08:00:00' WHERE `WeatherPointId` = '{pointId}'");
            await context.Database.ExecuteSqlRawAsync($"""
                INSERT INTO `WeatherStations` (`WeatherPointId`, `Group`, `StationId`, `Name`, `DistanceKm`, `ResolvedAtUtc`)
                VALUES ('{pointId}', 0, 'SN18700', 'OSLO - BLINDERN', 3.2, '2026-10-23 08:00:00'),
                       ('{pointId}', 1, NULL, NULL, NULL, '2026-10-23 08:00:00')
                """);
            await context.Database.ExecuteSqlRawAsync(
                $"UPDATE `WeatherPointSchedules` SET `ObservationsCheckedThroughDate` = '2026-10-22', `LastObservationSuccessUtc` = '2026-10-23 08:02:00' WHERE `WeatherPointId` = '{pointId}'");
            await SetAsync(context, pointId, forecast: true, history: false);
        }

        await using (var context = NewContext())
        {
            Assert.True(await Admin(context).PurgeHistoryAsync(pointId, AdminId, CancellationToken.None));
        }

        await using var verify = NewContext();
        var row = await verify.WeatherPoints.SingleAsync(w => w.WeatherPointId == pointId);
        Assert.True(row.ForecastEnabled);
        Assert.Null(row.PurgeStartedAt);
        Assert.Null(row.PurgeMode);
        Assert.Null(row.StationResolvedAtUtc);
        Assert.Equal(0, await verify.WeatherStations.CountAsync(st => st.WeatherPointId == pointId));
        Assert.Equal(50, await verify.WeatherForecastSteps.CountAsync(s => s.WeatherPointId == pointId));
        Assert.True(await verify.WeatherForecastRuns.AnyAsync(r => r.WeatherPointId == pointId));
        Assert.Equal(2, await verify.WeatherPointSites.CountAsync(s => s.WeatherPointId == pointId));
        Assert.False(await verify.WeatherObservations.AnyAsync(o => o.WeatherPointId == pointId));
        var day = await verify.WeatherDays.SingleAsync(d => d.WeatherPointId == pointId);
        Assert.Equal(new DateOnly(2026, 10, 23), day.LocalDate);
        Assert.Equal(24, day.ForecastCoverageHours);
        Assert.Null(day.ObservedMeanTempC);
        Assert.Null(day.ObservedFetchedAtUtc);
        var schedule = await verify.WeatherPointSchedules.SingleAsync(s => s.WeatherPointId == pointId);
        Assert.Null(schedule.ObservationsCheckedThroughDate);
        Assert.Null(schedule.LastObservationSuccessUtc);
        Assert.Null(schedule.LeaseUntilUtc);
    }

    [SkippableFact]
    public async Task A_point_with_data_cannot_be_deleted_raw_and_its_settings_rows_cascade()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await MigratedAsync();
        var pointId = await EnabledPointAsync();
        await SeedHistoryAsync(pointId, steps: 5);

        // RESTRICT from the three bulk tables: a raw delete cannot become one unbounded cascade.
        await using (var context = NewContext())
        {
            await Assert.ThrowsAnyAsync<Exception>(() => context.Database.ExecuteSqlRawAsync(
                $"DELETE FROM `WeatherPoints` WHERE `WeatherPointId` = '{pointId}'"));
        }

        await using (var context = NewContext())
        {
            foreach (var table in new[] { "WeatherForecastSteps", "WeatherForecastRuns", "WeatherObservations", "WeatherDays" })
            {
                await context.Database.ExecuteSqlRawAsync($"DELETE FROM `{table}` WHERE `WeatherPointId` = '{pointId}'");
            }

            await context.Database.ExecuteSqlRawAsync(
                $"INSERT INTO `WeatherStations` (`WeatherPointId`, `Group`, `ResolvedAtUtc`) VALUES ('{pointId}', 0, '2026-10-23 08:00:00')");
            await context.Database.ExecuteSqlRawAsync($"DELETE FROM `WeatherPoints` WHERE `WeatherPointId` = '{pointId}'");
        }

        await using var verify = NewContext();
        Assert.False(await verify.WeatherPointSites.AnyAsync(s => s.WeatherPointId == pointId));
        Assert.False(await verify.WeatherPointSchedules.AnyAsync(s => s.WeatherPointId == pointId));
        Assert.False(await verify.WeatherStations.AnyAsync(s => s.WeatherPointId == pointId));
    }

    [SkippableFact]
    public async Task Deleting_a_point_purges_it_in_batches_and_sets_every_linked_place_to_null()
    {
        // AC 21 on real MariaDB: the SET NULL key unlinks the places; the purger removes the rows in batches.
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await MigratedAsync();
        var pointId = await EnabledPointAsync();
        var keep = await EnabledPointAsync("Cabin");
        var first = await PlaceAsync("First", pointId);
        var second = await PlaceAsync("Second", pointId);
        var other = await PlaceAsync("Other", keep);
        await SeedHistoryAsync(pointId, steps: RelationalWeatherHistoryPurger.BatchSize * 2 + 500);
        await SeedHistoryAsync(keep, steps: 10);

        await using (var context = NewContext())
        {
            await SetAsync(context, pointId, forecast: false, history: false);
        }

        await using (var context = NewContext())
        {
            Assert.True(await Admin(context).DeleteAsync(pointId, AdminId, CancellationToken.None));
        }

        await using var verify = NewContext();
        Assert.False(await verify.WeatherPoints.AnyAsync(w => w.WeatherPointId == pointId));
        Assert.False(await verify.WeatherPointSites.AnyAsync(s => s.WeatherPointId == pointId));
        Assert.False(await verify.WeatherPointSchedules.AnyAsync(s => s.WeatherPointId == pointId));
        Assert.False(await verify.WeatherForecastRuns.AnyAsync(r => r.WeatherPointId == pointId));
        Assert.False(await verify.WeatherForecastSteps.AnyAsync(s => s.WeatherPointId == pointId));
        Assert.False(await verify.WeatherObservations.AnyAsync(o => o.WeatherPointId == pointId));
        Assert.False(await verify.WeatherDays.AnyAsync(d => d.WeatherPointId == pointId));
        Assert.Equal([null, null], await verify.Locations.Where(l => l.LocationId == first || l.LocationId == second).Select(l => l.WeatherPointId).ToListAsync());
        Assert.Equal(keep, (await verify.Locations.SingleAsync(l => l.LocationId == other)).WeatherPointId);

        Assert.Equal(10, await verify.WeatherForecastSteps.CountAsync(s => s.WeatherPointId == keep));
        Assert.True(await verify.WeatherPoints.AnyAsync(w => w.WeatherPointId == keep));
    }

    [SkippableFact]
    public async Task An_interrupted_delete_stays_fenced_and_a_repeat_completes_it()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await MigratedAsync();
        var pointId = await EnabledPointAsync();
        await SeedHistoryAsync(pointId, steps: 50);

        // The state after step 1 and part of step 2: the fence is up, some rows are gone, the rest remain.
        await using (var context = NewContext())
        {
            await SetAsync(context, pointId, forecast: false, history: false);
            await context.Database.ExecuteSqlRawAsync(
                $"UPDATE `WeatherPoints` SET `PurgeStartedAt` = UTC_TIMESTAMP(), `PurgeMode` = 0 WHERE `WeatherPointId` = '{pointId}'");
            await context.Database.ExecuteSqlRawAsync(
                $"UPDATE `WeatherPointSchedules` SET `LeaseUntilUtc` = '9999-12-31 23:59:59', `Version` = UUID() WHERE `WeatherPointId` = '{pointId}'");
            await context.Database.ExecuteSqlRawAsync(
                $"DELETE FROM `WeatherForecastSteps` WHERE `WeatherPointId` = '{pointId}' LIMIT 20");
        }

        await using (var context = NewContext())
        {
            var row = await Admin(context).GetAsync(pointId, true, CancellationToken.None);
            Assert.True(row!.PurgeInProgress);
            var refused = await Assert.ThrowsAsync<WeatherConflictException>(() => Admin(context).PurgeHistoryAsync(pointId, AdminId, CancellationToken.None));
            Assert.Equal(WeatherConflictException.PurgeInProgress, refused.Code);
        }

        await using (var context = NewContext())
        {
            // The lease is far future, yet a resumed purge is not refused for it.
            Assert.True(await Admin(context).DeleteAsync(pointId, AdminId, CancellationToken.None));
        }

        await using var verify = NewContext();
        Assert.False(await verify.WeatherPoints.AnyAsync(w => w.WeatherPointId == pointId));
        Assert.False(await verify.WeatherForecastSteps.AnyAsync(s => s.WeatherPointId == pointId));
        Assert.False(await verify.WeatherPointSites.AnyAsync(s => s.WeatherPointId == pointId));
    }

    [SkippableFact]
    public async Task A_fetch_in_flight_when_weather_is_turned_off_stores_nothing()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await MigratedAsync();
        var pointId = await EnabledPointAsync();

        var source = new Forecasts(async () =>
        {
            await using var other = NewContext();
            await SetAsync(other, pointId, forecast: false, history: false);
        });

        await using (var context = NewContext())
        {
            var attempt = await new WeatherFetchService(context, source, new NoObservations(), new Settings(), new NoSecrets(), new FixedTime())
                .FetchForecastAsync(pointId, WeatherFetchMode.Scheduled, CancellationToken.None);
            Assert.Equal(WeatherAttemptStatus.Discarded, attempt.Status);
        }

        await using var verify = NewContext();
        Assert.False(await verify.WeatherForecastRuns.AnyAsync(r => r.WeatherPointId == pointId));
        Assert.False(await verify.WeatherForecastSteps.AnyAsync(s => s.WeatherPointId == pointId));
    }

    [SkippableFact]
    public async Task A_fetch_round_trips_decimal_values_and_utc_times()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await MigratedAsync();
        var pointId = await EnabledPointAsync();

        await using (var context = NewContext())
        {
            var attempt = await new WeatherFetchService(context, new Forecasts(null), new NoObservations(), new Settings(), new NoSecrets(), new FixedTime())
                .FetchForecastAsync(pointId, WeatherFetchMode.Scheduled, CancellationToken.None);
            Assert.Equal(WeatherAttemptStatus.Completed, attempt.Status);
        }

        await using var verify = NewContext();
        var step = await verify.WeatherForecastSteps.OrderBy(s => s.TimeUtc).FirstAsync(s => s.WeatherPointId == pointId);
        Assert.Equal(-7.5m, step.AirTemperatureC);
        Assert.Equal(1013.2m, step.AirPressureHpa);
        Assert.Equal(Noon, step.TimeUtc);
        var day = await verify.WeatherDays.SingleAsync(d => d.WeatherPointId == pointId && d.LocalDate == new DateOnly(2026, 10, 23));
        Assert.Equal(-7.5m, day.ForecastMinTempC);
    }

    // ── Keys (AC 25) ──────────────────────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task The_point_keys_have_the_specified_on_delete_behaviour()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await MigratedAsync();
        await using var context = NewContext();

        var expected = new Dictionary<(string Table, string Column), string>
        {
            [("WeatherPointSites", "WeatherPointId")] = "CASCADE",
            [("WeatherPointSchedules", "WeatherPointId")] = "CASCADE",
            [("WeatherStations", "WeatherPointId")] = "CASCADE",
            [("WeatherForecastRuns", "WeatherPointId")] = "RESTRICT",
            [("WeatherObservations", "WeatherPointId")] = "RESTRICT",
            [("WeatherDays", "WeatherPointId")] = "RESTRICT",
            [("Locations", "WeatherPointId")] = "SET NULL",
        };
        foreach (var ((table, column), rule) in expected)
        {
            Assert.Equal(1, await MigrationSeam.CountAsync(context, $"""
                SELECT COUNT(*) FROM information_schema.REFERENTIAL_CONSTRAINTS r
                JOIN information_schema.KEY_COLUMN_USAGE k
                  ON k.CONSTRAINT_SCHEMA = r.CONSTRAINT_SCHEMA AND k.CONSTRAINT_NAME = r.CONSTRAINT_NAME
                WHERE r.CONSTRAINT_SCHEMA = DATABASE() AND k.TABLE_NAME = '{table}' AND k.COLUMN_NAME = '{column}'
                  AND r.REFERENCED_TABLE_NAME = 'WeatherPoints' AND r.DELETE_RULE = '{rule}'
                """));
        }

        // The largest table gains no key, and the point has none back to its site (a cycle otherwise).
        Assert.Equal(0, await MigrationSeam.CountAsync(context, """
            SELECT COUNT(*) FROM information_schema.REFERENTIAL_CONSTRAINTS
            WHERE CONSTRAINT_SCHEMA = DATABASE() AND TABLE_NAME = 'WeatherForecastSteps' AND REFERENCED_TABLE_NAME <> 'WeatherForecastRuns'
            """));
        Assert.Equal(0, await MigrationSeam.CountAsync(context, """
            SELECT COUNT(*) FROM information_schema.REFERENTIAL_CONSTRAINTS
            WHERE CONSTRAINT_SCHEMA = DATABASE() AND TABLE_NAME = 'WeatherPoints' AND REFERENCED_TABLE_NAME <> 'AspNetUsers'
            """));
        Assert.False(await MigrationSeam.TableExistsAsync(context, "WeatherLocations"));
        Assert.Equal(0, await MigrationSeam.CountAsync(context, """
            SELECT COUNT(*) FROM information_schema.COLUMNS
            WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME LIKE 'Weather%' AND COLUMN_NAME = 'LocationId'
            """));
    }

    [SkippableFact]
    public async Task The_current_site_version_always_names_an_existing_site()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await MigratedAsync();
        var pointId = await EnabledPointAsync();
        await using (var context = NewContext())
        {
            var current = (await Admin(context).GetAsync(pointId, true, CancellationToken.None))!;
            var moved = await Admin(context).UpdateAsync(pointId, Update(current, latitude: 60.39299m), AdminId, true, CancellationToken.None);
            Assert.Equal(2, moved!.Point.Site.Version);
        }

        await using var verify = NewContext();
        Assert.Equal(0, await MigrationSeam.CountAsync(verify, """
            SELECT COUNT(*) FROM `WeatherPoints` p
            WHERE NOT EXISTS (SELECT 1 FROM `WeatherPointSites` s WHERE s.`WeatherPointId` = p.`WeatherPointId` AND s.`SiteVersion` = p.`CurrentSiteVersion`)
            """));
        await Assert.ThrowsAnyAsync<Exception>(() => verify.Database.ExecuteSqlRawAsync(
            $"UPDATE `WeatherPoints` SET `CurrentSiteVersion` = 0 WHERE `WeatherPointId` = '{pointId}'"));
    }

    // ── The link (AC 11, 22) ─────────────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task A_link_to_a_point_deleted_after_the_check_is_a_422_never_a_500()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await MigratedAsync();
        var pointId = await EnabledPointAsync();
        var place = await PlaceAsync("Place", null);

        // The point read passes on a REPEATABLE READ snapshot taken before the delete commits; the key then
        // refuses the write (1452), which the service maps to the same 422 as an unknown point.
        await using var context = NewContext();
        var interceptor = new DeleteBeforeSave(fixture.ConnectionStringFor(Database), pointId);
        await using var racing = new OdysseyContext(new DbContextOptionsBuilder<OdysseyContext>()
            .UseMySql(fixture.ConnectionStringFor(Database), ServerVersion.AutoDetect(fixture.OdysseyConnectionString))
            .AddInterceptors(interceptor)
            .Options);

        var error = await Assert.ThrowsAsync<Odyssey.Core.DomainUnprocessableException>(() =>
            new WeatherLinkService(racing, new FixedTime()).SetAsync(place, pointId, AdminId, CancellationToken.None));

        Assert.True(interceptor.Fired);
        Assert.Equal(["weatherPointId"], error.Errors!.Keys);
        Assert.Null((await context.Locations.SingleAsync(l => l.LocationId == place)).WeatherPointId);
    }

    [SkippableFact]
    public async Task A_link_concurrent_with_a_merge_never_leaves_a_link_on_a_forwarder()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await MigratedAsync();
        var pointId = await EnabledPointAsync();
        for (var round = 0; round < 6; round++)
        {
            var source = await PlaceAsync($"Source {round}", null);
            var target = await PlaceAsync($"Target {round}", null);

            await using var linkContext = NewContext();
            await using var mergeContext = NewContext();
            var link = Task.Run(async () =>
            {
                try
                {
                    await new WeatherLinkService(linkContext, new FixedTime()).SetAsync(source, pointId, AdminId, CancellationToken.None);
                    return "linked";
                }
                catch (Odyssey.Core.DomainUnprocessableException ex) when (ex.Errors!.ContainsKey("id"))
                {
                    return "forwarder";
                }
            });
            var merge = Task.Run(() => new Odyssey.Core.Journal.LocationService(mergeContext, new FixedTime())
                .MergeAsync(source, target, Odyssey.Core.Locations.LocationClaims.Trusted, AdminId, CancellationToken.None));
            var outcome = await link;
            await merge;

            await using var verify = NewContext();
            var rows = await verify.Locations.Where(l => l.LocationId == source || l.LocationId == target)
                .ToDictionaryAsync(l => l.LocationId, l => l.WeatherPointId);
            Assert.Null(rows[source]);
            // Linked before the merge: the survivor inherited the link. After: the link was refused (422 keyed id).
            Assert.Equal(outcome == "linked" ? pointId : null, rows[target]);
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────────────────────────

    private async Task<Guid> EnabledPointAsync(string name = "Home")
    {
        await using var context = NewContext();
        var point = await Admin(context).CreateAsync(new NewWeatherPoint
        {
            Name = name + " " + Guid.NewGuid().ToString("N")[..6],
            Latitude = 59.91273m,
            Longitude = 10.75027m,
            AltitudeMeters = 12,
            TimeZoneId = "Europe/Oslo",
            ForecastEnabled = true,
            HistoryEnabled = true,
            IsTracked = true,
        }, AdminId, CancellationToken.None);
        return point.WeatherPointId;
    }

    private async Task<Guid> PlaceAsync(string name, Guid? pointId)
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
            WeatherPointId = pointId,
            CreatedAt = Noon,
            UpdatedAt = Noon,
        };
        context.Locations.Add(location);
        await context.SaveChangesAsync();
        return location.LocationId;
    }

    private static async Task SetAsync(OdysseyContext context, Guid pointId, bool forecast, bool history)
    {
        var current = (await Admin(context).GetAsync(pointId, true, CancellationToken.None))!;
        var request = Update(current);
        request.ForecastEnabled = forecast;
        request.HistoryEnabled = history;
        Assert.NotNull(await Admin(context).UpdateAsync(pointId, request, AdminId, true, CancellationToken.None));
    }

    private static UpdateWeatherPoint Update(ExistingWeatherPoint current, decimal? latitude = null) => new()
    {
        Name = current.Name,
        Latitude = latitude ?? current.Site.Latitude,
        Longitude = current.Site.Longitude,
        AltitudeMeters = current.Site.AltitudeMeters,
        TimeZoneId = current.Site.TimeZoneId,
        ForecastEnabled = current.ForecastEnabled,
        HistoryEnabled = current.HistoryEnabled,
        IsTracked = current.IsTracked,
        SettingsVersion = current.SettingsVersion,
    };

    /// <summary>Deletes the point from another connection just before the first save of the context it is on.</summary>
    private sealed class DeleteBeforeSave(string connectionString, Guid pointId) : Microsoft.EntityFrameworkCore.Diagnostics.SaveChangesInterceptor
    {
        public bool Fired { get; private set; }

        public override async ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int>> SavingChangesAsync(
            Microsoft.EntityFrameworkCore.Diagnostics.DbContextEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (!Fired)
            {
                Fired = true;
                await using var connection = new MySqlConnector.MySqlConnection(connectionString);
                await connection.OpenAsync(cancellationToken);
                foreach (var sql in new[]
                {
                    $"DELETE FROM `WeatherPointSchedules` WHERE `WeatherPointId` = '{pointId}'",
                    $"DELETE FROM `WeatherPoints` WHERE `WeatherPointId` = '{pointId}'",
                })
                {
                    await using var command = connection.CreateCommand();
                    command.CommandText = sql;
                    await command.ExecuteNonQueryAsync(cancellationToken);
                }
            }

            return result;
        }
    }

    /// <summary>One run with <paramref name="steps"/> steps, plus an observation, a day and a second site — written in bulk.</summary>
    private async Task SeedHistoryAsync(Guid locationId, int steps)
    {
        await using var context = NewContext();
        var runId = Guid.NewGuid();
        await context.Database.ExecuteSqlRawAsync($"""
            INSERT INTO `WeatherForecastRuns` (`WeatherForecastRunId`, `WeatherPointId`, `SiteVersion`, `Provider`, `ProviderUpdatedAt`, `FetchedAtUtc`, `Latitude`, `Longitude`)
            VALUES ('{runId}', '{locationId}', 1, 1, '2026-10-23 11:30:00', '2026-10-23 11:31:00', 59.913, 10.750)
            """);
        await context.Database.ExecuteSqlRawAsync($"""
            INSERT INTO `WeatherForecastSteps` (`WeatherForecastRunId`, `WeatherPointId`, `SiteVersion`, `RunProviderUpdatedAt`, `RunFetchedAtUtc`, `TimeUtc`, `PeriodHours`, `AirTemperatureC`)
            SELECT '{runId}', '{locationId}', 1, '2026-10-23 11:30:00', '2026-10-23 11:31:00', DATE_ADD('2026-01-01', INTERVAL seq MINUTE), 1, 5.0
            FROM seq_1_to_{steps}
            """);
        await context.Database.ExecuteSqlRawAsync($"""
            INSERT INTO `WeatherObservations` (`WeatherObservationId`, `WeatherPointId`, `SiteVersion`, `Provider`, `TemperatureStationId`, `LocalDate`, `MeanTempC`, `FetchedAtUtc`)
            VALUES ('{Guid.NewGuid()}', '{locationId}', 1, 1, 'SN18700', '2026-10-22', 6.8, '2026-10-23 08:02:00')
            """);
        await context.Database.ExecuteSqlRawAsync($"""
            INSERT INTO `WeatherDays` (`WeatherPointId`, `LocalDate`, `ForecastCoverageHours`, `UpdatedAtUtc`)
            VALUES ('{locationId}', '2026-10-23', 24, '2026-10-23 11:31:00')
            """);
        await context.Database.ExecuteSqlRawAsync($"""
            INSERT INTO `WeatherPointSites` (`WeatherPointId`, `SiteVersion`, `Latitude`, `Longitude`, `TimeZoneId`, `AppliedAt`)
            VALUES ('{locationId}', 2, 60.393, 5.324, 'Europe/Oslo', '2026-10-23 11:40:00')
            """);
        await context.Database.ExecuteSqlRawAsync(
            $"UPDATE `WeatherPoints` SET `CurrentSiteVersion` = 2 WHERE `WeatherPointId` = '{locationId}'");
    }

    private static WeatherPointService Admin(OdysseyContext context) =>
        new(context, new WeatherFetchService(context, new Forecasts(null), new NoObservations(), new Settings(), new NoSecrets(), new FixedTime()),
            new Settings(), new RelationalWeatherHistoryPurger(context), new FixedTime());

    private async Task MigratedAsync()
    {
        await fixture.CreateMigratedDatabaseAsync(Database);
        await using var context = NewContext();
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
        public Task<StationLookupResult> FindStationAsync(WeatherSiteQuery site, WeatherMeasureGroup group, string contact, string clientId, CancellationToken cancellationToken) =>
            Task.FromResult(new StationLookupResult(true, null));

        public Task<ObservationFetchResult> FetchDailyAsync(
            IReadOnlyCollection<string> stationIds, IReadOnlyCollection<string> elements, DateOnly from, DateOnly to, string contact, string clientId, CancellationToken cancellationToken) =>
            Task.FromResult(new ObservationFetchResult(true, []));
    }
}
