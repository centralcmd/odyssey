using Odyssey.Core;
using Odyssey.Core.Finance;
using Odyssey.Context;
using Odyssey.Dtos.Finance;
using Odyssey.Dtos;
using Xunit;
using DtoBudgetCategoryType = Odyssey.Dtos.Finance.BudgetCategoryType;

namespace Odyssey.Core.Tests;

public class BudgetItemServiceTests
{
    [Fact]
    public async Task CreateAndGetBudgetItemRoundTrips()
    {
        await using var context = TestContextFactory.Create();
        var budget = new Budget
        {
            Name = "Monthly Budget",
            Description = "February",
            StartDate = new DateTime(2025, 2, 1, 0, 0, 0, DateTimeKind.Utc),
            EndDate = new DateTime(2025, 2, 28, 0, 0, 0, DateTimeKind.Utc),
            Archived = null,
        };
        context.Budgets.Add(budget);
        await context.SaveChangesAsync();

        var service = new BudgetItemService(context);

        var created = await service.Create(new NewBudgetItem
        {
            BudgetId = budget.BudgetId,
            Name = "Groceries",
            Description = "Food budget",
            CategoryType = DtoBudgetCategoryType.Expense,
            PlannedAmount = 450,
        });

        var fetched = await service.Get(created.BudgetItemId);

        Assert.NotNull(fetched);
        Assert.Equal("Groceries", fetched!.Name);
        Assert.Equal(450, fetched.PlannedAmount);
    }

    [Fact]
    public async Task UpdateBudgetItemAndDeleteWork()
    {
        await using var context = TestContextFactory.Create();
        var budget = new Budget
        {
            Name = "Monthly Budget",
            Description = "March",
            StartDate = new DateTime(2025, 3, 1, 0, 0, 0, DateTimeKind.Utc),
            EndDate = new DateTime(2025, 3, 31, 0, 0, 0, DateTimeKind.Utc),
            Archived = null,
        };
        context.Budgets.Add(budget);
        await context.SaveChangesAsync();

        var service = new BudgetItemService(context);
        var created = await service.Create(new NewBudgetItem
        {
            BudgetId = budget.BudgetId,
            Name = "Dining",
            Description = "Eating out",
            CategoryType = DtoBudgetCategoryType.Expense,
            PlannedAmount = 200,
        });

        var updated = await service.Update(created.BudgetItemId, new NewBudgetItem
        {
            BudgetId = budget.BudgetId,
            Name = "Dining Out",
            Description = "Restaurants",
            CategoryType = DtoBudgetCategoryType.Expense,
            PlannedAmount = 250,
        });

        Assert.NotNull(updated);
        Assert.Equal("Dining Out", updated!.Name);
        Assert.Equal(250, updated.PlannedAmount);

        await service.Delete(created.BudgetItemId);
        Assert.Equal(0, context.BudgetItems.Count());
    }

    [Fact]
    public async Task ListAsync_AppliesOffsetAndLimit()
    {
        await using var context = TestContextFactory.Create();
        var budget = new Budget
        {
            Name = "Monthly Budget",
            Description = "April",
            StartDate = new DateTime(2025, 4, 1, 0, 0, 0, DateTimeKind.Utc),
            EndDate = new DateTime(2025, 4, 30, 0, 0, 0, DateTimeKind.Utc),
            Archived = null,
        };
        context.Budgets.Add(budget);
        await context.SaveChangesAsync();

        var service = new BudgetItemService(context);
        await service.Create(new NewBudgetItem
        {
            BudgetId = budget.BudgetId,
            Name = "Rent",
            Description = "Housing",
            CategoryType = DtoBudgetCategoryType.Expense,
            PlannedAmount = 1000,
        });
        await service.Create(new NewBudgetItem
        {
            BudgetId = budget.BudgetId,
            Name = "Utilities",
            Description = "Bills",
            CategoryType = DtoBudgetCategoryType.Expense,
            PlannedAmount = 150,
        });

        var results = (await service.ListAsync(new BudgetItemsQueryParams { Offset = 1, Limit = 1 })).Items;

        Assert.Single(results);
        Assert.Equal("Utilities", results[0].Name);
    }

    [Fact]
    public async Task CreateThrowsWhenTransactionTagAlreadyUsedInBudget()
    {
        await using var context = TestContextFactory.Create();
        var budget = new Budget
        {
            Name = "Monthly Budget",
            Description = "May",
            StartDate = new DateTime(2025, 5, 1, 0, 0, 0, DateTimeKind.Utc),
            EndDate = new DateTime(2025, 5, 31, 0, 0, 0, DateTimeKind.Utc),
            Archived = null,
        };
        var tag = new TransactionTag
        {
            Name = "Groceries",
            Description = "Food",
            Archived = null,
        };
        context.Budgets.Add(budget);
        context.TransactionTags.Add(tag);
        await context.SaveChangesAsync();

        var service = new BudgetItemService(context);
        await service.Create(new NewBudgetItem
        {
            BudgetId = budget.BudgetId,
            Name = "Food",
            Description = "Food budget",
            CategoryType = DtoBudgetCategoryType.Expense,
            PlannedAmount = 400,
            TransactionTagId = tag.TransactionTagId,
        });

        await Assert.ThrowsAsync<DomainConflictException>(() => service.Create(new NewBudgetItem
        {
            BudgetId = budget.BudgetId,
            Name = "More Food",
            Description = "Duplicate tag",
            CategoryType = DtoBudgetCategoryType.Expense,
            PlannedAmount = 100,
            TransactionTagId = tag.TransactionTagId,
        }));
    }

    [Fact]
    public async Task UpdateThrowsWhenTransactionTagAlreadyUsedByDifferentItemInBudget()
    {
        await using var context = TestContextFactory.Create();
        var budget = new Budget
        {
            Name = "Monthly Budget",
            Description = "June",
            StartDate = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            EndDate = new DateTime(2025, 6, 30, 0, 0, 0, DateTimeKind.Utc),
            Archived = null,
        };
        var firstTag = new TransactionTag
        {
            Name = "Tag One",
            Description = "First",
            Archived = null,
        };
        var secondTag = new TransactionTag
        {
            Name = "Tag Two",
            Description = "Second",
            Archived = null,
        };
        context.Budgets.Add(budget);
        context.TransactionTags.AddRange(firstTag, secondTag);
        await context.SaveChangesAsync();

        var service = new BudgetItemService(context);
        await service.Create(new NewBudgetItem
        {
            BudgetId = budget.BudgetId,
            Name = "Item One",
            Description = "One",
            CategoryType = DtoBudgetCategoryType.Expense,
            PlannedAmount = 200,
            TransactionTagId = firstTag.TransactionTagId,
        });
        var secondItem = await service.Create(new NewBudgetItem
        {
            BudgetId = budget.BudgetId,
            Name = "Item Two",
            Description = "Two",
            CategoryType = DtoBudgetCategoryType.Expense,
            PlannedAmount = 300,
            TransactionTagId = secondTag.TransactionTagId,
        });

        await Assert.ThrowsAsync<DomainConflictException>(() => service.Update(secondItem.BudgetItemId, new NewBudgetItem
        {
            BudgetId = budget.BudgetId,
            Name = "Item Two",
            Description = "Two",
            CategoryType = DtoBudgetCategoryType.Expense,
            PlannedAmount = 300,
            TransactionTagId = firstTag.TransactionTagId,
        }));

        var unchanged = await service.Get(secondItem.BudgetItemId);
        Assert.NotNull(unchanged);
        Assert.Equal(secondTag.TransactionTagId, unchanged!.TransactionTagId);
    }


    [Fact]
    public async Task CreateBudgetItemPersistsDecimalPlannedAmount()
    {
        await using var context = TestContextFactory.Create();
        var budget = new Budget
        {
            Name = "Decimal Budget",
            Description = "Decimal",
            StartDate = new DateTime(2025, 2, 1, 0, 0, 0, DateTimeKind.Utc),
            EndDate = new DateTime(2025, 2, 28, 0, 0, 0, DateTimeKind.Utc),
            Archived = null,
        };
        context.Budgets.Add(budget);
        await context.SaveChangesAsync();

        var service = new BudgetItemService(context);
        var created = await service.Create(new NewBudgetItem
        {
            BudgetId = budget.BudgetId,
            Name = "Decimal Item",
            Description = "Decimal",
            CategoryType = DtoBudgetCategoryType.Expense,
            PlannedAmount = 99.123456m,
        });

        var fetched = await service.Get(created.BudgetItemId);

        Assert.NotNull(fetched);
        Assert.Equal(99.123456m, fetched!.PlannedAmount);
    }

    // ── ListAsync (issue #277): name/description search + budget/category filters + allowlisted sort ──

    private static async Task<Budget> SeedBudgetAsync(OdysseyContext context, string name)
    {
        var budget = new Budget
        {
            Name = name,
            Description = "",
            StartDate = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            EndDate = new DateTime(2025, 12, 31, 0, 0, 0, DateTimeKind.Utc),
            Archived = null,
        };
        context.Budgets.Add(budget);
        await context.SaveChangesAsync();
        return budget;
    }

    private static NewBudgetItem Item(
        Guid budgetId, string name, DtoBudgetCategoryType category = DtoBudgetCategoryType.Expense,
        decimal amount = 100, string? description = null) => new()
    {
        BudgetId = budgetId,
        Name = name,
        Description = description,
        CategoryType = category,
        PlannedAmount = amount,
    };

    [Fact]
    public async Task ListAsync_Search_MatchesNameOrDescription()
    {
        await using var context = TestContextFactory.Create();
        var budget = await SeedBudgetAsync(context, "Budget");
        var service = new BudgetItemService(context);
        await service.Create(Item(budget.BudgetId, "Groceries", description: "Weekly food"));
        await service.Create(Item(budget.BudgetId, "Rent", description: "Housing"));
        await service.Create(Item(budget.BudgetId, "Fuel", description: "Car petrol"));

        // Matches on the description only.
        var byDescription = (await service.ListAsync(new BudgetItemsQueryParams { Search = "food" })).Items;
        Assert.Equal("Groceries", Assert.Single(byDescription).Name);

        // Matches on the name only.
        var byName = (await service.ListAsync(new BudgetItemsQueryParams { Search = "rent" })).Items;
        Assert.Equal("Rent", Assert.Single(byName).Name);
    }

    [Fact]
    public async Task ListAsync_FiltersByBudgetId()
    {
        await using var context = TestContextFactory.Create();
        var first = await SeedBudgetAsync(context, "First");
        var second = await SeedBudgetAsync(context, "Second");
        var service = new BudgetItemService(context);
        await service.Create(Item(first.BudgetId, "A1"));
        await service.Create(Item(first.BudgetId, "A2"));
        await service.Create(Item(second.BudgetId, "B1"));

        var result = await service.ListAsync(new BudgetItemsQueryParams { BudgetId = first.BudgetId });

        Assert.Equal(2, result.TotalCount);
        Assert.All(result.Items, i => Assert.Equal(first.BudgetId, i.BudgetId));
    }

    [Fact]
    public async Task ListAsync_FiltersByCategories()
    {
        await using var context = TestContextFactory.Create();
        var budget = await SeedBudgetAsync(context, "Budget");
        var service = new BudgetItemService(context);
        await service.Create(Item(budget.BudgetId, "Salary", DtoBudgetCategoryType.Income));
        await service.Create(Item(budget.BudgetId, "Groceries", DtoBudgetCategoryType.Expense));
        await service.Create(Item(budget.BudgetId, "Rent", DtoBudgetCategoryType.Expense));

        var incomeOnly = (await service.ListAsync(new BudgetItemsQueryParams
        {
            Categories = [DtoBudgetCategoryType.Income],
        })).Items;

        Assert.Equal("Salary", Assert.Single(incomeOnly).Name);
    }

    [Fact]
    public async Task ListAsync_SortsByPlannedAmount_AscAndDesc()
    {
        await using var context = TestContextFactory.Create();
        var budget = await SeedBudgetAsync(context, "Budget");
        var service = new BudgetItemService(context);
        await service.Create(Item(budget.BudgetId, "Mid", amount: 200));
        await service.Create(Item(budget.BudgetId, "Low", amount: 100));
        await service.Create(Item(budget.BudgetId, "High", amount: 300));

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

    [Fact]
    public async Task ListAsync_DefaultSort_IsNameAscending()
    {
        await using var context = TestContextFactory.Create();
        var budget = await SeedBudgetAsync(context, "Budget");
        var service = new BudgetItemService(context);
        await service.Create(Item(budget.BudgetId, "Charlie"));
        await service.Create(Item(budget.BudgetId, "Alpha"));
        await service.Create(Item(budget.BudgetId, "Bravo"));

        var items = (await service.ListAsync(new BudgetItemsQueryParams())).Items;

        Assert.Equal(["Alpha", "Bravo", "Charlie"], items.Select(i => i.Name));
    }

    [Fact]
    public async Task ListAsync_TotalCount_ReflectsFilteredSet_NotPageWindow()
    {
        await using var context = TestContextFactory.Create();
        var budget = await SeedBudgetAsync(context, "Budget");
        var service = new BudgetItemService(context);
        for (var i = 0; i < 5; i++)
        {
            await service.Create(Item(budget.BudgetId, $"Item {i}"));
        }

        var page = await service.ListAsync(new BudgetItemsQueryParams { Offset = 0, Limit = 2 });

        Assert.Equal(5, page.TotalCount);
        Assert.Equal(2, page.Items.Count);
    }
}
