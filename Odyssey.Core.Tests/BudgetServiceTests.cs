using Odyssey.Core;
using Odyssey.Context;
using Odyssey.Dtos.Finance;
using Xunit;
using ContextBudgetCategoryType = Odyssey.Context.BudgetCategoryType;
using ContextAccountType = Odyssey.Context.AccountType;
using Odyssey.Core.Finance;

namespace Odyssey.Core.Tests;

public class BudgetServiceTests
{
    [Fact]
    public async Task CreateAndGetBudget_RoundTrips()
    {
        await using var context = TestContextFactory.Create();
        var service = new BudgetService(context, TestContextFactory.EmptyContactLookup());

        var created = await service.Create(new NewBudget
        {
            Name = "Monthly",
            Description = "January",
            StartDate = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            EndDate = new DateTime(2025, 1, 31, 0, 0, 0, DateTimeKind.Utc),
            Archived = false,
            BaseCurrencyCode = "USD",
        });

        var fetched = await service.Get(created.BudgetId);

        Assert.NotNull(fetched);
        Assert.Equal("Monthly", fetched!.Name);
        Assert.Equal("USD", fetched.BaseCurrencyCode);
        Assert.Null(fetched.Archived);
    }

    [Fact]
    public async Task SearchFor_AppliesOffsetAndLimit()
    {
        await using var context = TestContextFactory.Create();
        var service = new BudgetService(context, TestContextFactory.EmptyContactLookup());

        for (var i = 1; i <= 3; i++)
        {
            await service.Create(new NewBudget
            {
                Name = $"Budget {i}",
                StartDate = new DateTime(2025, i, 1, 0, 0, 0, DateTimeKind.Utc),
                EndDate = new DateTime(2025, i, 28, 0, 0, 0, DateTimeKind.Utc),
                Archived = false,
                BaseCurrencyCode = "USD",
            });
        }

        var results = (await service.ListAsync(new BudgetsQueryParams { Offset = 1, Limit = 1 })).Items;

        Assert.Single(results);
    }

    [Fact]
    public async Task Update_ModifiesNameAndTogglesArchive()
    {
        await using var context = TestContextFactory.Create();
        var service = new BudgetService(context, TestContextFactory.EmptyContactLookup());

        var created = await service.Create(new NewBudget
        {
            Name = "Old Name",
            StartDate = new DateTime(2025, 2, 1, 0, 0, 0, DateTimeKind.Utc),
            EndDate = new DateTime(2025, 2, 28, 0, 0, 0, DateTimeKind.Utc),
            Archived = false,
            BaseCurrencyCode = "USD",
        });

        var updated = await service.Update(created.BudgetId, new NewBudget
        {
            Name = "New Name",
            StartDate = created.StartDate,
            EndDate = created.EndDate,
            Archived = true,
            BaseCurrencyCode = "USD",
        });

        Assert.NotNull(updated);
        Assert.Equal("New Name", updated!.Name);
        Assert.NotNull(updated.Archived);

        var unarchived = await service.Update(created.BudgetId, new NewBudget
        {
            Name = "New Name",
            StartDate = created.StartDate,
            EndDate = created.EndDate,
            Archived = false,
            BaseCurrencyCode = "USD",
        });
        Assert.Null(unarchived!.Archived);
    }

    [Fact]
    public async Task Delete_RemovesBudget()
    {
        await using var context = TestContextFactory.Create();
        var service = new BudgetService(context, TestContextFactory.EmptyContactLookup());

        var created = await service.Create(new NewBudget
        {
            Name = "Temp",
            StartDate = new DateTime(2025, 3, 1, 0, 0, 0, DateTimeKind.Utc),
            EndDate = new DateTime(2025, 3, 31, 0, 0, 0, DateTimeKind.Utc),
            Archived = false,
            BaseCurrencyCode = "EUR",
        });

        await service.Delete(created.BudgetId);

        Assert.Equal(0, context.Budgets.Count());
        Assert.Null(await service.Get(created.BudgetId));
    }

    [Fact]
    public async Task Create_WithUnsupportedCurrency_Throws()
    {
        await using var context = TestContextFactory.Create();
        var service = new BudgetService(context, TestContextFactory.EmptyContactLookup());

        await Assert.ThrowsAsync<DomainValidationException>(() => service.Create(new NewBudget
        {
            Name = "Invalid",
            StartDate = DateTime.UtcNow,
            EndDate = DateTime.UtcNow.AddDays(30),
            Archived = false,
            BaseCurrencyCode = "XYZ",
        }));
    }

    [Fact]
    public async Task GetTransactions_ReturnsNullWhenBudgetNotFound()
    {
        await using var context = TestContextFactory.Create();
        var service = new BudgetService(context, TestContextFactory.EmptyContactLookup());

        var result = await service.GetTransactions(Guid.NewGuid());

        Assert.Null(result);
    }

    [Fact]
    public async Task GetTransactions_WithNoBudgetItems_ReturnsEmptyReport()
    {
        await using var context = TestContextFactory.Create();
        var service = new BudgetService(context, TestContextFactory.EmptyContactLookup());

        var budget = await service.Create(new NewBudget
        {
            Name = "Empty",
            StartDate = new DateTime(2025, 4, 1, 0, 0, 0, DateTimeKind.Utc),
            EndDate = new DateTime(2025, 4, 30, 0, 0, 0, DateTimeKind.Utc),
            Archived = false,
            BaseCurrencyCode = "USD",
        });

        var report = await service.GetTransactions(budget.BudgetId);

        Assert.NotNull(report);
        Assert.Empty(report!.Transactions);
        Assert.Equal(0, report.ExcludedTransactionCount);
        Assert.Empty(report.ExcludedCurrencies);
    }

    [Fact]
    public async Task GetTransactions_ExcludesMismatchedCurrenciesAndReportsMetadata()
    {
        await using var context = TestContextFactory.Create();
        var tag = new TransactionTag
        {
            Name = "Food",
            Description = "Food tag",
        };
        context.TransactionTags.Add(tag);

        var account = new Account
        {
            Name = "Checking",
            Description = "Daily",
            AccountType = ContextAccountType.CheckingAccount,
            CurrencyCode = "USD",
            Opened = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        context.Accounts.Add(account);
        await context.SaveChangesAsync();

        var service = new BudgetService(context, TestContextFactory.EmptyContactLookup());
        var budget = await service.Create(new NewBudget
        {
            Name = "June",
            Description = "June budget",
            StartDate = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            EndDate = new DateTime(2025, 6, 30, 0, 0, 0, DateTimeKind.Utc),
            Archived = false,
            BaseCurrencyCode = "USD",
        });

        context.BudgetItems.Add(new BudgetItem
        {
            BudgetId = budget.BudgetId,
            Name = "Food",
            Description = "Food",
            PlannedAmount = 100,
            TransactionTagId = tag.TransactionTagId,
        });

        context.Transactions.AddRange(
            new Transaction
            {
                Description = "USD tx",
                Amount = 20,
                TimeStamp = new DateTime(2025, 6, 10, 0, 0, 0, DateTimeKind.Utc),
                AccountId = account.AccountId,
                TransactionTags = { tag },
                CurrencyCode = "USD",
                Status = TransactionStatus.New,
                StatusChangedAt = DateTime.UtcNow,
            },
            new Transaction
            {
                Description = "EUR tx",
                Amount = 10,
                TimeStamp = new DateTime(2025, 6, 11, 0, 0, 0, DateTimeKind.Utc),
                AccountId = account.AccountId,
                TransactionTags = { tag },
                CurrencyCode = "EUR",
                Status = TransactionStatus.New,
                StatusChangedAt = DateTime.UtcNow,
            });
        await context.SaveChangesAsync();

        var report = await service.GetTransactions(budget.BudgetId);

        Assert.NotNull(report);
        Assert.Single(report!.Transactions);
        Assert.Equal("USD", report.CurrencyCode);
        Assert.Equal(1, report.ExcludedTransactionCount);
        Assert.Equal(1, report.ExcludedCurrencies["EUR"]);
    }

    [Fact]
    public async Task SearchAndGet_PopulateTransactionCount_MatchingTagCurrencyAndDateRange()
    {
        await using var context = TestContextFactory.Create();
        var tag = new TransactionTag { Name = "Food", Description = "Food tag" };
        var otherTag = new TransactionTag { Name = "Rent", Description = "Rent tag" };
        context.TransactionTags.AddRange(tag, otherTag);

        var account = new Account
        {
            Name = "Checking",
            Description = "Daily",
            AccountType = ContextAccountType.CheckingAccount,
            CurrencyCode = "USD",
            Opened = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        context.Accounts.Add(account);
        await context.SaveChangesAsync();

        var service = new BudgetService(context, TestContextFactory.EmptyContactLookup());
        var budget = await service.Create(new NewBudget
        {
            Name = "June",
            StartDate = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            EndDate = new DateTime(2025, 6, 30, 0, 0, 0, DateTimeKind.Utc),
            Archived = false,
            BaseCurrencyCode = "USD",
        });

        context.BudgetItems.Add(new BudgetItem
        {
            BudgetId = budget.BudgetId,
            Name = "Food",
            PlannedAmount = 100,
            TransactionTagId = tag.TransactionTagId,
        });

        Transaction Tx(string desc, DateTime ts, TransactionTag? txTag, string currency) => new()
        {
            Description = desc,
            Amount = 20,
            TimeStamp = ts,
            AccountId = account.AccountId,
            TransactionTags = txTag is null ? new List<TransactionTag>() : new List<TransactionTag> { txTag },
            CurrencyCode = currency,
            Status = TransactionStatus.New,
            StatusChangedAt = DateTime.UtcNow,
        };

        context.Transactions.AddRange(
            // Two matching: right tag, currency and inside the date range.
            Tx("match 1", new DateTime(2025, 6, 10, 0, 0, 0, DateTimeKind.Utc), tag, "USD"),
            Tx("match 2", new DateTime(2025, 6, 20, 0, 0, 0, DateTimeKind.Utc), tag, "USD"),
            // Excluded: wrong currency.
            Tx("eur", new DateTime(2025, 6, 11, 0, 0, 0, DateTimeKind.Utc), tag, "EUR"),
            // Excluded: outside the date range.
            Tx("july", new DateTime(2025, 7, 1, 0, 0, 0, DateTimeKind.Utc), tag, "USD"),
            // Excluded: tag not referenced by the budget.
            Tx("other tag", new DateTime(2025, 6, 12, 0, 0, 0, DateTimeKind.Utc), otherTag, "USD"));
        await context.SaveChangesAsync();

        var fetched = await service.Get(budget.BudgetId);
        Assert.NotNull(fetched);
        Assert.Equal(2, fetched!.TransactionCount);

        var listed = (await service.ListAsync(new BudgetsQueryParams())).Items;
        Assert.Equal(2, Assert.Single(listed).TransactionCount);
    }

    [Fact]
    public async Task TransactionCount_DoesNotDoubleCountMultiTaggedTransaction()
    {
        await using var context = TestContextFactory.Create();
        var food = new TransactionTag { Name = "Food" };
        var dining = new TransactionTag { Name = "Dining" };
        context.TransactionTags.AddRange(food, dining);

        var account = new Account
        {
            Name = "Checking",
            Description = "Daily",
            AccountType = ContextAccountType.CheckingAccount,
            CurrencyCode = "USD",
            Opened = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        context.Accounts.Add(account);
        await context.SaveChangesAsync();

        var service = new BudgetService(context, TestContextFactory.EmptyContactLookup());
        var budget = await service.Create(new NewBudget
        {
            Name = "June",
            StartDate = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            EndDate = new DateTime(2025, 6, 30, 0, 0, 0, DateTimeKind.Utc),
            Archived = false,
            BaseCurrencyCode = "USD",
        });

        // Two budget items reference two different tags...
        context.BudgetItems.AddRange(
            new BudgetItem { BudgetId = budget.BudgetId, Name = "Food", PlannedAmount = 100, TransactionTagId = food.TransactionTagId },
            new BudgetItem { BudgetId = budget.BudgetId, Name = "Dining", PlannedAmount = 100, TransactionTagId = dining.TransactionTagId });

        // ...and a single transaction carries BOTH of them.
        context.Transactions.Add(new Transaction
        {
            Description = "Restaurant",
            Amount = 30,
            TimeStamp = new DateTime(2025, 6, 10, 0, 0, 0, DateTimeKind.Utc),
            AccountId = account.AccountId,
            TransactionTags = { food, dining },
            CurrencyCode = "USD",
            Status = TransactionStatus.New,
            StatusChangedAt = DateTime.UtcNow,
        });
        await context.SaveChangesAsync();

        var fetched = await service.Get(budget.BudgetId);

        // Counted once for the budget even though it matches two of the budget's tags.
        Assert.Equal(1, fetched!.TransactionCount);
    }

    [Fact]
    public async Task GetTransactions_AttributesMultiTaggedAmountToEachBucketButListsItOnce()
    {
        await using var context = TestContextFactory.Create();
        var food = new TransactionTag { Name = "Food" };
        var dining = new TransactionTag { Name = "Dining" };
        context.TransactionTags.AddRange(food, dining);

        var account = new Account
        {
            Name = "Checking",
            Description = "Daily",
            AccountType = ContextAccountType.CheckingAccount,
            CurrencyCode = "USD",
            Opened = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        context.Accounts.Add(account);
        await context.SaveChangesAsync();

        var service = new BudgetService(context, TestContextFactory.EmptyContactLookup());
        var budget = await service.Create(new NewBudget
        {
            Name = "June",
            StartDate = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            EndDate = new DateTime(2025, 6, 30, 0, 0, 0, DateTimeKind.Utc),
            Archived = false,
            BaseCurrencyCode = "USD",
        });

        context.BudgetItems.AddRange(
            new BudgetItem { BudgetId = budget.BudgetId, Name = "Food", PlannedAmount = 100, TransactionTagId = food.TransactionTagId },
            new BudgetItem { BudgetId = budget.BudgetId, Name = "Dining", PlannedAmount = 100, TransactionTagId = dining.TransactionTagId });

        context.Transactions.Add(new Transaction
        {
            Description = "Restaurant",
            Amount = 30,
            TimeStamp = new DateTime(2025, 6, 10, 0, 0, 0, DateTimeKind.Utc),
            AccountId = account.AccountId,
            TransactionTags = { food, dining },
            CurrencyCode = "USD",
            Status = TransactionStatus.New,
            StatusChangedAt = DateTime.UtcNow,
        });
        await context.SaveChangesAsync();

        var report = await service.GetTransactions(budget.BudgetId);

        Assert.NotNull(report);
        // The transaction appears once in the de-duplicated list...
        Assert.Single(report!.Transactions);
        // ...but its amount is attributed to BOTH tag buckets (no proportional splitting in v1).
        Assert.Equal(30, report.ExistingTransactionReport.Single(r => r.ExistingTransactionTag.TransactionTagId == food.TransactionTagId).Sum);
        Assert.Equal(30, report.ExistingTransactionReport.Single(r => r.ExistingTransactionTag.TransactionTagId == dining.TransactionTagId).Sum);
    }
}
