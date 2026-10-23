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
        var (_, currencies) = Services(context);

        var result = await currencies.PurgeAsync("USD", null);

        Assert.Equal(2, result.RowsDeleted);
        await using var verify = NewContext();
        Assert.Null((await verify.ExchangeRates.SingleAsync()).Provider);
        Assert.Null((await verify.ExchangeRateSyncCurrencies.SingleAsync()).BackfilledFrom);
        Assert.Null(await verify.ExchangeRateSyncRuns.Select(run => run.RunningGuard).SingleAsync());
    }

    [SkippableFact]
    public async Task A_purge_that_fails_part_way_rolls_back_every_batch_and_ends_failed()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await MigratedAsync();

        await using (var seed = NewContext())
        {
            seed.ExchangeRateSyncCurrencies.Add(new ExchangeRateSyncCurrency
            {
                CurrencyCode = "USD", Enabled = false, BackfilledFrom = new DateOnly(2026, 1, 1), UpdatedAtUtc = DateTime.UtcNow,
            });
            seed.ExchangeRates.Add(Rate(ExchangeRateProvider.NorgesBank));
            await seed.SaveChangesAsync();
        }

        // Fails the watermark reset, the save AFTER the deletes have been sent inside the transaction.
        await using var context = NewContext(new FailOnUpdate<ExchangeRateSyncCurrency>());
        var (_, currencies) = Services(context);

        await Assert.ThrowsAsync<InvalidOperationException>(() => currencies.PurgeAsync("USD", null));

        await using var verify = NewContext();
        Assert.Equal(ExchangeRateProvider.NorgesBank, (await verify.ExchangeRates.SingleAsync()).Provider);
        Assert.Equal(new DateOnly(2026, 1, 1), (await verify.ExchangeRateSyncCurrencies.SingleAsync()).BackfilledFrom);
        var run = await verify.ExchangeRateSyncRuns.SingleAsync();
        Assert.Equal(ExchangeRateSyncRunStatus.Failed, run.Status);
        Assert.Null(run.RunningGuard);
    }

    /// <summary>AC16 on the real engine: two runs racing for the guard — exactly one wins.</summary>
    [SkippableFact]
    public async Task Two_concurrent_starts_take_the_guard_exactly_once()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await MigratedAsync();

        for (var round = 0; round < 5; round++)
        {
            var started = await Task.WhenAll(Enumerable.Range(0, 2).Select(async _ =>
            {
                await using var context = NewContext();
                var (sync, _) = Services(context);
                return await sync.TryStartAsync(ExchangeRateSyncTrigger.Manual, null);
            }));

            Assert.Single(started, run => run is not null);

            await using var cleanup = NewContext();
            var running = await cleanup.ExchangeRateSyncRuns.SingleAsync(run => run.Status == ExchangeRateSyncRunStatus.Running);
            running.Status = ExchangeRateSyncRunStatus.Succeeded;
            await cleanup.SaveChangesAsync();
        }
    }

    /// <summary>
    /// The new read queries (issue #349 §3, §5) translated by MySQL's provider, not EF InMemory: the
    /// grouped-MAX latest lookup with the NOK hop, the status rank and filter, the summary, the run log
    /// and the per-currency list with its fetched extents.
    /// </summary>
    [SkippableFact]
    public async Task The_conversion_status_summary_and_list_queries_run_on_MariaDB()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await MigratedAsync();
        var now = DateTime.UtcNow;

        await using (var seed = NewContext())
        {
            seed.ExchangeRates.AddRange(
                new ExchangeRate { FromCurrencyCode = "USD", ToCurrencyCode = "EUR", Rate = 0.5m, AsOf = now.AddDays(-10), CreatedAt = now },
                new ExchangeRate { FromCurrencyCode = "USD", ToCurrencyCode = "EUR", Rate = 0.1m, AsOf = now.AddDays(5), CreatedAt = now },
                new ExchangeRate { FromCurrencyCode = "USD", ToCurrencyCode = "NOK", Rate = 10m, AsOf = now.AddDays(-2), CreatedAt = now, Provider = ExchangeRateProvider.NorgesBank },
                new ExchangeRate { FromCurrencyCode = "USD", ToCurrencyCode = "NOK", Rate = 10.5m, AsOf = now.AddDays(-1), CreatedAt = now, Provider = ExchangeRateProvider.NorgesBank },
                new ExchangeRate { FromCurrencyCode = "NOK", ToCurrencyCode = "EUR", Rate = 0.1m, AsOf = now.AddDays(-1), CreatedAt = now, Provider = ExchangeRateProvider.NorgesBank });
            seed.ExchangeRateSyncCurrencies.Add(new ExchangeRateSyncCurrency { CurrencyCode = "USD", Enabled = true, UpdatedAtUtc = now });
            seed.ExchangeRateSyncRuns.Add(new ExchangeRateSyncRun
            {
                Trigger = ExchangeRateSyncTrigger.Scheduled, Provider = ExchangeRateProvider.NorgesBank,
                Status = ExchangeRateSyncRunStatus.Succeeded, StartedAtUtc = now.AddHours(-1), FinishedAtUtc = now.AddHours(-1),
                CurrencyCodes = "USD",
            });
            await seed.SaveChangesAsync();
        }

        await using var context = NewContext();
        var conversion = new Odyssey.Core.Finance.CurrencyConversionService(context);

        // The hop (dated yesterday) beats the stale direct rate; the future-dated direct one is ignored.
        Assert.Equal(10.5m * 0.1m, (await conversion.GetLatestRatesToAsync("EUR", ["USD"]))["USD"]);
        Assert.Equal(1050m, await conversion.ConvertAsync(100m, "USD", "NOK"));
        var timeline = await conversion.GetRateTimelineToAsync("EUR", ["USD"], now.AddDays(-5), now);
        // The hop exists only once BOTH legs do (day −1), so the step series goes straight from the
        // direct rate to the hop at its newest USD → NOK leg.
        Assert.Equal([0.5m, 10.5m * 0.1m], timeline["USD"].Select(point => point.Rate));

        var rates = new Odyssey.Core.Finance.ExchangeRateService(context);
        var upcoming = await rates.ListAsync(new ExchangeRatesQueryParams { Status = [ExchangeRateStatus.Upcoming] });
        Assert.Equal(0.1m, Assert.Single(upcoming.Items).Rate);
        var current = await rates.ListAsync(new ExchangeRatesQueryParams
        {
            Status = [ExchangeRateStatus.Current], SortBy = ExchangeRateSortBy.Status,
        });
        Assert.Equal(3, current.TotalCount);
        var bySource = await rates.ListAsync(new ExchangeRatesQueryParams { Source = [ExchangeRateSource.Fetched] });
        Assert.Equal(3, bySource.TotalCount);
        var sortedByStatus = await rates.ListAsync(new ExchangeRatesQueryParams { SortBy = ExchangeRateSortBy.Status });
        Assert.Equal(ExchangeRateStatus.Upcoming, sortedByStatus.Items[^1].Status);

        var summary = await rates.GetSummaryAsync();
        Assert.Equal(5, summary.TotalCount);
        Assert.Equal(new ExchangeRateStatusCounts { Current = 3, Historical = 1, Upcoming = 1 }, summary.ByStatus);

        var (sync, currencies) = Services(context);
        var runs = await sync.ListRunsAsync(new ExchangeRateSyncRunsQueryParams());
        Assert.Single(runs.Items);
        var status = await sync.GetStatusAsync();
        Assert.Equal(1, status.EnabledCount);
        var list = await currencies.ListAsync(new ExchangeRateSyncCurrenciesQueryParams { Search = "USD" });
        Assert.Equal(DateOnly.FromDateTime(now.AddDays(-1)), Assert.Single(list.Items, item => item.CurrencyCode == "USD").NewestFetchedDate);
    }

    /// <summary>The two services wired to a provider that refuses every request: none of these paths may call out.</summary>
    private static (ExchangeRateSyncService Sync, ExchangeRateSyncCurrencyService Currencies) Services(OdysseyContext context)
    {
        var client = new NorgesBankClient(
            new HttpClient(new RefusingHandler()) { BaseAddress = new Uri(NorgesBankClient.BaseAddress) }, ExchangeRateSyncPacing.None);
        var cache = new ExchangeRateSupportedListCache(TimeProvider.System);
        var settings = new FixedSettings();
        var sync = new ExchangeRateSyncService(
            context, client, cache, settings, ExchangeRateSyncPacing.None, TimeProvider.System, NullLogger<ExchangeRateSyncService>.Instance);
        return (sync, new ExchangeRateSyncCurrencyService(context, client, cache, settings, sync, TimeProvider.System, NullLoggerFactory.Instance));
    }

    private sealed class RefusingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable));
    }

    /// <summary>Throws on the first save that modifies a <typeparamref name="TEntity"/>.</summary>
    private sealed class FailOnUpdate<TEntity> : Microsoft.EntityFrameworkCore.Diagnostics.SaveChangesInterceptor
        where TEntity : class
    {
        public override ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int>> SavingChangesAsync(
            Microsoft.EntityFrameworkCore.Diagnostics.DbContextEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<TEntity>().Any(entry => entry.State == EntityState.Modified))
            {
                throw new InvalidOperationException("Injected failure.");
            }

            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
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

    private OdysseyContext NewContext(Microsoft.EntityFrameworkCore.Diagnostics.IInterceptor? interceptor = null)
    {
        var options = new DbContextOptionsBuilder<OdysseyContext>()
            .UseMySql(fixture.ConnectionStringFor(Database), ServerVersion.AutoDetect(fixture.OdysseyConnectionString));
        if (interceptor is not null)
        {
            options.AddInterceptors(interceptor);
        }

        return new OdysseyContext(options.Options);
    }

    private async Task RecreateAsync()
    {
        await using var server = new OdysseyContext(new DbContextOptionsBuilder<OdysseyContext>()
            .UseMySql(fixture.OdysseyConnectionString, ServerVersion.AutoDetect(fixture.OdysseyConnectionString))
            .Options);
        await server.Database.ExecuteSqlRawAsync($"DROP DATABASE IF EXISTS `{Database}`");
        await server.Database.ExecuteSqlRawAsync($"CREATE DATABASE `{Database}`");
    }
}
