using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Odyssey.Api.Tests.Infrastructure;
using Odyssey.Context;
using Odyssey.Dtos;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos.Finance;
using Odyssey.Dtos.Journal;
using Xunit;

namespace Odyssey.Api.Tests;

/// <summary>
/// Issue #239 — a <c>PUT</c> against an unknown id used to call <c>Post</c> as a plain method, which
/// skips the filter pipeline: <c>Post</c>'s <c>*.create</c> policy never ran, so an update-only caller
/// could create rows, and the row got a server-generated id, so a retry after a lost <c>201</c>
/// duplicated it. Six routes now answer <c>404</c> and create nothing; <c>PUT /api/accounts/{id}</c>
/// keeps its documented upsert but authorizes <c>accounts.create</c> explicitly and creates under the
/// route id.
/// </summary>
public class PutUnknownIdApiTests
{
    private const string ActorUserId = "put-unknown-id-actor";

    public static TheoryData<string, string> NonUpsertRoutes() => new()
    {
        { "transactions", PermissionClaims.TransactionsCreate },
        { "budgets", PermissionClaims.BudgetsCreate },
        { "budget-items", PermissionClaims.BudgetsCreate },
        { "currencies", PermissionClaims.CurrenciesCreate },
        { "transaction-tags", PermissionClaims.TransactionTagsCreate },
        { "contacts", PermissionClaims.ContactsCreate },
    };

    /// <summary>
    /// The unknown-id <c>PUT</c> is a <c>404</c> that writes nothing — both for a caller holding only
    /// the update claim (the privilege-escalation case) and for one that also holds create (proving it
    /// is no longer an upsert at all, rather than an upsert that now checks the claim).
    /// </summary>
    [Theory]
    [MemberData(nameof(NonUpsertRoutes))]
    public async Task Put_UnknownId_Is404_AndCreatesNothing_ForUpdateOnlyAndForCreateHolders(
        string resource, string createClaim)
    {
        foreach (var withCreate in new[] { false, true })
        {
            var permissions = new List<string> { UpdateClaimFor(resource) };
            if (withCreate)
                permissions.Add(createClaim);

            await using var factory = new OdysseyApiFactory(permissions, ActorUserId);
            await EnsureCreatedAsync(factory);
            var before = await CountAsync(factory, resource);
            using var client = factory.CreateClient();

            var (key, body) = UnknownKeyAndBody(resource);
            var response = await client.PutAsJsonAsync($"/api/{resource}/{key}", body);

            Assert.True(
                response.StatusCode == HttpStatusCode.NotFound,
                $"PUT /api/{resource}/{{unknown}} (create claim: {withCreate}) returned {(int)response.StatusCode}: "
                + await response.Content.ReadAsStringAsync());
            Assert.Null(response.Headers.Location);
            Assert.Equal(before, await CountAsync(factory, resource));
        }
    }

    [Fact]
    public async Task PutAccount_UnknownId_UpdateOnly_Is403_AndCreatesNothing()
    {
        await using var factory = new OdysseyApiFactory([PermissionClaims.AccountsUpdate], ActorUserId);
        await EnsureCreatedAsync(factory);
        using var client = factory.CreateClient();

        var response = await client.PutAsJsonAsync($"/api/accounts/{Guid.NewGuid()}", AccountBody("Checking"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, await CountAsync(factory, "accounts"));
    }

    [Fact]
    public async Task PutAccount_UnknownId_WithCreate_CreatesUnderRouteId_AndARetryUpdatesTheSameRow()
    {
        await using var factory = new OdysseyApiFactory(
            [PermissionClaims.AccountsRead, PermissionClaims.AccountsCreate, PermissionClaims.AccountsUpdate],
            ActorUserId);
        await EnsureCreatedAsync(factory);
        using var client = factory.CreateClient();
        var id = Guid.NewGuid();

        var first = await client.PutAsJsonAsync($"/api/accounts/{id}", AccountBody("Checking"));

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.EndsWith($"/api/accounts/{id}", first.Headers.Location!.ToString(), StringComparison.OrdinalIgnoreCase);
        var created = await client.GetFromJsonAsync<ExistingAccount>($"/api/accounts/{id}");
        Assert.Equal(id, created!.AccountId);

        // The retry a client makes after losing the 201 finds the row and updates it.
        var retry = await client.PutAsJsonAsync($"/api/accounts/{id}", AccountBody("Checking (renamed)"));

        Assert.Equal(HttpStatusCode.NoContent, retry.StatusCode);
        Assert.Equal(1, await CountAsync(factory, "accounts"));
        var updated = await client.GetFromJsonAsync<ExistingAccount>($"/api/accounts/{id}");
        Assert.Equal("Checking (renamed)", updated!.Name);
    }

    [Fact]
    public async Task PutAccount_EmptyGuid_WithCreate_Is400_AndCreatesNothing()
    {
        await using var factory = new OdysseyApiFactory(
            [PermissionClaims.AccountsCreate, PermissionClaims.AccountsUpdate], ActorUserId);
        await EnsureCreatedAsync(factory);
        using var client = factory.CreateClient();

        var response = await client.PutAsJsonAsync($"/api/accounts/{Guid.Empty}", AccountBody("Checking"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, await CountAsync(factory, "accounts"));
    }

    [Fact]
    public async Task PutAccount_ExistingId_UpdateOnly_Is204()
    {
        await using var factory = new OdysseyApiFactory([PermissionClaims.AccountsUpdate], ActorUserId);
        await EnsureCreatedAsync(factory);
        var id = Guid.NewGuid();
        using (var scope = factory.Services.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
            context.Accounts.Add(new Account
            {
                AccountId = id,
                Name = "Existing",
                Description = "",
                AccountType = Odyssey.Context.AccountType.CheckingAccount,
                CurrencyCode = "USD",
                Opened = DateTime.UtcNow,
            });
            await context.SaveChangesAsync();
        }
        using var client = factory.CreateClient();

        var response = await client.PutAsJsonAsync($"/api/accounts/{id}", AccountBody("Renamed"));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(1, await CountAsync(factory, "accounts"));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string UpdateClaimFor(string resource) => resource switch
    {
        "transactions" => PermissionClaims.TransactionsUpdate,
        "budgets" or "budget-items" => PermissionClaims.BudgetsUpdate,
        "currencies" => PermissionClaims.CurrenciesUpdate,
        "transaction-tags" => PermissionClaims.TransactionTagsUpdate,
        "contacts" => PermissionClaims.ContactsUpdate,
        _ => throw new ArgumentOutOfRangeException(nameof(resource), resource, null),
    };

    private static (string Key, object Body) UnknownKeyAndBody(string resource) => resource switch
    {
        "transactions" => (Guid.NewGuid().ToString(), new NewTransaction
        {
            Description = "Lunch",
            Amount = 12.5m,
            AccountId = Guid.NewGuid(),
        }),
        "budgets" => (Guid.NewGuid().ToString(), new NewBudget
        {
            Name = "Monthly",
            StartDate = DateTime.UtcNow.Date,
            EndDate = DateTime.UtcNow.Date.AddDays(30),
            Archived = false,
        }),
        "budget-items" => (Guid.NewGuid().ToString(), new NewBudgetItem
        {
            BudgetId = Guid.NewGuid(),
            CategoryType = Odyssey.Dtos.Finance.BudgetCategoryType.Expense,
            PlannedAmount = 100,
            TransactionTagId = Guid.NewGuid(),
        }),
        // The currency route is keyed on the ISO code, and the service insists route and body agree.
        "currencies" => ("ZZZ", new NewCurrency
        {
            CurrencyCode = "ZZZ",
            Name = "Test currency",
            MinorUnits = 2,
            Symbol = "Z",
            Archived = false,
        }),
        "transaction-tags" => (Guid.NewGuid().ToString(), new NewTransactionTag
        {
            Name = "Groceries",
            Archived = false,
        }),
        "contacts" => (Guid.NewGuid().ToString(), new NewContact
        {
            Type = ContactType.Organization,
            Archived = false,
            OrganizationDetails = new() { LegalName = "Store" },
        }),
        _ => throw new ArgumentOutOfRangeException(nameof(resource), resource, null),
    };

    private static NewAccount AccountBody(string name) => new()
    {
        Name = name,
        Description = "Main",
        AccountType = Odyssey.Dtos.Finance.AccountType.CheckingAccount,
        CurrencyCode = "USD",
        Archived = false,
    };

    private static async Task EnsureCreatedAsync(OdysseyApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<OdysseyContext>().Database.EnsureCreatedAsync();
    }

    private static async Task<int> CountAsync(OdysseyApiFactory factory, string resource)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        return resource switch
        {
            "accounts" => await context.Accounts.CountAsync(),
            "transactions" => await context.Transactions.CountAsync(),
            "budgets" => await context.Budgets.CountAsync(),
            "budget-items" => await context.BudgetItems.CountAsync(),
            "currencies" => await context.Currencies.CountAsync(),
            "transaction-tags" => await context.TransactionTags.CountAsync(),
            "contacts" => await context.Contacts.CountAsync(),
            _ => throw new ArgumentOutOfRangeException(nameof(resource), resource, null),
        };
    }
}
