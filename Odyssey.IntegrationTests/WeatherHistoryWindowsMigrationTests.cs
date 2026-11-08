// EF1002 flags interpolation into ExecuteSqlRawAsync. Every value interpolated below is a Guid or a literal
// this test wrote itself — there is no external input.
#pragma warning disable EF1002

using Microsoft.EntityFrameworkCore;
using Odyssey.Context;
using Xunit;

namespace Odyssey.IntegrationTests;

/// <summary>
/// Issue #436 §11, AC 17, 18, 26 against real MariaDB: the <c>WeatherPointHistoryWindows</c> migration carries every
/// point's <c>HistoryFrom</c> into one ongoing window and its backfill markers into one coverage row; the two order
/// checks hold; an end-only and a start change write without a unique-index violation; both tables cascade with the
/// point; and <c>Down()</c> restores the columns from the earliest window.
/// </summary>
[Collection(MariaDbCollection.Name)]
public class WeatherHistoryWindowsMigrationTests(MariaDbFixture fixture)
{
    private const string Database = "odyssey_weather_windows";
    private const string Before = "_DropExchangeRateSyncRuns";

    private static readonly Guid MidBackfill = Guid.Parse("20000000-0000-0000-0000-000000000001");
    private static readonly Guid NeverFetched = Guid.Parse("20000000-0000-0000-0000-000000000002");
    private static readonly Guid Moved = Guid.Parse("20000000-0000-0000-0000-000000000003");

    [SkippableFact]
    public async Task The_migration_carries_history_from_and_the_markers_and_down_restores_them()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await RecreateAsync();

        await using (var context = NewContext())
        {
            await MigrationSeam.MigrateToAsync(context, Before);
            await SeedAsync(context, MidBackfill, historyEnabled: true, siteVersion: 1, historyFrom: "2025-01-01", backfilledFrom: "2025-06-01", checkedThrough: "2026-11-06");
            await SeedAsync(context, NeverFetched, historyEnabled: false, siteVersion: 1, historyFrom: "2025-08-01", backfilledFrom: null, checkedThrough: null);
            await SeedAsync(context, Moved, historyEnabled: true, siteVersion: 2, historyFrom: "2024-01-01", backfilledFrom: "2026-06-14", checkedThrough: "2026-11-06");
        }

        await using (var context = NewContext())
        {
            await context.Database.MigrateAsync();
            Assert.Empty(await context.Database.GetPendingMigrationsAsync());
        }

        await using (var verify = NewContext())
        {
            var windows = await verify.WeatherPointHistoryWindows.AsNoTracking().OrderBy(w => w.WeatherPointId).ToListAsync();
            Assert.Equal([MidBackfill, NeverFetched, Moved], windows.Select(w => w.WeatherPointId));
            Assert.Equal([new DateOnly(2025, 1, 1), new DateOnly(2025, 8, 1), new DateOnly(2024, 1, 1)], windows.Select(w => w.FromDate));
            Assert.All(windows, w => Assert.Null(w.ToDate));
            Assert.All(windows, w => Assert.NotEqual(Guid.Empty, w.WeatherPointHistoryWindowId));
            Assert.Equal(3, windows.Select(w => w.WeatherPointHistoryWindowId).Distinct().Count());

            var coverage = await verify.WeatherObservationCoverages.AsNoTracking().OrderBy(c => c.WeatherPointId).ToListAsync();
            Assert.Equal(2, coverage.Count);
            Assert.Equal((MidBackfill, 1, new DateOnly(2025, 6, 1), new DateOnly(2026, 11, 6)),
                (coverage[0].WeatherPointId, coverage[0].SiteVersion, coverage[0].FromDate, coverage[0].ToDate));
            Assert.Equal((Moved, 2), (coverage[1].WeatherPointId, coverage[1].SiteVersion));
        }

        // Down() restores the columns: HistoryFrom from the earliest window, the backfill marker from the coverage.
        await using (var context = NewContext())
        {
            await context.Database.ExecuteSqlRawAsync($"""
                INSERT INTO `WeatherPointHistoryWindows` (`WeatherPointHistoryWindowId`, `WeatherPointId`, `FromDate`, `ToDate`)
                VALUES ('{Guid.NewGuid()}', '{MidBackfill}', '2023-07-01', '2023-07-07');
                """);
            await MigrationSeam.MigrateToAsync(context, Before);
            Assert.Equal("2023-07-01", Date(await MigrationSeam.ScalarAsync(context,
                $"SELECT `HistoryFrom` FROM `WeatherPoints` WHERE `WeatherPointId` = '{MidBackfill}'")));
            Assert.Equal("2025-06-01", Date(await MigrationSeam.ScalarAsync(context,
                $"SELECT `ObservationsBackfilledFromDate` FROM `WeatherPointSchedules` WHERE `WeatherPointId` = '{MidBackfill}'")));
            Assert.False(await MigrationSeam.TableExistsAsync(context, "WeatherPointHistoryWindows"));
        }
    }

    [SkippableFact]
    public async Task The_order_checks_hold_and_writes_and_the_cascade_behave_on_mariadb()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await RecreateAsync();
        await using (var context = NewContext())
        {
            await MigrationSeam.MigrateToAsync(context, Before);
            await SeedAsync(context, MidBackfill, historyEnabled: true, siteVersion: 1, historyFrom: "2025-01-01", backfilledFrom: "2025-06-01", checkedThrough: "2026-11-06");
            await context.Database.MigrateAsync();
        }

        // AC 18: both CHECKs refuse an end before the start.
        await using (var context = NewContext())
        {
            await Assert.ThrowsAnyAsync<Exception>(() => context.Database.ExecuteSqlRawAsync($"""
                INSERT INTO `WeatherPointHistoryWindows` (`WeatherPointHistoryWindowId`, `WeatherPointId`, `FromDate`, `ToDate`)
                VALUES ('{Guid.NewGuid()}', '{MidBackfill}', '2026-06-10', '2026-06-09');
                """));
            await Assert.ThrowsAnyAsync<Exception>(() => context.Database.ExecuteSqlRawAsync($"""
                INSERT INTO `WeatherObservationCoverages` (`WeatherPointId`, `SiteVersion`, `FromDate`, `ToDate`)
                VALUES ('{MidBackfill}', 1, '2026-06-10', '2026-06-09');
                """));
        }

        // AC 26: the service's write shape — an end updated in place, and a start change as a remove plus an insert of a
        // different key in ONE save — never trips the unique (WeatherPointId, FromDate) index.
        await using (var context = NewContext())
        {
            context.WeatherPointHistoryWindows.Add(new WeatherPointHistoryWindow
            {
                WeatherPointHistoryWindowId = Guid.NewGuid(), WeatherPointId = MidBackfill, FromDate = new DateOnly(2026, 6, 10), ToDate = new DateOnly(2026, 6, 17),
            });
            await context.SaveChangesAsync();
        }

        await using (var context = NewContext())
        {
            var trip = await context.WeatherPointHistoryWindows.SingleAsync(w => w.FromDate == new DateOnly(2026, 6, 10));
            trip.ToDate = new DateOnly(2026, 6, 20);
            var ongoing = await context.WeatherPointHistoryWindows.SingleAsync(w => w.ToDate == null);
            context.WeatherPointHistoryWindows.Remove(ongoing);
            context.WeatherPointHistoryWindows.Add(new WeatherPointHistoryWindow
            {
                WeatherPointHistoryWindowId = Guid.NewGuid(), WeatherPointId = MidBackfill, FromDate = new DateOnly(2026, 7, 1), ToDate = null,
            });
            await context.SaveChangesAsync();
        }

        await using (var context = NewContext())
        {
            Assert.Equal([new DateOnly(2026, 6, 10), new DateOnly(2026, 7, 1)],
                await context.WeatherPointHistoryWindows.OrderBy(w => w.FromDate).Select(w => w.FromDate).ToListAsync());

            // Both tables cascade with the point.
            await context.Database.ExecuteSqlRawAsync($"DELETE FROM `WeatherPoints` WHERE `WeatherPointId` = '{MidBackfill}'");
            Assert.Equal(0, await context.WeatherPointHistoryWindows.CountAsync());
            Assert.Equal(0, await context.WeatherObservationCoverages.CountAsync());
        }
    }

    [SkippableFact]
    public async Task The_relational_history_purge_removes_coverage_and_keeps_the_windows()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await RecreateAsync();
        await using (var context = NewContext())
        {
            await MigrationSeam.MigrateToAsync(context, Before);
            await SeedAsync(context, MidBackfill, historyEnabled: false, siteVersion: 1, historyFrom: "2025-01-01", backfilledFrom: "2025-06-01", checkedThrough: "2026-11-06");
            await context.Database.MigrateAsync();
        }

        await using (var context = NewContext())
        {
            await new Odyssey.Core.Weather.RelationalWeatherHistoryPurger(context)
                .PurgeAsync(MidBackfill, Odyssey.Dtos.Journal.WeatherPurgeMode.HistoryOnly, CancellationToken.None);
        }

        await using var verify = NewContext();
        Assert.Equal(0, await verify.WeatherObservationCoverages.CountAsync());
        Assert.Equal(1, await verify.WeatherPointHistoryWindows.CountAsync());
    }

    private static string Date(object? value) => value switch
    {
        DateTime d => d.ToString("yyyy-MM-dd"),
        DateOnly d => d.ToString("yyyy-MM-dd"),
        _ => Convert.ToString(value) ?? string.Empty,
    };

    private static async Task SeedAsync(
        OdysseyContext context, Guid id, bool historyEnabled, int siteVersion, string historyFrom, string? backfilledFrom, string? checkedThrough)
    {
        var backfill = backfilledFrom is null ? "NULL" : $"'{backfilledFrom}'";
        var through = checkedThrough is null ? "NULL" : $"'{checkedThrough}'";
        await context.Database.ExecuteSqlRawAsync($"""
            INSERT INTO `WeatherPoints` (`WeatherPointId`, `Name`, `ForecastEnabled`, `HistoryEnabled`, `IsTracked`, `HistoryFrom`, `EnabledAt`, `CurrentSiteVersion`, `SettingsVersion`, `CreatedAt`, `UpdatedAt`)
            VALUES ('{id}', 'Point {id:N}', 0, {(historyEnabled ? 1 : 0)}, 0, '{historyFrom}', '2025-01-01 00:00:00', {siteVersion}, '{Guid.NewGuid()}', '2025-01-01 00:00:00', '2025-01-01 00:00:00');
            """);
        for (var v = 1; v <= siteVersion; v++)
        {
            await context.Database.ExecuteSqlRawAsync($"""
                INSERT INTO `WeatherPointSites` (`WeatherPointId`, `SiteVersion`, `Latitude`, `Longitude`, `TimeZoneId`, `AppliedAt`)
                VALUES ('{id}', {v}, 59.9, 10.7, 'Europe/Oslo', '{(v == 1 ? "2025-01-01" : "2026-06-14")} 08:00:00');
                """);
        }

        await context.Database.ExecuteSqlRawAsync($"""
            INSERT INTO `WeatherPointSchedules` (`WeatherPointId`, `Version`, `ForecastConsecutiveFailures`, `ObservationConsecutiveFailures`, `ObservationsBackfilledFromDate`, `ObservationsCheckedThroughDate`)
            VALUES ('{id}', '{Guid.NewGuid()}', 0, 0, {backfill}, {through});
            """);
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
}
