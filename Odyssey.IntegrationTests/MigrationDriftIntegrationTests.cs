using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Odyssey.Context;
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
[Collection(MariaDbCollection.Name)]
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
                migrationId = context.Database.GetMigrations().Single();
                // The table the runner will report is whichever the migration creates first, which is
                // decided by the model's FK graph — naming one here would break on any schema change.
                firstTable = MigrationOperations(context).OfType<CreateTableOperation>().First().Name;
                await MigrationRunner.MigrateAsync(context, CancellationToken.None);
            }

            await using (var context = new OdysseyContext(OdysseyOptions()))
            {
                Assert.True(await TableExistsAsync(context, firstTable));

                await context.Database.ExecuteSqlRawAsync(
                    "DELETE FROM `__EFMigrationsHistory` WHERE MigrationId = {0}", migrationId);
            }

            await using (var context = new OdysseyContext(OdysseyOptions()))
            {
                var error = await Assert.ThrowsAsync<MigrationDriftException>(
                    () => MigrationRunner.MigrateAsync(context, CancellationToken.None));

                Assert.Equal(migrationId, error.MigrationId);
                Assert.Equal(nameof(OdysseyContext), error.ContextName);
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
    /// The second cause the guard's message names, and the one a squash produces: the database is
    /// entirely intact, but it was built by a migration set this build no longer ships, so the current
    /// migration is pending and dies on the tables the old set already created. Nothing was interrupted.
    /// </summary>
    /// <remarks>
    /// The guard is expected to detect this identically to an interrupted run, and to keep naming both
    /// causes — which is why this test asserts on the message rather than on a distinct exception or
    /// property: the diagnosis is the operator's to make, and the message is what they make it from.
    ///
    /// <para>
    /// The reason used to be that the guard <em>could not</em> tell the two apart: two contexts shared
    /// one <c>__EFMigrationsHistory</c> table, so the other context's ids always looked unknown from
    /// here and "history holds an id this build does not ship" meant nothing. With one context that is
    /// no longer true, and an unrecognised id would now be real evidence of a superseded set. Telling
    /// the two apart is therefore newly *possible* — but it is a separate change with its own message
    /// and its own test, and until someone makes it deliberately the guard reports both causes and this
    /// test pins that it still does.
    /// </para>
    /// </remarks>
    [SkippableFact]
    public async Task A_database_built_by_a_superseded_migration_set_is_reported_as_drift()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await RecreateDatabaseAsync();

        // An id in the shape this repository's squashed history actually left behind.
        const string supersededId = "20260828172122_InitialCreate";

        try
        {
            string migrationId;
            string firstTable;

            await using (var context = new OdysseyContext(OdysseyOptions()))
            {
                migrationId = context.Database.GetMigrations().Single();
                firstTable = MigrationOperations(context).OfType<CreateTableOperation>().First().Name;
                await MigrationRunner.MigrateAsync(context, CancellationToken.None);
            }

            await using (var context = new OdysseyContext(OdysseyOptions()))
            {
                // Swap the applied id for one this build does not ship. The schema is untouched and
                // complete — the only thing wrong is that history describes it with a name that is gone.
                await context.Database.ExecuteSqlRawAsync(
                    "UPDATE `__EFMigrationsHistory` SET MigrationId = {0} WHERE MigrationId = {1}",
                    supersededId, migrationId);
            }

            await using (var context = new OdysseyContext(OdysseyOptions()))
            {
                var error = await Assert.ThrowsAsync<MigrationDriftException>(
                    () => MigrationRunner.MigrateAsync(context, CancellationToken.None));

                Assert.Equal(migrationId, error.MigrationId);
                Assert.Contains(firstTable, error.ExistingObject);

                // The operator has to be able to reach the right diagnosis from the message alone.
                Assert.Contains("superseded", error.Message, StringComparison.OrdinalIgnoreCase);
                Assert.Contains("__EFMigrationsHistory", error.Message, StringComparison.Ordinal);
                Assert.Contains("docs/migration-history-drift.md", error.Message, StringComparison.Ordinal);
            }
        }
        finally
        {
            await DropDatabaseAsync();
        }
    }

    /// <summary>
    /// The other half of the guard, and the more dangerous one to get wrong: the healthy paths must
    /// stay silent. A detector that fired here would take down every deployment it was meant to
    /// protect.
    /// </summary>
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

    private static IReadOnlyList<MigrationOperation> MigrationOperations(OdysseyContext context)
    {
        var assembly = context.GetService<IMigrationsAssembly>();
        var migrationId = context.Database.GetMigrations().Single();

        return assembly
            .CreateMigration(assembly.Migrations[migrationId], context.Database.ProviderName!)
            .UpOperations;
    }

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
