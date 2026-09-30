using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Odyssey.Api.Tests.Infrastructure;
using Odyssey.Context;
using Odyssey.Dtos.Authorization;
using Xunit;
using static Odyssey.Api.Tests.PropertyApiTestSupport;

namespace Odyssey.Api.Tests;

/// <summary>
/// <c>DELETE /api/currencies/{code}</c> over HTTP (issue #241). The EF InMemory tier enforces no
/// foreign keys, so the refusal here is the service's pre-check; the keys themselves are covered by
/// <c>Odyssey.IntegrationTests</c>.
/// </summary>
public class CurrencyDeleteApiTests
{
    private const string ActorUserId = "currency-delete-actor-id";

    [Fact]
    public async Task DeletingACurrencyInUse_ReturnsConflictNamingTheCounts_AndKeepsTheCurrency()
    {
        await using var factory = new ApiFactory([PermissionClaims.CurrenciesDelete]);
        await EnsureCreatedAsync(factory);
        await SeedAsync(factory, context =>
        {
            var account = new Account
            {
                Name = "Fjord Savings", Description = "Savings", Opened = DateTime.UtcNow, CurrencyCode = "NOK",
            };
            context.Accounts.Add(account);
            context.Transactions.Add(new Transaction
            {
                AccountId = account.AccountId, Description = "Deposit", Amount = 100m,
                TimeStamp = DateTime.UtcNow, CurrencyCode = "NOK",
            });
            context.Budgets.Add(new Budget
            {
                Name = "Household", StartDate = new DateTime(2026, 1, 1), EndDate = new DateTime(2026, 12, 31),
                BaseCurrencyCode = "NOK",
            });
            context.TaxStatements.Add(new TaxStatement
            {
                Name = "Return 2025", FiscalYear = 2025, StartDate = new DateTime(2025, 1, 1),
                EndDate = new DateTime(2025, 12, 31), BaseCurrencyCode = "NOK", CreatedAtUtc = DateTime.UtcNow,
            });
        });
        using var client = factory.CreateClient();

        var response = await client.DeleteAsync("/api/currencies/NOK");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("1 account.", body, StringComparison.Ordinal);
        Assert.Contains("1 transaction.", body, StringComparison.Ordinal);
        Assert.Contains("1 budget.", body, StringComparison.Ordinal);
        Assert.Contains("1 tax statement.", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Fjord Savings", body, StringComparison.Ordinal);
        Assert.True(await CurrencyExistsAsync(factory, "NOK"));
    }

    [Fact]
    public async Task DeletingAnUnusedCurrency_ReturnsNoContent_AndRemovesIt()
    {
        await using var factory = new ApiFactory([PermissionClaims.CurrenciesDelete]);
        await EnsureCreatedAsync(factory);
        using var client = factory.CreateClient();

        var response = await client.DeleteAsync("/api/currencies/NOK");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.False(await CurrencyExistsAsync(factory, "NOK"));
    }

    private static async Task SeedAsync(ApiFactory factory, Action<OdysseyContext> seed)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        seed(context);
        await context.SaveChangesAsync();
    }

    private static async Task<bool> CurrencyExistsAsync(ApiFactory factory, string code)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        return await context.Currencies.AnyAsync(c => c.CurrencyCode == code);
    }

    private sealed class ApiFactory(IReadOnlyCollection<string>? permissions)
        : OdysseyApiFactory(permissions, ActorUserId);
}
