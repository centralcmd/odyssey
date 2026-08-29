using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace Odyssey.MigrationService;

/// <summary>
/// The one place a context is migrated. A single step calls it today, but it stays a separate seam so
/// the drift guard and the cancellation rule below travel with any future one (issue #468).
/// </summary>
public static class MigrationRunner
{
    /// <summary>
    /// Checks for migration-history drift, then applies the context's pending migrations.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <paramref name="cancellationToken"/> governs the read-only drift check, and deliberately
    /// <em>not</em> <c>MigrateAsync</c>. Cancelling mid-migration is precisely how a database ends up
    /// drifted: MariaDB commits DDL implicitly, so tearing the run down between two
    /// <c>CREATE TABLE</c>s leaves those tables behind with no history row to say so, and no later run
    /// can recover. Once the DDL has started, finishing is materially safer than stopping, so a
    /// shutdown signal is allowed to wait for it — see <c>HostOptions.ShutdownTimeout</c> in
    /// <c>Program.cs</c>.
    /// </para>
    /// <para>
    /// This narrows the window; it does not close it. A <c>SIGKILL</c>, an OOM kill or a hard container
    /// stop still lands wherever it lands, which is why the drift guard exists at all. Atomic DDL is
    /// simply not available on this engine.
    /// </para>
    /// </remarks>
    public static async Task MigrateAsync(DbContext dbContext, CancellationToken cancellationToken)
    {
        var strategy = dbContext.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await GuardAgainstDriftAsync(dbContext, cancellationToken);

            await dbContext.Database.MigrateAsync(CancellationToken.None);
        });
    }

    /// <summary>
    /// Fails fast when a pending migration would recreate an object the database already has, rather
    /// than letting EF replay it and surface the engine's bare "already exists" error.
    /// </summary>
    internal static async Task GuardAgainstDriftAsync(DbContext dbContext, CancellationToken cancellationToken)
    {
        // The check reads information_schema, so it only means anything against a real engine. This is
        // NOT support for the in-memory provider: MigrateAsync below is relational-only and throws
        // against it either way. The short-circuit exists so that a non-relational context fails on
        // that call, with EF's own message, rather than on a drift query that could never have run.
        if (!dbContext.Database.IsRelational())
        {
            return;
        }

        var pending = await ReadPendingMigrationObjectsAsync(dbContext, cancellationToken);
        if (pending.Count == 0)
        {
            return;
        }

        var existing = await ReadSchemaObjectsAsync(dbContext, cancellationToken);

        if (MigrationDriftDetector.Detect(pending, existing) is { } drift)
        {
            throw new MigrationDriftException(dbContext.GetType().Name, drift);
        }
    }

    /// <summary>
    /// Reduces each pending migration to the objects it would create, by reading the migration's own
    /// <see cref="MigrationOperation"/>s rather than re-deriving them from the model — the operations
    /// are what actually runs, so they cannot disagree with it.
    /// </summary>
    private static async Task<IReadOnlyList<PendingMigrationObjects>> ReadPendingMigrationObjectsAsync(
        DbContext dbContext, CancellationToken cancellationToken)
    {
        var pendingIds = await dbContext.Database.GetPendingMigrationsAsync(cancellationToken);

        var migrationsAssembly = dbContext.GetService<IMigrationsAssembly>();
        var activeProvider = dbContext.Database.ProviderName
            ?? throw new InvalidOperationException("The context has no active provider.");

        var pending = new List<PendingMigrationObjects>();

        foreach (var migrationId in pendingIds)
        {
            if (!migrationsAssembly.Migrations.TryGetValue(migrationId, out var migrationType))
            {
                // Recorded as pending but absent from the assembly. Not drift, and not this guard's
                // problem — EF raises its own, clearer error for it.
                continue;
            }

            var operations = migrationsAssembly.CreateMigration(migrationType, activeProvider).UpOperations;

            pending.Add(new PendingMigrationObjects(
                migrationId,
                [.. operations.SelectMany(CreatedBy)]));
        }

        return pending;
    }

    /// <summary>
    /// Maps one EF migration operation onto the schema objects it creates. Operations that only
    /// modify or drop are ignored: replaying those cannot collide with something already present,
    /// which is the only failure this guard is about.
    /// </summary>
    /// <remarks>
    /// The four kinds here are the ones EF emits as independent DDL statements, so each is a point an
    /// interruption can land between. Every migration in the repository today bundles its indexes and
    /// foreign keys into <c>CreateTable</c>, which is why table and column cover the cases seen so far —
    /// but a later index-only or constraint-only migration would drift exactly the same way, and adding
    /// it to this switch is the whole change.
    /// </remarks>
    private static IEnumerable<SchemaObject> CreatedBy(MigrationOperation operation) => operation switch
    {
        CreateTableOperation table =>
            [new SchemaObject(SchemaObjectKind.Table, table.Name, table.Name)],
        AddColumnOperation column =>
            [new SchemaObject(SchemaObjectKind.Column, column.Table, column.Name)],
        CreateIndexOperation index =>
            [new SchemaObject(SchemaObjectKind.Index, index.Table, index.Name)],
        AddForeignKeyOperation foreignKey =>
            [new SchemaObject(SchemaObjectKind.ForeignKey, foreignKey.Table, foreignKey.Name)],
        _ => [],
    };

    /// <summary>
    /// The tables, columns, indexes and foreign keys the database already holds.
    /// </summary>
    internal static async Task<SchemaObjects> ReadSchemaObjectsAsync(
        DbContext dbContext, CancellationToken cancellationToken)
    {
        // One statement, three sources, so the snapshot is a single round trip and cannot be taken
        // half-way through someone else's DDL. COLUMNS carries the tables too — every table has at
        // least one column, so the table names fall out of the same rows.
        const string sql = """
            SELECT 'Column' AS Kind, TABLE_NAME, COLUMN_NAME AS NAME
            FROM information_schema.COLUMNS
            WHERE TABLE_SCHEMA = DATABASE()
            UNION ALL
            SELECT 'Index', TABLE_NAME, INDEX_NAME
            FROM information_schema.STATISTICS
            WHERE TABLE_SCHEMA = DATABASE()
            UNION ALL
            SELECT 'ForeignKey', TABLE_NAME, CONSTRAINT_NAME
            FROM information_schema.TABLE_CONSTRAINTS
            WHERE CONSTRAINT_SCHEMA = DATABASE() AND CONSTRAINT_TYPE = 'FOREIGN KEY'
            """;

        var objects = new List<SchemaObject>();

        await dbContext.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            await using var command = dbContext.Database.GetDbConnection().CreateCommand();
            command.CommandText = sql;

            await using DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var kind = reader.GetString(0);
                var table = reader.GetString(1);
                var name = reader.GetString(2);

                switch (kind)
                {
                    case "Column":
                        objects.Add(new SchemaObject(SchemaObjectKind.Table, table, table));
                        objects.Add(new SchemaObject(SchemaObjectKind.Column, table, name));
                        break;
                    case "Index":
                        objects.Add(new SchemaObject(SchemaObjectKind.Index, table, name));
                        break;
                    case "ForeignKey":
                        objects.Add(new SchemaObject(SchemaObjectKind.ForeignKey, table, name));
                        break;
                }
            }
        }
        finally
        {
            await dbContext.Database.CloseConnectionAsync();
        }

        return new SchemaObjects(objects);
    }
}
