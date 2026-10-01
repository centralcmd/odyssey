using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Odyssey.Api.Tests.Infrastructure;
using Odyssey.Context;
using Odyssey.Context.Authorization;
using Xunit;

namespace Odyssey.Api.Tests;

/// <summary>
/// Issue #287 H2 — <c>GET /api/accounts/{id}/transactions</c> is gone. It was the only route that
/// returned transactions without <c>transactions.read</c>, embedded contacts without
/// <c>contacts.read</c>, and had no row limit; nothing called it. <c>GET /api/transactions?accountIds=</c>
/// is the supported, paged and claim-gated way to list an account's transactions.
/// </summary>
public class AccountTransactionsRouteRemovedTests
{
    [Fact]
    public async Task The_account_transactions_route_is_not_found_even_with_every_claim()
    {
        await using var factory = new OdysseyApiFactory(RolePermissions.AllClaims);
        using var client = factory.CreateClient();
        var accountId = await SeedAccountAsync(factory);

        var response = await client.GetAsync($"/api/accounts/{accountId}/transactions");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private static async Task<Guid> SeedAccountAsync(OdysseyApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        await context.Database.EnsureCreatedAsync();

        var account = new Account
        {
            Name = "Checking",
            Description = "Exists, so a 404 cannot be the account's.",
            Opened = DateTime.UtcNow,
            AccountType = Odyssey.Context.AccountType.CheckingAccount,
            CurrencyCode = "USD",
        };
        context.Accounts.Add(account);
        await context.SaveChangesAsync();
        return account.AccountId;
    }
}
