// EF1002 flags interpolation into ExecuteSqlRawAsync. The interpolated values are a table name and the
// database name, both written by this test itself — there is no external input.
#pragma warning disable EF1002

using Microsoft.EntityFrameworkCore;
using Odyssey.Context;
using Odyssey.MigrationService;
using Xunit;

namespace Odyssey.IntegrationTests;

/// <summary>
/// The <c>MigrationRunner</c> drift guard as it applies to the <c>AccountTerms</c> → <c>Terms</c> rename
/// (issue #120): an interrupted rename is reported by name, the guard stays narrow, and a full migrate
/// records exactly the shipped ids.
/// </summary>
/// <remarks>
/// The rename's own data path (the row-preserving renames, the <c>Quarterly</c> remap and the
/// identity-count backfill) shipped before production went live and is no longer tested: it will never
/// run over real data again. What stays is the guard, which reads the rename's operations on every
/// startup.
/// </remarks>
[Collection(MariaDbCollection.Name)]
public class TermRenameMigrationTests(MariaDbFixture fixture)
{
    private const string Database = "odyssey_term_rename";

    private const string Rename = "_RenameAccountTermsToTermsAndReworkInterval";

    /// <summary>
    /// AC 46 — an interrupted rename is reported by name. Before the guard learned the
    /// <c>Rename*</c> operations, this half-state fell through <c>CreatedBy</c>'s default and the
    /// replay died on a bare <c>Table 'Terms' already exists</c> with no guidance.
    /// </summary>
    /// <remarks>
    /// The interruption is reproduced by its aftermath rather than by killing a process mid-run:
    /// start from a migrated database, then delete the history row. That is exactly the state MariaDB's
    /// implicit DDL commit leaves behind, and unlike a timing-based kill it is deterministic.
    /// </remarks>
    [SkippableFact]
    public async Task An_interrupted_rename_is_reported_as_drift_naming_the_renamed_table()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await fixture.CreateMigratedDatabaseAsync(Database);

        try
        {
            string migrationId;

            await using (var context = NewContext())
            {
                migrationId = MigrationSeam.IdOf(context, Rename);
            }

            await using (var context = NewContext())
            {
                Assert.True(await TableExistsAsync(context, "Terms"));

                await context.Database.ExecuteSqlRawAsync(
                    "DELETE FROM `__EFMigrationsHistory` WHERE MigrationId = {0}", migrationId);
            }

            await using (var context = NewContext())
            {
                var error = await Assert.ThrowsAsync<MigrationDriftException>(
                    () => MigrationRunner.MigrateAsync(context, CancellationToken.None));

                Assert.Equal(migrationId, error.MigrationId);
                Assert.Contains("Terms", error.ExistingObject);
                Assert.Contains("docs/migration-history-drift.md", error.Message);
            }
        }
        finally
        {
            await DropAsync();
        }
    }

    /// <summary>
    /// AC 47 — the guard stays NARROW. A clean database with every migration pending, and a fully
    /// migrated one with none, must both pass: the extension must not make an ordinary first run or
    /// an ordinary upgrade look like drift.
    /// </summary>
    [SkippableFact]
    public async Task A_clean_database_and_a_fully_migrated_one_both_pass_the_pre_flight_check()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await RecreateAsync();

        try
        {
            await using (var context = NewContext())
            {
                // Everything pending, nothing present — an ordinary first run.
                await MigrationRunner.GuardAgainstDriftAsync(context, CancellationToken.None);
                await MigrationRunner.MigrateAsync(context, CancellationToken.None);
            }

            await using (var context = NewContext())
            {
                // Nothing pending, everything present — an ordinary restart.
                await MigrationRunner.GuardAgainstDriftAsync(context, CancellationToken.None);
                await MigrationRunner.MigrateAsync(context, CancellationToken.None);
            }
        }
        finally
        {
            await DropAsync();
        }
    }

    /// <summary>
    /// AC 23 — the historical migration ids are untouched by the rename, and a full migrate records
    /// exactly the shipped set.
    /// </summary>
    /// <remarks>
    /// A migration id is a persisted string in <c>__EFMigrationsHistory</c>. Renaming the class
    /// renames the id, and every existing database would then see an unknown pending migration and
    /// replay it against a schema that already holds its objects — which is why
    /// <c>AddAccountTermLabel</c> and <c>CollapseFeeTermKinds</c> keep their names although the
    /// vocabulary around them moved on.
    /// </remarks>
    [SkippableFact]
    public async Task A_full_migrate_records_exactly_the_shipped_ids_including_the_historical_term_ones()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await RecreateAsync();

        try
        {
            await using var context = NewContext();
            await MigrationRunner.MigrateAsync(context, CancellationToken.None);

            var shipped = context.Database.GetMigrations().Order(StringComparer.Ordinal).ToList();
            var applied = (await context.Database.GetAppliedMigrationsAsync())
                .Order(StringComparer.Ordinal)
                .ToList();

            Assert.Equal(shipped, applied);

            Assert.Contains("20260911101930_AddAccountTermLabel", applied);
            Assert.Contains("20260911102006_CollapseFeeTermKinds", applied);
        }
        finally
        {
            await DropAsync();
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static async Task<bool> TableExistsAsync(OdysseyContext context, string table) =>
        await context.Database
            .SqlQueryRaw<int>(
                "SELECT COUNT(*) AS `Value` FROM information_schema.TABLES " +
                $"WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = '{table}'")
            .SingleAsync() > 0;

    private OdysseyContext NewContext() =>
        new(new DbContextOptionsBuilder<OdysseyContext>()
            .UseMySql(fixture.ConnectionStringFor(Database), ServerVersion.AutoDetect(fixture.OdysseyConnectionString))
            .Options);

    private async Task RecreateAsync()
    {
        await DropAsync();
        await using var server = fixture.CreateOdysseyContext();
        await server.Database.ExecuteSqlRawAsync($"CREATE DATABASE `{Database}`");
    }

    private async Task DropAsync()
    {
        await using var server = fixture.CreateOdysseyContext();
        await server.Database.ExecuteSqlRawAsync($"DROP DATABASE IF EXISTS `{Database}`");
    }
}
