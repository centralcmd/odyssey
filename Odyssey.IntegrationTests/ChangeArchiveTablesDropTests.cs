using Microsoft.EntityFrameworkCore;
using Odyssey.Context;
using Odyssey.MigrationService;
using Xunit;

namespace Odyssey.IntegrationTests;

/// <summary>
/// Issue #78: the migration dropping issue #75's two archive ledgers.
///
/// <para>
/// Real MariaDB, because every assertion here is about a SCHEMA — neither table is an EF entity, and the
/// InMemory provider has no migrations and no <c>information_schema</c>, so it would report this green
/// whatever the migration did.
/// </para>
/// </summary>
[Collection(MariaDbCollection.Name)]
public class ChangeArchiveTablesDropTests(MariaDbFixture fixture)
{
    private const string Database = "odyssey_change_archive_drop";

    /// <summary>The last migration of issue #75's set — the one immediately before the drop.</summary>
    private const string BeforeDrop = "MakeBudgetItemTagRequiredAndDropItemNames";

    /// <summary>The migration immediately before issue #75's set.</summary>
    private const string BeforeIssue75 = "DropPersonDetailsRelationshipType";

    private static readonly string[] ArchiveTables = ["_BudgetItemLabelArchive", "_TransactionTagNameDisambiguation"];

    private static readonly Guid TagId = new("11111111-1111-1111-1111-111111111111");
    private static readonly Guid BudgetId = new("aaaaaaaa-0000-0000-0000-000000000000");
    private static readonly Guid ItemId = new("b3333333-3333-3333-3333-333333333333");

    /// <summary>AC 1. Applying every migration from empty leaves neither table behind.</summary>
    [SkippableFact]
    public async Task Applying_every_migration_from_empty_leaves_neither_archive_table()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await RecreateAsync();

        try
        {
            await using var context = NewContext();
            await MigrationRunner.MigrateAsync(context, CancellationToken.None);

            foreach (var table in ArchiveTables)
            {
                Assert.False(await MigrationSeam.TableExistsAsync(context, table), $"{table} survived the migrations.");
            }
        }
        finally
        {
            await DropAsync();
        }
    }

    /// <summary>
    /// AC 2. <c>Down</c> across the drop recreates both tables with the columns and primary keys
    /// <c>AddChangeArchiveTables</c> gave them, and <c>Up</c> drops them again.
    /// </summary>
    /// <remarks>
    /// "Original" is the schema captured at <see cref="BeforeDrop"/> rather than a hardcoded column list,
    /// so the comparison is against what the creating migration actually produced — types, nullability,
    /// collations, column order and key order included.
    /// </remarks>
    [SkippableFact]
    public async Task Down_recreates_both_tables_as_they_were_and_Up_drops_them_again()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await RecreateAsync();

        try
        {
            await using var context = NewContext();
            await MigrationSeam.MigrateToAsync(context, BeforeDrop);
            var original = await SchemaAsync(context);

            await context.Database.MigrateAsync();
            foreach (var table in ArchiveTables)
            {
                Assert.False(await MigrationSeam.TableExistsAsync(context, table));
            }

            await MigrationSeam.MigrateToAsync(context, BeforeDrop);
            Assert.Equal(original, await SchemaAsync(context));
            foreach (var table in ArchiveTables)
            {
                Assert.Equal(0, await MigrationSeam.CountAsync(context, $"SELECT COUNT(*) FROM `{table}`"));
            }

            await context.Database.MigrateAsync();
            foreach (var table in ArchiveTables)
            {
                Assert.False(await MigrationSeam.TableExistsAsync(context, table));
            }
        }
        finally
        {
            await DropAsync();
        }
    }

    /// <summary>
    /// AC 3. A database that already ran the issue #75 set, with rows in both ledgers, migrates cleanly
    /// and keeps no trace of either table.
    /// </summary>
    [SkippableFact]
    public async Task Migrating_a_database_whose_archives_hold_rows_drops_both()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await RecreateAsync();

        try
        {
            await using (var context = NewContext())
            {
                await MigrationSeam.MigrateToAsync(context, BeforeDrop);
                await SeedArchivesAsync(context);

                foreach (var table in ArchiveTables)
                {
                    Assert.Equal(1, await MigrationSeam.CountAsync(context, $"SELECT COUNT(*) FROM `{table}`"));
                }
            }

            await using (var context = NewContext())
            {
                await MigrationRunner.MigrateAsync(context, CancellationToken.None);

                Assert.Empty(await context.Database.GetPendingMigrationsAsync());
                Assert.Equal(0, await MigrationSeam.CountAsync(context,
                    "SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA = DATABASE() "
                    + "AND TABLE_NAME IN ('_BudgetItemLabelArchive', '_TransactionTagNameDisambiguation')"));
            }
        }
        finally
        {
            await DropAsync();
        }
    }

    /// <summary>
    /// The price the drop's <c>Down</c> documents, pinned so it stays a known one: reverting all the
    /// way past issue #75 still runs without error on the empty ledgers, and a surviving item comes back
    /// named by its id because its label is gone.
    /// </summary>
    [SkippableFact]
    public async Task Reverting_past_issue_75_after_the_drop_still_succeeds_without_the_labels()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await RecreateAsync();

        try
        {
            await using var context = NewContext();
            await context.Database.MigrateAsync();
            await SeedBudgetItemAsync(context);

            await MigrationSeam.MigrateToAsync(context, BeforeIssue75);

            var item = await MigrationSeam.RowAsync(context,
                $"SELECT * FROM `BudgetItems` WHERE `BudgetItemId` = '{ItemId}'");
            Assert.NotNull(item);
            Assert.Equal(ItemId.ToString(), item["Name"]);
            Assert.Null(item["Description"]);
            foreach (var table in ArchiveTables)
            {
                Assert.False(await MigrationSeam.TableExistsAsync(context, table));
            }
        }
        finally
        {
            await DropAsync();
        }
    }

    // ── Seeding ───────────────────────────────────────────────────────────────

    private static async Task SeedArchivesAsync(OdysseyContext context)
    {
        await SeedBudgetItemAsync(context);

        await context.Database.ExecuteSqlRawAsync(
            "INSERT INTO `_BudgetItemLabelArchive` (`BudgetItemId`, `Disposition`, `BudgetId`, `CategoryType`, "
            + "`PlannedAmount`, `TransactionTagId`, `Name`, `Description`, `ArchivedAt`) "
            + "VALUES ({0}, 'LabelsDropped', {1}, 0, 100, {2}, 'Weekly shop', 'Food and household', UTC_TIMESTAMP(6))",
            ItemId, BudgetId, TagId);

        await context.Database.ExecuteSqlRawAsync(
            "INSERT INTO `_TransactionTagNameDisambiguation` (`TransactionTagId`, `OriginalName`, `NewName`, `ArchivedAt`) "
            + "VALUES ({0}, 'groceries', 'Groceries', UTC_TIMESTAMP(6))",
            TagId);
    }

    private static async Task SeedBudgetItemAsync(OdysseyContext context)
    {
        context.TransactionTags.Add(new TransactionTag { TransactionTagId = TagId, Name = "Groceries" });
        context.Budgets.Add(new Budget
        {
            BudgetId = BudgetId,
            Name = "2026",
            Description = "Annual",
            StartDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            EndDate = new DateTime(2026, 12, 31, 0, 0, 0, DateTimeKind.Utc),
            BaseCurrencyCode = "USD",
        });
        context.BudgetItems.Add(new BudgetItem
        {
            BudgetItemId = ItemId,
            BudgetId = BudgetId,
            CategoryType = BudgetCategoryType.Expense,
            PlannedAmount = 100m,
            TransactionTagId = TagId,
        });
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
    }

    // ── Schema probe ──────────────────────────────────────────────────────────

    /// <summary>
    /// Every column of both archive tables (name, position, type, nullability, collation) followed by
    /// every primary-key member in key order, one line each.
    /// </summary>
    private static async Task<List<string>> SchemaAsync(OdysseyContext context)
    {
        var lines = new List<string>();
        await using var command = context.Database.GetDbConnection().CreateCommand();
        command.CommandText = """
            SELECT CONCAT_WS('|', TABLE_NAME, ORDINAL_POSITION, COLUMN_NAME, COLUMN_TYPE, IS_NULLABLE,
                             IFNULL(COLLATION_NAME, '-'))
            FROM information_schema.COLUMNS
            WHERE TABLE_SCHEMA = DATABASE()
              AND TABLE_NAME IN ('_BudgetItemLabelArchive', '_TransactionTagNameDisambiguation')
            UNION ALL
            SELECT CONCAT_WS('|', TABLE_NAME, 'PK', SEQ_IN_INDEX, COLUMN_NAME)
            FROM information_schema.STATISTICS
            WHERE TABLE_SCHEMA = DATABASE()
              AND TABLE_NAME IN ('_BudgetItemLabelArchive', '_TransactionTagNameDisambiguation')
              AND INDEX_NAME = 'PRIMARY'
            """;

        await context.Database.OpenConnectionAsync();
        try
        {
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                lines.Add(reader.GetString(0));
            }
        }
        finally
        {
            await context.Database.CloseConnectionAsync();
        }

        lines.Sort(StringComparer.Ordinal);

        // A probe that matched nothing would make the Down comparison vacuous: 9 + 4 columns, 2 + 1 keys.
        Assert.Equal(16, lines.Count);
        return lines;
    }

    // ── Database lifecycle ────────────────────────────────────────────────────

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

    private OdysseyContext NewContext()
    {
        var connection = fixture.ConnectionStringFor(Database);
        return new OdysseyContext(new DbContextOptionsBuilder<OdysseyContext>()
            .UseMySql(connection, ServerVersion.AutoDetect(connection))
            .Options);
    }
}
