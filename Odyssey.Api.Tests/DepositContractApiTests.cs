using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Odyssey.Api.Tests.Infrastructure;
using Odyssey.Context;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos.Finance;
using Xunit;
using ContextAccountType = Odyssey.Context.AccountType;
using ContractPartyRole = Odyssey.Dtos.Finance.ContractPartyRole;
using ContractType = Odyssey.Dtos.Finance.ContractType;
using Interval = Odyssey.Dtos.Finance.Interval;
using TermDirection = Odyssey.Dtos.Finance.TermDirection;
using TermValueUnit = Odyssey.Dtos.Finance.TermValueUnit;

namespace Odyssey.Api.Tests;

/// <summary>
/// The HTTP half of issue #187: the <c>Deposit</c> contract type and its <c>Depositor</c> /
/// <c>Custodian</c> roles travel through the existing contract surface unchanged — create and read
/// back (AC 13), the party matrix (AC 14), the type-change refusal (AC 15) and the roll-up (AC 16).
/// No endpoint changed for any of this; what is pinned is that the new members bind, persist and
/// aggregate by name rather than falling through to <c>Other</c> anywhere on the way.
/// </summary>
/// <remarks>
/// AC 17 (every endpoint still <c>403</c>s without its claim) is covered by the existing suites, which
/// loop over <c>Enum.GetValues&lt;ContractType&gt;()</c> and so reach <c>Deposit</c> without a special
/// case. AC 18 (deleting a <c>Custodian</c> contact cascades) runs the relational-only contact-delete
/// cleanup and lives in <c>Odyssey.IntegrationTests</c>.
/// </remarks>
public class DepositContractApiTests
{
    private const string Path = "/api/contracts";

    private static readonly DateTime FixedToday = new(2026, 6, 15, 8, 0, 0, DateTimeKind.Utc);

    private static readonly string[] ReadWrite =
    [
        PermissionClaims.ContractsRead, PermissionClaims.ContractsCreate,
        PermissionClaims.ContractsUpdate, PermissionClaims.ContractsDelete,
    ];

    /// <summary>AC 13 — a <c>Deposit</c> is created, reads back as itself, and is found by the type filter.</summary>
    [Fact]
    public async Task CreateDeposit_Returns201_AndReadsBackAsDeposit()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();

        var post = await client.PostAsJsonAsync(Path, new NewContract
        {
            Name = "Fixed-term deposit",
            Type = ContractType.Deposit,
            StartDate = FixedToday.AddDays(-30),
        });

        Assert.Equal(HttpStatusCode.Created, post.StatusCode);
        var created = (await post.Content.ReadFromJsonAsync<ExistingContract>())!;
        Assert.Equal(ContractType.Deposit, created.Type);
        Assert.Equal(ContractType.Deposit, (await GetAsync(client, created.ContractId)).Type);

        var filtered = await client.GetFromJsonAsync<Odyssey.Dtos.PagedResult<ContractListItem>>(
            $"{Path}?types={ContractType.Deposit}");
        Assert.Contains(filtered!.Items, item => item.ContractId == created.ContractId);
    }

    /// <summary>
    /// AC 14 — both suggested roles are accepted on a <c>Deposit</c> and read back as themselves, while
    /// its mirror's <c>Lender</c> is a <c>422</c> keyed on <c>role</c> listing the legal roles in picker
    /// order.
    /// </summary>
    [Fact]
    public async Task DepositParties_TakeDepositorAndCustodian_AndRefuseLender()
    {
        await using var factory = new ApiFactory(ReadWrite);
        var accountId = await SeedAccountAsync(factory);
        var bankId = await SeedContactAsync(factory);
        using var client = factory.CreateClient();

        var id = await CreateAsync(client, ContractType.Deposit);

        var depositor = await client.PostAsJsonAsync($"{Path}/{id}/parties",
            new ContractPartyRequest { AccountId = accountId, Role = ContractPartyRole.Depositor });
        var custodian = await client.PostAsJsonAsync($"{Path}/{id}/parties",
            new ContractPartyRequest { ContactId = bankId, Role = ContractPartyRole.Custodian });

        Assert.Equal(HttpStatusCode.Created, depositor.StatusCode);
        Assert.Equal(HttpStatusCode.Created, custodian.StatusCode);
        Assert.Equal(
            [ContractPartyRole.Depositor, ContractPartyRole.Custodian],
            (await GetAsync(client, id)).Parties.Select(party => party.Role).Order().ToList());

        var lender = await client.PostAsJsonAsync($"{Path}/{id}/parties",
            new ContractPartyRequest { AccountId = accountId, Role = ContractPartyRole.Lender });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, lender.StatusCode);
        var body = await lender.Content.ReadAsStringAsync();
        using (var problem = JsonDocument.Parse(body))
        {
            Assert.Contains(problem.RootElement.GetProperty("errors").EnumerateObject(),
                error => string.Equals(error.Name, "role", StringComparison.OrdinalIgnoreCase));
        }

        Assert.Contains(
            "The roles it can have are: Depositor, Custodian, Object, Collateral, Guarantor, Broker, Other.",
            body,
            StringComparison.Ordinal);
        Assert.Equal(2, (await GetAsync(client, id)).Parties.Count);
    }

    /// <summary>
    /// AC 15 — a <c>Deposit</c> holding a <c>Depositor</c> cannot be re-typed to <c>Loan</c>: the
    /// existing type-change pre-check names the orphaned party and offers the loan's legal roles, and
    /// nothing — neither the type nor the name riding in the same body — is written.
    /// </summary>
    [Fact]
    public async Task RetypingADepositWithADepositor_ToLoan_Returns422_AndWritesNothing()
    {
        await using var factory = new ApiFactory(ReadWrite);
        var accountId = await SeedAccountAsync(factory);
        using var client = factory.CreateClient();

        var id = await CreateAsync(client, ContractType.Deposit);
        var add = await client.PostAsJsonAsync($"{Path}/{id}/parties",
            new ContractPartyRequest { AccountId = accountId, Role = ContractPartyRole.Depositor });
        var party = (await add.Content.ReadFromJsonAsync<ExistingContractParty>())!;

        var put = await client.PutAsJsonAsync($"{Path}/{id}", new UpdateContract
        {
            Name = "Renamed too",
            Type = ContractType.Loan,
            StartDate = FixedToday.AddDays(-30),
        });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, put.StatusCode);

        using var problem = JsonDocument.Parse(await put.Content.ReadAsStringAsync());
        var blockers = problem.RootElement.GetProperty("typeChange");
        var offending = Assert.Single(blockers.GetProperty("parties").EnumerateArray().ToList());
        Assert.Equal(party.ContractPartyId, offending.GetProperty("contractPartyId").GetGuid());
        Assert.Equal((int)ContractPartyRole.Depositor, offending.GetProperty("role").GetInt32());
        Assert.Equal(
            ContractPartyRoleMatrix.LegalFor(ContractType.Loan).Select(role => (int)role),
            blockers.GetProperty("legalRoles").EnumerateArray().Select(role => role.GetInt32()));

        var reread = await GetAsync(client, id);
        Assert.Equal(ContractType.Deposit, reread.Type);
        Assert.NotEqual("Renamed too", reread.Name);
    }

    /// <summary>
    /// AC 16 — an active <c>Deposit</c> earning monthly interest is headcounted under <c>Deposit</c>
    /// and lands in the INCOMING split under <c>Deposit</c>, not under <c>Other</c> and not on the
    /// cost side.
    /// </summary>
    [Fact]
    public async Task Summary_CountsADeposit_AndPutsItsInterestInTheIncomingSplit()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using (var scope = factory.Services.CreateScope())
        {
            // The currency reference data the term's CurrencyCode is validated against is model seed.
            await scope.ServiceProvider.GetRequiredService<OdysseyContext>().Database.EnsureCreatedAsync();
        }

        using var client = factory.CreateClient();

        var post = await client.PostAsJsonAsync(Path, new NewContract
        {
            Name = "Fixed-term deposit",
            Type = ContractType.Deposit,
            StartDate = FixedToday.AddYears(-1),
            // Signed, or the contract derives as Draft and never reaches the run rate.
            Ready = FixedToday.AddYears(-1).AddDays(-1),
            Signed = FixedToday.AddYears(-1),
        });
        Assert.True(post.IsSuccessStatusCode, await post.Content.ReadAsStringAsync());
        var id = (await post.Content.ReadFromJsonAsync<ExistingContract>())!.ContractId;

        var termPost = await client.PostAsJsonAsync($"{Path}/{id}/terms", new NewTerm
        {
            Label = "Interest",
            ValueUnit = TermValueUnit.Amount,
            Value = 125m,
            CurrencyCode = "USD",
            Interval = Interval.Monthly,
            IntervalCount = 1,
            Direction = TermDirection.Incoming,
            EffectiveFrom = FixedToday.AddDays(-30),
        });
        Assert.True(termPost.IsSuccessStatusCode, await termPost.Content.ReadAsStringAsync());

        var summary = (await client.GetFromJsonAsync<ContractSummary>($"{Path}/summary?baseCurrency=USD"))!;

        var counted = Assert.Single(summary.CountsByType);
        Assert.Equal(ContractType.Deposit, counted.Type);
        Assert.Equal(1, counted.Count);

        var incoming = Assert.Single(summary.RunRate.IncomingByType);
        Assert.Equal(ContractType.Deposit, incoming.Type);
        Assert.Equal(125m, incoming.Monthly);
        Assert.Equal(125m, summary.RunRate.IncomingMonthly);
        Assert.Empty(summary.RunRate.ByType);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static async Task<Guid> CreateAsync(HttpClient client, ContractType type)
    {
        var post = await client.PostAsJsonAsync(Path, new NewContract
        {
            Name = $"{type} agreement",
            Type = type,
            StartDate = FixedToday.AddDays(-30),
        });
        post.EnsureSuccessStatusCode();
        return (await post.Content.ReadFromJsonAsync<ExistingContract>())!.ContractId;
    }

    private static async Task<ExistingContract> GetAsync(HttpClient client, Guid id) =>
        (await client.GetFromJsonAsync<ExistingContract>($"{Path}/{id}"))!;

    private static async Task<Guid> SeedAccountAsync(ApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        await context.Database.EnsureCreatedAsync();

        var account = new Account
        {
            AccountId = Guid.NewGuid(),
            Name = "High-yield Savings",
            Description = "The account the deposit is placed from.",
            Opened = FixedToday.AddYears(-1),
            AccountType = ContextAccountType.SavingsAccount,
            CurrencyCode = "USD",
        };
        context.Accounts.Add(account);
        await context.SaveChangesAsync();
        return account.AccountId;
    }

    private static async Task<Guid> SeedContactAsync(ApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        await context.Database.EnsureCreatedAsync();

        var contactId = Guid.NewGuid();
        context.Contacts.Add(new Contact
        {
            ContactId = contactId,
            ExternalUid = $"urn:uuid:{Guid.NewGuid()}",
            NormalizedName = "NORTHWIND BANK",
            Type = Odyssey.Dtos.ContactType.Organization,
            OrganizationDetails = new() { ContactId = contactId, LegalName = "Northwind Bank" },
        });

        await context.SaveChangesAsync();
        return contactId;
    }

    private sealed class ApiFactory(IReadOnlyCollection<string>? permissions)
        : OdysseyApiFactory(permissions, "deposit-contract-actor", configuration: null, configureServices: services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(new FixedTimeProvider(FixedToday));
        });

    private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow);
    }
}
