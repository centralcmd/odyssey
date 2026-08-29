using System.Net;
using Odyssey.Dtos.Authorization;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;
using Odyssey.Api.Tests.Infrastructure;

namespace Odyssey.Api.Tests;

// Regression coverage for the access-control gap where GET /api/budgets/{id}/transactions
// shipped without an [Authorize] attribute and, absent a fail-closed default, was reachable
// fully unauthenticated — leaking transaction PII. The endpoint now requires transactions.read
// and MapControllers().RequireAuthorization() guards any future un-attributed action.
public class BudgetTransactionsAuthorizationTests
{
    private const string ActorUserId = "budget-transactions-actor-id";

    private static string TransactionsPath(Guid budgetId) => $"/api/budgets/{budgetId}/transactions";

    [Fact]
    public async Task GetBudgetTransactions_Unauthenticated_ReturnsUnauthorized()
    {
        await using var factory = new ApiFactory(permissions: null);
        using var client = factory.CreateClient();

        var response = await client.GetAsync(TransactionsPath(Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetBudgetTransactions_WithoutTransactionsReadPermission_ReturnsForbidden()
    {
        await using var factory = new ApiFactory(permissions: []);
        using var client = factory.CreateClient();

        var response = await client.GetAsync(TransactionsPath(Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task GetBudgetTransactions_WithTransactionsReadPermission_ReachesHandler()
    {
        await using var factory = new ApiFactory([PermissionClaims.TransactionsRead]);
        using var client = factory.CreateClient();

        // No budget with this id exists, so a caller that clears authorization reaches the
        // handler and gets 404 — proving the claim grants access rather than being blocked.
        var response = await client.GetAsync(TransactionsPath(Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private sealed class ApiFactory(IReadOnlyCollection<string>? permissions)
        : OdysseyApiFactory(permissions, ActorUserId);
}
