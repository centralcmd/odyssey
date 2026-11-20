using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Odyssey.Context;
using Odyssey.Core;
using Odyssey.Core.Finance;
using Odyssey.Dtos.Finance;
using Xunit;

namespace Odyssey.IntegrationTests;

/// <summary>
/// The two unique indexes and the delete refusal issue #75 added for budget items, against real MariaDB.
///
/// <para>
/// None of this is observable on the fast tiers by construction: the EF InMemory provider has no
/// schema, no migrations, no foreign keys and enforces no unique index across a relationship of this
/// shape. A green <c>Odyssey.Api.Tests</c> says the SERVICE guards hold; only this file says the
/// database does.
/// </para>
/// </summary>
[Collection(MariaDbSharedDatabaseCollection.Name)]
public class BudgetItemTagConstraintTests(MariaDbFixture fixture)
{
    private const string Database = "odyssey_budget_item_tag";

    private static readonly Guid TagKept = new("11111111-1111-1111-1111-111111111111");
    private static readonly Guid TagRenamed = new("22222222-2222-2222-2222-222222222222");
    private static readonly Guid BudgetId = new("aaaaaaaa-0000-0000-0000-000000000000");
    private static readonly Guid ItemDuplicateKept = new("b1111111-1111-1111-1111-111111111111");
    private static readonly Guid ItemDuplicateDropped = new("b2222222-2222-2222-2222-222222222222");
    private static readonly Guid ItemValid = new("b3333333-3333-3333-3333-333333333333");

    /// <summary>
    /// AC 13 and AC 20. The two unique indexes are real: a direct insert bypassing every service guard
    /// is rejected by the database.
    /// </summary>
    [SkippableFact]
    public async Task Both_unique_indexes_reject_a_direct_duplicate_insert()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        try
        {
            await fixture.CreateMigratedDatabaseAsync(Database);
            await using var context = NewContext();

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
        try
        {
            await fixture.CreateMigratedDatabaseAsync(Database);
            await using (var setup = NewContext())
            {

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
        try
        {
            await fixture.CreateMigratedDatabaseAsync(Database);
            await using var context = NewContext();

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

    // ── Database lifecycle ────────────────────────────────────────────────────

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
