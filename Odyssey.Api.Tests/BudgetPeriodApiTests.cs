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
