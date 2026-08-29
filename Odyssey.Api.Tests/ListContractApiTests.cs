using System.Net;
using System.Net.Http.Json;
using Odyssey.Dtos.Authorization;
using Odyssey.Context;
using Odyssey.Dtos.Finance;
using Odyssey.Dtos;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Odyssey.Api.Tests.Infrastructure;

namespace Odyssey.Api.Tests;

/// <summary>
/// Contract tests for the unified server-side list envelope (issue #277), exercised over the
/// accounts endpoint: the <see cref="PagedResult{T}"/> shape, allowlisted-sort fallback, offset/limit
/// clamping, and server-side search.
/// </summary>
public class ListContractApiTests
{
    private const string ActorUserId = "list-contract-actor-id";

    private static readonly string[] WriteAndRead =
        [PermissionClaims.AccountsRead, PermissionClaims.AccountsCreate];

    private static NewAccount NewAccount(string name) => new()
    {
        Name = name,
        Description = "desc",
        AccountType = Odyssey.Dtos.Finance.AccountType.CheckingAccount,
        CurrencyCode = "USD",
        Archived = false,
    };

    private static async Task SeedAsync(ApiFactory factory, HttpClient client, params string[] names)
    {
        // Creating the DB applies the seeded reference data (currencies), so USD is supported.
        using (var scope = factory.Services.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
            await context.Database.EnsureCreatedAsync();
        }

        foreach (var name in names)
        {
            var response = await client.PostAsJsonAsync("/api/accounts", NewAccount(name));
            Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        }
    }

    [Fact]
    public async Task List_ReturnsPagedResultEnvelope_WithTotalCount()
    {
        await using var factory = new ApiFactory(WriteAndRead);
        using var client = factory.CreateClient();
        await SeedAsync(factory, client, "Alpha", "Bravo", "Charlie");

        var page = await client.GetFromJsonAsync<PagedResult<ExistingAccount>>("/api/accounts?offset=0&limit=99999");

        Assert.NotNull(page);
        Assert.Equal(0, page!.Offset);
        Assert.Equal(3, page.TotalCount);
        Assert.Equal(3, page.Items.Count);
    }

    [Fact]
    public async Task List_NoSortBy_UsesResourceDefault()
    {
        await using var factory = new ApiFactory(WriteAndRead);
        using var client = factory.CreateClient();
        await SeedAsync(factory, client, "Bravo", "Alpha");

        // Absent sortBy resolves to the resource default (name ascending), not an arbitrary order.
        var page = await client.GetFromJsonAsync<PagedResult<ExistingAccount>>("/api/accounts");

        Assert.Equal(2, page!.TotalCount);
        Assert.Equal(["Alpha", "Bravo"], page.Items.Select(a => a.Name));
    }

    [Fact]
    public async Task List_InvalidSortBy_ReturnsBadRequest()
    {
        await using var factory = new ApiFactory(WriteAndRead);
        using var client = factory.CreateClient();
        await SeedAsync(factory, client, "Alpha");

        // sortBy is a strongly-typed per-resource enum; an unknown key (or injection attempt) is
        // rejected at binding, never reaching the query.
        var response = await client.GetAsync("/api/accounts?sortBy=; DROP TABLE Accounts");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task List_SortDir_BindsCaseInsensitively_AndReversesOrder()
    {
        await using var factory = new ApiFactory(WriteAndRead);
        using var client = factory.CreateClient();
        await SeedAsync(factory, client, "Bravo", "Alpha", "Charlie");

        // The client sends lowercase asc/desc; the SortDirection enum must bind them case-insensitively.
        var asc = await client.GetFromJsonAsync<PagedResult<ExistingAccount>>("/api/accounts?sortBy=name&sortDir=asc");
        Assert.Equal(["Alpha", "Bravo", "Charlie"], asc!.Items.Select(a => a.Name));

        var desc = await client.GetFromJsonAsync<PagedResult<ExistingAccount>>("/api/accounts?sortBy=name&sortDir=desc");
        Assert.Equal(["Charlie", "Bravo", "Alpha"], desc!.Items.Select(a => a.Name));
    }

    [Fact]
    public async Task List_InvalidSortDir_ReturnsBadRequest()
    {
        await using var factory = new ApiFactory(WriteAndRead);
        using var client = factory.CreateClient();
        await SeedAsync(factory, client, "Alpha");

        // sortDir is a strongly-typed enum (asc|desc); an unbindable value is rejected, not coerced.
        var response = await client.GetAsync("/api/accounts?sortDir=sideways");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task List_OversizedLimit_ReturnsBadRequest()
    {
        await using var factory = new ApiFactory(WriteAndRead);
        using var client = factory.CreateClient();
        await SeedAsync(factory, client, "Alpha");

        // limit is [Range(0, MaxLimit)]; a value above the ceiling is rejected at model validation.
        var response = await client.GetAsync($"/api/accounts?limit={ListDefaults.MaxLimit + 1}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task List_NegativeOffset_ReturnsBadRequest()
    {
        await using var factory = new ApiFactory(WriteAndRead);
        using var client = factory.CreateClient();
        await SeedAsync(factory, client, "Alpha");

        // offset is [Range(0, int.MaxValue)]; a negative value is rejected, not coerced.
        var response = await client.GetAsync("/api/accounts?offset=-5");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task List_OverCapStatusesFilter_ReturnsBadRequest()
    {
        await using var factory = new ApiFactory(WriteAndRead);
        using var client = factory.CreateClient();
        await SeedAsync(factory, client, "Alpha");

        // Statuses is [MaxLength(ListDefaults.MaxFilterArrayLength)]; an over-cap array is rejected
        // 400 by model validation rather than tripping ASP.NET's MaxModelBindingCollectionSize as a 500.
        var statuses = string.Join("&", Enumerable.Repeat("statuses=Open", ListDefaults.MaxFilterArrayLength + 1));
        var response = await client.GetAsync($"/api/accounts?{statuses}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task List_Search_FiltersAcrossTheWholeSet()
    {
        await using var factory = new ApiFactory(WriteAndRead);
        using var client = factory.CreateClient();
        await SeedAsync(factory, client, "Savings Pot", "Checking", "Holiday Savings");

        var page = await client.GetFromJsonAsync<PagedResult<ExistingAccount>>("/api/accounts?search=savings");

        Assert.NotNull(page);
        Assert.Equal(2, page!.TotalCount);
        Assert.All(page.Items, account => Assert.Contains("Savings", account.Name, StringComparison.OrdinalIgnoreCase));
    }

    private sealed class ApiFactory(IReadOnlyCollection<string>? permissions)
        : OdysseyApiFactory(permissions, ActorUserId);
}
