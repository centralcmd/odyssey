using Odyssey.Dtos;
using System.Net;
using System.Net.Http.Json;
using Odyssey.Api.Tests.Infrastructure;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos.Finance;
using Odyssey.Dtos.Journal;
using Odyssey.Context;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using DtoAccountType = Odyssey.Dtos.Finance.AccountType;
using ContractType = Odyssey.Dtos.Finance.ContractType;

namespace Odyssey.Api.Tests;

/// <summary>
/// Locks the DTO data-annotation contract (CLAUDE.md "DTOs" convention) at the API boundary.
/// The codebase documents <c>[StringLength]</c>/<c>[Range]</c>/<c>[EnumDataType]</c>/<c>[Required]</c>
/// on request DTOs, but the rest of the suite only exercises *business-rule* 400s — never the
/// model-binding 400 those annotations produce. A regression that drops an annotation (or widens a
/// limit) would otherwise pass every existing test.
///
/// Each case starts from a baseline body that the matching *positive control* proves is genuinely
/// accepted (201), then violates exactly one annotation — so the resulting 400 is attributable to
/// that field and nothing else. With <c>[ApiController]</c> the framework short-circuits to a
/// <see cref="ValidationProblemDetails"/> 400 before the service runs.
/// </summary>
public class DtoValidationBoundaryTests(DtoValidationBoundaryTests.Host host) : SharedApiHostTests<DtoValidationBoundaryTests.Host>(host)
{
    private const string ActorUserId = "dto-validation-actor";

    // One actor holding every create claim the cases below need.
    private static readonly string[] CreateClaims =
    [
        PermissionClaims.AccountsRead, PermissionClaims.AccountsCreate,
        PermissionClaims.ContractsRead, PermissionClaims.ContractsCreate,
        PermissionClaims.TransactionsRead, PermissionClaims.TransactionsCreate,
        PermissionClaims.BudgetsRead, PermissionClaims.BudgetsCreate,
        PermissionClaims.ContactsRead, PermissionClaims.ContactsCreate,
        PermissionClaims.CurrenciesRead, PermissionClaims.CurrenciesCreate,
        PermissionClaims.ExchangeRatesRead, PermissionClaims.ExchangeRatesCreate,
        PermissionClaims.TransactionTagsRead, PermissionClaims.TransactionTagsCreate,
    ];

    public class ApiFactory(IReadOnlyCollection<string>? permissions)
        : OdysseyApiFactory(permissions, ActorUserId);

    // ── Baseline (valid) request bodies ────────────────────────────────────────
    // Each must POST successfully on its own — the *_BaselineIsAccepted positive controls assert that.

    private static NewAccount ValidAccount() => new()
    {
        Name = "Brokerage",
        Description = "Within limits",
        AccountType = DtoAccountType.InvestmentAccount,
        CurrencyCode = "USD",
    };

    private static NewContract ValidContract() => new()
    {
        Name = "Employment agreement",
        Type = ContractType.Employment,
        Description = "Full-time role",
        StartDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
    };

    private static NewTransaction ValidTransaction(Guid accountId) => new()
    {
        Description = "Coffee",
        Amount = 1m,
        AccountId = accountId,
    };

    private static NewExchangeRate ValidExchangeRate() => new()
    {
        FromCurrencyCode = "USD",
        ToCurrencyCode = "EUR",
        Rate = 1.5m,
    };

    private static NewCurrency ValidCurrency() => new()
    {
        // "ZZZ" is ISO-format-valid and not among the seeded currencies, so the create succeeds.
        CurrencyCode = "ZZZ",
        Name = "Test currency",
        MinorUnits = 2,
        Symbol = "Z",
        Archived = false,
    };

    private static NewContact ValidContact() => new()
    {
        Type = ContactType.Organization,
        Archived = false,
        OrganizationDetails = new() { LegalName = "Acme Corp" },
    };

    private static NewTransactionTag ValidTransactionTag() => new()
    {
        Name = "Groceries",
        Archived = false,
    };

    private static NewBudget ValidBudget() => new()
    {
        Name = "2026 plan",
        StartDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        EndDate = new DateTime(2026, 12, 31, 0, 0, 0, DateTimeKind.Utc),
        BaseCurrencyCode = "USD",
        Archived = false,
    };

    private static NewBudgetItem ValidBudgetItem(Guid budgetId) => new()
    {
        BudgetId = budgetId,
        CategoryType = Odyssey.Dtos.Finance.BudgetCategoryType.Expense,
        PlannedAmount = 1000m,
        TransactionTagId = Guid.NewGuid(),
    };

    // EF InMemory only materializes HasData reference rows (the supported currencies the account/
    // contract create paths validate against) once the store is created. Trigger that so the
    // positive-control bodies are validated against real reference data, not an empty store.
    private static async Task EnsureReferenceDataAsync(WebApplicationFactory<Program> factory)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        await context.Database.EnsureCreatedAsync();
    }

    // ── Accepted bodies ────────────────────────────────────────────────────────
    // The positive controls: each baseline genuinely POSTs (201), so a 400 below is attributable to the
    // one field it changes. The two exact-limit cases prove the boundary sits AT the limit, not one
    // below it. NewTransaction and NewBudgetItem have no control — a successful create needs a seeded
    // account/budget — but model validation runs before that lookup, so their 400s still isolate.

    private static readonly Dictionary<string, (string Path, Func<object> Body)> AcceptedCases = new()
    {
        ["Account baseline"] = ("/api/accounts", () => ValidAccount()),
        ["Account Name at [StringLength(256)]"] = ("/api/accounts", () => ValidAccount() with { Name = new string('x', 256) }),
        ["Contract baseline"] = ("/api/contracts", () => ValidContract()),
        ["ExchangeRate baseline"] = ("/api/exchange-rates", () => ValidExchangeRate()),
        ["Currency baseline"] = ("/api/currencies", () => ValidCurrency()),
        ["Currency MinorUnits at [Range(0, 12)] max"] = ("/api/currencies", () => ValidCurrency() with { MinorUnits = 12 }),
        ["Contact baseline"] = ("/api/contacts", () => ValidContact()),
        ["TransactionTag baseline"] = ("/api/transaction-tags", () => ValidTransactionTag()),
        ["Budget baseline"] = ("/api/budgets", () => ValidBudget()),
    };

    public static TheoryData<string> AcceptedCaseNames => [.. AcceptedCases.Keys];

    [Theory]
    [MemberData(nameof(AcceptedCaseNames))]
    public async Task Create_WithinEveryAnnotation_IsAccepted(string caseName)
    {
        var (path, body) = AcceptedCases[caseName];
        var factory = Factory.ForCaller(CreateClaims);
        await EnsureReferenceDataAsync(factory);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(path, body());

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    // ── Rejected bodies ────────────────────────────────────────────────────────
    // Each violates exactly one annotation (or, where noted, one service rule) and names the field the
    // ValidationProblemDetails must be keyed on.

    private static readonly Dictionary<string, (string Path, string Field, Func<object> Body)> RejectedCases = new()
    {
        // NewAccount
        ["Account Name over [StringLength(256)]"] =
            ("/api/accounts", nameof(NewAccount.Name), () => ValidAccount() with { Name = new string('x', 257) }),
        ["Account CurrencyCode over [StringLength(3)]"] =
            ("/api/accounts", nameof(NewAccount.CurrencyCode), () => ValidAccount() with { CurrencyCode = "USDD" }),

        // NewContract
        ["Contract Name empty, [StringLength(256, MinimumLength = 1)]"] =
            ("/api/contracts", nameof(NewContract.Name), () => ValidContract() with { Name = "" }),
        ["Contract Description over [StringLength(1024)]"] =
            ("/api/contracts", nameof(NewContract.Description), () => ValidContract() with { Description = new string('x', 1025) }),
        ["Contract Type undefined, [EnumDataType]"] =
            ("/api/contracts", nameof(NewContract.Type), () => ValidContract() with { Type = (ContractType)999 }),

        // NewTransaction
        ["Transaction Description over [StringLength(256)]"] =
            ("/api/transactions", nameof(NewTransaction.Description), () => ValidTransaction(Guid.NewGuid()) with { Description = new string('x', 257) }),
        ["Transaction ExternalId over [StringLength(64)]"] =
            ("/api/transactions", nameof(NewTransaction.ExternalId), () => ValidTransaction(Guid.NewGuid()) with { ExternalId = new string('x', 65) }),
        ["Transaction StatusComment over [StringLength(256)]"] =
            ("/api/transactions", nameof(NewTransaction.StatusComment), () => ValidTransaction(Guid.NewGuid()) with { StatusComment = new string('x', 257) }),

        // NewExchangeRate. Rate's [Range] starts at the rate column's smallest step
        // (MoneyBounds.ExchangeRateMin), so zero is rejected; the precision cases live in MoneyBoundsTests (#240).
        ["ExchangeRate Rate zero, [Range(ExchangeRateMin, …)]"] =
            ("/api/exchange-rates", nameof(NewExchangeRate.Rate), () => ValidExchangeRate() with { Rate = 0m }),
        ["ExchangeRate FromCurrencyCode over [StringLength(3)]"] =
            ("/api/exchange-rates", nameof(NewExchangeRate.FromCurrencyCode), () => ValidExchangeRate() with { FromCurrencyCode = "USDD" }),

        // NewCurrency
        ["Currency MinorUnits over [Range(0, 12)]"] =
            ("/api/currencies", nameof(NewCurrency.MinorUnits), () => ValidCurrency() with { MinorUnits = 13 }),
        ["Currency Symbol over [StringLength(8)]"] =
            ("/api/currencies", nameof(NewCurrency.Symbol), () => ValidCurrency() with { Symbol = new string('$', 9) }),

        // NewContact
        ["Contact DisplayName over [StringLength(128)]"] =
            ("/api/contacts", nameof(NewContact.DisplayName), () => ValidContact() with { DisplayName = new string('x', 129) }),
        ["Contact Type undefined, [EnumDataType]"] =
            ("/api/contacts", nameof(NewContact.Type), () => ValidContact() with { Type = (ContactType)999 }),

        // NewTransactionTag
        ["TransactionTag Name over [StringLength(64)]"] =
            ("/api/transaction-tags", nameof(NewTransactionTag.Name), () => ValidTransactionTag() with { Name = new string('x', 65) }),
        ["TransactionTag Description over [StringLength(256)]"] =
            ("/api/transaction-tags", nameof(NewTransactionTag.Description), () => ValidTransactionTag() with { Description = new string('x', 257) }),

        // NewBudget
        ["Budget Name over [StringLength(64)]"] =
            ("/api/budgets", nameof(NewBudget.Name), () => ValidBudget() with { Name = new string('x', 65) }),
        ["Budget Description over [StringLength(256)]"] =
            ("/api/budgets", nameof(NewBudget.Description), () => ValidBudget() with { Description = new string('x', 257) }),

        // NewBudgetItem. The item has no Name or Description of its own any more (issue #75): its REQUIRED
        // transaction tag is its identity, so the boundary cases sit on that field.
        // AC 7 — omitting the tag is caught by [Required] in model validation, before the service runs.
        ["BudgetItem TransactionTagId missing, [Required]"] =
            ("/api/budget-items", nameof(NewBudgetItem.TransactionTagId), () => new
            {
                budgetId = Guid.NewGuid(),
                categoryType = Odyssey.Dtos.Finance.BudgetCategoryType.Expense,
                plannedAmount = 1000m,
            }),
        // AC 8 — [Required] does NOT reject Guid.Empty on a non-nullable Guid, so this is the service's own
        // rejection, keyed to the same field.
        ["BudgetItem TransactionTagId empty (service rule)"] =
            ("/api/budget-items", nameof(NewBudgetItem.TransactionTagId), () => ValidBudgetItem(Guid.NewGuid()) with { TransactionTagId = Guid.Empty }),
        // AC 9 — an unknown tag is a 400, not a 500, on the FK-free InMemory tier.
        ["BudgetItem TransactionTagId unknown (service rule)"] =
            ("/api/budget-items", nameof(NewBudgetItem.TransactionTagId), () => ValidBudgetItem(Guid.NewGuid())),
        ["BudgetItem CategoryType undefined, [EnumDataType]"] =
            ("/api/budget-items", nameof(NewBudgetItem.CategoryType), () => ValidBudgetItem(Guid.NewGuid()) with { CategoryType = (Odyssey.Dtos.Finance.BudgetCategoryType)999 }),
    };

    public static TheoryData<string> RejectedCaseNames => [.. RejectedCases.Keys];

    [Theory]
    [MemberData(nameof(RejectedCaseNames))]
    public async Task Create_ViolatingOneAnnotation_Returns400KeyedOnTheField(string caseName)
    {
        var (path, field, body) = RejectedCases[caseName];
        using var client = Factory.ClientFor(CreateClaims);

        var response = await client.PostAsJsonAsync(path, body());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await response.Content.ReadFromJsonAsync<ValidationProblemDetails>();
        Assert.Equal(StatusCodes.Status400BadRequest, problem?.Status);
        // An annotation failure is keyed on the property. A missing C# `required` member fails earlier, in
        // System.Text.Json, whose error is keyed on the body and names the member in its message instead.
        Assert.Contains(problem!.Errors, error =>
            error.Key.EndsWith(field, StringComparison.OrdinalIgnoreCase)
            || error.Value.Any(message => message.Contains(field, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>The class's shared host: every caller's claims travel per request (<see cref="SharedApiHostClients"/>).</summary>
    public sealed class Host() : ApiFactory([]);
}
