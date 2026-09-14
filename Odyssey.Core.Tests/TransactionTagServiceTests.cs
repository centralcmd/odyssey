using Odyssey.Context;
using Odyssey.Core;
using Odyssey.Dtos.Finance;
using Odyssey.Dtos;
using Xunit;
using Odyssey.Core.Finance;

namespace Odyssey.Core.Tests;

public class TransactionTagServiceTests
{
    [Fact]
    public async Task CreateAndGetTransactionTagRoundTrips_AsActive()
    {
        await using var context = TestContextFactory.Create();
        var service = new TransactionTagService(context);

        var created = await service.Create(new NewTransactionTag
        {
            Name = "Groceries",
            Description = "Food",
            Archived = true,
        });

        var fetched = await service.Get(created.TransactionTagId);
        Assert.NotNull(fetched);
        Assert.Null(fetched!.Archived);
    }

    [Fact]
    public async Task UpdateTransactionTag_ArchiveTransitions_AreCorrectAndIdempotent()
    {
        await using var context = TestContextFactory.Create();
        var service = new TransactionTagService(context);

        var created = await service.Create(new NewTransactionTag
        {
            Name = "Utilities",
            Description = "Bills",
            Archived = false,
        });

        var activeToActive = await service.Update(created.TransactionTagId, new NewTransactionTag
        {
            Name = created.Name,
            Description = created.Description,
            Archived = false,
        });
        Assert.Null(activeToActive!.Archived);

        var activeToArchived = await service.Update(created.TransactionTagId, new NewTransactionTag
        {
            Name = created.Name,
            Description = created.Description,
            Archived = true,
        });
        Assert.NotNull(activeToArchived!.Archived);
        var firstArchivedAt = activeToArchived.Archived;

        var archivedToArchived = await service.Update(created.TransactionTagId, new NewTransactionTag
        {
            Name = created.Name,
            Description = created.Description,
            Archived = true,
        });
        Assert.Equal(firstArchivedAt, archivedToArchived!.Archived);

        var archivedToActive = await service.Update(created.TransactionTagId, new NewTransactionTag
        {
            Name = created.Name,
            Description = created.Description,
            Archived = false,
        });
        Assert.Null(archivedToActive!.Archived);
    }

    [Fact]
    public async Task ListAsync_SortsByDescription()
    {
        await using var context = TestContextFactory.Create();
        var service = new TransactionTagService(context);

        await service.Create(new NewTransactionTag { Name = "Alpha", Description = "Zeta desc", Archived = false });
        await service.Create(new NewTransactionTag { Name = "Beta", Description = "Alpha desc", Archived = false });

        var result = await service.ListAsync(
            new TransactionTagsQueryParams { SortBy = TransactionTagSortBy.Description, SortDir = SortDirection.Asc });

        Assert.Equal("Alpha desc", result.Items[0].Description);
        Assert.Equal("Zeta desc", result.Items[1].Description);
    }

    [Fact]
    public async Task ListAsync_SortsByStatus_ActiveBeforeArchived()
    {
        await using var context = TestContextFactory.Create();
        var service = new TransactionTagService(context);

        var toArchive = await service.Create(new NewTransactionTag { Name = "Was Active", Description = "a", Archived = false });
        await service.Create(new NewTransactionTag { Name = "Still Active", Description = "b", Archived = false });
        await service.Update(toArchive.TransactionTagId, new NewTransactionTag
        {
            Name = toArchive.Name,
            Description = toArchive.Description,
            Archived = true,
        });

        var result = await service.ListAsync(
            new TransactionTagsQueryParams { SortBy = TransactionTagSortBy.Status, SortDir = SortDirection.Asc });

        Assert.Null(result.Items[0].Archived);      // active sorts first
        Assert.NotNull(result.Items[1].Archived);   // archived sinks to the bottom
    }

    // ── Name uniqueness (issue #75 §5.11) ───────────────────────────────────────
    // A tag name is an identity now: it names every budget item planning for the tag, so two
    // same-named tags would render as two indistinguishable budget rows. These run on the EF InMemory
    // tier, which has NO collation and NO unique index across this shape — so they pin the SERVICE
    // guard specifically, and prove the rule does not depend on the database (AC 18).

    [Fact]
    public async Task Create_RejectsANameDifferingOnlyInCase()
    {
        await using var context = TestContextFactory.Create();
        var service = new TransactionTagService(context);
        await service.Create(new NewTransactionTag { Name = "Groceries", Description = "Food", Archived = false });

        var conflict = await Assert.ThrowsAsync<DomainConflictException>(
            () => service.Create(new NewTransactionTag { Name = "GROCERIES", Description = null, Archived = false }));

        // Keyed to the field so the tag dialogs render it at the name input rather than as a toast.
        Assert.NotNull(conflict.Errors);
        Assert.True(conflict.Errors!.ContainsKey(nameof(NewTransactionTag.Name)));
        Assert.Equal(1, context.TransactionTags.Count());
    }

    /// <summary>
    /// AC 19. The unique index covers archived rows — MariaDB has no filtered indexes — so an archived
    /// clash is a real one, and it has no inline remedy: restoring or renaming it is a page action.
    /// The message has to say so.
    /// </summary>
    [Fact]
    public async Task Create_RejectsANameHeldByAnArchivedTag_AndSaysItIsArchived()
    {
        await using var context = TestContextFactory.Create();
        context.TransactionTags.Add(new TransactionTag
        {
            Name = "Retired",
            Archived = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        });
        await context.SaveChangesAsync();
        var service = new TransactionTagService(context);

        var conflict = await Assert.ThrowsAsync<DomainConflictException>(
            () => service.Create(new NewTransactionTag { Name = "retired", Description = null, Archived = false }));

        Assert.Contains("archived", conflict.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(conflict.Errors!.ContainsKey(nameof(NewTransactionTag.Name)));
    }

    [Fact]
    public async Task Update_RejectsAnotherTagsName_ButKeepsItsOwn()
    {
        await using var context = TestContextFactory.Create();
        var service = new TransactionTagService(context);
        var groceries = await service.Create(new NewTransactionTag { Name = "Groceries", Description = null, Archived = false });
        var rent = await service.Create(new NewTransactionTag { Name = "Rent", Description = null, Archived = false });

        await Assert.ThrowsAsync<DomainConflictException>(() => service.Update(
            rent.TransactionTagId,
            new NewTransactionTag { Name = "groceries", Description = null, Archived = false }));

        // Re-saving a tag under its OWN name is not a clash — the guard exempts the row being updated.
        var resaved = await service.Update(
            groceries.TransactionTagId,
            new NewTransactionTag { Name = "Groceries", Description = "Food and household", Archived = false });

        Assert.NotNull(resaved);
        Assert.Equal("Food and household", resaved!.Description);
    }

    // ── Delete guard (issue #75 §7.9) ──────────────────────────────────────────

    /// <summary>
    /// AC 15's InMemory clause. The RESTRICT foreign key says the same thing on MariaDB, but this tier
    /// enforces no foreign keys at all — so without the service pre-check the delete would simply
    /// succeed here and take the budget item's identity with it.
    /// </summary>
    [Fact]
    public async Task Delete_IsRefusedWhileABudgetItemPlansForTheTag_AndNamesTheCount()
    {
        await using var context = TestContextFactory.Create();
        var service = new TransactionTagService(context);
        var tag = await service.Create(new NewTransactionTag { Name = "Groceries", Description = null, Archived = false });

        var budget = new Budget
        {
            Name = "2026",
            Description = "Annual",
            StartDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            EndDate = new DateTime(2026, 12, 31, 0, 0, 0, DateTimeKind.Utc),
            Archived = null,
        };
        context.Budgets.Add(budget);
        await context.SaveChangesAsync();

        context.BudgetItems.Add(new BudgetItem
        {
            BudgetId = budget.BudgetId,
            CategoryType = Odyssey.Context.BudgetCategoryType.Expense,
            PlannedAmount = 100m,
            TransactionTagId = tag.TransactionTagId,
        });
        await context.SaveChangesAsync();

        var conflict = await Assert.ThrowsAsync<DomainConflictException>(
            () => service.Delete(tag.TransactionTagId));

        Assert.Contains("1 budget item", conflict.Message);
        // A COUNT, never the budgets: naming them would reach past transactions.tags.delete's boundary.
        Assert.DoesNotContain("2026", conflict.Message);
        Assert.Equal(1, context.TransactionTags.Count());
    }

    [Fact]
    public async Task Delete_SucceedsWhenNoBudgetItemPlansForTheTag()
    {
        await using var context = TestContextFactory.Create();
        var service = new TransactionTagService(context);
        var tag = await service.Create(new NewTransactionTag { Name = "Unused", Description = null, Archived = false });

        await service.Delete(tag.TransactionTagId);

        Assert.Equal(0, context.TransactionTags.Count());
    }
}
