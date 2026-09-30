using System.Net;
using System.Net.Http.Json;
using Odyssey.Api.Tests.Infrastructure;
using Odyssey.Context;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos.Finance;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Odyssey.Api.Tests;

/// <summary>
/// A budget's period must not end before it starts (issue #238). The refusal is the same 400 a tax
/// statement's inverted period gets, keyed on <c>EndDate</c> so the form can mark the field, and it
/// applies to <c>PUT</c> as well as <c>POST</c>.
/// </summary>
public class BudgetPeriodApiTests
{
    private const string ActorUserId = "budget-period-actor-id";
    private const string Path = "/api/budgets";

    private static readonly string[] ReadWrite =
        [PermissionClaims.BudgetsRead, PermissionClaims.BudgetsCreate, PermissionClaims.BudgetsUpdate];

    private static NewBudget June() => new()
    {
        Name = "June",
        StartDate = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        EndDate = new DateTime(2025, 6, 30, 0, 0, 0, DateTimeKind.Utc),
        BaseCurrencyCode = "USD",
        Archived = false,
    };

    private static NewBudget Inverted()
    {
        var body = June();
        body.StartDate = new DateTime(2025, 6, 30, 0, 0, 0, DateTimeKind.Utc);
        body.EndDate = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        return body;
    }

    [Fact]
    public async Task Post_EndBeforeStart_ReturnsBadRequestKeyedOnEndDate()
    {
        await using var factory = new ApiFactory(ReadWrite);
        await EnsureDatabaseAsync(factory);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(Path, Inverted());

        await AssertKeyedOnEndDate(response);
    }

    [Fact]
    public async Task Post_SameDayPeriod_IsAccepted()
    {
        await using var factory = new ApiFactory(ReadWrite);
        await EnsureDatabaseAsync(factory);
        using var client = factory.CreateClient();
        var body = June();
        body.EndDate = body.StartDate;

        var response = await client.PostAsJsonAsync(Path, body);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task Put_EndBeforeStart_ReturnsBadRequestAndLeavesTheBudgetUnchanged()
    {
        await using var factory = new ApiFactory(ReadWrite);
        await EnsureDatabaseAsync(factory);
        using var client = factory.CreateClient();
        var created = await client.PostAsJsonAsync(Path, June());
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = Guid.Parse(created.Headers.Location!.Segments[^1]);

        var response = await client.PutAsJsonAsync($"{Path}/{id}", Inverted());

        await AssertKeyedOnEndDate(response);
        var stored = await client.GetFromJsonAsync<ExistingBudget>($"{Path}/{id}");
        Assert.Equal(new DateTime(2025, 6, 1), stored!.StartDate);
        Assert.Equal(new DateTime(2025, 6, 30), stored.EndDate);
    }

    // PUT is not an upsert (#263), and the period is a service-side rule, not model validation: the
    // unknown id is resolved first, so this is a 404 and nothing is created, inverted or otherwise.
    [Fact]
    public async Task Put_UnknownIdWithEndBeforeStart_ReturnsNotFoundAndCreatesNothing()
    {
        await using var factory = new ApiFactory(ReadWrite);
        await EnsureDatabaseAsync(factory);
        using var client = factory.CreateClient();

        var response = await client.PutAsJsonAsync($"{Path}/{Guid.NewGuid()}", Inverted());

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        using var scope = factory.Services.CreateScope();
        Assert.Empty(scope.ServiceProvider.GetRequiredService<OdysseyContext>().Budgets);
    }

    /// <summary>
    /// The budget section lists its transactions through <c>GET /api/transactions</c>, whose <c>to</c>
    /// is an inclusive instant. Bounded with <see cref="PeriodBounds"/> as the client does, the list
    /// holds exactly the rows the budget counts: the whole end day in, the next midnight out.
    /// </summary>
    [Fact]
    public async Task TransactionList_BoundedLikeTheBudgetSection_MatchesTheBudgetCount()
    {
        await using var factory = new ApiFactory([.. ReadWrite, PermissionClaims.TransactionsRead]);
        await EnsureDatabaseAsync(factory);
        using var client = factory.CreateClient();
        var created = await client.PostAsJsonAsync(Path, June());
        var budgetId = Guid.Parse(created.Headers.Location!.Segments[^1]);

        Guid tagId;
        var inPeriod = new List<Guid>();
        using (var scope = factory.Services.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
            var tag = new TransactionTag { Name = "Food" };
            var account = new Account
            {
                Name = "Checking",
                Description = "Daily",
                AccountType = Odyssey.Context.AccountType.CheckingAccount,
                CurrencyCode = "USD",
                Opened = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            };
            context.AddRange(tag, account);
            await context.SaveChangesAsync();
            tagId = tag.TransactionTagId;
            context.BudgetItems.Add(new BudgetItem { BudgetId = budgetId, PlannedAmount = 100, TransactionTagId = tagId });

            Transaction Tx(DateTime ts) => new()
            {
                Description = ts.ToString("o"),
                Amount = 5,
                TimeStamp = ts,
                AccountId = account.AccountId,
                TransactionTags = { tag },
                CurrencyCode = "USD",
                Status = TransactionStatus.New,
                StatusChangedAt = DateTime.UtcNow,
            };

            var rows = new[]
            {
                Tx(new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc)),
                Tx(new DateTime(2025, 6, 30, 14, 0, 0, DateTimeKind.Utc)),
                Tx(new DateTime(2025, 6, 30, 23, 59, 59, DateTimeKind.Utc)),
            };
            context.Transactions.AddRange(rows);
            context.Transactions.AddRange(
                Tx(new DateTime(2025, 7, 1, 0, 0, 0, DateTimeKind.Utc)),
                Tx(new DateTime(2025, 5, 31, 23, 59, 59, DateTimeKind.Utc)));
            await context.SaveChangesAsync();
            inPeriod.AddRange(rows.Select(r => r.TransactionId));
        }

        var budget = await client.GetFromJsonAsync<ExistingBudget>($"{Path}/{budgetId}");
        var from = PeriodBounds.InclusiveStart(budget!.StartDate).ToString("o");
        var to = PeriodBounds.InclusiveEndInstant(budget.EndDate).ToString("o");
        var listed = await client.GetFromJsonAsync<Odyssey.Dtos.PagedResult<ExistingTransaction>>(
            $"/api/transactions?tagIds={tagId}&from={Uri.EscapeDataString(from)}&to={Uri.EscapeDataString(to)}");

        Assert.Equal(3, budget.TransactionCount);
        Assert.Equal(inPeriod.Order(), listed!.Items.Select(t => t.TransactionId).Order());
    }

    private static async Task AssertKeyedOnEndDate(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ValidationProblemDetails>();
        Assert.Contains(nameof(NewBudget.EndDate), problem!.Errors.Keys, StringComparer.OrdinalIgnoreCase);
    }

    private static async Task EnsureDatabaseAsync(WebApplicationFactory<Program> factory)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        await context.Database.EnsureCreatedAsync();
    }

    private sealed class ApiFactory(IReadOnlyCollection<string>? permissions)
        : OdysseyApiFactory(permissions, ActorUserId);
}
