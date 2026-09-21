using System.Net;
using System.Net.Http.Json;
using Odyssey.Context;
using Odyssey.Dtos;
using Odyssey.Context.Authorization;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos.Finance;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;
using Odyssey.Api.Tests.Infrastructure;
using ContextAccountType = Odyssey.Context.AccountType;
// Both halves of the aligned pair are in scope here (Odyssey.Context for the direct seeds,
// Odyssey.Dtos.Finance for the wire), so each wire type is named explicitly.
using ContractType = Odyssey.Dtos.Finance.ContractType;
using TermKind = Odyssey.Dtos.Finance.TermKind;
using TermValueUnit = Odyssey.Dtos.Finance.TermValueUnit;
using Interval = Odyssey.Dtos.Finance.Interval;
using TermDirection = Odyssey.Dtos.Finance.TermDirection;

namespace Odyssey.Api.Tests;

/// <summary>
/// The five contract-scoped term endpoints (issue #135), over real HTTP against the shared
/// <c>OdysseyApiFactory</c> fixture.
/// </summary>
/// <remarks>
/// Two things this tier reaches that the unit tier cannot: the claim gates (both reads on
/// <c>contracts.read</c>, all three writes on <c>contracts.update</c> — and NOT on the account
/// module's <c>accounts.terms.*</c>), and the application-code delete cascade, which exists precisely
/// because the EF InMemory provider this tier runs on enforces no database cascade.
/// </remarks>
public class ContractTermsApiTests
{
    private const string ActorUserId = "contract-terms-actor-id";
    private const string Path = "/api/contracts";

    private static readonly DateTime FixedToday = new(2026, 6, 15, 8, 0, 0, DateTimeKind.Utc);

    private static readonly string[] ReadOnly = [PermissionClaims.ContractsRead];

    private static readonly string[] ReadWrite =
    [
        PermissionClaims.ContractsRead, PermissionClaims.ContractsCreate,
        PermissionClaims.ContractsUpdate, PermissionClaims.ContractsDelete,
    ];

    /// <summary>
    /// Every claim in the vocabulary EXCEPT the two contract ones the endpoints gate on — including
    /// <c>accounts.terms.read</c>/<c>.write</c>, so a principal that may write account terms is shown
    /// to have no reach into a contract's.
    /// </summary>
    private static readonly string[] EverythingButContracts =
        [.. RolePermissions.AllClaims.Where(c =>
            c is not (PermissionClaims.ContractsRead or PermissionClaims.ContractsUpdate))];

    // ── Create ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Post_ValidFee_Returns201WithContractOwnerAndLocation()
    {
        await using var factory = await NewFactoryAsync(ReadWrite);
        using var client = factory.CreateClient();
        var contractId = await CreateContractAsync(client);

        var response = await client.PostAsJsonAsync(Terms(contractId), Rent(14500m, new DateTime(2026, 10, 1)));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Contains($"{Path}/{contractId}/terms", response.Headers.Location!.ToString(), StringComparison.OrdinalIgnoreCase);

        var created = await response.Content.ReadFromJsonAsync<ExistingTerm>();
        Assert.Equal(contractId, created!.ContractId);
        Assert.Null(created.AccountId);
        Assert.Equal("Monthly rent", created.Label);
        Assert.Equal("EUR", created.CurrencyCode);
    }

    [Fact]
    public async Task Post_ToAMissingContract_Returns404()
    {
        await using var factory = await NewFactoryAsync(ReadWrite);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(Terms(Guid.NewGuid()), Rent(1m, new DateTime(2026, 1, 1)));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>
    /// AC 8 — the owner comes from the ROUTE and from nowhere else. A body carrying owner ids, a term
    /// id, a label key or a created-at is accepted, and every one of those values is ignored: NewTerm
    /// has no such properties, so there is nothing for them to bind to.
    /// </summary>
    [Fact]
    public async Task Post_BodyCarryingOwnerIdsAndDerivedFields_IgnoresThemEntirely()
    {
        await using var factory = await NewFactoryAsync(ReadWrite);
        using var client = factory.CreateClient();
        var contractId = await CreateContractAsync(client);
        var otherContractId = await CreateContractAsync(client, name: "Other");
        var accountId = await SeedAccountAsync(factory);
        var forgedTermId = Guid.NewGuid();

        var response = await client.PostAsJsonAsync(Terms(contractId), new
        {
            accountId,
            contractId = otherContractId,
            termId = forgedTermId,
            labelKey = "forged-key",
            createdAtUtc = new DateTime(1990, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            termKind = TermKind.Fee,
            label = "  Monthly   RENT  ",
            valueUnit = TermValueUnit.Amount,
            value = 14500m,
            currencyCode = "EUR",
            effectiveFrom = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc),
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<ExistingTerm>();

        Assert.Equal(contractId, created!.ContractId);
        Assert.Null(created.AccountId);
        Assert.NotEqual(forgedTermId, created.TermId);
        // The label is the server's normalization of what was submitted, and the key is derived from
        // it — never the supplied one.
        Assert.Equal("Monthly RENT", created.Label);

        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        var row = await context.Terms.SingleAsync();
        Assert.Equal("monthly rent", row.LabelKey);
        Assert.NotEqual(new DateTime(1990, 1, 1), row.CreatedAtUtc);
        // Neither the named account nor the other contract gained anything.
        Assert.Empty(await context.Terms.Where(t => t.AccountId != null).ToListAsync());
        Assert.Empty(await context.Terms.Where(t => t.ContractId == otherContractId).ToListAsync());
    }

    [Fact]
    public async Task Post_ExpectedReturn_Returns400()
    {
        await using var factory = await NewFactoryAsync(ReadWrite);
        using var client = factory.CreateClient();
        var contractId = await CreateContractAsync(client);

        var response = await client.PostAsJsonAsync(Terms(contractId), new NewTerm
        {
            TermKind = TermKind.ExpectedReturn,
            ValueUnit = TermValueUnit.Percentage,
            Value = 0.07m,
            EffectiveFrom = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData(ContractType.Employment)]
    [InlineData(ContractType.Service)]
    [InlineData(ContractType.Rental)]
    [InlineData(ContractType.Other)]
    public async Task Post_FeeAndInterestRate_SucceedOnEveryContractType(ContractType type)
    {
        await using var factory = await NewFactoryAsync(ReadWrite);
        using var client = factory.CreateClient();
        var contractId = await CreateContractAsync(client, type: type);

        Assert.Equal(HttpStatusCode.Created,
            (await client.PostAsJsonAsync(Terms(contractId), Rent(14500m, new DateTime(2026, 10, 1)))).StatusCode);
        Assert.Equal(HttpStatusCode.Created,
            (await client.PostAsJsonAsync(Terms(contractId), InterestRate(0.0325m, new DateTime(2026, 10, 1)))).StatusCode);
    }

    /// <summary>AC 7 — an amount on a contract must name its currency; on an account it still defaults.</summary>
    [Fact]
    public async Task Post_AmountWithoutCurrency_Returns400OnAContractButNotOnAnAccount()
    {
        await using var factory = await NewFactoryAsync(
            [.. ReadWrite, PermissionClaims.AccountsTermsWrite, PermissionClaims.AccountsTermsRead]);
        using var client = factory.CreateClient();
        var contractId = await CreateContractAsync(client);
        var accountId = await SeedAccountAsync(factory);

        var refused = await client.PostAsJsonAsync(Terms(contractId), Rent(14500m, new DateTime(2026, 10, 1), currency: null));
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        var problem = await refused.Content.ReadFromJsonAsync<ApiProblemBody>();
        Assert.True(problem!.Errors!.ContainsKey(nameof(NewTerm.CurrencyCode)));

        var accepted = await client.PostAsJsonAsync(Terms(contractId), Rent(14500m, new DateTime(2026, 10, 1)));
        Assert.Equal(HttpStatusCode.Created, accepted.StatusCode);

        // The account path is unchanged: no currency in the body, and the account's own is used.
        var onAccount = await client.PostAsJsonAsync($"/api/accounts/{accountId}/terms", new NewTerm
        {
            TermKind = TermKind.Fee,
            Label = "Monthly fee",
            ValueUnit = TermValueUnit.Amount,
            Value = 4m,
            EffectiveFrom = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        });
        Assert.Equal(HttpStatusCode.Created, onAccount.StatusCode);
        Assert.Equal("USD", (await onAccount.Content.ReadFromJsonAsync<ExistingTerm>())!.CurrencyCode);
    }

    /// <summary>AC 10 — the duplicate guard is over the folded series key, within this contract.</summary>
    [Fact]
    public async Task Post_DuplicateSeriesEntry_Returns409WhileADifferentLabelIsAccepted()
    {
        await using var factory = await NewFactoryAsync(ReadWrite);
        using var client = factory.CreateClient();
        var contractId = await CreateContractAsync(client);
        var date = new DateTime(2026, 10, 1);

        Assert.Equal(HttpStatusCode.Created,
            (await client.PostAsJsonAsync(Terms(contractId), Rent(14500m, date))).StatusCode);

        var collision = await client.PostAsJsonAsync(Terms(contractId), Rent(15000m, date, label: "  monthly   RENT  "));
        Assert.Equal(HttpStatusCode.Conflict, collision.StatusCode);

        Assert.Equal(HttpStatusCode.Created,
            (await client.PostAsJsonAsync(Terms(contractId), Rent(450m, date, label: "Service charge"))).StatusCode);
    }

    /// <summary>
    /// <b>An archived contract still takes a term.</b> The closing fee on a lease that has ended is
    /// exactly the entry written after the agreement is filed away, so archival — which hides the
    /// contract from the default list — never refuses the write. The history reads either way.
    /// </summary>
    [Fact]
    public async Task Post_OnAnArchivedContract_Succeeds_AndTheHistoryStillReads()
    {
        await using var factory = await NewFactoryAsync(ReadWrite);
        using var client = factory.CreateClient();
        var contractId = await CreateContractAsync(client, start: FixedToday.AddDays(-30), end: FixedToday.AddDays(-1));

        Assert.Equal(HttpStatusCode.Created,
            (await client.PostAsJsonAsync(Terms(contractId), Rent(14500m, new DateTime(2026, 1, 1)))).StatusCode);

        await ArchiveAsync(client, contractId, start: FixedToday.AddDays(-30), end: FixedToday.AddDays(-1));

        var added = await client.PostAsJsonAsync(Terms(contractId), Rent(15000m, new DateTime(2026, 7, 1)));
        Assert.Equal(HttpStatusCode.Created, added.StatusCode);

        var history = await client.GetAsync(Terms(contractId));
        Assert.Equal(HttpStatusCode.OK, history.StatusCode);
        Assert.Equal(2, (await history.Content.ReadFromJsonAsync<List<ExistingTerm>>())!.Count);
    }

    /// <summary>AC 12 — the cap refuses a create and never an update.</summary>
    [Fact]
    public async Task Post_BeyondTheCap_Returns422WhilePutStillSucceeds()
    {
        await using var factory = await NewFactoryAsync(ReadWrite);
        using var client = factory.CreateClient();
        await SystemSettingsSeed.SetAsync(factory.Services, SystemSettingsKeys.ContractMaxTermsPerContract, "2");
        var contractId = await CreateContractAsync(client);

        var first = await PostTermAsync(client, contractId, Rent(14500m, new DateTime(2026, 1, 1)));
        await PostTermAsync(client, contractId, Rent(14800m, new DateTime(2026, 7, 1)));

        var overCap = await client.PostAsJsonAsync(Terms(contractId), Rent(15000m, new DateTime(2027, 1, 1)));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, overCap.StatusCode);
        Assert.Contains("2", await overCap.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        var put = await client.PutAsJsonAsync(
            $"{Terms(contractId)}/{first.TermId}", Rent(14600m, new DateTime(2026, 1, 1)));
        Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);
    }

    // ── Reads ────────────────────────────────────────────────────────────────

    /// <summary>AC 2 — newest effective first, and no account terms.</summary>
    [Fact]
    public async Task Get_History_IsNewestFirstAndCarriesNoAccountTerms()
    {
        await using var factory = await NewFactoryAsync([.. ReadWrite, PermissionClaims.AccountsTermsWrite]);
        using var client = factory.CreateClient();
        var contractId = await CreateContractAsync(client);
        var accountId = await SeedAccountAsync(factory);

        await PostTermAsync(client, contractId, Rent(14000m, new DateTime(2024, 1, 1)));
        await PostTermAsync(client, contractId, Rent(14500m, new DateTime(2025, 1, 1)));
        (await client.PostAsJsonAsync($"/api/accounts/{accountId}/terms", new NewTerm
        {
            TermKind = TermKind.Fee, Label = "Monthly fee", ValueUnit = TermValueUnit.Amount, Value = 4m,
            EffectiveFrom = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        })).EnsureSuccessStatusCode();

        var history = (await client.GetFromJsonAsync<List<ExistingTerm>>(Terms(contractId)))!;

        Assert.Equal(2, history.Count);
        Assert.Equal(new DateTime(2025, 1, 1), history[0].EffectiveFrom);
        Assert.All(history, t => Assert.Equal(contractId, t.ContractId));
        Assert.All(history, t => Assert.Null(t.AccountId));
    }

    /// <summary>AC 3 — one entry per series, the latest on or before today.</summary>
    [Fact]
    public async Task Get_Current_ReturnsOneEntryPerSeries()
    {
        await using var factory = await NewFactoryAsync(ReadWrite);
        using var client = factory.CreateClient();
        var contractId = await CreateContractAsync(client);

        await PostTermAsync(client, contractId, Rent(14000m, new DateTime(2024, 1, 1)));
        await PostTermAsync(client, contractId, Rent(14500m, new DateTime(2025, 1, 1)));
        await PostTermAsync(client, contractId, Rent(99999m, FixedToday.AddYears(5)));
        await PostTermAsync(client, contractId, Rent(450m, new DateTime(2025, 1, 1), label: "Service charge"));

        var current = (await client.GetFromJsonAsync<List<CurrentTerm>>($"{Terms(contractId)}/current"))!;

        Assert.Equal(2, current.Count);
        Assert.Equal(14500m, current.Single(t => t.Label == "Monthly rent").Value);
        Assert.Equal(450m, current.Single(t => t.Label == "Service charge").Value);
    }

    /// <summary>AC 4 — the two query filters behave as on the account endpoint.</summary>
    [Fact]
    public async Task Get_History_FiltersByKindAndAsOf()
    {
        await using var factory = await NewFactoryAsync(ReadWrite);
        using var client = factory.CreateClient();
        var contractId = await CreateContractAsync(client);

        await PostTermAsync(client, contractId, Rent(14000m, new DateTime(2024, 1, 1)));
        await PostTermAsync(client, contractId, InterestRate(0.05m, new DateTime(2026, 1, 1)));

        Assert.Single((await client.GetFromJsonAsync<List<ExistingTerm>>($"{Terms(contractId)}?kind={TermKind.Fee}"))!);
        Assert.Single((await client.GetFromJsonAsync<List<ExistingTerm>>($"{Terms(contractId)}?asOf=2024-06-01"))!);

        // An unbindable kind is model-validation's 400, before any query runs.
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"{Terms(contractId)}?kind=NotAKind")).StatusCode);
    }

    [Fact]
    public async Task Get_OnAMissingContract_Returns404()
    {
        await using var factory = await NewFactoryAsync(ReadOnly);
        using var client = factory.CreateClient();
        var missing = Guid.NewGuid();

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(Terms(missing))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"{Terms(missing)}/current")).StatusCode);
    }

    /// <summary>AC 15 — absence is healthy: an empty array and a zero count, both 200.</summary>
    [Fact]
    public async Task Get_OnAContractWithNoTerms_IsEmptyAndSuccessful()
    {
        await using var factory = await NewFactoryAsync(ReadWrite);
        using var client = factory.CreateClient();
        var contractId = await CreateContractAsync(client);

        Assert.Empty((await client.GetFromJsonAsync<List<ExistingTerm>>(Terms(contractId)))!);
        Assert.Empty((await client.GetFromJsonAsync<List<CurrentTerm>>($"{Terms(contractId)}/current"))!);

        var detail = (await client.GetFromJsonAsync<ExistingContract>($"{Path}/{contractId}"))!;
        Assert.Empty(detail.CurrentTerms);

        var list = (await client.GetFromJsonAsync<PagedResult<ContractListItem>>(Path))!;
        Assert.Equal(0, list.Items.Single(c => c.ContractId == contractId).TermCount);
    }

    /// <summary>AC 15 — the two contract reads carry the term projections.</summary>
    [Fact]
    public async Task Get_Contract_CarriesCurrentTermsAndTheListCarriesRowCount()
    {
        await using var factory = await NewFactoryAsync(ReadWrite);
        using var client = factory.CreateClient();
        var contractId = await CreateContractAsync(client);

        await PostTermAsync(client, contractId, Rent(14000m, new DateTime(2024, 1, 1)));
        await PostTermAsync(client, contractId, Rent(14500m, new DateTime(2025, 1, 1)));
        await PostTermAsync(client, contractId, Rent(450m, new DateTime(2025, 1, 1), label: "Service charge"));

        var detail = (await client.GetFromJsonAsync<ExistingContract>($"{Path}/{contractId}"))!;
        Assert.Equal(2, detail.CurrentTerms.Count);
        Assert.Equal(14500m, detail.CurrentTerms.Single(t => t.Label == "Monthly rent").Value);

        var list = (await client.GetFromJsonAsync<PagedResult<ContractListItem>>(Path))!;
        // ROWS, not values in force — three entries across two series.
        Assert.Equal(3, list.Items.Single(c => c.ContractId == contractId).TermCount);
    }

    // ── Update / delete, and cross-owner addressing ──────────────────────────

    [Fact]
    public async Task Put_ReplacesTheEntryAndKeepsItsOwner()
    {
        await using var factory = await NewFactoryAsync(ReadWrite);
        using var client = factory.CreateClient();
        var contractId = await CreateContractAsync(client);
        var term = await PostTermAsync(client, contractId, Rent(14500m, new DateTime(2026, 10, 1)));

        var put = await client.PutAsJsonAsync(
            $"{Terms(contractId)}/{term.TermId}", Rent(15000m, new DateTime(2026, 10, 1)));
        Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);

        var reloaded = (await client.GetFromJsonAsync<List<ExistingTerm>>(Terms(contractId)))!.Single();
        Assert.Equal(term.TermId, reloaded.TermId);
        Assert.Equal(15000m, reloaded.Value);
        Assert.Equal(contractId, reloaded.ContractId);
        Assert.Null(reloaded.AccountId);
    }

    [Fact]
    public async Task Delete_RemovesTheEntryAndLeavesTheContract()
    {
        await using var factory = await NewFactoryAsync(ReadWrite);
        using var client = factory.CreateClient();
        var contractId = await CreateContractAsync(client);
        var term = await PostTermAsync(client, contractId, Rent(14500m, new DateTime(2026, 10, 1)));

        Assert.Equal(HttpStatusCode.NoContent,
            (await client.DeleteAsync($"{Terms(contractId)}/{term.TermId}")).StatusCode);

        Assert.Empty((await client.GetFromJsonAsync<List<ExistingTerm>>(Terms(contractId)))!);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"{Path}/{contractId}")).StatusCode);
    }

    /// <summary>
    /// AC 9 — a term id belonging to an account, or to a different contract, is a 404 and the row is
    /// untouched. Not a 403 and not a silent success, so the endpoint is no existence oracle across
    /// owners.
    /// </summary>
    [Fact]
    public async Task PutAndDelete_WithAForeignTermId_Return404AndChangeNothing()
    {
        await using var factory = await NewFactoryAsync([.. ReadWrite, PermissionClaims.AccountsTermsWrite, PermissionClaims.AccountsTermsRead]);
        using var client = factory.CreateClient();
        var contractId = await CreateContractAsync(client);
        var otherContractId = await CreateContractAsync(client, name: "Other");
        var accountId = await SeedAccountAsync(factory);

        var accountTerm = await (await client.PostAsJsonAsync($"/api/accounts/{accountId}/terms", new NewTerm
        {
            TermKind = TermKind.Fee, Label = "Monthly fee", ValueUnit = TermValueUnit.Amount, Value = 4m,
            EffectiveFrom = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        })).Content.ReadFromJsonAsync<ExistingTerm>();

        var otherTerm = await PostTermAsync(client, otherContractId, Rent(14500m, new DateTime(2026, 10, 1)));

        foreach (var foreignId in new[] { accountTerm!.TermId, otherTerm.TermId })
        {
            Assert.Equal(HttpStatusCode.NotFound,
                (await client.PutAsJsonAsync($"{Terms(contractId)}/{foreignId}", Rent(99m, new DateTime(2026, 10, 1)))).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound,
                (await client.DeleteAsync($"{Terms(contractId)}/{foreignId}")).StatusCode);
        }

        Assert.Equal(4m, (await client.GetFromJsonAsync<List<ExistingTerm>>($"/api/accounts/{accountId}/terms"))!.Single().Value);
        Assert.Equal(14500m, (await client.GetFromJsonAsync<List<ExistingTerm>>(Terms(otherContractId)))!.Single().Value);
    }

    /// <summary>
    /// AC 13, the InMemory half — the delete cascade is simulated in application code, because the
    /// provider this tier runs on enforces none. This is the assertion that fails if
    /// <c>ContractService.Delete</c> loses its <c>.Include(c =&gt; c.Terms)</c>.
    /// </summary>
    [Fact]
    public async Task DeleteContract_RemovesItsTermsAndLeavesAccountTermsUntouched()
    {
        await using var factory = await NewFactoryAsync([.. ReadWrite, PermissionClaims.AccountsTermsWrite, PermissionClaims.AccountsTermsRead]);
        using var client = factory.CreateClient();
        var contractId = await CreateContractAsync(client);
        var survivingContractId = await CreateContractAsync(client, name: "Survivor");
        var accountId = await SeedAccountAsync(factory);

        await PostTermAsync(client, contractId, Rent(14500m, new DateTime(2026, 10, 1)));
        await PostTermAsync(client, contractId, Rent(450m, new DateTime(2026, 10, 1), label: "Service charge"));
        await PostTermAsync(client, survivingContractId, Rent(9000m, new DateTime(2026, 10, 1)));
        (await client.PostAsJsonAsync($"/api/accounts/{accountId}/terms", new NewTerm
        {
            TermKind = TermKind.Fee, Label = "Monthly fee", ValueUnit = TermValueUnit.Amount, Value = 4m,
            EffectiveFrom = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        })).EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"{Path}/{contractId}")).StatusCode);

        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        Assert.Empty(await context.Terms.Where(t => t.ContractId == contractId).ToListAsync());
        Assert.Single(await context.Terms.Where(t => t.ContractId == survivingContractId).ToListAsync());
        Assert.Single(await context.Terms.Where(t => t.AccountId == accountId).ToListAsync());
    }

    // ── Authorization (AC 5) ─────────────────────────────────────────────────

    [Fact]
    public async Task Endpoints_Unauthenticated_ReturnUnauthorized()
    {
        await using var factory = await NewFactoryAsync(permissions: null);
        using var client = factory.CreateClient();
        var id = Guid.NewGuid();

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(Terms(id))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync($"{Terms(id)}/current")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.PostAsJsonAsync(Terms(id), Rent(1m, new DateTime(2026, 1, 1)))).StatusCode);
    }

    /// <summary>
    /// Every claim EXCEPT the gating one, including the account module's term claims — a principal
    /// that may write account terms has no reach into a contract's, which is the privilege-escalation
    /// the route-only owner exists to prevent.
    /// </summary>
    [Fact]
    public async Task Reads_WithEveryClaimButContractsRead_ReturnForbidden()
    {
        await using var factory = await NewFactoryAsync(EverythingButContracts);
        using var client = factory.CreateClient();
        var id = Guid.NewGuid();

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(Terms(id))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"{Terms(id)}/current")).StatusCode);
    }

    [Fact]
    public async Task Writes_WithEveryClaimButContractsUpdate_ReturnForbidden()
    {
        await using var factory = await NewFactoryAsync(EverythingButContracts);
        using var client = factory.CreateClient();
        var id = Guid.NewGuid();

        Assert.Equal(HttpStatusCode.Forbidden,
            (await client.PostAsJsonAsync(Terms(id), Rent(1m, new DateTime(2026, 1, 1)))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await client.PutAsJsonAsync($"{Terms(id)}/{Guid.NewGuid()}", Rent(1m, new DateTime(2026, 1, 1)))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await client.DeleteAsync($"{Terms(id)}/{Guid.NewGuid()}")).StatusCode);
    }

    /// <summary>
    /// The reads need <c>contracts.read</c> ALONE — not the account module's term claims, and not
    /// <c>contracts.update</c>.
    /// </summary>
    [Fact]
    public async Task Reads_WithContractsReadAlone_Succeed()
    {
        await using var writeFactory = await NewFactoryAsync(ReadWrite);
        using var writer = writeFactory.CreateClient();
        var contractId = await CreateContractAsync(writer);

        await using var factory = await NewFactoryAsync(ReadOnly);
        using var client = factory.CreateClient();
        var readableId = await SeedContractDirectlyAsync(factory);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(Terms(readableId))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"{Terms(readableId)}/current")).StatusCode);

        // …and a read-only principal still cannot write.
        Assert.Equal(HttpStatusCode.Forbidden,
            (await client.PostAsJsonAsync(Terms(readableId), Rent(1m, new DateTime(2026, 1, 1)))).StatusCode);
    }

    // ── The list row's "money in" marker (issue #159) ────────────────────────

    /// <summary>
    /// A contract whose in-force fee is <c>Incoming</c> is marked on the LIST row, so "this file is
    /// money in" is legible without expanding the record.
    /// </summary>
    [Fact]
    public async Task List_MarksAContractWhoseInForceTermIsIncoming()
    {
        await using var factory = await NewFactoryAsync(ReadWrite);
        using var client = factory.CreateClient();
        var contractId = await CreateContractAsync(client, name: "Sublet income");

        await PostTermAsync(client, contractId, Incoming(2250m, new DateTime(2026, 1, 1)));

        Assert.True(await HasIncomingAsync(client, contractId));
    }

    /// <summary>An ordinary outgoing contract is not marked — the marker means something only if most rows lack it.</summary>
    [Fact]
    public async Task List_DoesNotMarkAnOutgoingOnlyContract()
    {
        await using var factory = await NewFactoryAsync(ReadWrite);
        using var client = factory.CreateClient();
        var contractId = await CreateContractAsync(client);

        await PostTermAsync(client, contractId, Rent(14500m, new DateTime(2026, 1, 1)));

        Assert.False(await HasIncomingAsync(client, contractId));
    }

    /// <summary>
    /// THE ONE THAT A CORRELATED <c>Any()</c> WOULD GET WRONG. An incoming entry superseded by a later
    /// outgoing entry of the SAME series is no longer in force, so the row must not be marked: the flag
    /// is read after the series collapse, exactly as the run rate and the record card read direction.
    /// </summary>
    [Fact]
    public async Task List_DoesNotMarkAnIncomingTermThatHasBeenSuperseded()
    {
        await using var factory = await NewFactoryAsync(ReadWrite);
        using var client = factory.CreateClient();
        var contractId = await CreateContractAsync(client);

        // Same label, so both entries are one series; the later effective date wins.
        await PostTermAsync(client, contractId, Incoming(2250m, new DateTime(2026, 1, 1), label: "Rent"));
        await PostTermAsync(client, contractId, Rent(1800m, new DateTime(2026, 3, 1), label: "Rent"));

        Assert.False(await HasIncomingAsync(client, contractId));
    }

    /// <summary>
    /// A future-dated incoming term is SCHEDULED, not in force, so it does not mark the row yet —
    /// the same <c>EffectiveFrom &lt;= today</c> narrowing every other current-value read applies.
    /// </summary>
    [Fact]
    public async Task List_DoesNotMarkAnIncomingTermThatStartsLater()
    {
        await using var factory = await NewFactoryAsync(ReadWrite);
        using var client = factory.CreateClient();
        var contractId = await CreateContractAsync(client);

        await PostTermAsync(client, contractId, Incoming(2250m, FixedToday.AddDays(30)));

        Assert.False(await HasIncomingAsync(client, contractId));
    }

    /// <summary>
    /// A SEPARATE series keeps its own winner, so an outgoing fee beside an incoming one still marks
    /// the row — the collapse is per series, never per contract.
    /// </summary>
    [Fact]
    public async Task List_MarksAContractThatIsBothOutgoingAndIncoming()
    {
        await using var factory = await NewFactoryAsync(ReadWrite);
        using var client = factory.CreateClient();
        var contractId = await CreateContractAsync(client);

        await PostTermAsync(client, contractId, Rent(1800m, new DateTime(2026, 1, 1), label: "Service charge"));
        await PostTermAsync(client, contractId, Incoming(2250m, new DateTime(2026, 1, 1), label: "Sublet"));

        Assert.True(await HasIncomingAsync(client, contractId));
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static NewTerm Incoming(decimal value, DateTime effectiveFrom, string? label = "Sublet") => new()
    {
        TermKind = TermKind.Fee,
        Label = label,
        ValueUnit = TermValueUnit.Amount,
        Value = value,
        CurrencyCode = "EUR",
        Interval = Interval.Monthly,
        Direction = TermDirection.Incoming,
        EffectiveFrom = DateTime.SpecifyKind(effectiveFrom, DateTimeKind.Utc),
    };

    private static async Task<bool> HasIncomingAsync(HttpClient client, Guid contractId)
    {
        var response = await client.GetAsync(Path);
        response.EnsureSuccessStatusCode();
        var page = await response.Content.ReadFromJsonAsync<PagedResult<ContractListItem>>();
        return page!.Items.Single(i => i.ContractId == contractId).HasIncomingTerm;
    }

    /// <summary>
    /// A factory whose database has actually been created. The reference-data currencies are a
    /// <c>HasData</c> seed on the model, and the InMemory provider materialises a seed only on
    /// <c>EnsureCreated</c> — so without this, the first term naming a currency is refused as
    /// unsupported, which reads as a validation bug rather than an unseeded fixture.
    /// </summary>
    private static async Task<ApiFactory> NewFactoryAsync(
        IReadOnlyCollection<string>? permissions,
        IReadOnlyDictionary<string, string?>? configuration = null)
    {
        var factory = new ApiFactory(permissions, configuration);
        using var scope = factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<OdysseyContext>().Database.EnsureCreatedAsync();
        return factory;
    }

    private static string Terms(Guid contractId) => $"{Path}/{contractId}/terms";

    private static NewTerm Rent(decimal value, DateTime effectiveFrom, string? label = "Monthly rent", string? currency = "EUR") => new()
    {
        TermKind = TermKind.Fee,
        Label = label,
        ValueUnit = TermValueUnit.Amount,
        Value = value,
        CurrencyCode = currency,
        Interval = Interval.Monthly,
        EffectiveFrom = DateTime.SpecifyKind(effectiveFrom, DateTimeKind.Utc),
    };

    private static NewTerm InterestRate(decimal value, DateTime effectiveFrom) => new()
    {
        TermKind = TermKind.InterestRate,
        ValueUnit = TermValueUnit.Percentage,
        Value = value,
        EffectiveFrom = DateTime.SpecifyKind(effectiveFrom, DateTimeKind.Utc),
    };

    private static async Task<ExistingTerm> PostTermAsync(HttpClient client, Guid contractId, NewTerm term)
    {
        var response = await client.PostAsJsonAsync(Terms(contractId), term);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ExistingTerm>())!;
    }

    private static async Task<Guid> CreateContractAsync(
        HttpClient client, ContractType type = ContractType.Rental, string name = "Maple St lease",
        DateTime? start = null, DateTime? end = null)
    {
        var response = await client.PostAsJsonAsync(Path, new NewContract
        {
            Name = name,
            Type = type,
            StartDate = start ?? new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            EndDate = end,
        });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ExistingContract>())!.ContractId;
    }

    /// <summary>Archiving goes through the ordinary update endpoint — there is no dedicated one.</summary>
    private static async Task ArchiveAsync(HttpClient client, Guid contractId, DateTime start, DateTime end)
    {
        var response = await client.PutAsJsonAsync($"{Path}/{contractId}", new UpdateContract
        {
            Name = "Maple St lease",
            Type = ContractType.Rental,
            StartDate = start,
            EndDate = end,
            IsArchived = true,
        });
        response.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// A contract seeded straight into the context, for a principal that holds <c>contracts.read</c>
    /// but not <c>contracts.create</c>.
    /// </summary>
    private static async Task<Guid> SeedContractDirectlyAsync(ApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        await context.Database.EnsureCreatedAsync();

        var contract = new Contract
        {
            Name = "Seeded lease",
            Type = Odyssey.Context.ContractType.Rental,
            StartDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            CreatedAtUtc = FixedToday,
        };
        context.Contracts.Add(contract);
        await context.SaveChangesAsync();
        return contract.ContractId;
    }

    private static async Task<Guid> SeedAccountAsync(ApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        await context.Database.EnsureCreatedAsync();

        var account = new Account
        {
            Name = "Savings",
            Description = "Seeded for the cross-owner assertions.",
            AccountType = ContextAccountType.SavingsAccount,
            CurrencyCode = "USD",
            Opened = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        context.Accounts.Add(account);
        await context.SaveChangesAsync();
        return account.AccountId;
    }

    /// <summary>The per-field shape of a ProblemDetails body, for the currency assertion.</summary>
    private sealed record ApiProblemBody
    {
        public Dictionary<string, string[]>? Errors { get; init; }
    }

    private sealed class ApiFactory : OdysseyApiFactory
    {
        public ApiFactory(
            IReadOnlyCollection<string>? permissions,
            IReadOnlyDictionary<string, string?>? configuration = null)
            : base(permissions, ActorUserId, configuration, configureServices: services =>
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(new FixedTimeProvider(FixedToday));
            })
        {
        }
    }

    private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow);
    }
}
