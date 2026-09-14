using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Odyssey.Context;
using Odyssey.Core;
using Odyssey.Core.Finance;
using Odyssey.Dtos.Finance;
using Odyssey.MigrationService;
using Xunit;
using ContextTransactionStatus = Odyssey.Dtos.Finance.TransactionStatus;

namespace Odyssey.IntegrationTests;

/// <summary>
/// Issue #75's migration set and the two constraints it adds, against real MariaDB.
///
/// <para>
/// None of this is observable on the fast tiers by construction: the EF InMemory provider has no
/// schema, no migrations, no foreign keys and enforces no unique index across a relationship of this
/// shape. A green <c>Odyssey.Api.Tests</c> says the SERVICE guards hold; only this file says the
/// database does.
/// </para>
/// </summary>
[Collection(MariaDbCollection.Name)]
public class BudgetItemTagMigrationTests(MariaDbFixture fixture)
{
    private const string Database = "odyssey_budget_item_tag";

    /// <summary>The migration immediately before this change's set.</summary>
    private const string Baseline = "DropPersonDetailsRelationshipType";

    // Fixed ids so "lowest" is deterministic: BudgetItemId and TransactionTagId are char(36), so the
    // migrations' ordering is LEXICOGRAPHIC on these strings, not chronological.
    private static readonly Guid TagKept = new("11111111-1111-1111-1111-111111111111");
    private static readonly Guid TagRenamed = new("22222222-2222-2222-2222-222222222222");
    private static readonly Guid TagShared = new("33333333-3333-3333-3333-333333333333");
    private static readonly Guid BudgetId = new("aaaaaaaa-0000-0000-0000-000000000000");
    private static readonly Guid ItemUntagged = new("b0000000-0000-0000-0000-000000000000");
    private static readonly Guid ItemDuplicateKept = new("b1111111-1111-1111-1111-111111111111");
    private static readonly Guid ItemDuplicateDropped = new("b2222222-2222-2222-2222-222222222222");
    private static readonly Guid ItemValid = new("b3333333-3333-3333-3333-333333333333");
    private static readonly Guid AccountId = new("cccccccc-0000-0000-0000-000000000000");
    private static readonly Guid TransactionId = new("dddddddd-0000-0000-0000-000000000000");

    /// <summary>
    /// AC 22. One untagged item, one duplicate-tag pair, one valid item and two same-named tags go in;
    /// the valid item and the lower-id member of the pair come out, the higher-id tag is renamed with a
    /// numeric suffix while every reference to it stays intact, both unique indexes exist, and the
    /// archives record exactly what was destroyed.
    /// </summary>
    [SkippableFact]
    public async Task The_migration_set_removes_what_it_must_renames_the_rest_and_archives_both()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await RecreateAsync();

        try
        {
            await using (var context = NewContext())
            {
                await MigrationSeam.MigrateToAsync(context, Baseline);
                await SeedBeforeStateAsync(context);
            }

            await using (var context = NewContext())
            {
                await context.Database.MigrateAsync();

                // ── The rows that survive ────────────────────────────────────────
                var survivors = await context.BudgetItems.AsNoTracking()
                    .Select(item => item.BudgetItemId).OrderBy(id => id).ToListAsync();
                Assert.Equal([ItemDuplicateKept, ItemValid], survivors.OrderBy(id => id.ToString(), StringComparer.Ordinal));

                // Every survivor carries a tag, which is what NOT NULL now means.
                Assert.All(await context.BudgetItems.AsNoTracking().ToListAsync(),
                    item => Assert.NotEqual(Guid.Empty, item.TransactionTagId));

                // ── The rename ───────────────────────────────────────────────────
                var kept = await context.TransactionTags.AsNoTracking().SingleAsync(t => t.TransactionTagId == TagKept);
                var renamed = await context.TransactionTags.AsNoTracking().SingleAsync(t => t.TransactionTagId == TagRenamed);
                Assert.Equal("Groceries", kept.Name);       // the lowest id keeps its name
                Assert.Equal("groceries (2)", renamed.Name); // the higher id takes the suffix

                // Renamed, NOT merged (Non-Goal 8): every reference to the renamed tag is still valid.
                Assert.Equal(1, await context.TransactionTagLinks.CountAsync(l => l.TransactionTagId == TagRenamed));
                Assert.Equal(1, await context.Transactions.CountAsync(
                    t => t.TransactionTags.Any(tag => tag.TransactionTagId == TagRenamed)));

                // ── The archives ─────────────────────────────────────────────────
                // One Deleted row per removed item; one LabelsDropped row per survivor.
                Assert.Equal(2, await MigrationSeam.CountAsync(context,
                    "SELECT COUNT(*) FROM `_BudgetItemLabelArchive` WHERE `Disposition` = 'Deleted'"));
                Assert.Equal(2, await MigrationSeam.CountAsync(context,
                    "SELECT COUNT(*) FROM `_BudgetItemLabelArchive` WHERE `Disposition` = 'LabelsDropped'"));

                // The labels themselves are what the archive is FOR — ids alone could not reconstruct.
                var deletedUntagged = await MigrationSeam.RowAsync(context,
                    $"SELECT * FROM `_BudgetItemLabelArchive` WHERE `BudgetItemId` = '{ItemUntagged}'");
                Assert.Equal("Deleted", deletedUntagged!["Disposition"]);
                Assert.Equal("Old line", deletedUntagged["Name"]);
                Assert.Null(deletedUntagged["TransactionTagId"]);

                var survivorLabels = await MigrationSeam.RowAsync(context,
                    $"SELECT * FROM `_BudgetItemLabelArchive` WHERE `BudgetItemId` = '{ItemValid}'");
                Assert.Equal("LabelsDropped", survivorLabels!["Disposition"]);
                Assert.Equal("Weekly shop", survivorLabels["Name"]);
                Assert.Equal("Food and household", survivorLabels["Description"]);

                var disambiguated = await MigrationSeam.RowAsync(context,
                    $"SELECT * FROM `_TransactionTagNameDisambiguation` WHERE `TransactionTagId` = '{TagRenamed}'");
                Assert.Equal("groceries", disambiguated!["OriginalName"]);
                Assert.Equal("groceries (2)", disambiguated["NewName"]);

                // ── The schema ───────────────────────────────────────────────────
                Assert.DoesNotContain("Name", await ColumnsAsync(context, "BudgetItems"));
                Assert.DoesNotContain("Description", await ColumnsAsync(context, "BudgetItems"));
                Assert.Equal("NO", await NullabilityAsync(context, "BudgetItems", "TransactionTagId"));

                Assert.True(await IndexExistsAsync(context, "BudgetItems", "IX_BudgetItems_BudgetId_TransactionTagId"));
                Assert.True(await IndexExistsAsync(context, "TransactionTags", "IX_TransactionTags_Name"));
                Assert.False(await IndexExistsAsync(context, "BudgetItems", "IX_BudgetItems_BudgetId_Name"));

                // AC 14. The composite index leads with BudgetId and cannot serve a lookup by tag
                // alone — which is exactly what the RESTRICT key does on every tag delete. A migration
                // that dropped this one as "redundant" would be wrong.
                Assert.True(await IndexExistsAsync(context, "BudgetItems", "IX_BudgetItems_TransactionTagId"));
            }
        }
        finally
        {
            await DropAsync();
        }
    }

    /// <summary>
    /// AC 13 and AC 20. The two unique indexes are real: a direct insert bypassing every service guard
    /// is rejected by the database.
    /// </summary>
    [SkippableFact]
    public async Task Both_unique_indexes_reject_a_direct_duplicate_insert()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await RecreateAsync();

        try
        {
            await using var context = NewContext();
            await MigrationRunner.MigrateAsync(context, CancellationToken.None);

            context.TransactionTags.Add(new TransactionTag { TransactionTagId = TagKept, Name = "Groceries" });
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
                BudgetItemId = ItemValid,
                BudgetId = BudgetId,
                CategoryType = Odyssey.Context.BudgetCategoryType.Expense,
                PlannedAmount = 100m,
                TransactionTagId = TagKept,
            });
            await context.SaveChangesAsync();

            // (BudgetId, TransactionTagId) — one item per tag per budget.
            await Assert.ThrowsAsync<MySqlConnector.MySqlException>(() => context.Database.ExecuteSqlRawAsync(
                "INSERT INTO `BudgetItems` (`BudgetItemId`, `BudgetId`, `CategoryType`, `PlannedAmount`, `TransactionTagId`) "
                + "VALUES ({0}, {1}, 0, 50, {2})",
                ItemDuplicateDropped, BudgetId, TagKept));

            // TransactionTags.Name — case-insensitively, because the column's utf8mb4_*_ci collation
            // makes the index so. That is the half the service guard cannot prove.
            await Assert.ThrowsAsync<MySqlConnector.MySqlException>(() => context.Database.ExecuteSqlRawAsync(
                "INSERT INTO `TransactionTags` (`TransactionTagId`, `Name`) VALUES ({0}, {1})",
                TagRenamed, "GROCERIES"));
        }
        finally
        {
            await DropAsync();
        }
    }

    /// <summary>
    /// AC 13, second half. A duplicate arriving through the SERVICE after the pre-check passed must
    /// surface as the same field-keyed 409 as the pre-check path, not as a generic one — otherwise the
    /// concurrent loser of the race gets a conflict the form cannot attach to a control.
    /// </summary>
    /// <remarks>
    /// The race is produced rather than hoped for: an interceptor inserts the conflicting row on a
    /// SEPARATE connection between the service's pre-check and its <c>SaveChangesAsync</c>, which is
    /// exactly the window the unique index exists to close.
    /// </remarks>
    [SkippableFact]
    public async Task A_duplicate_that_slips_past_the_precheck_is_the_same_keyed_conflict()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await RecreateAsync();

        try
        {
            await using (var setup = NewContext())
            {
                await MigrationRunner.MigrateAsync(setup, CancellationToken.None);

                setup.TransactionTags.Add(new TransactionTag { TransactionTagId = TagKept, Name = "Groceries" });
                setup.Budgets.Add(new Budget
                {
                    BudgetId = BudgetId,
                    Name = "2026",
                    Description = "Annual",
                    StartDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                    EndDate = new DateTime(2026, 12, 31, 0, 0, 0, DateTimeKind.Utc),
                    BaseCurrencyCode = "USD",
                });
                await setup.SaveChangesAsync();
            }

            var interloper = new RaceInterceptor(() => InsertConflictingRowAsync());
            await using var context = NewContext(interloper);

            var conflict = await Assert.ThrowsAsync<DomainConflictException>(
                () => new BudgetItemService(context).Create(new NewBudgetItem
                {
                    BudgetId = BudgetId,
                    CategoryType = Odyssey.Dtos.Finance.BudgetCategoryType.Expense,
                    PlannedAmount = 100m,
                    TransactionTagId = TagKept,
                }));

            Assert.True(interloper.Fired, "The interceptor never ran, so no race was produced.");
            Assert.NotNull(conflict.Errors);
            Assert.True(conflict.Errors!.ContainsKey(nameof(NewBudgetItem.TransactionTagId)));
            Assert.Contains(TagKept.ToString(), conflict.Message);

            // The winner's row is the only one, and the loser left nothing tracked behind.
            await using var after = NewContext();
            Assert.Equal(ItemDuplicateKept, (await after.BudgetItems.AsNoTracking().SingleAsync()).BudgetItemId);
        }
        finally
        {
            await DropAsync();
        }
    }

    private async Task InsertConflictingRowAsync()
    {
        await using var winner = NewContext();
        await winner.Database.ExecuteSqlRawAsync(
            "INSERT INTO `BudgetItems` (`BudgetItemId`, `BudgetId`, `CategoryType`, `PlannedAmount`, `TransactionTagId`) "
            + "VALUES ({0}, {1}, 0, 50, {2})",
            ItemDuplicateKept, BudgetId, TagKept);
    }

    /// <summary>
    /// AC 15. Deleting a tag a budget item plans for is refused with a 409 that says how many items
    /// reference it — not the generic <c>RowIsReferenced</c> conflict a raw FK violation produces, and
    /// not the silent success the FK-free InMemory tier would give.
    /// </summary>
    [SkippableFact]
    public async Task Deleting_a_tag_a_budget_item_plans_for_is_refused_and_says_how_many()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await RecreateAsync();

        try
        {
            await using var context = NewContext();
            await MigrationRunner.MigrateAsync(context, CancellationToken.None);

            context.TransactionTags.Add(new TransactionTag { TransactionTagId = TagKept, Name = "Groceries" });
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
                BudgetItemId = ItemValid,
                BudgetId = BudgetId,
                CategoryType = Odyssey.Context.BudgetCategoryType.Expense,
                PlannedAmount = 100m,
                TransactionTagId = TagKept,
            });
            await context.SaveChangesAsync();

            var conflict = await Assert.ThrowsAsync<DomainConflictException>(
                () => new TransactionTagService(context).Delete(TagKept));

            Assert.Contains("1 budget item", conflict.Message);
            // The message stays inside transactions.tags.delete's boundary: a count, never the budgets.
            Assert.DoesNotContain("2026", conflict.Message);

            Assert.Equal(1, await context.BudgetItems.CountAsync());
            Assert.Equal(1, await context.TransactionTags.CountAsync());
        }
        finally
        {
            await DropAsync();
        }
    }

    /// <summary>
    /// AC 23 and AC 24. <c>Down</c> across all four migrations restores every row, every label and
    /// every original tag name and recreates the old unique index — with no NOT NULL, duplicate-key or
    /// unique-index error at any step — and a <c>Down</c>/<c>Up</c> cycle leaves the archive at the same
    /// row count as a single <c>Up</c>.
    /// </summary>
    /// <remarks>
    /// This is the criterion a <c>Down</c> that omits reverting <c>TransactionTagId</c> to nullable, or
    /// omits dropping the new unique index, fails on: the rows the previous migration's <c>Down</c>
    /// re-inserts are by construction exactly the ones those two constraints reject. The tempting repair
    /// is to weaken the criterion; complete the <c>Down</c> instead.
    /// </remarks>
    [SkippableFact]
    public async Task Down_restores_everything_and_a_cycle_does_not_duplicate_the_archive()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await RecreateAsync();

        try
        {
            await using (var context = NewContext())
            {
                await MigrationSeam.MigrateToAsync(context, Baseline);
                await SeedBeforeStateAsync(context);
            }

            long archiveAfterFirstUp;
            await using (var context = NewContext())
            {
                await context.Database.MigrateAsync();
                archiveAfterFirstUp = await MigrationSeam.CountAsync(
                    context, "SELECT COUNT(*) FROM `_BudgetItemLabelArchive`");
            }

            await using (var context = NewContext())
            {
                await MigrationSeam.MigrateToAsync(context, Baseline);

                // Every row is back, including the two the Up deleted.
                Assert.Equal(4, await MigrationSeam.CountAsync(context, "SELECT COUNT(*) FROM `BudgetItems`"));

                var untagged = await MigrationSeam.RowAsync(context,
                    $"SELECT * FROM `BudgetItems` WHERE `BudgetItemId` = '{ItemUntagged}'");
                Assert.Equal("Old line", untagged!["Name"]);
                Assert.Null(untagged["TransactionTagId"]);

                var survivor = await MigrationSeam.RowAsync(context,
                    $"SELECT * FROM `BudgetItems` WHERE `BudgetItemId` = '{ItemValid}'");
                Assert.Equal("Weekly shop", survivor!["Name"]);
                Assert.Equal("Food and household", survivor["Description"]);

                // The original tag name is back, and so is the old identity index.
                Assert.Equal("groceries", await MigrationSeam.ScalarAsync(context,
                    $"SELECT `Name` FROM `TransactionTags` WHERE `TransactionTagId` = '{TagRenamed}'"));
                Assert.True(await IndexExistsAsync(context, "BudgetItems", "IX_BudgetItems_BudgetId_Name"));
                Assert.False(await IndexExistsAsync(context, "TransactionTags", "IX_TransactionTags_Name"));
                Assert.False(await IndexExistsAsync(context, "BudgetItems", "IX_BudgetItems_BudgetId_TransactionTagId"));
                Assert.Equal("YES", await NullabilityAsync(context, "BudgetItems", "TransactionTagId"));
            }

            await using (var context = NewContext())
            {
                await context.Database.MigrateAsync();

                Assert.Equal(archiveAfterFirstUp, await MigrationSeam.CountAsync(
                    context, "SELECT COUNT(*) FROM `_BudgetItemLabelArchive`"));
                Assert.Equal(1, await MigrationSeam.CountAsync(
                    context, "SELECT COUNT(*) FROM `_TransactionTagNameDisambiguation`"));
            }
        }
        finally
        {
            await DropAsync();
        }
    }

    // ── Seeding ───────────────────────────────────────────────────────────────

    /// <summary>
    /// The "before" state, at the baseline migration. <c>BudgetItems</c> is written with raw SQL: the
    /// entity no longer has <c>Name</c> or <c>Description</c>, so EF cannot express the very rows this
    /// set exists to migrate.
    /// </summary>
    private static async Task SeedBeforeStateAsync(OdysseyContext context)
    {
        context.TransactionTags.AddRange(
            new TransactionTag { TransactionTagId = TagKept, Name = "Groceries", Description = "Food" },
            // Differs from the above by CASE only — the duplicate migration 3 has to disambiguate.
            new TransactionTag { TransactionTagId = TagRenamed, Name = "groceries" },
            new TransactionTag { TransactionTagId = TagShared, Name = "Rent" });

        context.Budgets.Add(new Budget
        {
            BudgetId = BudgetId,
            Name = "2026",
            Description = "Annual",
            StartDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            EndDate = new DateTime(2026, 12, 31, 0, 0, 0, DateTimeKind.Utc),
            BaseCurrencyCode = "USD",
        });

        context.Accounts.Add(new Account
        {
            AccountId = AccountId,
            Name = "Checking",
            Description = "Daily",
            AccountType = Odyssey.Context.AccountType.CheckingAccount,
            CurrencyCode = "USD",
            Opened = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        });

        context.Transactions.Add(new Transaction
        {
            TransactionId = TransactionId,
            Description = "Shop",
            Amount = -50m,
            TimeStamp = new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc),
            AccountId = AccountId,
            CurrencyCode = "USD",
            Status = ContextTransactionStatus.Approved,
        });

        await context.SaveChangesAsync();

        // The reference that must survive the rename intact.
        context.TransactionTagLinks.Add(new TransactionTagLink
        {
            TransactionId = TransactionId,
            TransactionTagId = TagRenamed,
        });
        await context.SaveChangesAsync();

        // The four budget items. Names are distinct because the baseline schema still has the unique
        // (BudgetId, Name) index — so the duplicate PAIR has to duplicate on the TAG, which is the
        // constraint this set introduces.
        await InsertLegacyItemAsync(context, ItemUntagged, "Old line", null, null);
        await InsertLegacyItemAsync(context, ItemDuplicateKept, "Rent", "Roof", TagShared);
        await InsertLegacyItemAsync(context, ItemDuplicateDropped, "Rent again", null, TagShared);
        await InsertLegacyItemAsync(context, ItemValid, "Weekly shop", "Food and household", TagKept);
    }

    private static Task InsertLegacyItemAsync(
        OdysseyContext context, Guid id, string name, string? description, Guid? tagId) =>
        context.Database.ExecuteSqlRawAsync(
            "INSERT INTO `BudgetItems` (`BudgetItemId`, `BudgetId`, `Name`, `Description`, `CategoryType`, "
            + "`PlannedAmount`, `TransactionTagId`) VALUES ({0}, {1}, {2}, {3}, 0, 100, {4})",
            id, BudgetId, name, (object?)description ?? DBNull.Value, (object?)tagId ?? DBNull.Value);

    // ── Schema probes ─────────────────────────────────────────────────────────

    private static async Task<IReadOnlyCollection<string>> ColumnsAsync(OdysseyContext context, string table)
    {
        var names = new List<string>();
        await using var command = context.Database.GetDbConnection().CreateCommand();
        command.CommandText =
            "SELECT COLUMN_NAME FROM information_schema.COLUMNS "
            + $"WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = '{table}'";

        await context.Database.OpenConnectionAsync();
        try
        {
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                names.Add(reader.GetString(0));
            }
        }
        finally
        {
            await context.Database.CloseConnectionAsync();
        }

        return names;
    }

    private static async Task<string?> NullabilityAsync(OdysseyContext context, string table, string column) =>
        (string?)await MigrationSeam.ScalarAsync(context,
            "SELECT IS_NULLABLE FROM information_schema.COLUMNS "
            + $"WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = '{table}' AND COLUMN_NAME = '{column}'");

    private static async Task<bool> IndexExistsAsync(OdysseyContext context, string table, string index) =>
        await MigrationSeam.CountAsync(context,
            "SELECT COUNT(*) FROM information_schema.STATISTICS "
            + $"WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = '{table}' AND INDEX_NAME = '{index}'") > 0;

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

    private OdysseyContext NewContext(IInterceptor? interceptor = null)
    {
        var connection = fixture.ConnectionStringFor(Database);
        var builder = new DbContextOptionsBuilder<OdysseyContext>()
            .UseMySql(connection, ServerVersion.AutoDetect(connection));
        if (interceptor is not null)
        {
            builder = builder.AddInterceptors(interceptor);
        }

        return new OdysseyContext(builder.Options);
    }

    /// <summary>
    /// Runs one action on the last possible beat before a save commits — the window between a
    /// service's in-memory pre-check and the database's own verdict.
    /// </summary>
    private sealed class RaceInterceptor(Func<Task> onFirstSave) : SaveChangesInterceptor
    {
        public bool Fired { get; private set; }

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (!Fired)
            {
                Fired = true;
                await onFirstSave();
            }

            return result;
        }
    }
}
