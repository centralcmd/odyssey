// EF1002 flags interpolation into ExecuteSqlRawAsync. Every value interpolated below is a Guid or a
// literal this test wrote itself — there is no external input.
#pragma warning disable EF1002

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MySqlConnector;
using Odyssey.Context;
using Odyssey.MigrationService;
using Xunit;

namespace Odyssey.IntegrationTests;

/// <summary>
/// What <c>RetirePropertyAndVehicleAccountTypes</c> (issue #218) leaves in force, against real MariaDB:
/// <c>CK_Accounts_AccountTypeNotRetired</c> (AC 9), the drift guard recognising that <c>CHECK</c> (AC 11), and the
/// migrations job reporting nothing where there is nothing to retire (AC 12). None of it is observable on the fast
/// tiers: the EF InMemory provider runs no migrations and enforces no <c>CHECK</c>.
/// </summary>
/// <remarks>
/// The move of property and vehicle accounts onto properties, its idempotent replay and its count report are no
/// longer tested: every production database has applied the migration (see the README's <i>Data migration tests are
/// retired once production has applied them</i>).
/// </remarks>
[Collection(MariaDbCollection.Name)]
public class RetirePropertyAndVehicleAccountTypesMigrationTests(MariaDbFixture fixture)
{
    private const string Database = "odyssey_retire_account_types";

    private const string Subject = AccountTypeRetirementReport.MigrationName;

    private const string Check = "CK_Accounts_AccountTypeNotRetired";

    /// <summary>AC 9 — the CHECK refuses a retired ordinal written past the API.</summary>
    [SkippableFact]
    public async Task The_check_refuses_a_retired_account_type()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await fixture.CreateMigratedDatabaseAsync(Database);

        try
        {
            await using var context = NewContext();
            foreach (var retired in new[] { 6, 7 })
            {
                var error = await Assert.ThrowsAnyAsync<MySqlException>(() => context.Database.ExecuteSqlRawAsync(
                    "INSERT INTO `Accounts` (`AccountId`, `Name`, `Description`, `Opened`, `AccountType`, `CurrencyCode`) " +
                    $"VALUES ('{Guid.NewGuid()}', 'Sneaky', '', '2026-01-01', {retired}, 'USD')"));
                Assert.Contains(Check, error.Message, StringComparison.Ordinal);
            }
        }
        finally
        {
            await DropAsync();
        }
    }

    /// <summary>
    /// AC 11 — interrupted after the <c>CHECK</c>, before the history row: the drift guard refuses, naming the
    /// <c>CHECK</c>, instead of a raw duplicate-constraint error. This is the drift guard's real-engine test of
    /// recognising a <c>CHECK</c> (read from <c>TABLE_CONSTRAINTS</c>).
    /// </summary>
    [SkippableFact]
    public async Task A_replay_after_the_check_is_reported_as_drift_naming_it()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await fixture.CreateMigratedDatabaseAsync(Database);

        try
        {
            await using (var context = NewContext())
            {
                await MigrationSeam.ForgetAsync(context, Subject);
            }

            await using (var context = NewContext())
            {
                var drift = await Assert.ThrowsAsync<MigrationDriftException>(
                    () => MigrationRunner.MigrateAsync(context, CancellationToken.None));

                Assert.Equal(MigrationSeam.IdOf(context, Subject), drift.MigrationId);
                Assert.Contains(Check, drift.ExistingObject, StringComparison.Ordinal);
                Assert.Contains("docs/migration-history-drift.md", drift.Message, StringComparison.Ordinal);
            }
        }
        finally
        {
            await DropAsync();
        }
    }

    /// <summary>
    /// AC 12 through the real <see cref="OdysseyMigrationService"/>: an empty database — where every migration is
    /// pending and there is no <c>Accounts</c> table to count — migrates without a retirement report, and a database
    /// already past the migration never logs one.
    /// </summary>
    [SkippableFact]
    public async Task The_migrations_job_logs_no_retirement_on_a_fresh_or_an_up_to_date_database()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);

        // An empty database: the whole history is pending.
        await RecreateAsync();
        try
        {
            var fresh = await RunMigrationServiceAsync();
            Assert.DoesNotContain(fresh, line => line.Contains("Retired the Property and Vehicle", StringComparison.Ordinal));
            await using (var context = NewContext())
            {
                Assert.True(await MigrationSeam.HasRunAsync(context, Subject));
                Assert.Null(await AccountTypeRetirementReport.ReadIfPendingAsync(context, CancellationToken.None));
            }

            // Already applied: the next run of the job reports nothing.
            Assert.DoesNotContain(await RunMigrationServiceAsync(),
                l => l.Contains("Retired the Property and Vehicle", StringComparison.Ordinal));
        }
        finally
        {
            await DropAsync();
        }
    }

    private async Task<List<string>> RunMigrationServiceAsync()
    {
        var services = new ServiceCollection();
        services.AddDbContext<OdysseyContext>(options => options.UseMySql(
            fixture.ConnectionStringFor(Database), ServerVersion.AutoDetect(fixture.OdysseyConnectionString)));
        services.AddScoped<Odyssey.Core.Locations.Conversion.ILocationConversion, Odyssey.Core.Locations.Conversion.LocationConversion>();
        await using var provider = services.BuildServiceProvider();

        var logger = new CapturingLogger<OdysseyMigrationService>();
        await new OdysseyMigrationService(provider, logger).ExecuteAsync(CancellationToken.None);
        return logger.Lines;
    }

    // ── Infrastructure ────────────────────────────────────────────────────────

    private sealed class CapturingLogger<T> : CapturingLogger, ILogger<T>;

    private class CapturingLogger : ILogger
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Lines.Add(formatter(state, exception));
    }

    private OdysseyContext NewContext() =>
        new(new DbContextOptionsBuilder<OdysseyContext>()
            .UseMySql(fixture.ConnectionStringFor(Database), ServerVersion.AutoDetect(fixture.OdysseyConnectionString))
            .Options);

    private async Task RecreateAsync()
    {
        await DropAsync();
        await using var server = ServerContext();
        await server.Database.ExecuteSqlRawAsync($"CREATE DATABASE `{Database}`");
    }

    private async Task DropAsync()
    {
        await using var server = ServerContext();
        await server.Database.ExecuteSqlRawAsync($"DROP DATABASE IF EXISTS `{Database}`");
    }

    private OdysseyContext ServerContext() =>
        new(new DbContextOptionsBuilder<OdysseyContext>()
            .UseMySql(fixture.OdysseyConnectionString, ServerVersion.AutoDetect(fixture.OdysseyConnectionString))
            .Options);
}

#pragma warning restore EF1002
