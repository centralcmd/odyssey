// EF1002 flags interpolation into ExecuteSqlRawAsync. Every value interpolated below is a constant or a Guid
// this test generated itself — there is no external input.
#pragma warning disable EF1002

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using MySqlConnector;
using Odyssey.Context;
using Odyssey.Core.Finance.ExchangeRateSync;
using Odyssey.Core.Finance.ExchangeRateSync.Providers;
using Odyssey.Dtos.Finance;
using Xunit;

namespace Odyssey.IntegrationTests;

/// <summary>
/// The relational half of issue #349: the migration in both directions over existing rates, the run guard's
/// generated column and unique index, the idempotency index, the CHECKs and the purge's transaction. None of
/// it is observable on the EF InMemory provider, which computes no column and enforces no index or CHECK.
/// </summary>
[Collection(MariaDbCollection.Name)]
public class ExchangeRateSyncRelationalTests(MariaDbFixture fixture)
{
    private const string Database = "odyssey_exchange_rate_sync";
    private const string PreviousMigration = "_AddLocations";
    private const string ThisMigration = "_AddExchangeRateSync";

    private static readonly DateTime AsOf = new(2026, 10, 22, 14, 0, 0, DateTimeKind.Utc);

    [SkippableFact]
    public async Task The_migration_keeps_existing_rates_as_manual_and_its_down_restores_the_old_index()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await RecreateAsync();

        await using var context = NewContext();
        await MigrationSeam.MigrateToAsync(context, PreviousMigration);
        await context.Database.ExecuteSqlRawAsync(
            "INSERT INTO `ExchangeRates` (`ExchangeRateId`, `FromCurrencyCode`, `ToCurrencyCode`, `Rate`, `AsOf`, `CreatedAt`) "
            + $"VALUES ('{Guid.NewGuid()}', 'USD', 'NOK', 10.5, '2026-10-01 00:00:00', NOW())");

        await MigrationSeam.MigrateToAsync(context, ThisMigration);
        Assert.Equal(1L, await MigrationSeam.CountAsync(context, "SELECT COUNT(*) FROM `ExchangeRates` WHERE `Provider` IS NULL"));
        Assert.Equal(1L, await MigrationSeam.CountAsync(context,
            "SELECT COUNT(*) FROM `SystemSettings` WHERE `Key` = 'ExchangeRateHistoryStartDate' AND `Value` = ''"));
        Assert.Equal(1L, await MigrationSeam.CountAsync(context,
            "SELECT COUNT(*) FROM `SystemSettings` WHERE `Key` = 'ExchangeRateSyncIntervalHours' AND `Value` = '24'"));

        await MigrationSeam.MigrateToAsync(context, PreviousMigration);
        Assert.False(await MigrationSeam.TableExistsAsync(context, "ExchangeRateSyncRuns"));
        Assert.Equal(1L, await MigrationSeam.CountAsync(context,
            "SELECT COUNT(*) FROM information_schema.STATISTICS WHERE TABLE_SCHEMA = DATABASE() "
            + "AND INDEX_NAME = 'IX_ExchangeRates_FromCurrencyCode_ToCurrencyCode_AsOf' AND SEQ_IN_INDEX = 1"));
    }

    [SkippableFact]
    public async Task The_run_guard_admits_one_running_row_and_releases_on_finish()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await MigratedAsync();

        await using (var context = NewContext())
        {
            context.ExchangeRateSyncRuns.Add(Run(ExchangeRateSyncRunStatus.Running));
            await context.SaveChangesAsync();
        }

        await using (var context = NewContext())
        {
            context.ExchangeRateSyncRuns.Add(Run(ExchangeRateSyncRunStatus.Running));
            var ex = await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
            Assert.Equal(1062, (ex.GetBaseException() as MySqlException)?.Number);
        }

        await using (var context = NewContext())
        {
            var running = await context.ExchangeRateSyncRuns.SingleAsync();
            running.Status = ExchangeRateSyncRunStatus.Succeeded;
            context.ExchangeRateSyncRuns.Add(Run(ExchangeRateSyncRunStatus.Succeeded));
            await context.SaveChangesAsync();
            context.ExchangeRateSyncRuns.Add(Run(ExchangeRateSyncRunStatus.Running));
            await context.SaveChangesAsync();
        }
    }

    [SkippableFact]
    public async Task A_fetched_row_is_unique_per_key_while_manual_rows_may_repeat()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await MigratedAsync();

        await using (var context = NewContext())
        {
            context.ExchangeRates.AddRange(Rate(null), Rate(null), Rate(ExchangeRateProvider.NorgesBank));
            await context.SaveChangesAsync();
        }

        await using (var context = NewContext())
        {
            context.ExchangeRates.Add(Rate(ExchangeRateProvider.NorgesBank));
            var ex = await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
            Assert.Equal(1062, (ex.GetBaseException() as MySqlException)?.Number);
        }

        await using (var context = NewContext())
        {
            var ex = await Assert.ThrowsAnyAsync<Exception>(() => context.Database.ExecuteSqlRawAsync(
                "INSERT INTO `ExchangeRates` (`ExchangeRateId`, `FromCurrencyCode`, `ToCurrencyCode`, `Rate`, `AsOf`, `CreatedAt`, `Provider`) "
                + $"VALUES ('{Guid.NewGuid()}', 'USD', 'NOK', 1, NOW(), NOW(), 2)"));
            Assert.Contains("CK_ExchangeRates_Provider", ex.GetBaseException().Message, StringComparison.Ordinal);
        }
    }

    [SkippableFact]
    public async Task A_purge_runs_in_one_real_transaction_and_releases_the_guard()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await MigratedAsync();

        await using (var seed = NewContext())
        {
            seed.ExchangeRateSyncCurrencies.Add(new ExchangeRateSyncCurrency
            {
                CurrencyCode = "USD", Enabled = false, BackfilledFrom = new DateOnly(2026, 1, 1), UpdatedAtUtc = DateTime.UtcNow,
            });
            seed.ExchangeRates.AddRange(Rate(ExchangeRateProvider.NorgesBank), Rate(null));
            seed.ExchangeRates.Add(new ExchangeRate
            {
                FromCurrencyCode = "NOK", ToCurrencyCode = "USD", Rate = 0.1m, AsOf = AsOf, CreatedAt = DateTime.UtcNow,
                Provider = ExchangeRateProvider.NorgesBank,
            });
            await seed.SaveChangesAsync();
        }

        await using var context = NewContext();
        var client = new NorgesBankClient(new HttpClient { BaseAddress = new Uri(NorgesBankClient.BaseAddress) }, ExchangeRateSyncPacing.None);
        var cache = new ExchangeRateSupportedListCache(TimeProvider.System);
        var settings = new FixedSettings();
        var sync = new ExchangeRateSyncService(
            context, client, cache, settings, ExchangeRateSyncPacing.None, TimeProvider.System, NullLogger<ExchangeRateSyncService>.Instance);
        var currencies = new ExchangeRateSyncCurrencyService(context, client, cache, settings, sync, TimeProvider.System, NullLoggerFactory.Instance);

        var result = await currencies.PurgeAsync("USD", null);

        Assert.Equal(2, result.RowsDeleted);
        await using var verify = NewContext();
        Assert.Null((await verify.ExchangeRates.SingleAsync()).Provider);
        Assert.Null((await verify.ExchangeRateSyncCurrencies.SingleAsync()).BackfilledFrom);
        Assert.Null(await verify.ExchangeRateSyncRuns.Select(run => run.RunningGuard).SingleAsync());
    }

    private sealed class FixedSettings : IExchangeRateSyncSettingsLookup
    {
        public Task<ExchangeRateSyncSettings> GetAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new ExchangeRateSyncSettings(null, 24));
    }

    private static ExchangeRateSyncRun Run(ExchangeRateSyncRunStatus status) => new()
    {
        Trigger = ExchangeRateSyncTrigger.Scheduled,
        Provider = ExchangeRateProvider.NorgesBank,
        Status = status,
        StartedAtUtc = DateTime.UtcNow,
    };

    private static ExchangeRate Rate(ExchangeRateProvider? provider) => new()
    {
        FromCurrencyCode = "USD",
        ToCurrencyCode = "NOK",
        Rate = 10.5m,
        AsOf = AsOf,
        CreatedAt = DateTime.UtcNow,
        Provider = provider,
    };

    private async Task MigratedAsync()
    {
        await RecreateAsync();
        await using var context = NewContext();
        await context.Database.MigrateAsync();
    }

    private OdysseyContext NewContext() =>
        new(new DbContextOptionsBuilder<OdysseyContext>()
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
