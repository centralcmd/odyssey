// EF1002: the database names interpolated below are this class's own constants.
#pragma warning disable EF1002

using Microsoft.EntityFrameworkCore;
using Odyssey.Context;
using Odyssey.Core.Locations.Conversion;
using Odyssey.MigrationService;
using Xunit;

namespace Odyssey.IntegrationTests;

/// <summary>
/// Issue #380 §11 — how <see cref="MigrationRunner"/> stages the location conversion between
/// <c>UnifyLocationsExpand</c> and <c>UnifyLocationsContract</c> against real MariaDB: a fresh database passes
/// through all three steps, and a half-applied Contract is reported as drift. The fixtures that call
/// <c>Database.MigrateAsync()</c> directly bypass the conversion by design (L3).
/// </summary>
/// <remarks>
/// The conversion over old-shape rows is no longer tested: every production database has run it (see the
/// README's <i>Data migration tests are retired once production has applied them</i>).
/// </remarks>
[Collection(MariaDbCollection.Name)]
public class LocationConversionMigrationTests(MariaDbFixture fixture)
{
    private const string Expand = "_" + ILocationConversion.ExpandMigration;
    private const string Contract = "_" + ILocationConversion.ContractMigration;

    /// <summary>
    /// AC 4 — a start after a partly applied Contract (one table's columns dropped, the other's not)
    /// returns from the conversion without error, and the drift guard reports the half-applied DDL.
    /// </summary>
    [SkippableFact]
    public async Task A_partly_applied_contract_skips_the_conversion_and_is_reported_as_drift()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        const string database = "odyssey_location_conversion_partial";
        await fixture.CreateDatabaseAtMigrationAsync(database, Expand);

        try
        {
            await using (var context = NewContext(database))
            {
                await new LocationConversion(context).ConvertAsync(CancellationToken.None);

                // What an interruption part-way through UnifyLocationsContract leaves: the index it creates
                // first, and the Addresses columns dropped while RealEstateDetails keeps its own.
                await context.Database.ExecuteSqlRawAsync(
                    "CREATE UNIQUE INDEX `IX_Addresses_ContactId_LocationId` ON `Addresses` (`ContactId`, `LocationId`)");
                await context.Database.ExecuteSqlRawAsync(
                    "ALTER TABLE `Addresses` DROP COLUMN `Line1`, DROP COLUMN `Line2`, DROP COLUMN `City`, "
                    + "DROP COLUMN `PostalCode`, DROP COLUMN `Region`, DROP COLUMN `CountryCode`");
            }

            var conversion = new RecordingConversion(database, fixture);
            await using (var context = NewContext(database))
            {
                var drift = await Assert.ThrowsAsync<MigrationDriftException>(
                    () => MigrationRunner.MigrateAsync(context, conversion, CancellationToken.None));
                Assert.EndsWith(Contract, drift.MigrationId, StringComparison.Ordinal);
            }

            var result = Assert.Single(conversion.Results);
            Assert.False(result.Ran);
        }
        finally
        {
            await DropAsync(database);
        }
    }

    /// <summary>AC 4 — a fresh database upgrades through all three steps; the conversion finds nothing.</summary>
    [SkippableFact]
    public async Task A_fresh_database_upgrades_through_all_three_steps()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        const string database = "odyssey_location_conversion_fresh";
        await RecreateAsync(database);

        try
        {
            var conversion = new RecordingConversion(database, fixture);
            await using (var context = NewContext(database))
            {
                await MigrationRunner.MigrateAsync(context, conversion, CancellationToken.None);
                Assert.Empty(await context.Database.GetPendingMigrationsAsync());
            }

            Assert.Equal(new LocationConversionResult(true, 0, 0, 0), Assert.Single(conversion.Results));
        }
        finally
        {
            await DropAsync(database);
        }
    }

    /// <summary>Records each conversion run's result, so a test can see whether and how the runner called it.</summary>
    private sealed class RecordingConversion(string database, MariaDbFixture fixture) : ILocationConversion
    {
        public List<LocationConversionResult> Results { get; } = [];

        public async Task<LocationConversionResult> ConvertAsync(CancellationToken cancellationToken)
        {
            await using var context = new OdysseyContext(new DbContextOptionsBuilder<OdysseyContext>()
                .UseMySql(fixture.ConnectionStringFor(database), ServerVersion.AutoDetect(fixture.OdysseyConnectionString))
                .Options);
            var result = await new LocationConversion(context).ConvertAsync(cancellationToken);
            Results.Add(result);
            return result;
        }
    }

    // ── Database lifecycle ────────────────────────────────────────────────────

    private OdysseyContext NewContext(string database) =>
        new(new DbContextOptionsBuilder<OdysseyContext>()
            .UseMySql(fixture.ConnectionStringFor(database), ServerVersion.AutoDetect(fixture.OdysseyConnectionString))
            .Options);

    private async Task RecreateAsync(string database)
    {
        await DropAsync(database);
        await using var server = ServerContext();
        await server.Database.ExecuteSqlRawAsync($"CREATE DATABASE `{database}`");
    }

    private async Task DropAsync(string database)
    {
        await using var server = ServerContext();
        await server.Database.ExecuteSqlRawAsync($"DROP DATABASE IF EXISTS `{database}`");
    }

    private OdysseyContext ServerContext() =>
        new(new DbContextOptionsBuilder<OdysseyContext>()
            .UseMySql(fixture.OdysseyConnectionString, ServerVersion.AutoDetect(fixture.OdysseyConnectionString))
            .Options);
}

#pragma warning restore EF1002
