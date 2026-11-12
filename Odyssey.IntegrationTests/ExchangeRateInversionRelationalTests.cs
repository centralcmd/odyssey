using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Odyssey.Context;
using Odyssey.Core.Finance;
using Odyssey.Dtos.Finance;
using Odyssey.MigrationService;
using Xunit;

namespace Odyssey.IntegrationTests;

/// <summary>
/// Real-engine coverage for issue #472 release 1: <c>CK_ExchangeRates_RatePositive</c>, the pre-migration check
/// that names the rows it would reject, and the summary's query shape. None of it can run on EF InMemory: it
/// enforces no CHECK, has no migrations and never invokes a command interceptor.
/// </summary>
[Collection(MariaDbCollection.Name)]
public class ExchangeRateInversionRelationalTests(MariaDbFixture fixture)
{
    private const string Database = "odyssey_exchange_rate_inversion";

    /// <summary>The migration directly before <see cref="MigrationRunner.RatePositiveMigration"/>.</summary>
    private static string PreviousMigration(OdysseyContext context)
    {
        var all = context.Database.GetMigrations().ToList();
        var index = all.FindIndex(id => id.EndsWith("_" + MigrationRunner.RatePositiveMigration, StringComparison.Ordinal));
        Assert.True(index > 0, "The CHECK migration must exist and not be the first.");
        return all[index - 1];
    }

    [SkippableFact] // AC 13
    public async Task The_database_refuses_a_zero_rate()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await RecreateAsync();

        try
        {
            await using var context = NewContext();
            await MigrationRunner.MigrateAsync(context, CancellationToken.None);

            var error = await Assert.ThrowsAnyAsync<Exception>(() => context.Database.ExecuteSqlRawAsync(
                "INSERT INTO ExchangeRates (ExchangeRateId, FromCurrencyCode, ToCurrencyCode, Rate, AsOf, CreatedAt) " +
                "VALUES (UUID(), 'USD', 'EUR', 0, UTC_TIMESTAMP(6), UTC_TIMESTAMP(6))"));
            Assert.Contains("CK_ExchangeRates_RatePositive", error.ToString());
        }
        finally
        {
            await DropAsync();
        }
    }

    [SkippableFact] // AC 28
    public async Task A_pending_check_migration_names_the_rows_it_would_reject_and_applies_nothing()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await RecreateAsync();

        try
        {
            var bad = Enumerable.Range(0, 25).Select(_ => Guid.NewGuid()).ToList();
            var good = Enumerable.Range(0, 5).Select(_ => Guid.NewGuid()).ToList();
            await using (var context = NewContext())
            {
                await context.Database.MigrateAsync(PreviousMigration(context));
                var i = 0;
                foreach (var (id, rate) in bad.Select((id, n) => (id, n == 0 ? -1m : 0m)).Concat(good.Select(id => (id, 1.5m))))
                {
                    await context.Database.ExecuteSqlRawAsync(
                        "INSERT INTO ExchangeRates (ExchangeRateId, FromCurrencyCode, ToCurrencyCode, Rate, AsOf, CreatedAt) " +
                        "VALUES ({0}, 'USD', 'EUR', {1}, UTC_TIMESTAMP(6) - INTERVAL {2} SECOND, UTC_TIMESTAMP(6))",
                        id.ToString(), rate, i++);
                }
            }

            // The 20 lowest of the bad ids, in the column's own (string) order; never a good one.
            var expectedIds = bad.Select(id => id.ToString()).Order(StringComparer.Ordinal).Take(20).ToList();

            await using (var context = NewContext())
            {
                var pendingBefore = (await context.Database.GetPendingMigrationsAsync()).ToList();

                var error = await Assert.ThrowsAsync<NonPositiveExchangeRateException>(
                    () => MigrationRunner.MigrateAsync(context, CancellationToken.None));

                Assert.Equal(25, error.TotalCount);
                Assert.Equal(expectedIds, error.ExchangeRateIds);
                Assert.Contains("CK_ExchangeRates_RatePositive", error.Message);
                Assert.Contains("but 25 row(s) in ExchangeRates have Rate <= 0", error.Message);
                Assert.Contains("The first 20 ids", error.Message);
                Assert.All(expectedIds, id => Assert.Contains(id, error.Message));
                Assert.All(good, id => Assert.DoesNotContain(id.ToString(), error.Message));
                Assert.DoesNotContain("-1.0", error.Message); // ids only, never a rate
                Assert.DoesNotContain("1.5", error.Message);
                Assert.Equal(pendingBefore, await context.Database.GetPendingMigrationsAsync());
            }

            // Repaired, it migrates.
            await using (var context = NewContext())
            {
                await context.Database.ExecuteSqlRawAsync("DELETE FROM ExchangeRates WHERE Rate <= 0");
                await MigrationRunner.MigrateAsync(context, CancellationToken.None);
                Assert.Empty(await context.Database.GetPendingMigrationsAsync());
            }
        }
        finally
        {
            await DropAsync();
        }
    }

    [SkippableFact] // AC 28, fresh-database half
    public async Task A_fresh_database_migrates_to_the_head_without_the_check_complaining()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await RecreateAsync();

        try
        {
            await using var context = NewContext();
            await MigrationRunner.MigrateAsync(context, CancellationToken.None);
            Assert.Empty(await context.Database.GetPendingMigrationsAsync());
        }
        finally
        {
            await DropAsync();
        }
    }

    [SkippableFact] // AC 29, plus the release-1 dual-write state on the real engine (AC 25)
    public async Task The_summary_issues_a_fixed_number_of_commands_and_no_correlated_subquery()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await RecreateAsync();

        try
        {
            await using (var context = NewContext())
            {
                await MigrationRunner.MigrateAsync(context, CancellationToken.None);
            }

            // Small, then four times the pairs and ten times the rows: a per-pair or per-row query would grow.
            await SeedDualWriteAsync(["USD"], days: 4);
            var small = await SummaryCommandsAsync();
            Assert.Equal((8, 2), (small.Summary.TotalCount, small.Summary.ByStatus.Current));

            await SeedDualWriteAsync(["EUR", "SEK", "GBP"], days: 40);
            var large = await SummaryCommandsAsync();
            Assert.Equal(8 + 240, large.Summary.TotalCount);
            // One Current row per stored pair: eight pairs, the stored row winning every dual-write tie.
            Assert.Equal(8, large.Summary.ByStatus.Current);
            Assert.Equal(large.Summary.TotalCount - 8, large.Summary.ByStatus.Historical);

            Assert.Equal(small.Commands.Count, large.Commands.Count);
            Assert.All(large.Commands, sql => Assert.DoesNotContain("EXISTS", sql, StringComparison.OrdinalIgnoreCase));

            await using var check = NewContext();
            var current = await new ExchangeRateService(check).ListAsync(new ExchangeRatesQueryParams { Status = [ExchangeRateStatus.Current], Limit = 50 });
            Assert.Equal(8, current.TotalCount);
            Assert.All(current.Items, row => Assert.Equal(ExchangeRateStatus.Current, row.Status));
            Assert.Equal(Math.Round(1m / 10m, 8), await new CurrencyConversionService(check).ConvertAsync(1m, "NOK", "USD"));
        }
        finally
        {
            await DropAsync();
        }
    }

    private async Task SeedDualWriteAsync(IEnumerable<string> codes, int days)
    {
        var asOf = DateTime.UtcNow.AddDays(-1);
        await using var context = NewContext();
        foreach (var code in codes)
        {
            for (var day = 0; day < days; day++)
            {
                var created = asOf.AddDays(-day);
                context.ExchangeRates.AddRange(
                    new ExchangeRate { FromCurrencyCode = code, ToCurrencyCode = "NOK", Rate = 10m + day, AsOf = created, CreatedAt = created, Provider = ExchangeRateProvider.NorgesBank },
                    new ExchangeRate { FromCurrencyCode = "NOK", ToCurrencyCode = code, Rate = Math.Round(1m / (10m + day), 8), AsOf = created, CreatedAt = created, Provider = ExchangeRateProvider.NorgesBank });
            }
        }

        await context.SaveChangesAsync();
    }

    private async Task<(ExchangeRateSummary Summary, List<string> Commands)> SummaryCommandsAsync()
    {
        var counter = new CommandCounter();
        await using var context = NewContext(counter);
        var summary = await new ExchangeRateService(context).GetSummaryAsync();
        return (summary, counter.Commands);
    }

    private sealed class CommandCounter : DbCommandInterceptor
    {
        public List<string> Commands { get; } = [];

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Commands.Add(command.CommandText);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<object> result,
            CancellationToken cancellationToken = default)
        {
            Commands.Add(command.CommandText);
            return base.ScalarExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    private OdysseyContext NewContext(IInterceptor? interceptor = null)
    {
        var connection = fixture.ConnectionStringFor(Database);
        var builder = new DbContextOptionsBuilder<OdysseyContext>()
            .UseMySql(connection, ServerVersion.AutoDetect(connection));
        if (interceptor is not null)
        {
            builder.AddInterceptors(interceptor);
        }

        return new OdysseyContext(builder.Options);
    }

    private async Task RecreateAsync()
    {
        await using var admin = AdminContext();
        await admin.Database.ExecuteSqlRawAsync($"DROP DATABASE IF EXISTS `{Database}`");
        await admin.Database.ExecuteSqlRawAsync($"CREATE DATABASE `{Database}`");
    }

    private async Task DropAsync()
    {
        await using var admin = AdminContext();
        await admin.Database.ExecuteSqlRawAsync($"DROP DATABASE IF EXISTS `{Database}`");
    }

    private OdysseyContext AdminContext() =>
        new(new DbContextOptionsBuilder<OdysseyContext>()
            .UseMySql(fixture.OdysseyConnectionString, ServerVersion.AutoDetect(fixture.OdysseyConnectionString))
            .Options);
}
