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
public class DtoValidationBoundaryTests
{
    private const string ActorUserId = "dto-validation-actor";

    // One actor holding every create claim the cases below need.
    private static readonly string[] CreateClaims =
    [
        PermissionClaims.AccountsRead, PermissionClaims.AccountsCreate,
        PermissionClaims.ContractsRead, PermissionClaims.ContractsCreate,
        PermissionClaims.InsuranceRead, PermissionClaims.InsuranceCreate, PermissionClaims.InsuranceUpdate,
        PermissionClaims.TransactionsRead, PermissionClaims.TransactionsCreate,
        PermissionClaims.BudgetsRead, PermissionClaims.BudgetsCreate,
        PermissionClaims.ContactsRead, PermissionClaims.ContactsCreate,
        PermissionClaims.CurrenciesRead, PermissionClaims.CurrenciesCreate,
        PermissionClaims.ExchangeRatesRead, PermissionClaims.ExchangeRatesCreate,
        PermissionClaims.TransactionTagsRead, PermissionClaims.TransactionTagsCreate,
    ];

    private sealed class ApiFactory(IReadOnlyCollection<string>? permissions)
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

    private static NewPolicyRenewal ValidRenewal() => new()
    {
        FromDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        ToDate = new DateTime(2026, 12, 31, 0, 0, 0, DateTimeKind.Utc),
        Premium = 100m,
        PremiumCurrencyCode = "USD",
        CoverageAmount = 10_000m,
        CoverageCurrencyCode = "USD",
    };

    private static UpdatePolicyRenewal ValidRenewalUpdate() => new()
    {
        FromDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        ToDate = new DateTime(2026, 12, 31, 0, 0, 0, DateTimeKind.Utc),
        Premium = 100m,
        PremiumCurrencyCode = "USD",
        CoverageAmount = 10_000m,
        CoverageCurrencyCode = "USD",
    };

    private static NewBudgetItem ValidBudgetItem(Guid budgetId) => new()
    {
        BudgetId = budgetId,
        Name = "Rent",
        CategoryType = Odyssey.Dtos.Finance.BudgetCategoryType.Expense,
        PlannedAmount = 1000m,
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

    private static async Task PostExpectingBadRequest(string path, object body)
    {
        await using var factory = new ApiFactory(CreateClaims);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(path, body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await response.Content.ReadFromJsonAsync<ValidationProblemDetails>();
        Assert.Equal(StatusCodes.Status400BadRequest, problem?.Status);
        Assert.NotEmpty(problem!.Errors);
    }

    // ── NewAccount ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateAccount_BaselineIsAccepted()
    {
        await using var factory = new ApiFactory(CreateClaims);
        await EnsureReferenceDataAsync(factory);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/accounts", ValidAccount());

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task CreateAccount_NameOverStringLength_Returns400()
    {
        var body = ValidAccount();
        body.Name = new string('x', 257); // [StringLength(256)]
        await PostExpectingBadRequest("/api/accounts", body);
    }

    [Fact]
    public async Task CreateAccount_NameAtExactStringLength_IsAccepted()
    {
        // The boundary is 256, not 255 — the max-length value must still be accepted.
        await using var factory = new ApiFactory(CreateClaims);
        await EnsureReferenceDataAsync(factory);
        using var client = factory.CreateClient();

        var body = ValidAccount();
        body.Name = new string('x', 256);
        var response = await client.PostAsJsonAsync("/api/accounts", body);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task CreateAccount_CurrencyCodeOverStringLength_Returns400()
    {
        var body = ValidAccount();
        body.CurrencyCode = "USDD"; // [StringLength(3)]
        await PostExpectingBadRequest("/api/accounts", body);
    }

    // ── NewContract ────────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateContract_BaselineIsAccepted()
    {
        await using var factory = new ApiFactory(CreateClaims);
        await EnsureReferenceDataAsync(factory);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/contracts", ValidContract());

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task CreateContract_EmptyName_Returns400()
    {
        var body = ValidContract();
        body.Name = ""; // [StringLength(256, MinimumLength = 1)]
        await PostExpectingBadRequest("/api/contracts", body);
    }

    [Fact]
    public async Task CreateContract_DescriptionOverStringLength_Returns400()
    {
        var body = ValidContract();
        body.Description = new string('x', 1025); // [StringLength(1024)]
        await PostExpectingBadRequest("/api/contracts", body);
    }

    [Fact]
    public async Task CreateContract_UndefinedEnumType_Returns400()
    {
        var body = ValidContract();
        body.Type = (ContractType)999; // [EnumDataType(typeof(ContractType))]
        await PostExpectingBadRequest("/api/contracts", body);
    }

    // ── NewInsurancePolicy ─────────────────────────────────────────────────────
    // No positive control here: a successful create needs a seeded insurer. Model-binding validation
    // runs before that lookup, so an over-length field still short-circuits to 400 — which is the
    // contract under test. The InsuranceApiTests cover the seeded happy path.

    [Fact]
    public async Task CreateInsurancePolicy_NameOverStringLength_Returns400() =>
        await PostExpectingBadRequest("/api/insurance-policies", new NewInsurancePolicy
        {
            Name = new string('x', 129), // [StringLength(128, MinimumLength = 1)]
            InsurerIds = [Guid.NewGuid()],
        });

    [Fact]
    public async Task CreateInsurancePolicy_LinkArrayOverCompileTimeCeiling_Returns400() =>
        // The compile-time ceiling on each link collection (issue #27 §9). It runs in model validation,
        // ahead of the service's live effective cap, so an over-length array never reaches the lookup.
        await PostExpectingBadRequest("/api/insurance-policies", new NewInsurancePolicy
        {
            Name = "Home contents",
            BeneficiaryIds = [.. Enumerable.Range(0, InsuranceLinkLimits.MaxLinksPerPolicy + 1).Select(_ => Guid.NewGuid())],
        });

    [Fact]
    public async Task CreateInsurancePolicy_NotesOverStringLength_Returns400() =>
        await PostExpectingBadRequest("/api/insurance-policies", new NewInsurancePolicy
        {
            Name = "Home contents",
            InsurerIds = [Guid.NewGuid()],
            Notes = new string('x', 1025), // [StringLength(1024)]
        });

    // ── NewTransaction ─────────────────────────────────────────────────────────
    // As above, a successful create needs a seeded account; the annotation 400 precedes that.

    [Fact]
    public async Task CreateTransaction_DescriptionOverStringLength_Returns400()
    {
        var body = ValidTransaction(Guid.NewGuid());
        body.Description = new string('x', 257); // [StringLength(256)]
        await PostExpectingBadRequest("/api/transactions", body);
    }

    [Fact]
    public async Task CreateTransaction_ExternalIdOverStringLength_Returns400()
    {
        var body = ValidTransaction(Guid.NewGuid());
        body.ExternalId = new string('x', 65); // [StringLength(64)]
        await PostExpectingBadRequest("/api/transactions", body);
    }

    [Fact]
    public async Task CreateTransaction_StatusCommentOverStringLength_Returns400()
    {
        var body = ValidTransaction(Guid.NewGuid());
        body.StatusComment = new string('x', 257); // [StringLength(256)]
        await PostExpectingBadRequest("/api/transactions", body);
    }

    // ── NewExchangeRate ────────────────────────────────────────────────────────
    // Rate carries an exclusive-minimum [Range], the one place a "0 is invalid" boundary is annotated.

    [Fact]
    public async Task CreateExchangeRate_BaselineIsAccepted()
    {
        await using var factory = new ApiFactory(CreateClaims);
        await EnsureReferenceDataAsync(factory);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/exchange-rates", ValidExchangeRate());

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task CreateExchangeRate_ZeroRate_Returns400()
    {
        var body = ValidExchangeRate();
        body.Rate = 0m; // [Range(..., MinimumIsExclusive = true)] — zero is rejected, not accepted.
        await PostExpectingBadRequest("/api/exchange-rates", body);
    }

    [Fact]
    public async Task CreateExchangeRate_FromCurrencyOverStringLength_Returns400()
    {
        var body = ValidExchangeRate();
        body.FromCurrencyCode = "USDD"; // [StringLength(3)]
        await PostExpectingBadRequest("/api/exchange-rates", body);
    }

    // ── NewCurrency ────────────────────────────────────────────────────────────
    // MinorUnits carries a bounded [Range(0, 12)] — exercise both the reject and the exact-max accept.

    [Fact]
    public async Task CreateCurrency_BaselineIsAccepted()
    {
        await using var factory = new ApiFactory(CreateClaims);
        await EnsureReferenceDataAsync(factory);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/currencies", ValidCurrency());

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task CreateCurrency_MinorUnitsOverRange_Returns400()
    {
        var body = ValidCurrency();
        body.MinorUnits = 13; // [Range(0, 12)]
        await PostExpectingBadRequest("/api/currencies", body);
    }

    [Fact]
    public async Task CreateCurrency_MinorUnitsAtExactMax_IsAccepted()
    {
        // The boundary is 12, not 11 — the max value must still be accepted.
        await using var factory = new ApiFactory(CreateClaims);
        await EnsureReferenceDataAsync(factory);
        using var client = factory.CreateClient();

        var body = ValidCurrency();
        body.MinorUnits = 12;
        var response = await client.PostAsJsonAsync("/api/currencies", body);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task CreateCurrency_SymbolOverStringLength_Returns400()
    {
        var body = ValidCurrency();
        body.Symbol = new string('$', 9); // [StringLength(8)]
        await PostExpectingBadRequest("/api/currencies", body);
    }

    // ── NewContact ────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateContact_BaselineIsAccepted()
    {
        await using var factory = new ApiFactory(CreateClaims);
        await EnsureReferenceDataAsync(factory);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/contacts", ValidContact());

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task CreateContact_DisplayNameOverStringLength_Returns400()
    {
        var body = ValidContact();
        body.DisplayName = new string('x', 129); // [StringLength(128)]
        await PostExpectingBadRequest("/api/contacts", body);
    }

    [Fact]
    public async Task CreateContact_UndefinedEnumType_Returns400()
    {
        var body = ValidContact();
        body.Type = (ContactType)999; // [EnumDataType(typeof(ContactType))]
        await PostExpectingBadRequest("/api/contacts", body);
    }

    // ── NewTransactionTag ──────────────────────────────────────────────────────

    [Fact]
    public async Task CreateTransactionTag_BaselineIsAccepted()
    {
        await using var factory = new ApiFactory(CreateClaims);
        await EnsureReferenceDataAsync(factory);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/transaction-tags", ValidTransactionTag());

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task CreateTransactionTag_NameOverStringLength_Returns400()
    {
        var body = ValidTransactionTag();
        body.Name = new string('x', 65); // [StringLength(64)]
        await PostExpectingBadRequest("/api/transaction-tags", body);
    }

    [Fact]
    public async Task CreateTransactionTag_DescriptionOverStringLength_Returns400()
    {
        var body = ValidTransactionTag();
        body.Description = new string('x', 257); // [StringLength(256)]
        await PostExpectingBadRequest("/api/transaction-tags", body);
    }

    // ── NewBudget ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateBudget_BaselineIsAccepted()
    {
        await using var factory = new ApiFactory(CreateClaims);
        await EnsureReferenceDataAsync(factory);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/budgets", ValidBudget());

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task CreateBudget_NameOverStringLength_Returns400()
    {
        var body = ValidBudget();
        body.Name = new string('x', 65); // [StringLength(64)]
        await PostExpectingBadRequest("/api/budgets", body);
    }

    [Fact]
    public async Task CreateBudget_DescriptionOverStringLength_Returns400()
    {
        var body = ValidBudget();
        body.Description = new string('x', 257); // [StringLength(256)]
        await PostExpectingBadRequest("/api/budgets", body);
    }

    // ── NewBudgetItem ──────────────────────────────────────────────────────────
    // No positive control: a successful create needs a seeded budget (FK). Model-binding validation
    // runs before that lookup, so an annotation violation still short-circuits to 400.

    [Fact]
    public async Task CreateBudgetItem_NameOverStringLength_Returns400()
    {
        var body = ValidBudgetItem(Guid.NewGuid());
        body.Name = new string('x', 65); // [StringLength(64)]
        await PostExpectingBadRequest("/api/budget-items", body);
    }

    [Fact]
    public async Task CreateBudgetItem_UndefinedEnumCategory_Returns400()
    {
        var body = ValidBudgetItem(Guid.NewGuid());
        body.CategoryType = (Odyssey.Dtos.Finance.BudgetCategoryType)999; // [EnumDataType]
        await PostExpectingBadRequest("/api/budget-items", body);
    }

    // ── NewPolicyRenewal ───────────────────────────────────────────────────────
    // The currency codes carry [StringLength(3, Min = 3)] and Notes a [StringLength(512)].
    // Premium/CoverageAmount deliberately carry NO lower bound — a refund or a correction to a
    // period already recorded is a real figure — so the two cases below are the negative controls
    // for that: they assert a negative amount gets PAST model validation, which the 404 (the policy
    // id is unseeded, and that lookup runs after binding) is the only available proof of.
    // No positive control: a successful add needs a seeded policy; the annotation 400 precedes it.

    private static readonly string RenewalsPath = $"/api/insurance-policies/{Guid.NewGuid()}/renewals";

    private static async Task PostExpectingPastModelValidation(string path, object body)
    {
        await using var factory = new ApiFactory(CreateClaims);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(path, body);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task AddRenewal_NegativePremium_PassesModelValidation()
    {
        var body = ValidRenewal();
        body.Premium = -1m; // no [Range] — negatives are a supported premium figure
        await PostExpectingPastModelValidation(RenewalsPath, body);
    }

    [Fact]
    public async Task AddRenewal_NegativeCoverageAmount_PassesModelValidation()
    {
        var body = ValidRenewal();
        body.CoverageAmount = -1m; // no [Range] — a correcting term may reduce recorded cover
        await PostExpectingPastModelValidation(RenewalsPath, body);
    }

    // ── UpdatePolicyRenewal ────────────────────────────────────────────────────
    // A SEPARATE record with its own annotations, so nothing above covers it: the two types have
    // drifted before and only a test on this path would notice. Same shape — the amounts carry no
    // lower bound, the currency codes and Notes do carry limits.

    private static readonly string RenewalPath =
        $"/api/insurance-policies/{Guid.NewGuid()}/renewals/{Guid.NewGuid()}";

    private static async Task PutExpectingBadRequest(string path, object body)
    {
        await using var factory = new ApiFactory(CreateClaims);
        using var client = factory.CreateClient();

        var response = await client.PutAsJsonAsync(path, body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await response.Content.ReadFromJsonAsync<ValidationProblemDetails>();
        Assert.Equal(StatusCodes.Status400BadRequest, problem?.Status);
        Assert.NotEmpty(problem!.Errors);
    }

    private static async Task PutExpectingPastModelValidation(string path, object body)
    {
        await using var factory = new ApiFactory(CreateClaims);
        using var client = factory.CreateClient();

        var response = await client.PutAsJsonAsync(path, body);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task UpdateRenewal_NegativePremium_PassesModelValidation()
    {
        var body = ValidRenewalUpdate();
        body.Premium = -1m; // no [Range] — negatives are a supported premium figure
        await PutExpectingPastModelValidation(RenewalPath, body);
    }

    [Fact]
    public async Task UpdateRenewal_NegativeCoverageAmount_PassesModelValidation()
    {
        var body = ValidRenewalUpdate();
        body.CoverageAmount = -1m; // no [Range] — a correcting term may reduce recorded cover
        await PutExpectingPastModelValidation(RenewalPath, body);
    }

    [Fact]
    public async Task UpdateRenewal_PremiumCurrencyTooShort_Returns400()
    {
        // The positive control for the two above: model validation IS running on this path, so their
        // 404 means the amount got past it rather than that nothing was checked at all.
        var body = ValidRenewalUpdate();
        body.PremiumCurrencyCode = "US"; // [StringLength(3, MinimumLength = 3)]
        await PutExpectingBadRequest(RenewalPath, body);
    }

    [Fact]
    public async Task UpdateRenewal_NotesOverStringLength_Returns400()
    {
        var body = ValidRenewalUpdate();
        body.Notes = new string('x', 513); // [StringLength(512)]
        await PutExpectingBadRequest(RenewalPath, body);
    }

    [Fact]
    public async Task AddRenewal_PremiumCurrencyTooShort_Returns400()
    {
        var body = ValidRenewal();
        body.PremiumCurrencyCode = "US"; // [StringLength(3, MinimumLength = 3)]
        await PostExpectingBadRequest(RenewalsPath, body);
    }

    [Fact]
    public async Task AddRenewal_NotesOverStringLength_Returns400()
    {
        var body = ValidRenewal();
        body.Notes = new string('x', 513); // [StringLength(512)]
        await PostExpectingBadRequest(RenewalsPath, body);
    }
}
