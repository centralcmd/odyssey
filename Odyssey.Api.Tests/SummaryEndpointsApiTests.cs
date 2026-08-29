using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Odyssey.Api.Tests.Infrastructure;
using Odyssey.Context;
using Odyssey.Dtos.Finance;
using Odyssey.Dtos.Authorization;
using Xunit;
using ContextAccountType = Odyssey.Context.AccountType;
using ContextBudgetCategoryType = Odyssey.Context.BudgetCategoryType;
using DtoAccountType = Odyssey.Dtos.Finance.AccountType;

namespace Odyssey.Api.Tests;

/// <summary>
/// The four page-header summary endpoints added by issue #372, which replace a whole-table fetch on
/// Transactions, Accounts, Budgets and Tax statements. Each is asserted on three things: it is gated
/// on the resource's existing read claim, it aggregates over the <em>whole</em> set (the point — the
/// header must not track the grid's filters), and it moves after a mutation.
/// </summary>
public class SummaryEndpointsApiTests
{
    private const string ActorUserId = "summary-actor-id";

    // ── Authorization ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData("/api/transactions/summary")]
    [InlineData("/api/accounts/summary")]
    [InlineData("/api/budgets/summary")]
    [InlineData("/api/tax-statements/summary")]
    public async Task Summary_Unauthenticated_ReturnsUnauthorized(string path)
    {
        await using var factory = new ApiFactory(permissions: null);
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(path)).StatusCode);
    }

    [Theory]
    [InlineData("/api/transactions/summary")]
    [InlineData("/api/accounts/summary")]
    [InlineData("/api/budgets/summary")]
    [InlineData("/api/tax-statements/summary")]
    public async Task Summary_WithoutReadPermission_ReturnsForbidden(string path)
    {
        await using var factory = new ApiFactory(permissions: []);
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(path)).StatusCode);
    }

    // The literal "summary" segment has to win over the sibling "{id}" route, or the request would
    // reach the by-id action and fail Guid model binding with a 400.
    [Theory]
    [InlineData("/api/transactions/summary", PermissionClaims.TransactionsRead)]
    [InlineData("/api/accounts/summary", PermissionClaims.AccountsRead)]
    [InlineData("/api/budgets/summary", PermissionClaims.BudgetsRead)]
    [InlineData("/api/tax-statements/summary", PermissionClaims.TaxesRead)]
    public async Task Summary_WithReadPermission_RoutesToSummaryNotById(string path, string claim)
    {
        await using var factory = new ApiFactory([claim]);
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(path)).StatusCode);
    }

    // ── Transactions ──────────────────────────────────────────────────────────

    [Fact]
    public async Task TransactionSummary_CountsAndTotals_SpanTheWholeLedger()
    {
        await using var factory = new ApiFactory([PermissionClaims.TransactionsRead]);
        var accountId = await SeedAccountAsync(factory);
        await SeedAsync(factory, context =>
        {
            context.Transactions.Add(Transaction(accountId, 100m, TransactionStatus.Approved));
            context.Transactions.Add(Transaction(accountId, 25.50m, TransactionStatus.New));
            context.Transactions.Add(Transaction(accountId, -40m, TransactionStatus.Flagged));
            context.Transactions.Add(Transaction(accountId, -10.25m, TransactionStatus.New));
        });
        using var client = factory.CreateClient();

        var summary = await client.GetFromJsonAsync<TransactionSummary>("/api/transactions/summary");

        Assert.Equal(4, summary!.TotalTransactions);
        Assert.Equal(2, summary.CountsByStatus.New);
        Assert.Equal(1, summary.CountsByStatus.Approved);
        Assert.Equal(1, summary.CountsByStatus.Flagged);
        Assert.Equal(2, summary.IncomeCount);
        Assert.Equal(2, summary.ExpenseCount);
        Assert.Equal(125.50m, summary.TotalIn);
        // Reported as a positive magnitude, which is how the header renders "out".
        Assert.Equal(50.25m, summary.TotalOut);
    }

    [Fact]
    public async Task TransactionSummary_IsEmpty_WhenThereAreNoTransactions()
    {
        await using var factory = new ApiFactory([PermissionClaims.TransactionsRead]);
        using var client = factory.CreateClient();

        var summary = await client.GetFromJsonAsync<TransactionSummary>("/api/transactions/summary");

        Assert.Equal(0, summary!.TotalTransactions);
        Assert.Equal(0, summary.TotalIn);
        Assert.Equal(0, summary.TotalOut);
        Assert.Equal(0, summary.CountsByStatus.New);
    }

    [Fact]
    public async Task TransactionSummary_MovesAfterCreateAndDelete()
    {
        await using var factory = new ApiFactory(
            [PermissionClaims.TransactionsRead, PermissionClaims.TransactionsCreate, PermissionClaims.TransactionsDelete]);
        var accountId = await SeedAccountAsync(factory);
        await SeedCurrencyAsync(factory);
        using var client = factory.CreateClient();

        var post = await client.PostAsJsonAsync("/api/transactions", new NewTransaction
        {
            Description = "Salary",
            Amount = 500m,
            AccountId = accountId,
            CurrencyCode = "USD",
        });
        Assert.Equal(HttpStatusCode.Created, post.StatusCode);

        var afterCreate = await client.GetFromJsonAsync<TransactionSummary>("/api/transactions/summary");
        Assert.Equal(1, afterCreate!.TotalTransactions);
        Assert.Equal(500m, afterCreate.TotalIn);

        var created = post.Headers.Location!.Segments[^1];
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/transactions/{created}")).StatusCode);

        var afterDelete = await client.GetFromJsonAsync<TransactionSummary>("/api/transactions/summary");
        Assert.Equal(0, afterDelete!.TotalTransactions);
        Assert.Equal(0m, afterDelete.TotalIn);
    }

    // ── Accounts ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task AccountSummary_BucketsByStatusAndType_AndExcludesArchivedFromTheAggregates()
    {
        await using var factory = new ApiFactory([PermissionClaims.AccountsRead]);
        var open = await SeedAccountAsync(factory, DtoAccountType.CheckingAccount);
        var closed = await SeedAccountAsync(factory, DtoAccountType.SavingsAccount, closed: true);
        var archived = await SeedAccountAsync(factory, DtoAccountType.SavingsAccount, archived: true);
        await SeedAsync(factory, context =>
        {
            context.Transactions.Add(Transaction(open, 300m));
            context.Transactions.Add(Transaction(closed, -120m));
            // The archived account's balance must not reach any aggregate.
            context.Transactions.Add(Transaction(archived, 9_999m));
        });
        using var client = factory.CreateClient();

        var summary = await client.GetFromJsonAsync<AccountSummary>("/api/accounts/summary");

        Assert.Equal(3, summary!.TotalAccounts);
        Assert.Equal(1, summary.CountsByStatus.Open);
        Assert.Equal(1, summary.CountsByStatus.Closed);
        Assert.Equal(1, summary.CountsByStatus.Archived);

        // "By type" covers the live set only, so the archived savings account is not counted.
        var savings = Assert.Single(summary.CountsByType, c => c.Type == DtoAccountType.SavingsAccount);
        Assert.Equal(1, savings.Count);

        Assert.Equal(300m, summary.TotalAssets);
        Assert.Equal(-120m, summary.TotalLiabilities);
        Assert.Equal(180m, summary.CombinedValue);

        // One allocation row per live, non-zero account, largest asset first.
        Assert.Equal(2, summary.Allocations.Count);
        Assert.Equal(open, summary.Allocations[0].AccountId);
        Assert.Equal(300m, summary.Allocations[0].Value);
        Assert.Equal(closed, summary.Allocations[1].AccountId);
        Assert.Equal(-120m, summary.Allocations[1].Value);
    }

    [Fact]
    public async Task AccountSummary_InForceEstimateReplacesTheTransactionBalance()
    {
        await using var factory = new ApiFactory([PermissionClaims.AccountsRead]);
        var accountId = await SeedAccountAsync(factory, DtoAccountType.Property);
        await SeedAsync(factory, context =>
        {
            context.Transactions.Add(Transaction(accountId, 1_000m));
            // Two estimates: the later effective date is the one in force.
            context.AccountEstimates.Add(new AccountEstimate
            {
                AccountEstimateId = Guid.NewGuid(),
                AccountId = accountId,
                Value = 250_000m,
                EffectiveFrom = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                CreatedAtUtc = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            });
            context.AccountEstimates.Add(new AccountEstimate
            {
                AccountEstimateId = Guid.NewGuid(),
                AccountId = accountId,
                Value = 400_000m,
                EffectiveFrom = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                CreatedAtUtc = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            });
        });
        using var client = factory.CreateClient();

        var summary = await client.GetFromJsonAsync<AccountSummary>("/api/accounts/summary");

        // Replace, not add: the 1,000 transaction balance is superseded entirely (issue #182 §9).
        Assert.Equal(400_000m, summary!.CombinedValue);
        Assert.Equal(400_000m, summary.TotalAssets);
        Assert.Equal(400_000m, Assert.Single(summary.Allocations).Value);
    }

    [Fact]
    public async Task AccountSummary_OmitsZeroValueAccountsFromTheAllocations()
    {
        await using var factory = new ApiFactory([PermissionClaims.AccountsRead]);
        await SeedAccountAsync(factory);
        using var client = factory.CreateClient();

        var summary = await client.GetFromJsonAsync<AccountSummary>("/api/accounts/summary");

        Assert.Equal(1, summary!.CountsByStatus.Open);
        Assert.Empty(summary.Allocations);
    }

    // ── Budgets ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task BudgetSummary_CountsLiveBudgetsAndNetsTheirPlannedItems()
    {
        await using var factory = new ApiFactory([PermissionClaims.BudgetsRead]);
        await SeedAsync(factory, context =>
        {
            var live = Budget("2026");
            live.BudgetItems.Add(BudgetItem("Salary", ContextBudgetCategoryType.Income, 5_000m));
            live.BudgetItems.Add(BudgetItem("Rent", ContextBudgetCategoryType.Expense, 1_800m));
            context.Budgets.Add(live);

            var archived = Budget("2025", archived: true);
            // Archived items must not reach the planned balance.
            archived.BudgetItems.Add(BudgetItem("Old salary", ContextBudgetCategoryType.Income, 99_000m));
            context.Budgets.Add(archived);
        });
        using var client = factory.CreateClient();

        var summary = await client.GetFromJsonAsync<BudgetSummary>("/api/budgets/summary");

        Assert.Equal(2, summary!.TotalBudgets);
        Assert.Equal(1, summary.ActiveCount);
        Assert.Equal(1, summary.ArchivedCount);
        Assert.Equal(3_200m, summary.PlannedBalance);
    }

    [Fact]
    public async Task BudgetSummary_MovesAfterArchiving()
    {
        await using var factory = new ApiFactory([PermissionClaims.BudgetsRead, PermissionClaims.BudgetsUpdate]);
        var budgetId = Guid.NewGuid();
        await SeedAsync(factory, context =>
        {
            var budget = Budget("2026");
            budget.BudgetId = budgetId;
            budget.BudgetItems.Add(BudgetItem("Salary", ContextBudgetCategoryType.Income, 5_000m));
            context.Budgets.Add(budget);
        });
        await SeedCurrencyAsync(factory);
        using var client = factory.CreateClient();

        Assert.Equal(1, (await client.GetFromJsonAsync<BudgetSummary>("/api/budgets/summary"))!.ActiveCount);

        var put = await client.PutAsJsonAsync($"/api/budgets/{budgetId}", new NewBudget
        {
            Name = "2026",
            StartDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            EndDate = new DateTime(2026, 12, 31, 0, 0, 0, DateTimeKind.Utc),
            BaseCurrencyCode = "USD",
            Archived = true,
        });
        Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);

        var after = await client.GetFromJsonAsync<BudgetSummary>("/api/budgets/summary");
        Assert.Equal(0, after!.ActiveCount);
        Assert.Equal(1, after.ArchivedCount);
        Assert.Equal(0m, after.PlannedBalance);
    }

    // ── Tax statements ────────────────────────────────────────────────────────

    [Fact]
    public async Task TaxStatementSummary_ReturnsLiveYearsAscendingWithTheirDeclaredFigures()
    {
        await using var factory = new ApiFactory([PermissionClaims.TaxesRead]);
        await SeedAsync(factory, context =>
        {
            context.TaxStatements.Add(TaxStatement(2024, netWorth: 1_000_000m, assessedTax: 120_000m));
            context.TaxStatements.Add(TaxStatement(2022, netWorth: 600_000m, assessedTax: 80_000m));
            context.TaxStatements.Add(TaxStatement(2021, netWorth: 400_000m, assessedTax: 60_000m, archived: true));
        });
        using var client = factory.CreateClient();

        var summary = await client.GetFromJsonAsync<TaxStatementSummary>("/api/tax-statements/summary");

        Assert.Equal(3, summary!.TotalStatements);
        Assert.Equal(2, summary.ActiveCount);
        Assert.Equal(2022, summary.FirstFiscalYear);
        Assert.Equal(2024, summary.LatestFiscalYear);

        // Oldest first — the charts plot the series in that order and the archived year is excluded.
        Assert.Equal([2022, 2024], summary.Years.Select(y => y.FiscalYear));
        Assert.Equal(600_000m, summary.Years[0].DeclaredNetWorth);
        Assert.Equal(120_000m, summary.Years[1].AssessedTax);
        Assert.Equal("USD", summary.Years[1].BaseCurrencyCode);
    }

    [Fact]
    public async Task TaxStatementSummary_YearBoundsAreNull_WhenNothingIsOnFile()
    {
        await using var factory = new ApiFactory([PermissionClaims.TaxesRead]);
        using var client = factory.CreateClient();

        var summary = await client.GetFromJsonAsync<TaxStatementSummary>("/api/tax-statements/summary");

        Assert.Equal(0, summary!.ActiveCount);
        Assert.Null(summary.FirstFiscalYear);
        Assert.Null(summary.LatestFiscalYear);
        Assert.Empty(summary.Years);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static Transaction Transaction(Guid accountId, decimal amount, TransactionStatus status = TransactionStatus.New) => new()
    {
        TransactionId = Guid.NewGuid(),
        Description = "Seeded",
        Amount = amount,
        TimeStamp = new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc),
        AccountId = accountId,
        CurrencyCode = "USD",
        Status = status,
    };

    private static Budget Budget(string name, bool archived = false) => new()
    {
        BudgetId = Guid.NewGuid(),
        Name = name,
        StartDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        EndDate = new DateTime(2026, 12, 31, 0, 0, 0, DateTimeKind.Utc),
        BaseCurrencyCode = "USD",
        Archived = archived ? new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc) : null,
    };

    private static BudgetItem BudgetItem(string name, ContextBudgetCategoryType category, decimal planned) => new()
    {
        BudgetItemId = Guid.NewGuid(),
        BudgetId = Guid.Empty, // set by the owning Budget's collection fixup
        Name = name,
        CategoryType = category,
        PlannedAmount = planned,
    };

    private static TaxStatement TaxStatement(int year, decimal netWorth, decimal assessedTax, bool archived = false) => new()
    {
        TaxStatementId = Guid.NewGuid(),
        Name = $"Tax year {year}",
        FiscalYear = year,
        StartDate = new DateTime(year, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        EndDate = new DateTime(year, 12, 31, 0, 0, 0, DateTimeKind.Utc),
        BaseCurrencyCode = "USD",
        DeclaredNetWorth = netWorth,
        AssessedTax = assessedTax,
        CreatedAtUtc = new DateTime(year, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        Archived = archived ? new DateTime(year, 6, 1, 0, 0, 0, DateTimeKind.Utc) : null,
    };

    private static async Task<Guid> SeedAccountAsync(
        WebApplicationFactory<Program> factory,
        DtoAccountType accountType = DtoAccountType.CheckingAccount,
        bool closed = false,
        bool archived = false)
    {
        var accountId = Guid.NewGuid();
        await SeedAsync(factory, context => context.Accounts.Add(new Account
        {
            AccountId = accountId,
            Name = $"Account {accountId:N}"[..16],
            Description = "Seeded account",
            Opened = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            Closed = closed ? new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc) : null,
            Archived = archived ? new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc) : null,
            AccountType = (ContextAccountType)(int)accountType,
            CurrencyCode = "USD",
        }));
        return accountId;
    }

    // The write endpoints validate the currency against the reference table, which the InMemory
    // context starts without.
    private static Task SeedCurrencyAsync(WebApplicationFactory<Program> factory, string code = "USD") =>
        SeedAsync(factory, context =>
        {
            if (!context.Currencies.Any(c => c.CurrencyCode == code))
            {
                context.Currencies.Add(new Currency { CurrencyCode = code, Name = code, Symbol = "$", MinorUnits = 2 });
            }
        });

    private static async Task SeedAsync(WebApplicationFactory<Program> factory, Action<OdysseyContext> seed)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        await context.Database.EnsureCreatedAsync();
        seed(context);
        await context.SaveChangesAsync();
    }

    private sealed class ApiFactory(IReadOnlyCollection<string>? permissions)
        : OdysseyApiFactory(permissions, ActorUserId);
}
