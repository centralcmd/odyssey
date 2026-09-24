using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Odyssey.Api.Tests.Infrastructure;
using Odyssey.Context;
using Odyssey.Context.Authorization;
using Odyssey.Dtos.Finance;
using Xunit;
using TermValueUnit = Odyssey.Dtos.Finance.TermValueUnit;

namespace Odyssey.Api.Tests;

/// <summary>
/// Issue #190 AC 16 — the five account-owned term routes are gone: each answers a plain <c>404</c>,
/// even to a caller holding every claim and naming an account that exists. There is no redirect or
/// shim; the contract term routes are the only term API.
/// </summary>
public class AccountTermRoutesRemovedTests
{
    private static readonly NewTerm Body = new()
    {
        Label = "Interest rate",
        ValueUnit = TermValueUnit.Percentage,
        Value = 0.03m,
        EffectiveFrom = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
    };

    public static TheoryData<string, string> RemovedRoutes() => new()
    {
        { "GET", "" },
        { "GET", "/current" },
        { "POST", "" },
        { "PUT", $"/{Guid.NewGuid()}" },
        { "DELETE", $"/{Guid.NewGuid()}" },
    };

    [Theory]
    [MemberData(nameof(RemovedRoutes))]
    public async Task Every_account_term_route_is_not_found(string method, string suffix)
    {
        await using var factory = new OdysseyApiFactory(RolePermissions.AllClaims);
        using var client = factory.CreateClient();
        var accountId = await SeedAccountAsync(factory);

        using var request = new HttpRequestMessage(new HttpMethod(method), $"/api/accounts/{accountId}/terms{suffix}");
        if (method is "POST" or "PUT")
            request.Content = JsonContent.Create(Body);

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private static async Task<Guid> SeedAccountAsync(OdysseyApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        await context.Database.EnsureCreatedAsync();

        var account = new Account
        {
            Name = "Savings",
            Description = "Exists, so a 404 cannot be the account's.",
            Opened = DateTime.UtcNow,
            AccountType = Odyssey.Context.AccountType.SavingsAccount,
            CurrencyCode = "USD",
        };
        context.Accounts.Add(account);
        await context.SaveChangesAsync();
        return account.AccountId;
    }
}
