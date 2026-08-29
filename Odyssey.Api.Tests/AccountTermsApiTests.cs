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
using TermKind = Odyssey.Dtos.Finance.TermKind;
using TermValueUnit = Odyssey.Dtos.Finance.TermValueUnit;

namespace Odyssey.Api.Tests;

public class AccountTermsApiTests
{
    private const string ActorUserId = "account-terms-actor-id";

    private static string TermsPath(Guid accountId) => $"/api/accounts/{accountId}/terms";
    private static string CurrentPath(Guid accountId) => $"/api/accounts/{accountId}/terms/current";

    private static NewAccountTerm InterestRate(decimal value, DateTime effectiveFrom) => new()
    {
        TermKind = TermKind.InterestRate,
        ValueUnit = TermValueUnit.Percentage,
        Value = value,
        EffectiveFrom = effectiveFrom,
    };

    // ── Authorization matrix (spec §7) ────────────────────────────────────────

    [Fact]
    public async Task List_Unauthenticated_ReturnsUnauthorized()
    {
        await using var factory = new ApiFactory(permissions: null);
        using var client = factory.CreateClient();

        var response = await client.GetAsync(TermsPath(Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task List_WithoutTermsReadPermission_ReturnsForbidden()
    {
        await using var factory = new ApiFactory(permissions: []);
        using var client = factory.CreateClient();

        var response = await client.GetAsync(TermsPath(Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Write_WithReadOnlyPermission_ReturnsForbidden()
    {
        await using var factory = new ApiFactory([PermissionClaims.AccountsTermsRead]);
        var accountId = await SeedAccountAsync(factory);
        using var client = factory.CreateClient();

        var post = await client.PostAsJsonAsync(TermsPath(accountId), InterestRate(0.03m, new DateTime(2026, 1, 1)));
        Assert.Equal(HttpStatusCode.Forbidden, post.StatusCode);

        var put = await client.PutAsJsonAsync($"{TermsPath(accountId)}/{Guid.NewGuid()}", InterestRate(0.03m, new DateTime(2026, 1, 1)));
        Assert.Equal(HttpStatusCode.Forbidden, put.StatusCode);

        var delete = await client.DeleteAsync($"{TermsPath(accountId)}/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.Forbidden, delete.StatusCode);
    }

    [Fact]
    public async Task Read_WithReadPermission_Succeeds()
    {
        await using var factory = new ApiFactory([PermissionClaims.AccountsTermsRead]);
        var accountId = await SeedAccountAsync(factory);
        using var client = factory.CreateClient();

        var list = await client.GetAsync(TermsPath(accountId));
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);

        var current = await client.GetAsync(CurrentPath(accountId));
        Assert.Equal(HttpStatusCode.OK, current.StatusCode);
    }

    // ── CRUD behaviour (spec §16) ─────────────────────────────────────────────

    [Fact]
    public async Task Post_ValidInterestRate_ReturnsCreatedAndIsRetrievable()
    {
        await using var factory = new ApiFactory(WriteAndRead);
        var accountId = await SeedAccountAsync(factory, DtoAccountType.SavingsAccount);
        using var client = factory.CreateClient();

        var post = await client.PostAsJsonAsync(TermsPath(accountId), InterestRate(0.0325m, new DateTime(2026, 1, 1)));
        Assert.Equal(HttpStatusCode.Created, post.StatusCode);

        var list = await client.GetFromJsonAsync<List<ExistingAccountTerm>>(TermsPath(accountId));
        var term = Assert.Single(list!);
        Assert.Equal(0.0325m, term.Value);
        Assert.Null(term.CurrencyCode);
    }

    [Fact]
    public async Task Post_AmountFeeWithoutCurrency_DefaultsToAccountCurrency()
    {
        await using var factory = new ApiFactory(WriteAndRead);
        var accountId = await SeedAccountAsync(factory, DtoAccountType.CheckingAccount, "EUR");
        using var client = factory.CreateClient();

        var post = await client.PostAsJsonAsync(TermsPath(accountId), new NewAccountTerm
        {
            TermKind = TermKind.ServiceFee,
            ValueUnit = TermValueUnit.Amount,
            Value = 5m,
            EffectiveFrom = new DateTime(2026, 1, 1),
        });
        Assert.Equal(HttpStatusCode.Created, post.StatusCode);

        var list = await client.GetFromJsonAsync<List<ExistingAccountTerm>>(TermsPath(accountId));
        Assert.Equal("EUR", Assert.Single(list!).CurrencyCode);
    }

    [Fact]
    public async Task Post_IneligibleAccountType_ReturnsBadRequest()
    {
        await using var factory = new ApiFactory(WriteAndRead);
        var accountId = await SeedAccountAsync(factory, DtoAccountType.Cash);
        using var client = factory.CreateClient();

        var post = await client.PostAsJsonAsync(TermsPath(accountId), InterestRate(0.03m, new DateTime(2026, 1, 1)));

        Assert.Equal(HttpStatusCode.BadRequest, post.StatusCode);
    }

    [Fact]
    public async Task Post_OnMissingAccount_ReturnsNotFound()
    {
        await using var factory = new ApiFactory(WriteAndRead);
        using var client = factory.CreateClient();

        var post = await client.PostAsJsonAsync(TermsPath(Guid.NewGuid()), InterestRate(0.03m, new DateTime(2026, 1, 1)));

        Assert.Equal(HttpStatusCode.NotFound, post.StatusCode);
    }

    [Fact]
    public async Task Post_Duplicate_ReturnsConflict()
    {
        await using var factory = new ApiFactory(WriteAndRead);
        var accountId = await SeedAccountAsync(factory, DtoAccountType.SavingsAccount);
        using var client = factory.CreateClient();

        var date = new DateTime(2026, 1, 1);
        await client.PostAsJsonAsync(TermsPath(accountId), InterestRate(0.03m, date));
        var duplicate = await client.PostAsJsonAsync(TermsPath(accountId), InterestRate(0.04m, date));

        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
    }

    [Fact]
    public async Task Current_RespectsAsOfAndSupersession()
    {
        await using var factory = new ApiFactory(WriteAndRead);
        var accountId = await SeedAccountAsync(factory, DtoAccountType.SavingsAccount);
        using var client = factory.CreateClient();

        await client.PostAsJsonAsync(TermsPath(accountId), InterestRate(0.03m, new DateTime(2026, 1, 1)));
        await client.PostAsJsonAsync(TermsPath(accountId), InterestRate(0.02m, new DateTime(2026, 6, 1)));

        var current = await client.GetFromJsonAsync<List<CurrentAccountTerm>>($"{CurrentPath(accountId)}?asOf=2026-03-01");
        Assert.Equal(0.03m, Assert.Single(current!).Value);
    }

    [Fact]
    public async Task Put_TermNotOnAccount_ReturnsNotFound()
    {
        await using var factory = new ApiFactory(WriteAndRead);
        var accountId = await SeedAccountAsync(factory, DtoAccountType.SavingsAccount);
        using var client = factory.CreateClient();

        var put = await client.PutAsJsonAsync($"{TermsPath(accountId)}/{Guid.NewGuid()}", InterestRate(0.03m, new DateTime(2026, 1, 1)));

        Assert.Equal(HttpStatusCode.NotFound, put.StatusCode);
    }

    [Fact]
    public async Task Delete_TermNotOnAccount_ReturnsNotFound()
    {
        await using var factory = new ApiFactory(WriteAndRead);
        var accountId = await SeedAccountAsync(factory, DtoAccountType.SavingsAccount);
        using var client = factory.CreateClient();

        var delete = await client.DeleteAsync($"{TermsPath(accountId)}/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, delete.StatusCode);
    }

    [Fact]
    public async Task Delete_ExistingTerm_ReturnsNoContent()
    {
        await using var factory = new ApiFactory(WriteAndRead);
        var accountId = await SeedAccountAsync(factory, DtoAccountType.SavingsAccount);
        using var client = factory.CreateClient();

        await client.PostAsJsonAsync(TermsPath(accountId), InterestRate(0.03m, new DateTime(2026, 1, 1)));
        var term = Assert.Single((await client.GetFromJsonAsync<List<ExistingAccountTerm>>(TermsPath(accountId)))!);

        var delete = await client.DeleteAsync($"{TermsPath(accountId)}/{term.AccountTermId}");
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
    }

    private static readonly string[] WriteAndRead =
        [PermissionClaims.AccountsTermsRead, PermissionClaims.AccountsTermsWrite];

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static async Task<Guid> SeedAccountAsync(
        WebApplicationFactory<Program> factory,
        DtoAccountType accountType = DtoAccountType.SavingsAccount,
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
