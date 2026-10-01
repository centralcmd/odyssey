using Microsoft.EntityFrameworkCore;
using Odyssey.Context;
using Odyssey.Core.Finance;
using Odyssey.Dtos.Finance;
using Xunit;
using ContextAccountType = Odyssey.Context.AccountType;
using ContextBudgetCategoryType = Odyssey.Context.BudgetCategoryType;

namespace Odyssey.Core.Tests;

/// <summary>
/// Issue #279: the tag icon on the write path, its read-side projection, and the ordered tags plus
/// <c>DisplayIcon</c> on every producer of <c>ExistingTransaction</c>.
/// </summary>
public class TransactionTagIconServiceTests
{
    private static readonly DateTime June = new(2025, 6, 10, 0, 0, 0, DateTimeKind.Utc);

    // ── The tag itself (AC 1–5) ───────────────────────────────────────────────

    [Fact]
    public async Task Create_stores_the_icon_and_reads_it_back()
    {
        await using var context = TestContextFactory.Create();
        var service = new TransactionTagService(context);

        var created = await service.Create(NewTag("Groceries", "shopping_cart"));

        Assert.Equal("shopping_cart", created.Icon);
        Assert.Equal("shopping_cart", (await service.Get(created.TransactionTagId))!.Icon);
        Assert.Equal("shopping_cart", (await context.TransactionTags.AsNoTracking().SingleAsync()).Icon);
    }

    [Fact]
    public async Task Create_without_an_icon_stores_null()
    {
        await using var context = TestContextFactory.Create();
        var service = new TransactionTagService(context);

        var created = await service.Create(NewTag("Groceries", null));

        Assert.Null(created.Icon);
        Assert.Null((await context.TransactionTags.AsNoTracking().SingleAsync()).Icon);
    }

    [Fact]
    public async Task Update_changes_the_icon_and_null_resets_it()
    {
        await using var context = TestContextFactory.Create();
        var service = new TransactionTagService(context);
        var created = await service.Create(NewTag("Groceries", "shopping_cart"));

        var changed = await service.Update(created.TransactionTagId, NewTag("Groceries", "restaurant"));
        Assert.Equal("restaurant", changed!.Icon);

        var reset = await service.Update(created.TransactionTagId, NewTag("Groceries", null));
        Assert.Null(reset!.Icon);
        Assert.Null((await context.TransactionTags.AsNoTracking().SingleAsync()).Icon);
    }

    [Theory]
    [InlineData("local_offer")]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("Shopping_Cart")]
    [InlineData("<svg>")]
    [InlineData("not_an_icon")]
    public async Task The_service_refuses_an_unknown_icon_for_non_HTTP_callers(string icon)
    {
        await using var context = TestContextFactory.Create();
        var service = new TransactionTagService(context);
        var existing = await service.Create(NewTag("Existing", "home"));

        var onCreate = await Assert.ThrowsAsync<DomainValidationException>(() => service.Create(NewTag("New", icon)));
        var onUpdate = await Assert.ThrowsAsync<DomainValidationException>(
            () => service.Update(existing.TransactionTagId, NewTag("Existing", icon)));

        foreach (var exception in new[] { onCreate, onUpdate })
        {
            Assert.Equal(TransactionTagIcons.InvalidIconMessage, exception.Message);
            Assert.Equal([TransactionTagIcons.InvalidIconMessage], exception.Errors!["Icon"]);
        }

        var stored = await context.TransactionTags.AsNoTracking().ToListAsync();
        Assert.Equal("home", Assert.Single(stored).Icon);
    }

    [Fact]
    public async Task A_stored_unknown_key_reads_as_null_on_get_and_list()
    {
        await using var context = TestContextFactory.Create();
        var tag = new TransactionTag { Name = "Legacy", Icon = "retired_key" };
        context.TransactionTags.Add(tag);
        await context.SaveChangesAsync();
        var service = new TransactionTagService(context);

        Assert.Null((await service.Get(tag.TransactionTagId))!.Icon);
        Assert.Null(Assert.Single((await service.ListAsync(new TransactionTagsQueryParams())).Items).Icon);
    }

    // ── Every ExistingTransaction producer (AC 6–9) ───────────────────────────

    [Fact]
    public async Task Transaction_list_and_detail_order_tags_and_resolve_the_display_icon()
    {
        await using var context = TestContextFactory.Create();
        var seeded = await SeedAsync(context);
        var service = new TransactionService(context, TestContextFactory.EmptyContactLookup());

        var listed = (await service.ListAsync(new TransactionsQueryParams())).Items;
        AssertMixed(listed.Single(t => t.TransactionId == seeded.Mixed));
        AssertUntagged(listed.Single(t => t.TransactionId == seeded.Untagged));

        AssertMixed((await service.Get(seeded.Mixed))!);
        AssertUntagged((await service.Get(seeded.Untagged))!);
    }

    [Fact]
    public async Task Account_transactions_order_tags_and_resolve_the_display_icon()
    {
        await using var context = TestContextFactory.Create();
        var seeded = await SeedAsync(context);
        var service = new AccountService(context, TestContextFactory.EmptyContactLookup());

        var transactions = await service.GetTransactions(seeded.AccountId);

        AssertMixed(transactions!.Single(t => t.TransactionId == seeded.Mixed));
        AssertUntagged(transactions!.Single(t => t.TransactionId == seeded.Untagged));
    }

    [Fact]
    public async Task Budget_report_orders_tags_resolves_the_display_icon_and_embeds_tag_icons()
    {
        await using var context = TestContextFactory.Create();
        var seeded = await SeedAsync(context);
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
            PlannedAmount = 100,
            TransactionTagId = seeded.Food.TransactionTagId,
            CategoryType = ContextBudgetCategoryType.Expense,
        });
        await context.SaveChangesAsync();

        var report = await service.GetTransactions(budget.BudgetId);

        AssertMixed(Assert.Single(report!.Transactions));
        Assert.Equal("restaurant", Assert.Single(report.ExistingTransactionReport).ExistingTransactionTag.Icon);
    }

    [Fact]
    public async Task Budget_item_embeds_the_tag_icon_normalised()
    {
        await using var context = TestContextFactory.Create();
        var seeded = await SeedAsync(context);
        var budget = new Budget
        {
            Name = "June",
            StartDate = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            EndDate = new DateTime(2025, 6, 30, 0, 0, 0, DateTimeKind.Utc),
            BaseCurrencyCode = "USD",
        };
        context.Budgets.Add(budget);
        await context.SaveChangesAsync();
        var iconned = new BudgetItem { BudgetId = budget.BudgetId, PlannedAmount = 1, TransactionTagId = seeded.Food.TransactionTagId };
        var stale = new BudgetItem { BudgetId = budget.BudgetId, PlannedAmount = 1, TransactionTagId = seeded.Legacy.TransactionTagId };
        context.BudgetItems.AddRange(iconned, stale);
        await context.SaveChangesAsync();
        var service = new BudgetItemService(context);

        Assert.Equal("restaurant", (await service.Get(iconned.BudgetItemId))!.Tag.Icon);
        Assert.Null((await service.Get(stale.BudgetItemId))!.Tag.Icon);
    }

    [Fact]
    public async Task Renaming_a_tag_so_it_sorts_first_changes_the_display_icon()
    {
        await using var context = TestContextFactory.Create();
        var seeded = await SeedAsync(context);
        var transactions = new TransactionService(context, TestContextFactory.EmptyContactLookup());
        var tags = new TransactionTagService(context);

        Assert.Equal("receipt_long", (await transactions.Get(seeded.Mixed))!.DisplayIcon);

        await tags.Update(seeded.Food.TransactionTagId, NewTag("Aardvark food", "restaurant"));
        context.ChangeTracker.Clear();

        Assert.Equal("restaurant", (await transactions.Get(seeded.Mixed))!.DisplayIcon);
    }

    /// <summary>
    /// The mixed transaction carries <c>Food → restaurant</c>, <c>bills → receipt_long</c>,
    /// <c>Apple → (unknown key)</c> and <c>Zed → null</c>: ordered Apple, bills, Food, Zed, with the
    /// unknown key projected as null and skipped by the resolver.
    /// </summary>
    private static void AssertMixed(ExistingTransaction transaction)
    {
        Assert.Equal(["Apple", "bills", "Food", "Zed"], transaction.TransactionTags.Select(tag => tag.Name));
        Assert.Equal([null, "receipt_long", "restaurant", null], transaction.TransactionTags.Select(tag => tag.Icon));
        Assert.Equal("receipt_long", transaction.DisplayIcon);
    }

    private static void AssertUntagged(ExistingTransaction transaction)
    {
        Assert.Empty(transaction.TransactionTags);
        Assert.Equal(TransactionTagIcons.Default, transaction.DisplayIcon);
    }

    private sealed record Seeded(Guid AccountId, Guid Mixed, Guid Untagged, TransactionTag Food, TransactionTag Legacy);

    private static async Task<Seeded> SeedAsync(OdysseyContext context)
    {
        var food = new TransactionTag { Name = "Food", Icon = "restaurant" };
        var bills = new TransactionTag { Name = "bills", Icon = "receipt_long" };
        var legacy = new TransactionTag { Name = "Apple", Icon = "retired_key" };
        var plain = new TransactionTag { Name = "Zed" };
        context.TransactionTags.AddRange(food, bills, legacy, plain);

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

        // Inserted in an order that differs from the expected one, so an unordered read fails.
        var mixed = Transaction(account, "Mixed", food, plain, bills, legacy);
        var untagged = Transaction(account, "Untagged");
        context.Transactions.AddRange(mixed, untagged);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        return new Seeded(account.AccountId, mixed.TransactionId, untagged.TransactionId, food, legacy);
    }

    private static Transaction Transaction(Account account, string description, params TransactionTag[] tags)
    {
        var transaction = new Transaction
        {
            Description = description,
            Amount = -20,
            TimeStamp = June,
            AccountId = account.AccountId,
            CurrencyCode = "USD",
            Status = TransactionStatus.New,
            StatusChangedAt = June,
        };
        foreach (var tag in tags)
        {
            transaction.TransactionTags.Add(tag);
        }

        return transaction;
    }

    private static NewTransactionTag NewTag(string name, string? icon) => new()
    {
        Name = name,
        Description = null,
        Archived = false,
        Icon = icon,
    };
}
