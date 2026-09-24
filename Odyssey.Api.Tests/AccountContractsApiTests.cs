using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Odyssey.Api.Tests.Infrastructure;
using Odyssey.Context;
using Odyssey.Dtos;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos.Finance;
using Xunit;
using ContextAccountType = Odyssey.Context.AccountType;
using ContextContractPartyRole = Odyssey.Context.ContractPartyRole;
using ContextContractType = Odyssey.Context.ContractType;
using ContractPartyRole = Odyssey.Dtos.Finance.ContractPartyRole;
using ContractType = Odyssey.Dtos.Finance.ContractType;

namespace Odyssey.Api.Tests;

/// <summary>
/// The account record's Contracts section: <c>GET /api/accounts/{id}/contracts</c> and the
/// <see cref="ExistingAccount.ContractCount"/> the account list carries for its header count.
/// </summary>
/// <remarks>
/// Both are claim-conditional on <c>contracts.read</c>, and that is what most of this pins. The
/// account routes are gated on <c>accounts.read</c> alone and Guest holds no contract claim, so an
/// unconditional count would tell Guest how many agreements each account is party to.
/// </remarks>
public class AccountContractsApiTests
{
    private static readonly DateTime FixedToday = new(2026, 6, 15, 8, 0, 0, DateTimeKind.Utc);

    private static readonly string[] Reader = [PermissionClaims.AccountsRead, PermissionClaims.ContractsRead];

    [Fact]
    public async Task GetAccountContracts_ReturnsOneRowPerContract_WithEveryRoleTheAccountHolds()
    {
        await using var factory = new ApiFactory(Reader);
        var accountId = await SeedAccountAsync(factory);
        var otherAccountId = await SeedAccountAsync(factory, "Savings");
        var loanId = await SeedContractAsync(factory, "Mortgage", ContextContractType.Loan,
            (accountId, ContextContractPartyRole.Borrower), (accountId, ContextContractPartyRole.Collateral),
            (otherAccountId, ContextContractPartyRole.Guarantor));
        await SeedContractAsync(factory, "Unrelated", ContextContractType.Other,
            (otherAccountId, ContextContractPartyRole.Other));
        using var client = factory.CreateClient();

        var rows = await client.GetFromJsonAsync<List<AccountContractLink>>($"/api/accounts/{accountId}/contracts");

        var row = Assert.Single(rows!);
        Assert.Equal(loanId, row.ContractId);
        Assert.Equal("Mortgage", row.Name);
        Assert.Equal(ContractType.Loan, row.Type);
        Assert.Equal(ContractStatus.Active, row.Status);
        Assert.Equal(new HashSet<ContractPartyRole> { ContractPartyRole.Borrower, ContractPartyRole.Collateral },
            row.Roles.ToHashSet());
    }

    [Fact]
    public async Task GetAccountContracts_OrdersByName_AndIsEmptyForAnAccountPartyToNothing()
    {
        await using var factory = new ApiFactory(Reader);
        var accountId = await SeedAccountAsync(factory);
        var loneId = await SeedAccountAsync(factory, "Lonely");
        await SeedContractAsync(factory, "Zeta lease", ContextContractType.Rental, (accountId, ContextContractPartyRole.Tenant));
        await SeedContractAsync(factory, "alpha cover", ContextContractType.Insurance, (accountId, ContextContractPartyRole.Insured));
        using var client = factory.CreateClient();

        var rows = await client.GetFromJsonAsync<List<AccountContractLink>>($"/api/accounts/{accountId}/contracts");
        Assert.Equal(["alpha cover", "Zeta lease"], rows!.Select(r => r.Name));

        Assert.Empty((await client.GetFromJsonAsync<List<AccountContractLink>>($"/api/accounts/{loneId}/contracts"))!);
    }

    /// <summary>
    /// Roles come back in PARTY order (by party id), as <see cref="AccountContractLink.Roles"/>
    /// promises — the order the contract detail reads its parties in.
    /// </summary>
    [Fact]
    public async Task GetAccountContracts_ListsRolesInPartyIdOrder()
    {
        await using var factory = new ApiFactory(Reader);
        var accountId = await SeedAccountAsync(factory);
        await SeedContractAsync(factory, "Mortgage", ContextContractType.Loan,
            [
                (Guid.Parse("00000000-0000-0000-0000-000000000003"), accountId, ContextContractPartyRole.Guarantor),
                (Guid.Parse("00000000-0000-0000-0000-000000000001"), accountId, ContextContractPartyRole.Collateral),
                (Guid.Parse("00000000-0000-0000-0000-000000000002"), accountId, ContextContractPartyRole.Borrower),
            ]);
        using var client = factory.CreateClient();

        var row = Assert.Single((await client.GetFromJsonAsync<List<AccountContractLink>>($"/api/accounts/{accountId}/contracts"))!);

        Assert.Equal(
            [ContractPartyRole.Collateral, ContractPartyRole.Borrower, ContractPartyRole.Guarantor],
            row.Roles);
    }

    /// <summary>
    /// Archived contracts are INCLUDED, as <c>ListForAccountAsync</c> documents: the link is still on
    /// record, and the tile states the status rather than hiding the row.
    /// </summary>
    [Fact]
    public async Task GetAccountContracts_IncludesArchivedContracts_WithTheArchivedStatus()
    {
        await using var factory = new ApiFactory(Reader);
        var accountId = await SeedAccountAsync(factory);
        var archivedId = await SeedContractAsync(factory, "Old lease", ContextContractType.Rental,
            [(Guid.NewGuid(), accountId, ContextContractPartyRole.Tenant)], archived: FixedToday.AddDays(-1));
        using var client = factory.CreateClient();

        var row = Assert.Single((await client.GetFromJsonAsync<List<AccountContractLink>>($"/api/accounts/{accountId}/contracts"))!);

        Assert.Equal(archivedId, row.ContractId);
        Assert.Equal(ContractStatus.Archived, row.Status);
        Assert.Equal(1, (await client.GetFromJsonAsync<ExistingAccount>($"/api/accounts/{accountId}"))!.ContractCount);
    }

    [Fact]
    public async Task GetAccountContracts_ForAMissingAccount_Returns404()
    {
        await using var factory = new ApiFactory(Reader);
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"/api/accounts/{Guid.NewGuid()}/contracts");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>
    /// Both claims are required. The rows NAME contracts, which <c>accounts.read</c> alone does not
    /// license, and a contract reader without <c>accounts.read</c> has no business on an account route.
    /// </summary>
    [Theory]
    [InlineData(PermissionClaims.AccountsRead)]
    [InlineData(PermissionClaims.ContractsRead)]
    public async Task GetAccountContracts_WithOnlyOneOfTheTwoClaims_Returns403(string onlyClaim)
    {
        await using var owner = new ApiFactory(Reader);
        var accountId = await SeedAccountAsync(owner);
        await using var factory = new ApiFactory([onlyClaim], owner);
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"/api/accounts/{accountId}/contracts");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>
    /// The count is of distinct CONTRACTS, not party rows — two roles on one contract are one contract
    /// — and it reaches both the list and the single-account read.
    /// </summary>
    [Fact]
    public async Task ContractCount_CountsDistinctContracts_OnListAndGet()
    {
        await using var factory = new ApiFactory(Reader);
        var accountId = await SeedAccountAsync(factory);
        var loneId = await SeedAccountAsync(factory, "Lonely");
        await SeedContractAsync(factory, "Mortgage", ContextContractType.Loan,
            (accountId, ContextContractPartyRole.Borrower), (accountId, ContextContractPartyRole.Collateral));
        await SeedContractAsync(factory, "Lease", ContextContractType.Rental, (accountId, ContextContractPartyRole.Tenant));
        using var client = factory.CreateClient();

        var page = await client.GetFromJsonAsync<PagedResult<ExistingAccount>>("/api/accounts");
        Assert.Equal(2, page!.Items.Single(a => a.AccountId == accountId).ContractCount);
        Assert.Equal(0, page.Items.Single(a => a.AccountId == loneId).ContractCount);

        var single = await client.GetFromJsonAsync<ExistingAccount>($"/api/accounts/{accountId}");
        Assert.Equal(2, single!.ContractCount);
    }

    /// <summary>
    /// Without <c>contracts.read</c> the count is WITHHELD — null, not zero. A zero would tell the
    /// caller "no contracts", which is a statement about contracts they may not read either way.
    /// </summary>
    [Fact]
    public async Task ContractCount_WithoutContractsRead_IsNull_OnListAndGet()
    {
        await using var owner = new ApiFactory(Reader);
        var accountId = await SeedAccountAsync(owner);
        await SeedContractAsync(owner, "Mortgage", ContextContractType.Loan, (accountId, ContextContractPartyRole.Borrower));
        await using var guest = new ApiFactory([PermissionClaims.AccountsRead], owner);
        using var client = guest.CreateClient();

        var page = await client.GetFromJsonAsync<PagedResult<ExistingAccount>>("/api/accounts");
        Assert.Null(page!.Items.Single(a => a.AccountId == accountId).ContractCount);

        var single = await client.GetFromJsonAsync<ExistingAccount>($"/api/accounts/{accountId}");
        Assert.Null(single!.ContractCount);
    }

    private sealed class ApiFactory : OdysseyApiFactory
    {
        private const string ActorUserId = "account-contracts-actor";

        public ApiFactory(IReadOnlyCollection<string>? permissions)
            : base(permissions, ActorUserId, configuration: null, configureServices: Clock)
        {
        }

        public ApiFactory(IReadOnlyCollection<string>? permissions, OdysseyApiFactory sharing)
            : base(permissions, ActorUserId, configuration: null, configureServices: Clock,
                sharingStoreWith: sharing)
        {
        }

        private static void Clock(IServiceCollection services)
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(new FixedTimeProvider(FixedToday));
        }
    }

    private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow);
    }

    private static async Task<Guid> SeedAccountAsync(OdysseyApiFactory factory, string name = "Everyday Checking")
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();

        var account = new Account
        {
            AccountId = Guid.NewGuid(),
            Name = name,
            Description = "Primary",
            Opened = FixedToday.AddYears(-1),
            AccountType = ContextAccountType.CheckingAccount,
            CurrencyCode = "USD",
        };
        context.Accounts.Add(account);
        await context.SaveChangesAsync();
        return account.AccountId;
    }

    /// <summary>A signed contract that started a month ago — so it derives as Active.</summary>
    private static Task<Guid> SeedContractAsync(
        OdysseyApiFactory factory, string name, ContextContractType type,
        params (Guid AccountId, ContextContractPartyRole Role)[] parties) =>
        SeedContractAsync(factory, name, type, [.. parties.Select(p => (Guid.NewGuid(), p.AccountId, p.Role))]);

    /// <summary>As above, with explicit party ids (for ordering) and an optional archive stamp.</summary>
    private static async Task<Guid> SeedContractAsync(
        OdysseyApiFactory factory, string name, ContextContractType type,
        (Guid PartyId, Guid AccountId, ContextContractPartyRole Role)[] parties, DateTime? archived = null)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();

        var contract = new Contract
        {
            ContractId = Guid.NewGuid(),
            Name = name,
            Type = type,
            StartDate = FixedToday.AddDays(-30),
            Signed = FixedToday.AddDays(-31),
            CreatedAtUtc = FixedToday.AddDays(-31),
            Archived = archived,
        };
        foreach (var (partyId, accountId, role) in parties)
        {
            contract.Parties.Add(new ContractParty
            {
                ContractPartyId = partyId,
                ContractId = contract.ContractId,
                AccountId = accountId,
                Role = role,
            });
        }

        context.Contracts.Add(contract);
        await context.SaveChangesAsync();
        return contract.ContractId;
    }
}
