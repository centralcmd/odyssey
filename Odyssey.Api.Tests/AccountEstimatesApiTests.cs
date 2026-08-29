using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Odyssey.Context;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos.Finance;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;
using Odyssey.Api.Tests.Infrastructure;
using DtoAccountType = Odyssey.Dtos.Finance.AccountType;

namespace Odyssey.Api.Tests;

public class AccountEstimatesApiTests
{
    private const string ActorUserId = "account-estimates-actor-id";

    private static string EstimatesPath(Guid accountId) => $"/api/accounts/{accountId}/estimates";
    private static string CurrentPath(Guid accountId) => $"/api/accounts/{accountId}/estimates/current";

    private static NewAccountEstimate Estimate(decimal value, DateTime effectiveFrom, string? currencyCode = null) => new()
    {
        Value = value,
        EffectiveFrom = effectiveFrom,
        CurrencyCode = currencyCode,
    };

    // ── Authorization matrix (spec §7) ────────────────────────────────────────

    [Fact]
    public async Task List_Unauthenticated_ReturnsUnauthorized()
    {
        await using var factory = new ApiFactory(permissions: null);
        using var client = factory.CreateClient();

        var response = await client.GetAsync(EstimatesPath(Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task List_WithoutEstimatesReadPermission_ReturnsForbidden()
    {
        await using var factory = new ApiFactory(permissions: []);
        using var client = factory.CreateClient();

        var response = await client.GetAsync(EstimatesPath(Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Write_WithReadOnlyPermission_ReturnsForbidden()
    {
        await using var factory = new ApiFactory([PermissionClaims.AccountsEstimatesRead]);
        var accountId = await SeedAccountAsync(factory);
        using var client = factory.CreateClient();

        var post = await client.PostAsJsonAsync(EstimatesPath(accountId), Estimate(1000m, new DateTime(2026, 1, 1)));
        Assert.Equal(HttpStatusCode.Forbidden, post.StatusCode);

        var put = await client.PutAsJsonAsync($"{EstimatesPath(accountId)}/{Guid.NewGuid()}", Estimate(1000m, new DateTime(2026, 1, 1)));
        Assert.Equal(HttpStatusCode.Forbidden, put.StatusCode);

        var delete = await client.DeleteAsync($"{EstimatesPath(accountId)}/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.Forbidden, delete.StatusCode);
    }

    [Fact]
    public async Task Read_WithReadPermission_Succeeds()
    {
        await using var factory = new ApiFactory([PermissionClaims.AccountsEstimatesRead]);
        var accountId = await SeedAccountAsync(factory);
        using var client = factory.CreateClient();

        var list = await client.GetAsync(EstimatesPath(accountId));
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);

        var current = await client.GetAsync(CurrentPath(accountId));
        Assert.Equal(HttpStatusCode.OK, current.StatusCode);
    }

    // ── CRUD behaviour (spec §16) ─────────────────────────────────────────────

    [Fact]
    public async Task Post_ValidEstimate_ReturnsCreatedAndIsRetrievable()
    {
        await using var factory = new ApiFactory(WriteAndRead);
        var accountId = await SeedAccountAsync(factory, DtoAccountType.Property, "USD");
        using var client = factory.CreateClient();

        var post = await client.PostAsJsonAsync(EstimatesPath(accountId), Estimate(350000m, new DateTime(2026, 1, 1)));
        Assert.Equal(HttpStatusCode.Created, post.StatusCode);

        var list = await client.GetFromJsonAsync<List<ExistingAccountEstimate>>(EstimatesPath(accountId));
        var estimate = Assert.Single(list!);
        Assert.Equal(350000m, estimate.Value);
        Assert.Equal("USD", estimate.CurrencyCode);
    }

    [Fact]
    public async Task Post_WithoutCurrency_DefaultsToAccountCurrency()
    {
        await using var factory = new ApiFactory(WriteAndRead);
        var accountId = await SeedAccountAsync(factory, DtoAccountType.Property, "EUR");
        using var client = factory.CreateClient();

        var post = await client.PostAsJsonAsync(EstimatesPath(accountId), Estimate(1000m, new DateTime(2026, 1, 1)));
        Assert.Equal(HttpStatusCode.Created, post.StatusCode);

        var list = await client.GetFromJsonAsync<List<ExistingAccountEstimate>>(EstimatesPath(accountId));
        Assert.Equal("EUR", Assert.Single(list!).CurrencyCode);
    }

    [Fact]
    public async Task Post_CurrencyDifferentFromAccount_ReturnsBadRequest()
    {
        await using var factory = new ApiFactory(WriteAndRead);
        var accountId = await SeedAccountAsync(factory, DtoAccountType.Property, "USD");
        using var client = factory.CreateClient();

        var post = await client.PostAsJsonAsync(EstimatesPath(accountId), Estimate(1000m, new DateTime(2026, 1, 1), currencyCode: "EUR"));

        Assert.Equal(HttpStatusCode.BadRequest, post.StatusCode);
    }

    [Fact]
    public async Task Post_OnMissingAccount_ReturnsNotFound()
    {
        await using var factory = new ApiFactory(WriteAndRead);
        using var client = factory.CreateClient();

        var post = await client.PostAsJsonAsync(EstimatesPath(Guid.NewGuid()), Estimate(1000m, new DateTime(2026, 1, 1)));

        Assert.Equal(HttpStatusCode.NotFound, post.StatusCode);
    }

    [Fact]
    public async Task Post_Duplicate_ReturnsConflict()
    {
        await using var factory = new ApiFactory(WriteAndRead);
        var accountId = await SeedAccountAsync(factory, DtoAccountType.Property);
        using var client = factory.CreateClient();

        var date = new DateTime(2026, 1, 1);
        await client.PostAsJsonAsync(EstimatesPath(accountId), Estimate(1000m, date));
        var duplicate = await client.PostAsJsonAsync(EstimatesPath(accountId), Estimate(2000m, date));

        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
    }

    [Fact]
    public async Task Current_RespectsAsOfAndSupersession()
    {
        await using var factory = new ApiFactory(WriteAndRead);
        var accountId = await SeedAccountAsync(factory, DtoAccountType.Property);
        using var client = factory.CreateClient();

        await client.PostAsJsonAsync(EstimatesPath(accountId), Estimate(300000m, new DateTime(2026, 1, 1)));
        await client.PostAsJsonAsync(EstimatesPath(accountId), Estimate(280000m, new DateTime(2026, 6, 1)));

        var current = await client.GetFromJsonAsync<CurrentAccountEstimate>($"{CurrentPath(accountId)}?asOf=2026-03-01");
        Assert.Equal(300000m, current!.Value);
    }

    [Fact]
    public async Task Put_EstimateNotOnAccount_ReturnsNotFound()
    {
        await using var factory = new ApiFactory(WriteAndRead);
        var accountId = await SeedAccountAsync(factory, DtoAccountType.Property);
        using var client = factory.CreateClient();

        var put = await client.PutAsJsonAsync($"{EstimatesPath(accountId)}/{Guid.NewGuid()}", Estimate(1000m, new DateTime(2026, 1, 1)));

        Assert.Equal(HttpStatusCode.NotFound, put.StatusCode);
    }

    [Fact]
    public async Task Delete_ExistingEstimate_ReturnsNoContent()
    {
        await using var factory = new ApiFactory(WriteAndRead);
        var accountId = await SeedAccountAsync(factory, DtoAccountType.Property);
        using var client = factory.CreateClient();

        await client.PostAsJsonAsync(EstimatesPath(accountId), Estimate(1000m, new DateTime(2026, 1, 1)));
        var estimate = Assert.Single((await client.GetFromJsonAsync<List<ExistingAccountEstimate>>(EstimatesPath(accountId)))!);

        var delete = await client.DeleteAsync($"{EstimatesPath(accountId)}/{estimate.AccountEstimateId}");
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
    }

    [Fact]
    public async Task Delete_EstimateNotOnAccount_ReturnsNotFound()
    {
        await using var factory = new ApiFactory(WriteAndRead);
        var accountId = await SeedAccountAsync(factory, DtoAccountType.Property);
        using var client = factory.CreateClient();

        var delete = await client.DeleteAsync($"{EstimatesPath(accountId)}/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, delete.StatusCode);
    }

    [Fact]
    public async Task GetAccount_ExposesCurrentEstimatedValue()
    {
        await using var factory = new ApiFactory([.. WriteAndRead, PermissionClaims.AccountsRead]);
        var accountId = await SeedAccountAsync(factory, DtoAccountType.Property, "USD");
        using var client = factory.CreateClient();

        await client.PostAsJsonAsync(EstimatesPath(accountId), Estimate(350000m, new DateTime(2026, 1, 1)));

        var account = await client.GetFromJsonAsync<ExistingAccount>($"/api/accounts/{accountId}");
        Assert.Equal(350000m, account!.CurrentEstimatedValue);
        Assert.Equal("USD", account.CurrentEstimatedValueCurrencyCode);
    }

    private static readonly string[] WriteAndRead =
        [PermissionClaims.AccountsEstimatesRead, PermissionClaims.AccountsEstimatesWrite];

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static async Task<Guid> SeedAccountAsync(
        WebApplicationFactory<Program> factory,
        DtoAccountType accountType = DtoAccountType.Property,
        string currencyCode = "USD")
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        await context.Database.EnsureCreatedAsync();

        var accountId = Guid.NewGuid();
        context.Accounts.Add(new Account
        {
            AccountId = accountId,
            Name = "Test",
            Description = "Test account",
            Opened = DateTime.UtcNow,
            AccountType = (Odyssey.Context.AccountType)(int)accountType,
            CurrencyCode = currencyCode,
        });
        await context.SaveChangesAsync();
        return accountId;
    }

    private sealed class ApiFactory(IReadOnlyCollection<string>? permissions)
        : OdysseyApiFactory(permissions, ActorUserId);
}
