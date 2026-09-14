using Mapster;
using Odyssey.Core;
using Odyssey.Core.Finance;
using Odyssey.Context;
using Odyssey.Dtos.Finance;
using Odyssey.Dtos;
using Xunit;
using DtoBudgetCategoryType = Odyssey.Dtos.Finance.BudgetCategoryType;

namespace Odyssey.Core.Tests;

/// <summary>
/// A budget item is a planned amount for a TAG (issue #75): every case here names one, because there
/// is no other way to identify a line.
/// </summary>
public class BudgetItemServiceTests
{
    // ── Fixtures ────────────────────────────────────────────────────────────────

    private static async Task<Budget> SeedBudgetAsync(OdysseyContext context, string name, string description = "")
    {
        var budget = new Budget
        {
            Name = name,
            Description = description,
            StartDate = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            EndDate = new DateTime(2025, 12, 31, 0, 0, 0, DateTimeKind.Utc),
            Archived = null,
        };
        context.Budgets.Add(budget);
        await context.SaveChangesAsync();
        return budget;
    }

    private static async Task<TransactionTag> SeedTagAsync(
        OdysseyContext context, string name, string? description = null, DateTime? archived = null)
    {
        var tag = new TransactionTag { Name = name, Description = description, Archived = archived };
        context.TransactionTags.Add(tag);
        await context.SaveChangesAsync();
        return tag;
    }

    private static NewBudgetItem Item(
        Guid budgetId, Guid tagId, DtoBudgetCategoryType category = DtoBudgetCategoryType.Expense,
        decimal amount = 100) => new()
    {
        BudgetId = budgetId,
        CategoryType = category,
        PlannedAmount = amount,
        TransactionTagId = tagId,
    };

    // ── Round trip ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateAndGetBudgetItemRoundTrips()
    {
        await using var context = TestContextFactory.Create();
        var budget = await SeedBudgetAsync(context, "Monthly Budget", "February");
        var tag = await SeedTagAsync(context, "Groceries", "Food budget");
        var service = new BudgetItemService(context);

        var created = await service.Create(Item(budget.BudgetId, tag.TransactionTagId, amount: 450));

        var fetched = await service.Get(created.BudgetItemId);

        Assert.NotNull(fetched);
        Assert.Equal(450, fetched!.PlannedAmount);
        Assert.Equal(tag.TransactionTagId, fetched.TransactionTagId);
        Assert.Equal("Groceries", fetched.Tag.Name);
        Assert.Equal("Food budget", fetched.Tag.Description);
    }

    /// <summary>
    /// AC 5. The <c>TransactionTag</c> navigation does not match <c>ExistingBudgetItem.Tag</c> by name,
    /// so without the explicit registration in <c>MapsterConfig</c> Mapster leaves it unset — and
    /// <c>required</c> does not catch it, because Mapster constructs through a runtime
    /// <c>Expression.MemberInit</c>. Removing that registration must fail this test.
    /// </summary>
    [Fact]
    public async Task AdaptPopulatesTagFromTheTransactionTagNavigation()
    {
        await using var context = TestContextFactory.Create();
        var budget = await SeedBudgetAsync(context, "Budget");
        var tag = await SeedTagAsync(context, "Travel", "Trips");
        var service = new BudgetItemService(context);
        var created = await service.Create(Item(budget.BudgetId, tag.TransactionTagId));

        var entity = new BudgetItem
        {
            BudgetItemId = created.BudgetItemId,
            BudgetId = budget.BudgetId,
            CategoryType = Odyssey.Context.BudgetCategoryType.Expense,
            PlannedAmount = 100,
            TransactionTagId = tag.TransactionTagId,
            TransactionTag = tag,
        };

        var adapted = entity.Adapt<ExistingBudgetItem>();

        Assert.NotNull(adapted.Tag);
        Assert.Equal(tag.TransactionTagId, adapted.Tag.TransactionTagId);
        Assert.Equal("Travel", adapted.Tag.Name);
        Assert.Equal(adapted.TransactionTagId, adapted.Tag.TransactionTagId);
    }

    [Fact]
    public async Task UpdateBudgetItemAndDeleteWork()
    {
        await using var context = TestContextFactory.Create();
        var budget = await SeedBudgetAsync(context, "Monthly Budget", "March");
        var dining = await SeedTagAsync(context, "Dining");
        var diningOut = await SeedTagAsync(context, "Dining Out");
        var service = new BudgetItemService(context);
        var created = await service.Create(Item(budget.BudgetId, dining.TransactionTagId, amount: 200));

        var updated = await service.Update(
            created.BudgetItemId, Item(budget.BudgetId, diningOut.TransactionTagId, amount: 250));

        Assert.NotNull(updated);
        Assert.Equal("Dining Out", updated!.Tag.Name);
        Assert.Equal(250, updated.PlannedAmount);

        await service.Delete(created.BudgetItemId);
        Assert.Equal(0, context.BudgetItems.Count());
    }

    [Fact]
    public async Task CreateBudgetItemPersistsDecimalPlannedAmount()
    {
        await using var context = TestContextFactory.Create();
        var budget = await SeedBudgetAsync(context, "Decimal Budget");
        var tag = await SeedTagAsync(context, "Decimal Item");
        var service = new BudgetItemService(context);

        var created = await service.Create(
            Item(budget.BudgetId, tag.TransactionTagId, amount: 99.123456m));

        var fetched = await service.Get(created.BudgetItemId);

        Assert.NotNull(fetched);
        Assert.Equal(99.123456m, fetched!.PlannedAmount);
    }

    // ── Write-path validation (issue #75 §9) ────────────────────────────────────

    /// <summary>
    /// AC 8. <c>[Required]</c> does not reject <see cref="Guid.Empty"/> on a non-nullable
    /// <see cref="Guid"/>, so the service has to.
    /// </summary>
    [Fact]
    public async Task CreateRejectsAnEmptyTagId()
    {
        await using var context = TestContextFactory.Create();
        var budget = await SeedBudgetAsync(context, "Budget");
        var service = new BudgetItemService(context);

        var rejected = await Assert.ThrowsAsync<DomainValidationException>(
            () => service.Create(Item(budget.BudgetId, Guid.Empty)));

        Assert.NotNull(rejected.Errors);
        Assert.True(rejected.Errors!.ContainsKey(nameof(NewBudgetItem.TransactionTagId)));
    }

    /// <summary>AC 9 — a 400, not a 500, on the FK-free InMemory tier.</summary>
    [Fact]
    public async Task CreateRejectsATagThatDoesNotExist()
    {
        await using var context = TestContextFactory.Create();
        var budget = await SeedBudgetAsync(context, "Budget");
        var service = new BudgetItemService(context);

        var rejected = await Assert.ThrowsAsync<DomainValidationException>(
            () => service.Create(Item(budget.BudgetId, Guid.NewGuid())));

        Assert.True(rejected.Errors!.ContainsKey(nameof(NewBudgetItem.TransactionTagId)));
        Assert.Equal(0, context.BudgetItems.Count());
    }

    [Fact]
    public async Task CreateRejectsAnArchivedTag()
    {
        await using var context = TestContextFactory.Create();
        var budget = await SeedBudgetAsync(context, "Budget");
        var archived = await SeedTagAsync(context, "Retired", archived: new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var service = new BudgetItemService(context);

        await Assert.ThrowsAsync<DomainValidationException>(
            () => service.Create(Item(budget.BudgetId, archived.TransactionTagId)));
    }

    /// <summary>
    /// AC 10. Keeping a link that already exists removes no capability; moving it into a budget that
    /// never planned for that tag is a NEW link, and a new link to an archived tag is refused.
    /// </summary>
    [Fact]
    public async Task UpdateKeepsAnArchivedTagInTheSameBudgetButRefusesToMoveIt()
    {
        await using var context = TestContextFactory.Create();
        var budget = await SeedBudgetAsync(context, "Budget");
        var otherBudget = await SeedBudgetAsync(context, "Other budget");
        var tag = await SeedTagAsync(context, "Sunset");
        var service = new BudgetItemService(context);
        var created = await service.Create(Item(budget.BudgetId, tag.TransactionTagId, amount: 100));

        // Archive the tag out from under the existing link.
        var tracked = await context.TransactionTags.FindAsync(tag.TransactionTagId);
        tracked!.Archived = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        await context.SaveChangesAsync();

        var kept = await service.Update(
            created.BudgetItemId, Item(budget.BudgetId, tag.TransactionTagId, amount: 175));

        Assert.NotNull(kept);
        Assert.Equal(175, kept!.PlannedAmount);

        await Assert.ThrowsAsync<DomainValidationException>(() => service.Update(
            created.BudgetItemId, Item(otherBudget.BudgetId, tag.TransactionTagId, amount: 175)));
    }

    /// <summary>AC 12 — keyed to the field, naming the GUID and never the tag's name.</summary>
    [Fact]
    public async Task CreateThrowsWhenTransactionTagAlreadyUsedInBudget()
    {
        await using var context = TestContextFactory.Create();
        var budget = await SeedBudgetAsync(context, "Monthly Budget", "May");
        var tag = await SeedTagAsync(context, "Groceries", "Food");
        var service = new BudgetItemService(context);
        await service.Create(Item(budget.BudgetId, tag.TransactionTagId, amount: 400));

        var conflict = await Assert.ThrowsAsync<DomainConflictException>(
            () => service.Create(Item(budget.BudgetId, tag.TransactionTagId, amount: 100)));

        Assert.True(conflict.Errors!.ContainsKey(nameof(NewBudgetItem.TransactionTagId)));
        Assert.Contains(tag.TransactionTagId.ToString(), conflict.Message);
        Assert.DoesNotContain("Groceries", conflict.Message);
    }

    [Fact]
    public async Task SameTagInADifferentBudgetIsAllowed()
    {
        await using var context = TestContextFactory.Create();
        var first = await SeedBudgetAsync(context, "2025");
        var second = await SeedBudgetAsync(context, "2026");
        var tag = await SeedTagAsync(context, "Groceries");
        var service = new BudgetItemService(context);

        await service.Create(Item(first.BudgetId, tag.TransactionTagId));
        var other = await service.Create(Item(second.BudgetId, tag.TransactionTagId));

        Assert.Equal(tag.TransactionTagId, other.TransactionTagId);
        Assert.Equal(2, context.BudgetItems.Count());
    }

    [Fact]
    public async Task UpdateThrowsWhenTransactionTagAlreadyUsedByDifferentItemInBudget()
    {
        await using var context = TestContextFactory.Create();
        var budget = await SeedBudgetAsync(context, "Monthly Budget", "June");
        var firstTag = await SeedTagAsync(context, "Tag One", "First");
        var secondTag = await SeedTagAsync(context, "Tag Two", "Second");
        var service = new BudgetItemService(context);
        await service.Create(Item(budget.BudgetId, firstTag.TransactionTagId, amount: 200));
        var secondItem = await service.Create(Item(budget.BudgetId, secondTag.TransactionTagId, amount: 300));

        await Assert.ThrowsAsync<DomainConflictException>(() => service.Update(
            secondItem.BudgetItemId, Item(budget.BudgetId, firstTag.TransactionTagId, amount: 300)));

        var unchanged = await service.Get(secondItem.BudgetItemId);
        Assert.NotNull(unchanged);
        Assert.Equal(secondTag.TransactionTagId, unchanged!.TransactionTagId);
    }

    // ── ListAsync (issue #277), now reading through the joined tag ───────────────

    [Fact]
    public async Task ListAsync_AppliesOffsetAndLimit()
    {
        await using var context = TestContextFactory.Create();
        var budget = await SeedBudgetAsync(context, "Monthly Budget", "April");
        var rent = await SeedTagAsync(context, "Rent", "Housing");
        var utilities = await SeedTagAsync(context, "Utilities", "Bills");
        var service = new BudgetItemService(context);
        await service.Create(Item(budget.BudgetId, rent.TransactionTagId, amount: 1000));
        await service.Create(Item(budget.BudgetId, utilities.TransactionTagId, amount: 150));

        var results = (await service.ListAsync(new BudgetItemsQueryParams { Offset = 1, Limit = 1 })).Items;

        Assert.Equal("Utilities", Assert.Single(results).Tag.Name);
    }

    /// <summary>AC 16 — search matches the JOINED tag's name or description.</summary>
    [Fact]
    public async Task ListAsync_Search_MatchesTheTagNameOrDescription()
    {
        await using var context = TestContextFactory.Create();
        var budget = await SeedBudgetAsync(context, "Budget");
        var groceries = await SeedTagAsync(context, "Groceries", "Weekly food");
        var rent = await SeedTagAsync(context, "Rent", "Housing");
        var fuel = await SeedTagAsync(context, "Fuel", "Car petrol");
        var service = new BudgetItemService(context);
        await service.Create(Item(budget.BudgetId, groceries.TransactionTagId));
        await service.Create(Item(budget.BudgetId, rent.TransactionTagId));
        await service.Create(Item(budget.BudgetId, fuel.TransactionTagId));

        // Matches on the tag's description only.
        var byDescription = (await service.ListAsync(new BudgetItemsQueryParams { Search = "food" })).Items;
        Assert.Equal("Groceries", Assert.Single(byDescription).Tag.Name);

        // Matches on the tag's name only.
        var byName = (await service.ListAsync(new BudgetItemsQueryParams { Search = "rent" })).Items;
        Assert.Equal("Rent", Assert.Single(byName).Tag.Name);
    }

    [Fact]
    public async Task ListAsync_FiltersByBudgetId()
    {
        await using var context = TestContextFactory.Create();
        var first = await SeedBudgetAsync(context, "First");
        var second = await SeedBudgetAsync(context, "Second");
        var a1 = await SeedTagAsync(context, "A1");
        var a2 = await SeedTagAsync(context, "A2");
        var b1 = await SeedTagAsync(context, "B1");
        var service = new BudgetItemService(context);
        await service.Create(Item(first.BudgetId, a1.TransactionTagId));
        await service.Create(Item(first.BudgetId, a2.TransactionTagId));
        await service.Create(Item(second.BudgetId, b1.TransactionTagId));

        var result = await service.ListAsync(new BudgetItemsQueryParams { BudgetId = first.BudgetId });

        Assert.Equal(2, result.TotalCount);
        Assert.All(result.Items, i => Assert.Equal(first.BudgetId, i.BudgetId));
    }

    [Fact]
    public async Task ListAsync_FiltersByCategories()
    {
        await using var context = TestContextFactory.Create();
        var budget = await SeedBudgetAsync(context, "Budget");
        var salary = await SeedTagAsync(context, "Salary");
        var groceries = await SeedTagAsync(context, "Groceries");
        var rent = await SeedTagAsync(context, "Rent");
        var service = new BudgetItemService(context);
        await service.Create(Item(budget.BudgetId, salary.TransactionTagId, DtoBudgetCategoryType.Income));
        await service.Create(Item(budget.BudgetId, groceries.TransactionTagId));
        await service.Create(Item(budget.BudgetId, rent.TransactionTagId));

        var incomeOnly = (await service.ListAsync(new BudgetItemsQueryParams
        {
            Categories = [DtoBudgetCategoryType.Income],
        })).Items;

        Assert.Equal("Salary", Assert.Single(incomeOnly).Tag.Name);
    }

    [Fact]
    public async Task ListAsync_SortsByPlannedAmount_AscAndDesc()
    {
        await using var context = TestContextFactory.Create();
        var budget = await SeedBudgetAsync(context, "Budget");
        var mid = await SeedTagAsync(context, "Mid");
        var low = await SeedTagAsync(context, "Low");
        var high = await SeedTagAsync(context, "High");
        var service = new BudgetItemService(context);
        await service.Create(Item(budget.BudgetId, mid.TransactionTagId, amount: 200));
        await service.Create(Item(budget.BudgetId, low.TransactionTagId, amount: 100));
        await service.Create(Item(budget.BudgetId, high.TransactionTagId, amount: 300));

        var asc = (await service.ListAsync(new BudgetItemsQueryParams
        {
            SortBy = BudgetItemSortBy.PlannedAmount, SortDir = SortDirection.Asc,
        })).Items;
        Assert.Equal([100m, 200m, 300m], asc.Select(i => i.PlannedAmount));

        var desc = (await service.ListAsync(new BudgetItemsQueryParams
        {
            SortBy = BudgetItemSortBy.PlannedAmount, SortDir = SortDirection.Desc,
        })).Items;
        Assert.Equal([300m, 200m, 100m], desc.Select(i => i.PlannedAmount));
    }

    /// <summary>
    /// AC 17. The query-string spelling stays <c>Name</c> — it is a wire contract — but it now means
    /// the joined tag's name.
    /// </summary>
    [Fact]
    public async Task ListAsync_DefaultSort_IsTagNameAscending()
    {
        await using var context = TestContextFactory.Create();
        var budget = await SeedBudgetAsync(context, "Budget");
        var charlie = await SeedTagAsync(context, "Charlie");
        var alpha = await SeedTagAsync(context, "Alpha");
        var bravo = await SeedTagAsync(context, "Bravo");
        var service = new BudgetItemService(context);
        await service.Create(Item(budget.BudgetId, charlie.TransactionTagId));
        await service.Create(Item(budget.BudgetId, alpha.TransactionTagId));
        await service.Create(Item(budget.BudgetId, bravo.TransactionTagId));

        var items = (await service.ListAsync(new BudgetItemsQueryParams())).Items;

        Assert.Equal(["Alpha", "Bravo", "Charlie"], items.Select(i => i.Tag.Name));
    }

    [Fact]
    public async Task ListAsync_EveryItemCarriesItsTag()
    {
        await using var context = TestContextFactory.Create();
        var budget = await SeedBudgetAsync(context, "Budget");
        var service = new BudgetItemService(context);
        for (var i = 0; i < 3; i++)
        {
            var tag = await SeedTagAsync(context, $"Tag {i}", $"Description {i}");
            await service.Create(Item(budget.BudgetId, tag.TransactionTagId));
        }

        var items = (await service.ListAsync(new BudgetItemsQueryParams())).Items;

        Assert.Equal(3, items.Count);
        Assert.All(items, item =>
        {
            Assert.NotNull(item.Tag);
            Assert.Equal(item.TransactionTagId, item.Tag.TransactionTagId);
            Assert.False(string.IsNullOrEmpty(item.Tag.Name));
        });
    }

    [Fact]
    public async Task ListAsync_TotalCount_ReflectsFilteredSet_NotPageWindow()
    {
        await using var context = TestContextFactory.Create();
        var budget = await SeedBudgetAsync(context, "Budget");
        var service = new BudgetItemService(context);
        for (var i = 0; i < 5; i++)
        {
            var tag = await SeedTagAsync(context, $"Item {i}");
            await service.Create(Item(budget.BudgetId, tag.TransactionTagId));
        }

        var page = await service.ListAsync(new BudgetItemsQueryParams { Offset = 0, Limit = 2 });

        Assert.Equal(5, page.TotalCount);
        Assert.Equal(2, page.Items.Count);
    }
}
