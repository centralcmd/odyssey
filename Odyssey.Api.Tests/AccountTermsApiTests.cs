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

    private static NewAccountTerm Fee(string? label, decimal value, DateTime effectiveFrom) => new()
    {
        TermKind = TermKind.Fee,
        Label = label,
        ValueUnit = TermValueUnit.Amount,
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

        var post = await client.PostAsJsonAsync(TermsPath(accountId), Fee("Account fee", 5m, new DateTime(2026, 1, 1)));
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


    // ── Series labels (spec §16) ──────────────────────────────────────────────

    [Fact]
    public async Task Post_TwoLabelledFeesOnTheSameDate_BothCreatedAndBothCurrent()
    {
        await using var factory = new ApiFactory(WriteAndRead);
        var accountId = await SeedAccountAsync(factory, DtoAccountType.CreditCard);
        using var client = factory.CreateClient();

        var date = new DateTime(2026, 1, 1);
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync(TermsPath(accountId), Fee("ATM · abroad", 25m, date))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync(TermsPath(accountId), Fee("ATM · domestic", 5m, date))).StatusCode);

        var current = await client.GetFromJsonAsync<List<CurrentAccountTerm>>(CurrentPath(accountId));
        Assert.Equal(2, current!.Count);
        Assert.Equal(new[] { "ATM · abroad", "ATM · domestic" }, current.Select(t => t.Label));
    }

    [Theory]
    [InlineData("atm · abroad")]
    [InlineData("  ATM   ·   abroad ")]
    public async Task Post_LabelDifferingOnlyByCaseOrSpacing_ReturnsConflict(string collidingLabel)
    {
        await using var factory = new ApiFactory(WriteAndRead);
        var accountId = await SeedAccountAsync(factory, DtoAccountType.CreditCard);
        using var client = factory.CreateClient();

        var date = new DateTime(2026, 1, 1);
        await client.PostAsJsonAsync(TermsPath(accountId), Fee("ATM · abroad", 25m, date));
        var duplicate = await client.PostAsJsonAsync(TermsPath(accountId), Fee(collidingLabel, 30m, date));

        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Post_FeeWithoutLabel_ReturnsBadRequest(string? label)
    {
        await using var factory = new ApiFactory(WriteAndRead);
        var accountId = await SeedAccountAsync(factory, DtoAccountType.Cash);
        using var client = factory.CreateClient();

        var post = await client.PostAsJsonAsync(TermsPath(accountId), Fee(label, 5m, new DateTime(2026, 1, 1)));

        Assert.Equal(HttpStatusCode.BadRequest, post.StatusCode);
    }

    [Theory]
    [InlineData(TermKind.InterestRate, DtoAccountType.SavingsAccount)]
    [InlineData(TermKind.ExpectedReturn, DtoAccountType.InvestmentAccount)]
    public async Task Post_RateKindWithLabel_ReturnsBadRequest(TermKind kind, DtoAccountType accountType)
    {
        await using var factory = new ApiFactory(WriteAndRead);
        var accountId = await SeedAccountAsync(factory, accountType);
        using var client = factory.CreateClient();

        var post = await client.PostAsJsonAsync(TermsPath(accountId), new NewAccountTerm
        {
            TermKind = kind,
            Label = "Headline",
            ValueUnit = TermValueUnit.Percentage,
            Value = 0.03m,
            EffectiveFrom = new DateTime(2026, 1, 1),
        });

        Assert.Equal(HttpStatusCode.BadRequest, post.StatusCode);
    }

    [Fact]
    public async Task Post_OverlongLabel_IsRejectedByModelValidation()
    {
        await using var factory = new ApiFactory(WriteAndRead);
        var accountId = await SeedAccountAsync(factory, DtoAccountType.CreditCard);
        using var client = factory.CreateClient();

        var post = await client.PostAsJsonAsync(
            TermsPath(accountId), Fee(new string('x', TermLabel.MaxLength + 1), 5m, new DateTime(2026, 1, 1)));

        Assert.Equal(HttpStatusCode.BadRequest, post.StatusCode);
    }

    [Fact]
    public async Task Post_NormalizesLabelOnWriteAndRoundTripsThroughHistory()
    {
        await using var factory = new ApiFactory(WriteAndRead);
        var accountId = await SeedAccountAsync(factory, DtoAccountType.CreditCard);
        using var client = factory.CreateClient();

        await client.PostAsJsonAsync(TermsPath(accountId), Fee("  ATM   Abroad  ", 25m, new DateTime(2026, 1, 1)));

        var list = await client.GetFromJsonAsync<List<ExistingAccountTerm>>(TermsPath(accountId));
        Assert.Equal("ATM Abroad", Assert.Single(list!).Label);
    }

    [Fact]
    public async Task Post_LabelKeyInTheRequestBody_IsNotBound()
    {
        // Mass assignment: LabelKey is on no request DTO, so a body that names it must be ignored and
        // the stored value derived from Label alone.
        await using var factory = new ApiFactory(WriteAndRead);
        var accountId = await SeedAccountAsync(factory, DtoAccountType.CreditCard);
        using var client = factory.CreateClient();

        var post = await client.PostAsJsonAsync(TermsPath(accountId), new
        {
            termKind = TermKind.Fee,
            label = "ATM Abroad",
            labelKey = "smuggled",
            valueUnit = TermValueUnit.Amount,
            value = 25m,
            effectiveFrom = new DateTime(2026, 1, 1),
        });
        Assert.Equal(HttpStatusCode.Created, post.StatusCode);

        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        var stored = await context.AccountTerms.AsNoTracking().SingleAsync(t => t.AccountId == accountId);
        Assert.Equal("ATM Abroad", stored.Label);
        Assert.Equal("atm abroad", stored.LabelKey);
    }

    [Fact]
    public async Task Put_ChangingOnlyTheLabel_SplitsOneCurrentEntryIntoTwo()
    {
        await using var factory = new ApiFactory(WriteAndRead);
        var accountId = await SeedAccountAsync(factory, DtoAccountType.CreditCard);
        using var client = factory.CreateClient();

        await client.PostAsJsonAsync(TermsPath(accountId), Fee("ATM", 25m, new DateTime(2026, 1, 1)));
        await client.PostAsJsonAsync(TermsPath(accountId), Fee("ATM", 30m, new DateTime(2026, 6, 1)));

        var beforeCurrent = await client.GetFromJsonAsync<List<CurrentAccountTerm>>($"{CurrentPath(accountId)}?asOf=2026-12-01");
        Assert.Single(beforeCurrent!);

        var later = (await client.GetFromJsonAsync<List<ExistingAccountTerm>>(TermsPath(accountId)))!
            .Single(t => t.EffectiveFrom.Date == new DateTime(2026, 6, 1));
        var put = await client.PutAsJsonAsync(
            $"{TermsPath(accountId)}/{later.AccountTermId}", Fee("ATM · abroad", 30m, new DateTime(2026, 6, 1)));
        Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);

        var afterCurrent = await client.GetFromJsonAsync<List<CurrentAccountTerm>>($"{CurrentPath(accountId)}?asOf=2026-12-01");
        Assert.Equal(2, afterCurrent!.Count);
    }

    [Fact]
    public async Task AccountRecord_CarriesOneDistinctlyNamedTermPerInForceSeries()
    {
        // The record card renders one tile per in-force term, so a card charging three fees must
        // carry three DIFFERENT names — otherwise it renders three indistinguishable tiles.
        await using var factory = new ApiFactory(
            [PermissionClaims.AccountsTermsRead, PermissionClaims.AccountsTermsWrite, PermissionClaims.AccountsRead]);
        var accountId = await SeedAccountAsync(factory, DtoAccountType.CreditCard);
        using var client = factory.CreateClient();

        var date = new DateTime(2026, 1, 1);
        await client.PostAsJsonAsync(TermsPath(accountId), Fee("Annual card fee", 95m, date));
        await client.PostAsJsonAsync(TermsPath(accountId), Fee("ATM · abroad", 25m, date));
        await client.PostAsJsonAsync(TermsPath(accountId), Fee("Paper statement", 2m, date));

        var account = await client.GetFromJsonAsync<ExistingAccount>($"/api/accounts/{accountId}");

        Assert.Equal(3, account!.CurrentTerms.Count);
        Assert.Equal(3, account.CurrentTerms.Select(t => t.Label).Distinct().Count());
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
