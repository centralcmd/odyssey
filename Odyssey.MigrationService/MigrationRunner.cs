using System.Globalization;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Odyssey.Core.Locations.Conversion;

namespace Odyssey.MigrationService;

/// <summary>
/// The one place a context is migrated. A single step calls it today, but it stays a separate seam so
/// the drift guard and the cancellation rule below travel with any future one (issue #468).
/// </summary>
public static class MigrationRunner
{
    /// <summary>The migration that adds <c>CK_ExchangeRates_RatePositive</c> (issue #472).</summary>
    internal const string RatePositiveMigration = "AddExchangeRatePositiveCheck";

    /// <summary>The most row ids a <see cref="NonPositiveExchangeRateException"/> lists.</summary>
    internal const int NonPositiveRateIdLimit = 20;

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
    public static Task MigrateAsync(DbContext dbContext, CancellationToken cancellationToken) =>
        MigrateAsync(dbContext, conversion: null, cancellationToken);

    /// <summary>
    /// Migrates in stages around the issue #380 location conversion (§11): up to and including
    /// <see cref="ILocationConversion.ExpandMigration"/>, then the conversion, then the rest. The conversion
    /// runs only while <see cref="ILocationConversion.ContractMigration"/> is still pending, so a later
    /// upgrade never runs it again; without a <paramref name="conversion"/> (the test harnesses that build a
    /// schema with no data) everything migrates in one stage.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The drift guard runs before EACH stage, over that stage's pending migrations only, so a contract
    /// migration interrupted on an earlier start is reported before anything else runs. Both stage
    /// migrations are passed <see cref="CancellationToken.None"/> for the reason above. The conversion runs
    /// between them, outside any other execution strategy: each of its batches is its own strategy and
    /// transaction, so it is restart-safe and may be cancelled between batches.
    /// </para>
    /// </remarks>
    public static async Task MigrateAsync(
        DbContext dbContext, ILocationConversion? conversion, CancellationToken cancellationToken)
    {
        // The non-relational short-circuit stays first: MigrateAsync throws EF's own, clear error for a
        // non-relational provider, which nothing below — a pending-migrations read included — would.
        if (!dbContext.Database.IsRelational())
        {
            await dbContext.Database.MigrateAsync(CancellationToken.None);
            return;
        }

        var pending = (await dbContext.Database.GetPendingMigrationsAsync(cancellationToken)).ToList();
        var plan = PlanStages(pending, conversion is not null);

        if (plan.RunConversion)
        {
            if (plan.ExpandStage is { } expandStage)
            {
                var expandId = expandStage[^1];
                await RunStageAsync(dbContext, expandStage, cancellationToken,
                    () => dbContext.Database.MigrateAsync(expandId, CancellationToken.None));
            }

            await conversion!.ConvertAsync(cancellationToken);
            pending = (await dbContext.Database.GetPendingMigrationsAsync(cancellationToken)).ToList();
        }

        await RunStageAsync(dbContext, pending, cancellationToken,
            () => dbContext.Database.MigrateAsync(CancellationToken.None));
    }

    /// <summary>One stage: the drift guard over <paramref name="stage"/>, then <paramref name="migrate"/>.</summary>
    private static async Task RunStageAsync(
        DbContext dbContext, IReadOnlyCollection<string> stage, CancellationToken cancellationToken, Func<Task> migrate)
    {
        var strategy = dbContext.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await GuardAgainstDriftAsync(dbContext, cancellationToken, stage);

            // After the drift guard, so a drifted database is reported as drift first.
            await GuardAgainstNonPositiveRatesAsync(dbContext, stage, cancellationToken);

            await migrate();
        });
    }

    /// <summary>What <see cref="MigrateAsync(DbContext, ILocationConversion?, CancellationToken)"/> does before its final stage.</summary>
    /// <param name="ExpandStage">
    /// The pending migrations up to and including <see cref="ILocationConversion.ExpandMigration"/>, migrated as a
    /// stage of their own before the conversion; null when Expand is already applied (or there is no conversion).
    /// </param>
    /// <param name="RunConversion">Whether the conversion runs: only while the contract migration is still pending.</param>
    internal sealed record StagePlan(IReadOnlyList<string>? ExpandStage, bool RunConversion);

    /// <summary>
    /// The staging decision alone, with no database: a pure function of the pending migration ids, so the fast
    /// tier can pin it (senior-tester L3). The conversion runs only while the contract migration is pending —
    /// never on a later upgrade, never on a fresh database that a previous start already took past it — and
    /// Expand gets its own stage only when it is itself still pending.
    /// </summary>
    internal static StagePlan PlanStages(IReadOnlyList<string> pending, bool hasConversion)
    {
        if (!hasConversion || !pending.Any(id => IsNamed(id, ILocationConversion.ContractMigration)))
        {
            return new StagePlan(null, false);
        }

        var expandAt = pending.ToList().FindIndex(id => IsNamed(id, ILocationConversion.ExpandMigration));
        return new StagePlan(expandAt >= 0 ? pending.Take(expandAt + 1).ToList() : null, true);
    }

    /// <summary>
    /// Stops before <see cref="RatePositiveMigration"/> when <c>ExchangeRates</c> holds a row its CHECK forbids
    /// (issue #472 §11), naming at most <see cref="NonPositiveRateIdLimit"/> ids and the total. Reports and never
    /// repairs, like <see cref="GuardAgainstDriftAsync"/>.
    /// </summary>
    /// <remarks>
    /// It runs only while that migration is in the stage about to run, and does nothing on a non-relational
    /// provider or when the table does not exist yet — a fresh database has <c>InitialCreate</c> in the same stage
    /// and must migrate normally.
    /// </remarks>
    internal static async Task GuardAgainstNonPositiveRatesAsync(
        DbContext dbContext, IReadOnlyCollection<string> stage, CancellationToken cancellationToken)
    {
        if (!dbContext.Database.IsRelational() || !stage.Any(id => IsNamed(id, RatePositiveMigration)))
        {
            return;
        }

        await dbContext.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            var connection = dbContext.Database.GetDbConnection();

            await using (var exists = connection.CreateCommand())
            {
                exists.CommandText = """
                    SELECT COUNT(*) FROM information_schema.TABLES
                    WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'ExchangeRates'
                    """;
                if (Convert.ToInt64(await exists.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) == 0)
                {
                    return;
                }
            }

            long total;
            await using (var count = connection.CreateCommand())
            {
                count.CommandText = "SELECT COUNT(*) FROM ExchangeRates WHERE Rate <= 0";
                total = Convert.ToInt64(await count.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
            }

            if (total == 0)
            {
                return;
            }

            var ids = new List<string>();
            await using (var select = connection.CreateCommand())
            {
                select.CommandText =
                    $"SELECT ExchangeRateId FROM ExchangeRates WHERE Rate <= 0 ORDER BY ExchangeRateId LIMIT {NonPositiveRateIdLimit}";
                await using DbDataReader reader = await select.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    ids.Add(Convert.ToString(reader.GetValue(0), CultureInfo.InvariantCulture)!);
                }
            }

            throw new NonPositiveExchangeRateException(ids, total);
        }
        finally
        {
            await dbContext.Database.CloseConnectionAsync();
        }
    }

    private static bool IsNamed(string migrationId, string name) =>
        migrationId.EndsWith("_" + name, StringComparison.Ordinal);

    /// <summary>
    /// Fails fast when a pending migration would recreate an object the database already has, rather
    /// than letting EF replay it and surface the engine's bare "already exists" error.
    /// </summary>
    /// <param name="stage">
    /// The pending migrations of the stage about to run; null for every pending migration.
    /// </param>
    internal static async Task GuardAgainstDriftAsync(
        DbContext dbContext, CancellationToken cancellationToken, IReadOnlyCollection<string>? stage = null)
    {
        // The check reads information_schema, so it only means anything against a real engine. This is
        // NOT support for the in-memory provider: MigrateAsync below is relational-only and throws
        // against it either way. The short-circuit exists so that a non-relational context fails on
        // that call, with EF's own message, rather than on a drift query that could never have run.
        if (!dbContext.Database.IsRelational())
        {
            return;
        }

        var pending = await ReadPendingMigrationObjectsAsync(dbContext, stage, cancellationToken);
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
        DbContext dbContext, IReadOnlyCollection<string>? stage, CancellationToken cancellationToken)
    {
        var pendingIds = (await dbContext.Database.GetPendingMigrationsAsync(cancellationToken))
            .Where(id => stage is null || stage.Contains(id));

        var migrationsAssembly = dbContext.GetService<IMigrationsAssembly>();
        var activeProvider = dbContext.Database.ProviderName
            ?? throw new InvalidOperationException("The context has no active provider.");

        var pending = new List<PendingMigrationObjects>();
        var droppedEarlier = new HashSet<SchemaObject>(SchemaObjectComparer.Instance);

        foreach (var migrationId in pendingIds)
        {
            if (!migrationsAssembly.Migrations.TryGetValue(migrationId, out var migrationType))
            {
                // Recorded as pending but absent from the assembly. Not drift, and not this guard's
                // problem — EF raises its own, clearer error for it.
                continue;
            }

            var operations = migrationsAssembly.CreateMigration(migrationType, activeProvider).UpOperations;

            pending.Add(new PendingMigrationObjects(migrationId, CreatedAfterDrops(operations, droppedEarlier)));
        }

        return pending;
    }

    /// <summary>
    /// The objects <paramref name="operations"/> create, less any that an earlier operation of the
    /// pending run drops first — recorded into <paramref name="droppedEarlier"/> as it goes, so the set
    /// carries across the migrations of one run in order.
    /// </summary>
    /// <remarks>
    /// A drop-then-recreate of one name cannot collide on replay: whatever an interruption left behind
    /// under that name, the replayed drop removes it before the create runs. EF scaffolds every change to
    /// a <c>CHECK</c>'s expression exactly that way (<c>DropCheckConstraint</c> + <c>AddCheckConstraint</c>
    /// of the same name), so without this a guard that recognises check constraints would refuse every
    /// ordinary upgrade across such a migration — the false positive the narrow test exists to avoid.
    /// </remarks>
    internal static IReadOnlyList<SchemaObject> CreatedAfterDrops(
        IEnumerable<MigrationOperation> operations, ISet<SchemaObject> droppedEarlier)
    {
        var created = new List<SchemaObject>();

        foreach (var operation in operations)
        {
            foreach (var dropped in DroppedBy(operation))
            {
                droppedEarlier.Add(dropped);
            }

            created.AddRange(CreatedBy(operation).Where(candidate => !droppedEarlier.Contains(candidate)));
        }

        return created;
    }

    /// <summary>
    /// Maps one EF migration operation onto the schema objects it removes — a drop, or the OLD name of a
    /// rename. Used only to discount a later re-creation of the same name; see <see cref="CreatedAfterDrops"/>.
    /// </summary>
    internal static IEnumerable<SchemaObject> DroppedBy(MigrationOperation operation) => operation switch
    {
        DropTableOperation table =>
            [new SchemaObject(SchemaObjectKind.Table, table.Name, table.Name)],
        DropColumnOperation column =>
            [new SchemaObject(SchemaObjectKind.Column, column.Table, column.Name)],
        DropIndexOperation index when index.Table is { } indexTable =>
            [new SchemaObject(SchemaObjectKind.Index, indexTable, index.Name)],
        DropForeignKeyOperation foreignKey =>
            [new SchemaObject(SchemaObjectKind.ForeignKey, foreignKey.Table, foreignKey.Name)],
        DropCheckConstraintOperation check =>
            [new SchemaObject(SchemaObjectKind.CheckConstraint, check.Table, check.Name)],
        RenameTableOperation table =>
            [new SchemaObject(SchemaObjectKind.Table, table.Name, table.Name)],
        RenameColumnOperation column =>
            [new SchemaObject(SchemaObjectKind.Column, column.Table, column.Name)],
        RenameIndexOperation index when index.Table is { } indexTable =>
            [new SchemaObject(SchemaObjectKind.Index, indexTable, index.Name)],
        _ => [],
    };

    /// <summary>
    /// Maps one EF migration operation onto the schema objects it creates. Operations that only
    /// modify or drop are ignored here: replaying those cannot collide with something already present,
    /// which is the only failure this guard is about. (A drop does matter to a later create of the same
    /// name — <see cref="CreatedAfterDrops"/> handles that.)
    /// </summary>
    /// <remarks>
    /// The kinds here are the ones EF emits as independent DDL statements, so each is a point an
    /// interruption can land between. Most migrations bundle their indexes and foreign keys into
    /// <c>CreateTable</c>, which is why table and column cover the cases seen longest — but an
    /// index-only or constraint-only migration drifts exactly the same way, and adding it to this
    /// switch is the whole change.
    /// <para>
    /// A <c>Rename*</c> operation counts as a create for this guard's purpose: it is the object under
    /// its NEW name that a replay would collide with, and an interrupted rename leaves precisely that
    /// object present with no history row. There is deliberately no arm for the corresponding drop of
    /// the old name — replaying a drop cannot collide with something already present, which is the
    /// only failure this guard is about.
    /// </para>
    /// </remarks>
    internal static IEnumerable<SchemaObject> CreatedBy(MigrationOperation operation) => operation switch
    {
        CreateTableOperation table =>
            [new SchemaObject(SchemaObjectKind.Table, table.Name, table.Name)],
        AddColumnOperation column =>
            [new SchemaObject(SchemaObjectKind.Column, column.Table, column.Name)],
        CreateIndexOperation index =>
            [new SchemaObject(SchemaObjectKind.Index, index.Table, index.Name)],
        AddForeignKeyOperation foreignKey =>
            [new SchemaObject(SchemaObjectKind.ForeignKey, foreignKey.Table, foreignKey.Name)],
        AddCheckConstraintOperation check =>
            [new SchemaObject(SchemaObjectKind.CheckConstraint, check.Table, check.Name)],
        RenameTableOperation table when table.NewName is { } newTableName =>
            [new SchemaObject(SchemaObjectKind.Table, newTableName, newTableName)],
        RenameColumnOperation column =>
            [new SchemaObject(SchemaObjectKind.Column, column.Table, column.NewName)],
        RenameIndexOperation index when index.Table is { } indexTable =>
            [new SchemaObject(SchemaObjectKind.Index, indexTable, index.NewName)],
        _ => [],
    };

    /// <summary>
    /// The tables, columns, indexes, foreign keys and check constraints the database already holds.
    /// </summary>
    /// <remarks>
    /// Check constraints are read from <c>TABLE_CONSTRAINTS</c>, not <c>CHECK_CONSTRAINTS</c>: only the
    /// former carries <c>TABLE_NAME</c>, and a <see cref="SchemaObject"/> is matched on its table too.
    /// </remarks>
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
            SELECT IF(CONSTRAINT_TYPE = 'CHECK', 'CheckConstraint', 'ForeignKey'), TABLE_NAME, CONSTRAINT_NAME
            FROM information_schema.TABLE_CONSTRAINTS
            WHERE CONSTRAINT_SCHEMA = DATABASE() AND CONSTRAINT_TYPE IN ('FOREIGN KEY', 'CHECK')
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
                    case "CheckConstraint":
                        objects.Add(new SchemaObject(SchemaObjectKind.CheckConstraint, table, name));
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
