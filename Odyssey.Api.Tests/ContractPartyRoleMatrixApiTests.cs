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
/// <c>Loan</c> type (AC 19), the status/field-key contract both refusals carry (AC 24), and the two
/// contact-delete criteria that are decided before any relational statement runs — the claim-gated
/// <c>409</c> payload (AC 23) and the composed detach gate's <c>403</c> (AC 13).
///
/// <para>
/// Issue #169 adds the same enforcement for the three object roles and the universal
/// <c>Guarantor</c> (its AC 1–6, 9, 14–17) and for how the pre-existing per-contract party cap
/// interacts with them (its AC 22–24).
/// </para>
/// </summary>
/// <remarks>
/// Everything here runs on the fast tier, and the dividing line is <b>not</b> "does it touch
/// <c>ContactReferenceGuard</c>" — the last two do. It is whether the response is decided <em>before</em>
/// that guard's relational-only <c>ExecuteUpdate</c>/<c>ExecuteDelete</c> cleanup, which throws on the
/// InMemory provider. The <c>409</c> returns from <c>ContactController</c> without entering the service
/// at all, and the <c>403</c> is raised by <c>EnsureDetachPermitted</c> ahead of <c>StageLinkDetach</c>,
/// so both are reachable — and only here do they go through the real ASP.NET Core pipeline, which is
/// what AC 13 actually asserts about a status no <c>DomainException</c> subtype had mapped to before.
///
/// <para>
/// The remaining beneficiary-blocker criteria (11–14, 21–22) and the migration one (25) do reach that
/// cleanup, so they live in <c>Odyssey.IntegrationTests</c> against real MariaDB.
/// </para>
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
    /// AC 9, re-pinned by issue #169 AC 8 and issue #187 AC 9 — <b>every one of the 200 cells</b> is
    /// exercised against the live endpoint: the 78 legal ones are accepted and the 122 rejected ones
    /// refused. Iterating the
    /// matrix rather than sampling it is what makes a cell unable to disagree silently between the
    /// declaration and the validator.
    /// </summary>
    /// <remarks>
    /// These two figures are a SECOND, independent pin of the count
    /// <c>ContractPartyRoleGuardTests.LegalCellCount_Is78Of200</c> asserts off the declaration alone.
    /// Nothing links them, so a widening that updates one and not the other is green on one file and
    /// red on the other. This one drives a real HTTP round trip per cell (200 of them since issue
    /// #187, up from 162 and 135 before that); if it ever outgrows its time budget, narrow it by type rather than dropping
    /// the assertion.
    /// </remarks>
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

        Assert.Equal(78, legal);
        Assert.Equal(122, rejected);
    }

    // ── The three object roles and the universal Guarantor (issue #169) ──────

    /// <summary>
    /// Issue #169 AC 1 — every legal (type, object-role) pair is accepted and reads back as itself.
    /// The cross-product test above already counts these cells; this one proves the role SURVIVES the
    /// round trip rather than merely being accepted, which is what an ordinal appended to two
    /// declarations can get wrong while every status code stays right.
    /// </summary>
    [Theory]
    [InlineData(ContractType.Rental, ContractPartyRole.Property)]
    [InlineData(ContractType.Purchase, ContractPartyRole.Property)]
    [InlineData(ContractType.Loan, ContractPartyRole.Collateral)]
    [InlineData(ContractType.Service, ContractPartyRole.Object)]
    [InlineData(ContractType.Rental, ContractPartyRole.Object)]
    [InlineData(ContractType.Subscription, ContractPartyRole.Object)]
    [InlineData(ContractType.Purchase, ContractPartyRole.Object)]
    [InlineData(ContractType.Loan, ContractPartyRole.Object)]
    [InlineData(ContractType.Membership, ContractPartyRole.Object)]
    [InlineData(ContractType.Other, ContractPartyRole.Object)]
    public async Task AddParty_WithAnObjectRoleItsTypeTakes_Returns201AndPersistsIt(
        ContractType type, ContractPartyRole role)
    {
        await using var factory = new ApiFactory(ReadWrite);
        var accountId = await SeedAccountAsync(factory);
        using var client = factory.CreateClient();

        var id = await CreateAsync(client, type);
        var add = await client.PostAsJsonAsync($"{Path}/{id}/parties",
            new ContractPartyRequest { AccountId = accountId, Role = role });

        Assert.Equal(HttpStatusCode.Created, add.StatusCode);
        Assert.Equal(role, (await add.Content.ReadFromJsonAsync<ExistingContractParty>())!.Role);
        Assert.Equal(role, Assert.Single((await GetAsync(client, id)).Parties).Role);
    }

    /// <summary>
    /// Issue #169 AC 2, 3 and 4 — the exclusions hold over HTTP and nothing is written. The first two
    /// are the deliberate §4.3 exclusions (an employment contract's object is the employee's labour;
    /// an insurance contract's is already named by <c>Insured</c>); the last two are the type-specific
    /// roles failing to leak outside their own columns.
    /// </summary>
    [Theory]
    [InlineData(ContractType.Employment, ContractPartyRole.Object)]
    [InlineData(ContractType.Insurance, ContractPartyRole.Object)]
    [InlineData(ContractType.Rental, ContractPartyRole.Collateral)]
    [InlineData(ContractType.Loan, ContractPartyRole.Property)]
    public async Task AddParty_WithAnObjectRoleItsTypeRejects_Returns422_AndWritesNothing(
        ContractType type, ContractPartyRole role)
    {
        await using var factory = new ApiFactory(ReadWrite);
        var accountId = await SeedAccountAsync(factory);
        using var client = factory.CreateClient();

        var id = await CreateAsync(client, type);
        var add = await client.PostAsJsonAsync($"{Path}/{id}/parties",
            new ContractPartyRequest { AccountId = accountId, Role = role });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, add.StatusCode);
        Assert.True(await HasErrorKeyAsync(add, nameof(ContractPartyRequest.Role)));
        Assert.Empty((await GetAsync(client, id)).Parties);
    }

    /// <summary>
    /// Issue #169 AC 5 — role stays ORTHOGONAL to the target kind. An object party is <em>expected</em>
    /// to be an account, but a contact target is equally legal and neither is refused on grounds of
    /// kind. The non-goal this pins is a constraint tying the two, which a later reader is likely to
    /// add as a "missing" check.
    /// </summary>
    [Theory]
    [InlineData(ContractType.Rental, ContractPartyRole.Object)]
    [InlineData(ContractType.Rental, ContractPartyRole.Property)]
    [InlineData(ContractType.Loan, ContractPartyRole.Collateral)]
    public async Task AddParty_InANewRole_TakesEitherTargetKind(ContractType type, ContractPartyRole role)
    {
        await using var factory = new ApiFactory(ReadWrite);
        var accountId = await SeedAccountAsync(factory);
        var contactId = await SeedContactAsync(factory);
        using var client = factory.CreateClient();

        var id = await CreateAsync(client, type);

        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync($"{Path}/{id}/parties",
            new ContractPartyRequest { AccountId = accountId, Role = role })).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync($"{Path}/{id}/parties",
            new ContractPartyRequest { ContactId = contactId, Role = role })).StatusCode);

        Assert.Equal(2, (await GetAsync(client, id)).Parties.Count(party => party.Role == role));
    }

    /// <summary>
    /// Issue #169 AC 6 — <c>Guarantor</c> is accepted on ALL TEN types (<c>Deposit</c> since issue #187), including the five that
    /// rejected it before this change. A party standing behind another's obligation belongs to no
    /// particular kind of agreement.
    /// </summary>
    [Fact]
    public async Task AddParty_WithGuarantor_Returns201_OnEveryContractType()
    {
        await using var factory = new ApiFactory(ReadWrite);
        var accountId = await SeedAccountAsync(factory);
        using var client = factory.CreateClient();

        foreach (var type in Enum.GetValues<ContractType>())
        {
            var id = await CreateAsync(client, type);
            var add = await client.PostAsJsonAsync($"{Path}/{id}/parties",
                new ContractPartyRequest { AccountId = accountId, Role = ContractPartyRole.Guarantor });

            Assert.Equal(HttpStatusCode.Created, add.StatusCode);
        }
    }

    /// <summary>
    /// Issue #169 AC 9 — the <c>422</c> message lists a Rental's legal roles VERBATIM, in
    /// suggested-then-allowed order. The whole list is asserted rather than a member of it, because
    /// what changed is the ORDER as much as the membership: <c>Guarantor</c> now leads the universal
    /// trio on every type, and a new suggested role precedes it here.
    /// </summary>
    [Fact]
    public async Task ARejectedRoleOnARental_NamesTheLegalRoles_InSuggestedThenAllowedOrder()
    {
        await using var factory = new ApiFactory(ReadWrite);
        var accountId = await SeedAccountAsync(factory);
        using var client = factory.CreateClient();

        var id = await CreateAsync(client, ContractType.Rental);
        var add = await client.PostAsJsonAsync($"{Path}/{id}/parties",
            new ContractPartyRequest { AccountId = accountId, Role = ContractPartyRole.Employee });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, add.StatusCode);
        Assert.Contains(
            "The roles it can have are: Landlord, Tenant, Property, Object, Guarantor, Broker, Other.",
            await add.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Issue #169 AC 15 and AC 16 — several parties may hold the SAME new role against DIFFERENT
    /// targets (two purchased items, a car and a boat as collateral), while the same target twice in
    /// one role is still the <c>409</c> the composite unique indexes describe.
    /// </summary>
    [Fact]
    public async Task TwoObjectPartiesAgainstDifferentTargets_BothPersist_AndADuplicateIs409()
    {
        await using var factory = new ApiFactory(ReadWrite);
        var accountId = await SeedAccountAsync(factory);
        var secondAccountId = await SeedAccountAsync(factory, "Holiday Cabin");
        using var client = factory.CreateClient();

        var id = await CreateAsync(client, ContractType.Purchase);

        foreach (var target in new[] { accountId, secondAccountId })
        {
            Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync($"{Path}/{id}/parties",
                new ContractPartyRequest { AccountId = target, Role = ContractPartyRole.Property })).StatusCode);
        }

        Assert.Equal(2, (await GetAsync(client, id)).Parties.Count);

        var duplicate = await client.PostAsJsonAsync($"{Path}/{id}/parties",
            new ContractPartyRequest { AccountId = accountId, Role = ContractPartyRole.Property });
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
    }

    /// <summary>
    /// Issue #169 AC 17 — an ordinal outside the enum is still a <c>400</c> from model validation,
    /// before the service runs. The two retired holes are included: widening the enum must not make
    /// <c>0</c> or <c>5</c> bindable again. <c>22</c> is the first ordinal past <c>Custodian</c>
    /// (issue #187 §7.9).
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(22)]
    [InlineData(99)]
    public async Task AddParty_WithAnUndefinedRoleOrdinal_Returns400(int ordinal)
    {
        await using var factory = new ApiFactory(ReadWrite);
        var accountId = await SeedAccountAsync(factory);
        using var client = factory.CreateClient();

        var id = await CreateAsync(client, ContractType.Rental);
        var add = await client.PostAsJsonAsync($"{Path}/{id}/parties", new { accountId, role = ordinal });

        Assert.Equal(HttpStatusCode.BadRequest, add.StatusCode);
        Assert.Empty((await GetAsync(client, id)).Parties);
    }

    /// <summary>
    /// Issue #169 AC 14 — the mass-assignment regression, re-asserted for a NEW role. The request
    /// carries scalar ids only, so a nested account object rides along unbound whichever role names
    /// the link.
    /// </summary>
    [Fact]
    public async Task AddParty_InANewRole_WithANestedAccountObject_NeitherCreatesNorMutatesIt()
    {
        await using var factory = new ApiFactory(ReadWrite);
        var accountId = await SeedAccountAsync(factory);
        using var client = factory.CreateClient();

        var id = await CreateAsync(client, ContractType.Rental);
        var add = await client.PostAsJsonAsync($"{Path}/{id}/parties", new
        {
            accountId,
            role = (int)ContractPartyRole.Property,
            account = new { accountId, name = "HACKED", accountNumber = "EVIL" },
        });
        Assert.Equal(HttpStatusCode.Created, add.StatusCode);

        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        Assert.Equal(1, await context.Accounts.CountAsync());
        Assert.Equal("Everyday Checking", (await context.Accounts.FirstAsync()).Name);
    }

    // ── The party cap, against the new roles (issue #169 AC 22–24) ───────────

    /// <summary>
    /// Issue #169 AC 22 — the pre-existing per-contract cap is unchanged and counts across ALL roles,
    /// so an object party on a full contract is refused like any other. The <c>422</c> is keyed on the
    /// TARGET field rather than <c>role</c>: the role was fine, the contract was full, and keying it
    /// on <c>role</c> would mark the one control that is not the problem.
    /// </summary>
    [Fact]
    public async Task AddParty_InANewRole_OnAFullContract_Returns422KeyedOnTheTargetField()
    {
        await using var factory = new ApiFactory(ReadWrite);
        await SystemSettingsSeed.SetAsync(factory.Services, SystemSettingsKeys.ContractMaxPartiesPerContract, "1");
        var accountId = await SeedAccountAsync(factory);
        var contactId = await SeedContactAsync(factory);
        using var client = factory.CreateClient();

        var id = await CreateAsync(client, ContractType.Rental);
        (await client.PostAsJsonAsync($"{Path}/{id}/parties",
            new ContractPartyRequest { AccountId = accountId, Role = ContractPartyRole.Landlord }))
            .EnsureSuccessStatusCode();

        var overCap = await client.PostAsJsonAsync($"{Path}/{id}/parties",
            new ContractPartyRequest { ContactId = contactId, Role = ContractPartyRole.Property });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, overCap.StatusCode);
        Assert.True(await HasErrorKeyAsync(overCap, nameof(ContractPartyRequest.ContactId)));
        Assert.False(await HasErrorKeyAsync(overCap, nameof(ContractPartyRequest.Role)));
    }

    /// <summary>
    /// Issue #169 AC 23 — a contract that is BOTH full and given an illegal role answers the role
    /// <c>422</c>, keyed on <c>role</c>. The legality check runs first, so the caller is told the
    /// actionable thing: a legal role would still be refused by the cap, but an illegal one is refused
    /// whatever the cap says.
    /// </summary>
    [Fact]
    public async Task AddParty_OnAFullContract_WithAnIllegalRole_ReportsTheRole_NotTheCap()
    {
        await using var factory = new ApiFactory(ReadWrite);
        await SystemSettingsSeed.SetAsync(factory.Services, SystemSettingsKeys.ContractMaxPartiesPerContract, "1");
        var accountId = await SeedAccountAsync(factory);
        var contactId = await SeedContactAsync(factory);
        using var client = factory.CreateClient();

        var id = await CreateAsync(client, ContractType.Rental);
        (await client.PostAsJsonAsync($"{Path}/{id}/parties",
            new ContractPartyRequest { AccountId = accountId, Role = ContractPartyRole.Landlord }))
            .EnsureSuccessStatusCode();

        var refused = await client.PostAsJsonAsync($"{Path}/{id}/parties",
            new ContractPartyRequest { ContactId = contactId, Role = ContractPartyRole.Collateral });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        Assert.True(await HasErrorKeyAsync(refused, nameof(ContractPartyRequest.Role)));
        Assert.False(await HasErrorKeyAsync(refused, nameof(ContractPartyRequest.ContactId)));
    }

    /// <summary>
    /// Issue #169 AC 24, the inverse of AC 22 — an EDIT on a contract already ABOVE the cap succeeds.
    /// An in-place update is row-count-neutral, so the cap is deliberately not re-checked there; a
    /// "consistency" change that re-checked it would strand every party on an over-cap contract as
    /// uneditable, with no way back down.
    /// </summary>
    /// <remarks>
    /// The over-cap state is produced the only way it occurs in life — by lowering the cap under a
    /// contract that already holds more, here by writing the extra rows behind the API rather than
    /// through it.
    /// </remarks>
    [Fact]
    public async Task UpdateParty_OnAContractAboveItsCap_Returns200()
    {
        await using var factory = new ApiFactory(ReadWrite);
        await SystemSettingsSeed.SetAsync(factory.Services, SystemSettingsKeys.ContractMaxPartiesPerContract, "1");
        var accountId = await SeedAccountAsync(factory);
        using var client = factory.CreateClient();

        var id = await CreateAsync(client, ContractType.Rental);
        var add = await client.PostAsJsonAsync($"{Path}/{id}/parties",
            new ContractPartyRequest { AccountId = accountId, Role = ContractPartyRole.Landlord });
        var party = await add.Content.ReadFromJsonAsync<ExistingContractParty>();

        using (var scope = factory.Services.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
            foreach (var role in new[]
                     {
                         Odyssey.Context.ContractPartyRole.Property,
                         Odyssey.Context.ContractPartyRole.Object,
                     })
            {
                context.ContractParties.Add(new ContractParty
                {
                    ContractPartyId = Guid.NewGuid(),
                    ContractId = id,
                    AccountId = accountId,
                    Role = role,
                });
            }

            await context.SaveChangesAsync();
        }

        var put = await client.PutAsJsonAsync($"{Path}/{id}/parties/{party!.ContractPartyId}",
            new ContractPartyRequest { AccountId = accountId, Role = ContractPartyRole.Tenant });

        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        Assert.Equal(ContractPartyRole.Tenant, (await put.Content.ReadFromJsonAsync<ExistingContractParty>())!.Role);
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

    // ── The contact-delete blocker, over real HTTP (AC 13, AC 23) ────────────
    //
    // These live on the FAST tier deliberately, even though the rest of the beneficiary-blocker
    // criteria are in Odyssey.IntegrationTests. What pushes those there is ContactReferenceGuard's
    // relational-only ExecuteUpdate/ExecuteDelete cleanup — and BOTH refusals below fire strictly
    // BEFORE it: the 409 returns from the controller without entering the service at all, and the 403
    // is raised by EnsureDetachPermitted ahead of StageLinkDetach. So the controller's
    // claim-conditional shaping and the exception-to-status wiring are reachable here, and only here
    // do they go through the real ASP.NET Core pipeline.

    /// <summary>
    /// AC 23 — the <c>409</c> payload's contract NAMES are claim-gated. A caller holding
    /// <c>contracts.read</c> is told which contracts block the delete.
    /// </summary>
    [Fact]
    public async Task DeleteContact_NamedAsAContractBeneficiary_Returns409_NamingTheContract()
    {
        await using var factory = new ApiFactory([.. ReadWrite, PermissionClaims.ContactsDelete]);
        var (contactId, contractId) = await SeedBeneficiaryAsync(factory);
        using var client = factory.CreateClient();

        var delete = await client.DeleteAsync($"/api/contacts/{contactId}");

        Assert.Equal(HttpStatusCode.Conflict, delete.StatusCode);

        using var problem = JsonDocument.Parse(await delete.Content.ReadAsStringAsync());
        var blockers = problem.RootElement.GetProperty("contractBeneficiaries");
        Assert.Equal(1, blockers.GetProperty("totalLinks").GetInt32());
        Assert.Equal(1, blockers.GetProperty("contractCount").GetInt32());

        var named = Assert.Single(blockers.GetProperty("contracts").EnumerateArray().ToList());
        Assert.Equal(contractId, named.GetProperty("contractId").GetGuid());
        Assert.Equal("Whole-of-life cover", named.GetProperty("contractName").GetString());

        // Refused, not partially applied.
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        Assert.True(await context.Contacts.AnyAsync(c => c.ContactId == contactId));
    }

    /// <summary>
    /// AC 23, the other half — a caller holding <c>contacts.delete</c> but NOT <c>contracts.read</c>
    /// gets the same COUNTS and an EMPTY array. The count is what makes the <c>409</c> actionable: it
    /// says how many links must go, and the detach valve never asks the caller to name them.
    /// </summary>
    /// <remarks>
    /// An empty array beside a non-zero count is precisely the "you may not see which" case, and it is
    /// why the two are separate fields rather than one list whose length is the count.
    /// </remarks>
    [Fact]
    public async Task DeleteContact_WithoutContractsRead_Returns409_WithCountsButNoNames()
    {
        await using var factory = new ApiFactory([.. ReadWrite, PermissionClaims.ContactsDelete]);
        var (contactId, _) = await SeedBeneficiaryAsync(factory);

        await using var blindFactory = new ApiFactory([PermissionClaims.ContactsDelete], factory);
        using var blind = blindFactory.CreateClient();

        var delete = await blind.DeleteAsync($"/api/contacts/{contactId}");

        Assert.Equal(HttpStatusCode.Conflict, delete.StatusCode);

        var body = await delete.Content.ReadAsStringAsync();
        using var problem = JsonDocument.Parse(body);
        var blockers = problem.RootElement.GetProperty("contractBeneficiaries");

        Assert.Equal(1, blockers.GetProperty("totalLinks").GetInt32());
        Assert.Equal(1, blockers.GetProperty("contractCount").GetInt32());
        Assert.Empty(blockers.GetProperty("contracts").EnumerateArray().ToList());

        // The name is withheld from the whole document, not merely from that array.
        Assert.DoesNotContain("Whole-of-life cover", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// AC 13 — the composed detach gate over real HTTP: <c>contacts.delete</c> without
    /// <c>contracts.update</c> is a <c>403</c>, and the contact survives.
    /// </summary>
    /// <remarks>
    /// This is the test issue #157 §7.3's "wiring consequence" asks for. <c>DomainForbiddenException</c>
    /// is the first <c>DomainException</c> subtype to map to <c>403</c>, and asserting its
    /// <c>StatusCode</c> property in a unit test would prove only that the constant is right — not that
    /// <c>GlobalExceptionHandler</c> turns it into a <c>403</c> response. Only a request through the
    /// real pipeline shows that, and a silent downgrade to the refused delete would show up here as a
    /// <c>409</c>.
    /// </remarks>
    [Fact]
    public async Task DeleteContact_WithDetach_WithoutContractsUpdate_Returns403_AndKeepsTheContact()
    {
        await using var factory = new ApiFactory([.. ReadWrite, PermissionClaims.ContactsDelete]);
        var (contactId, _) = await SeedBeneficiaryAsync(factory);

        // contacts.delete alone: enough to ask, not enough to destroy a contract party.
        await using var deleterFactory = new ApiFactory([PermissionClaims.ContactsDelete], factory);
        using var deleter = deleterFactory.CreateClient();

        var delete = await deleter.DeleteAsync($"/api/contacts/{contactId}?detachBlockingLinks=true");

        Assert.Equal(HttpStatusCode.Forbidden, delete.StatusCode);
        Assert.Contains("update contracts", await delete.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);

        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        Assert.True(await context.Contacts.AnyAsync(c => c.ContactId == contactId));
        Assert.Equal(1, await context.ContractParties.CountAsync(p => p.ContactId == contactId));
    }

    /// <summary>
    /// The same request by a caller that DOES hold <c>contracts.update</c> is not refused — which is
    /// what makes the test above a claim check rather than a blanket refusal of the detach flag.
    /// </summary>
    [Fact]
    public async Task DeleteContact_WithDetach_WithContractsUpdate_IsNotForbidden()
    {
        await using var factory = new ApiFactory([.. ReadWrite, PermissionClaims.ContactsDelete]);
        var (contactId, _) = await SeedBeneficiaryAsync(factory);
        using var client = factory.CreateClient();

        var delete = await client.DeleteAsync($"/api/contacts/{contactId}?detachBlockingLinks=true");

        // NOT asserting success: the delete itself runs ContactReferenceGuard's relational-only
        // cleanup, which throws on the InMemory provider — that half is covered against real MariaDB
        // in ContractBeneficiaryBlockerIntegrationTests. What this pins is that the request gets PAST
        // the claim gate, so the 403 above is attributable to the missing claim and nothing else.
        Assert.NotEqual(HttpStatusCode.Forbidden, delete.StatusCode);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// A contact named as a <c>Beneficiary</c> on an Insurance contract — the one type that SUGGESTS
    /// that role, so the fixture is a contract the API itself would have accepted.
    /// </summary>
    private static async Task<(Guid ContactId, Guid ContractId)> SeedBeneficiaryAsync(
        WebApplicationFactory<Program> factory)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        await context.Database.EnsureCreatedAsync();

        var contactId = Guid.NewGuid();
        context.Contacts.Add(new Contact
        {
            ContactId = contactId,
            ExternalUid = $"urn:uuid:{Guid.NewGuid()}",
            NormalizedName = "SAM RIVERA",
            Type = Odyssey.Dtos.ContactType.Person,
            PersonDetails = new() { ContactId = contactId, FirstName = "Sam", LastName = "Rivera" },
        });

        var contractId = Guid.NewGuid();
        context.Contracts.Add(new Contract
        {
            ContractId = contractId,
            Name = "Whole-of-life cover",
            Type = Odyssey.Context.ContractType.Insurance,
            CreatedAtUtc = FixedToday,
        });

        context.ContractParties.Add(new ContractParty
        {
            ContractPartyId = Guid.NewGuid(),
            ContractId = contractId,
            ContactId = contactId,
            Role = Odyssey.Context.ContractPartyRole.Beneficiary,
        });

        await context.SaveChangesAsync();
        return (contactId, contractId);
    }

    /// <summary>The shared fixture, with the same fixed clock the sibling contract suite uses.</summary>
    private sealed class ApiFactory : OdysseyApiFactory
    {
        private const string ActorUserId = "contract-matrix-actor";

        public ApiFactory(IReadOnlyCollection<string>? permissions)
            : base(permissions, ActorUserId, configuration: null, configureServices: Clock)
        {
        }

        /// <summary>
        /// A second principal over the SAME in-memory store, so a test can exercise one caller's write
        /// against another caller's claims — the only way to reach the claim-conditional 409 and the
        /// composed detach gate.
        /// </summary>
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

    private static async Task<Guid> SeedAccountAsync(
        WebApplicationFactory<Program> factory, string name = "Everyday Checking")
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

    /// <summary>
    /// A bare contact to link, for the cases that prove a role takes either target kind. Deliberately
    /// not the beneficiary fixture: nothing here should be able to block a delete.
    /// </summary>
    private static async Task<Guid> SeedContactAsync(
        WebApplicationFactory<Program> factory, string first = "Dana", string last = "Okafor")
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        await context.Database.EnsureCreatedAsync();

        var contactId = Guid.NewGuid();
        context.Contacts.Add(new Contact
        {
            ContactId = contactId,
            ExternalUid = $"urn:uuid:{Guid.NewGuid()}",
            NormalizedName = $"{first.ToUpperInvariant()} {last.ToUpperInvariant()}",
            Type = Odyssey.Dtos.ContactType.Person,
            PersonDetails = new() { ContactId = contactId, FirstName = first, LastName = last },
        });

        await context.SaveChangesAsync();
        return contactId;
    }

    private static async Task<bool> HasErrorKeyAsync(HttpResponseMessage response, string field)
    {
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return problem.RootElement.TryGetProperty("errors", out var errors)
            && errors.EnumerateObject().Any(error => string.Equals(error.Name, field, StringComparison.OrdinalIgnoreCase));
    }
}
