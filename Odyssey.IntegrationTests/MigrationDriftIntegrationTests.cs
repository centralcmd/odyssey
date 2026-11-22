using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Odyssey.Context;
using Odyssey.Context.Migrations;
using Odyssey.MigrationService;
using Xunit;

namespace Odyssey.IntegrationTests;

/// <summary>
/// Real-engine coverage for the migration-history drift guard (issue #468).
///
/// <para>
/// This has to run against MariaDB. The bug exists precisely because MariaDB commits DDL implicitly,
/// so a migration is not atomic there — EF InMemory has no schema, no history table and no notion of a
/// half-applied migration, and would report the guard working while it did nothing.
/// </para>
///
/// <para>
/// The interruption is reproduced by its aftermath rather than by killing a process mid-run: migrate
/// cleanly, then delete the history row. That leaves exactly the state the original failure was found
/// in — every table present, nothing recorded as applied — and unlike a timing-based kill it is
/// deterministic.
/// </para>
/// </summary>
[Collection(MariaDbPrivateDatabaseCollection.Name)]
public class MigrationDriftIntegrationTests(MariaDbFixture fixture)
{
    private const string Database = "odyssey_migration_drift";

    /// <summary>
    /// The regression guard. Before this, the second run replayed <c>InitialCreate</c> from the top and
    /// died with a bare <c>Table 'Accounts' already exists</c> — every subsequent run identically, with
    /// the API held down behind it and nothing anywhere saying why.
    /// </summary>
    [SkippableFact]
    public async Task A_migration_whose_history_row_is_missing_is_reported_as_drift()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await RecreateDatabaseAsync();

        try
        {
            string migrationId;
            string firstTable;

            await using (var context = new OdysseyContext(OdysseyOptions()))
            {
                migrationId = SchemaMigrationId(context);
                // The table the runner will report is whichever the migration creates first, which is
                // decided by the model's FK graph — naming one here would break on any schema change.
                firstTable = MigrationOperations(context).OfType<CreateTableOperation>().First().Name;
                await MigrationRunner.MigrateAsync(context, CancellationToken.None);
            }

            await using (var context = new OdysseyContext(OdysseyOptions()))
            {
                Assert.True(await TableExistsAsync(context, firstTable));

                // The whole history, not just InitialCreate's row: InitialCreate is the baseline, and a history
                // that kept the later rows but lost the baseline's would be refused by the cut-point guard
                // first (issue #581) — a state no interruption produces, since nothing is recorded before it.
                await context.Database.ExecuteSqlRawAsync("DELETE FROM `__EFMigrationsHistory`");
            }

            await using (var context = new OdysseyContext(OdysseyOptions()))
            {
                var error = await Assert.ThrowsAsync<MigrationDriftException>(
                    () => MigrationRunner.MigrateAsync(context, CancellationToken.None));

                Assert.Equal(migrationId, error.MigrationId);
                Assert.Contains(firstTable, error.ExistingObject);
                Assert.Contains("docs/migration-history-drift.md", error.Message);
            }
        }
        finally
        {
            await DropDatabaseAsync();
        }
    }

    /// <summary>
    /// A superseded set AFTER the baseline (issue #581 AC 17): the newest table-creating migration's history row
    /// carries an id this build does not ship, as a renumber leaves it. The baseline row is intact, so the cut-point
    /// guard passes, and the drift message names the superseded set — listing the unknown id — rather than offering
    /// both causes.
    /// </summary>
    /// <remarks>
    /// The squash shape this test used to model — InitialCreate's own row renamed — no longer reaches the drift guard:
    /// a history lacking the baseline id is the cut-point guard's to refuse, which
    /// <c>MigrationBaselineGuardIntegrationTests</c> covers.
    /// </remarks>
    [SkippableFact]
    public async Task A_database_built_by_a_superseded_migration_set_is_reported_as_drift()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await RecreateDatabaseAsync();

        try
        {
            string migrationId;
            string supersededId;
            string createdTable;

            await using (var context = new OdysseyContext(OdysseyOptions()))
            {
                (migrationId, createdTable) = NewestTableCreatingMigration(context);
                supersededId = migrationId + "Renumbered";
                await MigrationRunner.MigrateAsync(context, CancellationToken.None);
            }

            await using (var context = new OdysseyContext(OdysseyOptions()))
            {
                await context.Database.ExecuteSqlRawAsync(
                    "UPDATE `__EFMigrationsHistory` SET MigrationId = {0} WHERE MigrationId = {1}",
                    supersededId, migrationId);
            }

            await using (var context = new OdysseyContext(OdysseyOptions()))
            {
                var error = await Assert.ThrowsAsync<MigrationDriftException>(
                    () => MigrationRunner.MigrateAsync(context, CancellationToken.None));

                Assert.Equal(migrationId, error.MigrationId);
                Assert.Contains(createdTable, error.ExistingObject);
                Assert.Equal([supersededId], error.SupersededIds);
                Assert.Contains("superseded set", error.Message, StringComparison.Ordinal);
                Assert.Contains($"'{supersededId}'", error.Message, StringComparison.Ordinal);
                Assert.DoesNotContain("interrupted", error.Message, StringComparison.Ordinal);
                Assert.Contains("docs/migration-history-drift.md", error.Message, StringComparison.Ordinal);
            }
        }
        finally
        {
            await DropDatabaseAsync();
        }
    }

    /// <summary>
    /// Retired ids never count (issue #581 B2): an unknown id EARLIER than the baseline is what every healthy
    /// database holds after a rebaseline, so an ordinary interruption beside one keeps the interruption message.
    /// </summary>
    [SkippableFact]
    public async Task An_unknown_id_earlier_than_the_baseline_leaves_an_interruption_reported_as_one()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await RecreateDatabaseAsync();

        try
        {
            string migrationId;

            await using (var context = new OdysseyContext(OdysseyOptions()))
            {
                (migrationId, _) = NewestTableCreatingMigration(context);
                await MigrationRunner.MigrateAsync(context, CancellationToken.None);
            }

            await using (var context = new OdysseyContext(OdysseyOptions()))
            {
                await context.Database.ExecuteSqlRawAsync(
                    "DELETE FROM `__EFMigrationsHistory` WHERE MigrationId = {0}", migrationId);
                await context.Database.ExecuteSqlRawAsync(
                    "INSERT INTO `__EFMigrationsHistory` (MigrationId, ProductVersion) VALUES ('20200101000000_Retired', '9.0.0')");
            }

            await using (var context = new OdysseyContext(OdysseyOptions()))
            {
                var error = await Assert.ThrowsAsync<MigrationDriftException>(
                    () => MigrationRunner.MigrateAsync(context, CancellationToken.None));

                Assert.Equal(migrationId, error.MigrationId);
                Assert.Empty(error.SupersededIds);
                Assert.Contains("interrupted", error.Message, StringComparison.Ordinal);
            }
        }
        finally
        {
            await DropDatabaseAsync();
        }
    }

    /// <summary>The newest migration that creates a table, and the first table it creates.</summary>
    private static (string MigrationId, string Table) NewestTableCreatingMigration(OdysseyContext context)
    {
        var assembly = context.GetService<IMigrationsAssembly>();
        foreach (var id in assembly.Migrations.Keys.OrderDescending(StringComparer.Ordinal))
        {
            var created = assembly.CreateMigration(assembly.Migrations[id], context.Database.ProviderName!)
                .UpOperations.OfType<CreateTableOperation>().FirstOrDefault();
            if (created is not null && id != MigrationBaseline.Current.BaselineMigrationId)
            {
                return (id, created.Name);
            }
        }

        throw new InvalidOperationException("No migration after the baseline creates a table.");
    }

    [SkippableFact]
    public async Task A_fresh_database_and_an_up_to_date_one_both_migrate_without_complaint()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await RecreateDatabaseAsync();

        try
        {
            await using (var context = new OdysseyContext(OdysseyOptions()))
            {
                await MigrationRunner.MigrateAsync(context, CancellationToken.None);
                Assert.True(await TableExistsAsync(context, "Accounts"));
            }

            // Re-running with nothing pending is the ordinary case on every restart.
            await using (var context = new OdysseyContext(OdysseyOptions()))
            {
                await MigrationRunner.MigrateAsync(context, CancellationToken.None);
                Assert.Empty(await context.Database.GetPendingMigrationsAsync());
            }
        }
        finally
        {
            await DropDatabaseAsync();
        }
    }

    /// <summary>
    /// The snapshot has to actually see every kind the detector can look for. A <c>UNION</c> arm that
    /// silently matched nothing would leave indexes and foreign keys permanently undetectable while
    /// every other test here stayed green — the detector's own unit tests are fed hand-built objects
    /// and would never notice.
    /// </summary>
    [SkippableFact]
    public async Task The_schema_snapshot_sees_tables_columns_indexes_and_foreign_keys()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await RecreateDatabaseAsync();

        try
        {
            await using var context = new OdysseyContext(OdysseyOptions());
            await MigrationRunner.MigrateAsync(context, CancellationToken.None);

            var snapshot = await MigrationRunner.ReadSchemaObjectsAsync(context, CancellationToken.None);

            Assert.True(snapshot.Contains(new SchemaObject(SchemaObjectKind.Table, "Accounts", "Accounts")));
            Assert.True(snapshot.Contains(new SchemaObject(SchemaObjectKind.Column, "Accounts", "AccountId")));

            // Read the names out of the migration itself rather than restating them, so a schema change
            // cannot leave this asserting on objects that no longer exist.
            var operations = MigrationOperations(context);

            var index = operations.OfType<CreateIndexOperation>().First();
            Assert.True(
                snapshot.Contains(new SchemaObject(SchemaObjectKind.Index, index.Table, index.Name)),
                $"The snapshot did not see index '{index.Name}' on '{index.Table}'.");

            var foreignKey = operations.OfType<CreateTableOperation>()
                .SelectMany(table => table.ForeignKeys)
                .First();
            Assert.True(
                snapshot.Contains(
                    new SchemaObject(SchemaObjectKind.ForeignKey, foreignKey.Table, foreignKey.Name)),
                $"The snapshot did not see foreign key '{foreignKey.Name}' on '{foreignKey.Table}'.");
        }
        finally
        {
            await DropDatabaseAsync();
        }
    }

    /// <summary>
    /// The guard phase honours cancellation — it is read-only, so stopping there is free. Only the
    /// DDL below it is shielded from the token, and that asymmetry is the point (issue #468).
    /// </summary>
    [SkippableFact]
    public async Task A_cancelled_token_stops_the_guard_before_any_ddl_runs()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await RecreateDatabaseAsync();

        try
        {
            await using var context = new OdysseyContext(OdysseyOptions());
            using var cancelled = new CancellationTokenSource();
            await cancelled.CancelAsync();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => MigrationRunner.MigrateAsync(context, cancelled.Token));

            Assert.False(
                await TableExistsAsync(context, "Accounts"),
                "Cancelling during the guard must happen before any DDL is issued.");
        }
        finally
        {
            await DropDatabaseAsync();
        }
    }

    /// <summary>
    /// The operations of the SCHEMA-CREATING migration — the first one, <c>InitialCreate</c>.
    ///
    /// <para>
    /// It was <c>.Single()</c> while the squash left exactly one migration in the folder, and issue #8
    /// added a second: a data-only <c>InsertData</c> that creates no objects at all. <c>.First()</c>
    /// rather than a search for the one with a <c>CreateTableOperation</c>, because the drift guard
    /// this suite exercises is specifically about a migration replaying object creation from the top,
    /// and that is the first one by construction — every later migration is additive.
    /// </para>
    /// </summary>
    private static IReadOnlyList<MigrationOperation> MigrationOperations(OdysseyContext context)
    {
        var assembly = context.GetService<IMigrationsAssembly>();
        var migrationId = SchemaMigrationId(context);

        return assembly
            .CreateMigration(assembly.Migrations[migrationId], context.Database.ProviderName!)
            .UpOperations;
    }

    /// <summary>
    /// The migration whose history row these tests tamper with. It has to be the one that CREATES the
    /// schema: the guard reports "a pending migration would create an object the database already
    /// has", so pointing this at issue #8's data-only migration would make every assertion here
    /// vacuous — nothing it creates could already exist.
    /// </summary>
    private static string SchemaMigrationId(OdysseyContext context) =>
        context.Database.GetMigrations().First();

    private static async Task<bool> TableExistsAsync(OdysseyContext context, string table)
    {
        var connection = context.Database.GetDbConnection();
        await context.Database.OpenConnectionAsync();
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT COUNT(*) FROM information_schema.TABLES "
                + "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @table";

            var parameter = command.CreateParameter();
            parameter.ParameterName = "@table";
            parameter.Value = table;
            command.Parameters.Add(parameter);

            return Convert.ToInt64(await command.ExecuteScalarAsync()) > 0;
        }
        finally
        {
            await context.Database.CloseConnectionAsync();
        }
    }

    private async Task RecreateDatabaseAsync()
    {
        await using var admin = AdminContext();

        await admin.Database.ExecuteSqlRawAsync($"DROP DATABASE IF EXISTS `{Database}`");
        await admin.Database.ExecuteSqlRawAsync($"CREATE DATABASE `{Database}`");
    }

    private async Task DropDatabaseAsync()
    {
        await using var admin = AdminContext();

        await admin.Database.ExecuteSqlRawAsync($"DROP DATABASE IF EXISTS `{Database}`");
    }

    private OdysseyContext AdminContext() =>
        new(new DbContextOptionsBuilder<OdysseyContext>()
            .UseMySql(
                fixture.OdysseyConnectionString,
                ServerVersion.AutoDetect(fixture.OdysseyConnectionString))
            .Options);

    private DbContextOptions<OdysseyContext> OdysseyOptions()
    {
        var connection = fixture.ConnectionStringFor(Database);
        return new DbContextOptionsBuilder<OdysseyContext>()
            .UseMySql(connection, ServerVersion.AutoDetect(connection)).Options;
    }
}
