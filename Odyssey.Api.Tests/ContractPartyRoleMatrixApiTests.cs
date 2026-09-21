using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
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

namespace Odyssey.Api.Tests;

/// <summary>
/// The HTTP half of issue #157's write-path enforcement: the party-write matrix check (AC 1–4, 9),
/// the contract type-change refusal (AC 7, 8), the mass-assignment regression (AC 10), the new
/// <c>Loan</c> type (AC 19) and the status/field-key contract both refusals carry (AC 24).
/// </summary>
/// <remarks>
/// These run on the fast tier because none of them touches <c>ContactReferenceGuard</c>, whose
/// statements are relational-only. The beneficiary-blocker criteria (11–15, 21–23) and the migration
/// one (25) live in <c>Odyssey.IntegrationTests</c> for that reason.
/// </remarks>
public class ContractPartyRoleMatrixApiTests
{
    private const string Path = "/api/contracts";

    private static readonly DateTime FixedToday = new(2026, 6, 15, 8, 0, 0, DateTimeKind.Utc);

    private static readonly string[] ReadWrite =
    [
        PermissionClaims.ContractsRead, PermissionClaims.ContractsCreate,
        PermissionClaims.ContractsUpdate, PermissionClaims.ContractsDelete,
    ];

    // ── Party writes (AC 1–4, 9, 24) ─────────────────────────────────────────

    /// <summary>AC 1 — a SUGGESTED role for the contract's type is accepted and persisted.</summary>
    [Fact]
    public async Task AddParty_WithASuggestedRole_Returns201AndPersistsIt()
    {
        await using var factory = new ApiFactory(ReadWrite);
        var accountId = await SeedAccountAsync(factory);
        using var client = factory.CreateClient();

        var id = await CreateAsync(client, ContractType.Rental);
        var add = await client.PostAsJsonAsync($"{Path}/{id}/parties",
            new ContractPartyRequest { AccountId = accountId, Role = ContractPartyRole.Landlord });

        Assert.Equal(HttpStatusCode.Created, add.StatusCode);
        var created = await add.Content.ReadFromJsonAsync<ExistingContractParty>();
        Assert.Equal(ContractPartyRole.Landlord, created!.Role);
    }

    /// <summary>
    /// AC 2 — an ALLOWED-but-not-suggested role is accepted too. The distinction is a picker ordering
    /// and carries no server-side meaning; a validator that enforced "suggested" would refuse a
    /// perfectly legal guarantor.
    /// </summary>
    [Fact]
    public async Task AddParty_WithAnAllowedRole_Returns201()
    {
        await using var factory = new ApiFactory(ReadWrite);
        var accountId = await SeedAccountAsync(factory);
        using var client = factory.CreateClient();

        var id = await CreateAsync(client, ContractType.Rental);
        Assert.Equal(ContractPartyRoleLegality.Allowed,
            ContractPartyRoleMatrix.LegalityOf(ContractType.Rental, ContractPartyRole.Guarantor));

        var add = await client.PostAsJsonAsync($"{Path}/{id}/parties",
            new ContractPartyRequest { AccountId = accountId, Role = ContractPartyRole.Guarantor });

        Assert.Equal(HttpStatusCode.Created, add.StatusCode);
    }

    /// <summary>
    /// AC 3 and AC 24 — a rejected role is a <c>422</c>, NOT a <c>400</c>: the body is well-formed and
    /// every value in it is a real member, so what fails is the combination. The message names the
    /// legal roles, it lands under <c>errors.role</c> so the picker can mark itself, and no row is
    /// written.
    /// </summary>
    [Fact]
    public async Task AddParty_WithARejectedRole_Returns422NamingTheLegalRoles_AndWritesNothing()
    {
        await using var factory = new ApiFactory(ReadWrite);
        var accountId = await SeedAccountAsync(factory);
        using var client = factory.CreateClient();

        var id = await CreateAsync(client, ContractType.Rental);
        var add = await client.PostAsJsonAsync($"{Path}/{id}/parties",
            new ContractPartyRequest { AccountId = accountId, Role = ContractPartyRole.Employee });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, add.StatusCode);
        Assert.True(await HasErrorKeyAsync(add, nameof(ContractPartyRequest.Role)));

        var body = await add.Content.ReadAsStringAsync();
        Assert.Contains("Landlord", body, StringComparison.Ordinal);
        Assert.Contains("Tenant", body, StringComparison.Ordinal);

        Assert.Empty((await GetAsync(client, id)).Parties);
    }

    /// <summary>AC 4 — the edit enforces the identical matrix against the identical pair.</summary>
    [Fact]
    public async Task UpdateParty_WithARejectedRole_Returns422_AndLeavesThePartyAlone()
    {
        await using var factory = new ApiFactory(ReadWrite);
        var accountId = await SeedAccountAsync(factory);
        using var client = factory.CreateClient();

        var id = await CreateAsync(client, ContractType.Rental);
        var add = await client.PostAsJsonAsync($"{Path}/{id}/parties",
            new ContractPartyRequest { AccountId = accountId, Role = ContractPartyRole.Landlord });
        var party = await add.Content.ReadFromJsonAsync<ExistingContractParty>();

        var put = await client.PutAsJsonAsync($"{Path}/{id}/parties/{party!.ContractPartyId}",
            new ContractPartyRequest { AccountId = accountId, Role = ContractPartyRole.Employee });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, put.StatusCode);
        Assert.True(await HasErrorKeyAsync(put, nameof(ContractPartyRequest.Role)));
        Assert.Equal(ContractPartyRole.Landlord, Assert.Single((await GetAsync(client, id)).Parties).Role);
    }

    /// <summary>
    /// AC 9 — <b>every one of the 135 cells</b> is exercised against the live endpoint: the 52 legal
    /// ones are accepted and the 83 rejected ones refused. Iterating the matrix rather than sampling
    /// it is what makes a cell unable to disagree silently between the declaration and the validator.
    /// </summary>
    [Fact]
    public async Task EveryMatrixCell_IsAcceptedOrRefusedExactlyAsDeclared()
    {
        await using var factory = new ApiFactory(ReadWrite);
        var accountId = await SeedAccountAsync(factory);
        using var client = factory.CreateClient();

        var legal = 0;
        var rejected = 0;

        foreach (var type in Enum.GetValues<ContractType>())
        {
            // A fresh contract per type, so the (contract, target, role) uniqueness never interferes
            // with what is being measured.
            var id = await CreateAsync(client, type);

            foreach (var role in Enum.GetValues<ContractPartyRole>())
            {
                var add = await client.PostAsJsonAsync($"{Path}/{id}/parties",
                    new ContractPartyRequest { AccountId = accountId, Role = role });

                if (ContractPartyRoleMatrix.IsLegal(type, role))
                {
                    legal++;
                    Assert.Equal(HttpStatusCode.Created, add.StatusCode);

                    // Removed again so the next role's write is not refused as a duplicate target.
                    var created = await add.Content.ReadFromJsonAsync<ExistingContractParty>();
                    (await client.DeleteAsync($"{Path}/{id}/parties/{created!.ContractPartyId}"))
                        .EnsureSuccessStatusCode();
                }
                else
                {
                    rejected++;
                    Assert.Equal(HttpStatusCode.UnprocessableEntity, add.StatusCode);
                }
            }
        }

        Assert.Equal(52, legal);
        Assert.Equal(83, rejected);
    }

    // ── The contract type change (AC 7, 8, 24) ───────────────────────────────

    /// <summary>
    /// AC 7 and AC 24 — a type change that would orphan an existing party is a <c>422</c> naming that
    /// party by id, role and display name, and NOTHING is written: the contract is re-read to prove
    /// its name and type are both unchanged, since a check that ran after the field assignments would
    /// still refuse while having already mutated the tracked entity.
    /// </summary>
    [Fact]
    public async Task UpdateContract_ToATypeThatRejectsAnExistingParty_Returns422_AndWritesNothing()
    {
        await using var factory = new ApiFactory(ReadWrite);
        var accountId = await SeedAccountAsync(factory);
        using var client = factory.CreateClient();

        var id = await CreateAsync(client, ContractType.Rental);
        var add = await client.PostAsJsonAsync($"{Path}/{id}/parties",
            new ContractPartyRequest { AccountId = accountId, Role = ContractPartyRole.Landlord });
        var party = await add.Content.ReadFromJsonAsync<ExistingContractParty>();

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
        Assert.Equal(party!.ContractPartyId, offending.GetProperty("contractPartyId").GetGuid());
        Assert.Equal((int)ContractPartyRole.Landlord, offending.GetProperty("role").GetInt32());
        Assert.Equal("Everyday Checking", offending.GetProperty("displayName").GetString());

        // The roles that WOULD work, so the caller has the way out without consulting the docs.
        var legal = blockers.GetProperty("legalRoles").EnumerateArray().Select(r => r.GetInt32()).ToList();
        Assert.Contains((int)ContractPartyRole.Lender, legal);
        Assert.Contains((int)ContractPartyRole.Borrower, legal);

        // Nothing written — neither the type nor the name that rode along in the same body.
        var reread = await GetAsync(client, id);
        Assert.Equal(ContractType.Rental, reread.Type);
        Assert.NotEqual("Renamed too", reread.Name);
        Assert.Equal(ContractPartyRole.Landlord, Assert.Single(reread.Parties).Role);
    }

    /// <summary>
    /// AC 8 — a type change every existing party survives is applied normally. <c>Broker</c> is legal
    /// on every type, so this is the case the refusal must not swallow.
    /// </summary>
    [Fact]
    public async Task UpdateContract_ToATypeEveryPartySurvives_Returns200()
    {
        await using var factory = new ApiFactory(ReadWrite);
        var accountId = await SeedAccountAsync(factory);
        using var client = factory.CreateClient();

        var id = await CreateAsync(client, ContractType.Rental);
        (await client.PostAsJsonAsync($"{Path}/{id}/parties",
            new ContractPartyRequest { AccountId = accountId, Role = ContractPartyRole.Broker }))
            .EnsureSuccessStatusCode();

        var put = await client.PutAsJsonAsync($"{Path}/{id}", new UpdateContract
        {
            Name = "Now a loan",
            Type = ContractType.Loan,
            StartDate = FixedToday.AddDays(-30),
        });

        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        Assert.Equal(ContractType.Loan, (await GetAsync(client, id)).Type);
    }

    /// <summary>
    /// An edit that leaves the type ALONE is never refused, however illegal an existing party's role
    /// is. The server checks a type CHANGE, not a contract's standing legality: a legacy party is the
    /// party endpoint's problem to fix, and freezing every other field on the contract would take
    /// away the only route to fixing it.
    /// </summary>
    [Fact]
    public async Task UpdateContract_KeepingTheType_IsNotRefusedByALegacyParty()
    {
        await using var factory = new ApiFactory(ReadWrite);
        var accountId = await SeedAccountAsync(factory);
        using var client = factory.CreateClient();

        var id = await CreateAsync(client, ContractType.Rental);
        (await client.PostAsJsonAsync($"{Path}/{id}/parties",
            new ContractPartyRequest { AccountId = accountId, Role = ContractPartyRole.Landlord }))
            .EnsureSuccessStatusCode();

        // The contract's stored type is changed underneath the party, behind the API — the shape a
        // pre-matrix row has after an upgrade the migration could not reach.
        using (var scope = factory.Services.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
            var contract = await context.Contracts.FirstAsync(c => c.ContractId == id);
            contract.Type = Odyssey.Context.ContractType.Loan;
            await context.SaveChangesAsync();
        }

        var put = await client.PutAsJsonAsync($"{Path}/{id}", new UpdateContract
        {
            Name = "Renamed",
            Type = ContractType.Loan,
            StartDate = FixedToday.AddDays(-30),
        });

        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        Assert.Equal("Renamed", (await GetAsync(client, id)).Name);
    }

    // ── The Loan type (AC 19) ────────────────────────────────────────────────

    /// <summary>
    /// AC 19 — <c>Loan</c> round-trips through create, read, update and the type filter, and its
    /// column takes <c>Lender</c>/<c>Borrower</c> while refusing <c>Buyer</c>/<c>Seller</c>.
    /// </summary>
    [Fact]
    public async Task LoanContracts_RoundTripAndTakeLenderAndBorrowerOnly()
    {
        await using var factory = new ApiFactory(ReadWrite);
        var accountId = await SeedAccountAsync(factory);
        using var client = factory.CreateClient();

        var id = await CreateAsync(client, ContractType.Loan);
        Assert.Equal(ContractType.Loan, (await GetAsync(client, id)).Type);

        foreach (var role in new[] { ContractPartyRole.Lender, ContractPartyRole.Borrower })
        {
            var add = await client.PostAsJsonAsync($"{Path}/{id}/parties",
                new ContractPartyRequest { AccountId = accountId, Role = role });
            Assert.Equal(HttpStatusCode.Created, add.StatusCode);
        }

        foreach (var role in new[] { ContractPartyRole.Buyer, ContractPartyRole.Seller })
        {
            var add = await client.PostAsJsonAsync($"{Path}/{id}/parties",
                new ContractPartyRequest { AccountId = accountId, Role = role });
            Assert.Equal(HttpStatusCode.UnprocessableEntity, add.StatusCode);
        }

        // The type filter resolves the new member rather than dropping the row.
        var filtered = await client.GetFromJsonAsync<Odyssey.Dtos.PagedResult<ContractListItem>>(
            $"{Path}?type={(int)ContractType.Loan}");
        Assert.Contains(filtered!.Items, item => item.ContractId == id);
    }

    // ── Mass assignment (AC 10) ──────────────────────────────────────────────

    /// <summary>
    /// AC 10 — a party write carrying a POPULATED nested account object neither creates nor mutates
    /// that account. <c>ContractPartyRequest</c> carries scalar ids only, so the nested object is not
    /// bound at all; re-pinned here because issue #157 rewrote the property the request hangs on.
    /// </summary>
    [Fact]
    public async Task AddParty_WithANestedAccountObject_NeitherCreatesNorMutatesIt()
    {
        await using var factory = new ApiFactory(ReadWrite);
        var accountId = await SeedAccountAsync(factory);
        using var client = factory.CreateClient();

        var id = await CreateAsync(client, ContractType.Rental);
        var add = await client.PostAsJsonAsync($"{Path}/{id}/parties", new
        {
            accountId,
            role = (int)ContractPartyRole.Landlord,
            account = new { accountId, name = "HACKED", accountNumber = "EVIL" },
        });
        Assert.Equal(HttpStatusCode.Created, add.StatusCode);

        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        Assert.Equal(1, await context.Accounts.CountAsync());
        Assert.Equal("Everyday Checking", (await context.Accounts.FirstAsync()).Name);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>The shared fixture, with the same fixed clock the sibling contract suite uses.</summary>
    private sealed class ApiFactory(IReadOnlyCollection<string>? permissions) : OdysseyApiFactory(
        permissions, "contract-matrix-actor", configuration: null, configureServices: services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(new FixedTimeProvider(FixedToday));
        })
    {
    }

    private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow);
    }

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

    private static async Task<Guid> SeedAccountAsync(WebApplicationFactory<Program> factory)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();

        var account = new Account
        {
            AccountId = Guid.NewGuid(),
            Name = "Everyday Checking",
            Description = "Primary",
            Opened = FixedToday.AddYears(-1),
            AccountType = ContextAccountType.CheckingAccount,
            CurrencyCode = "USD",
        };
        context.Accounts.Add(account);
        await context.SaveChangesAsync();
        return account.AccountId;
    }

    private static async Task<bool> HasErrorKeyAsync(HttpResponseMessage response, string field)
    {
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return problem.RootElement.TryGetProperty("errors", out var errors)
            && errors.EnumerateObject().Any(error => string.Equals(error.Name, field, StringComparison.OrdinalIgnoreCase));
    }
}
