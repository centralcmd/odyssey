using Odyssey.Dtos;
using System.Net;
using System.Text.Json;
using System.Net.Http.Json;
using Odyssey.Dtos.Authorization;
using Odyssey.Context;
using Odyssey.Dtos.Finance;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;
using Odyssey.Api.Tests.Infrastructure;
using ContextAccountType = Odyssey.Context.AccountType;
using ContractType = Odyssey.Dtos.Finance.ContractType;
// Both halves of the aligned pair are in scope here (Odyssey.Context for the seed, Odyssey.Dtos.Finance
// for the wire), so the wire one is named explicitly — the same shape ContractType already needed.
using ContractPartyRole = Odyssey.Dtos.Finance.ContractPartyRole;
using ContractFileType = Odyssey.Dtos.Finance.ContractFileType;

namespace Odyssey.Api.Tests;

public class ContractsApiTests
{
    private const string ActorUserId = "contracts-actor-id";
    private const string Path = "/api/contracts";

    // A fixed UTC "today" so derived status is deterministic.
    private static readonly DateTime FixedToday = new(2026, 6, 15, 8, 0, 0, DateTimeKind.Utc);

    private static readonly string[] ReadOnly = [PermissionClaims.ContractsRead];

    private static readonly string[] ReadWrite =
    [
        PermissionClaims.ContractsRead, PermissionClaims.ContractsCreate,
        PermissionClaims.ContractsUpdate, PermissionClaims.ContractsDelete,
    ];

    private static readonly string[] ReadWriteWithFiles =
    [
        PermissionClaims.ContractsRead, PermissionClaims.ContractsCreate,
        PermissionClaims.ContractsUpdate, PermissionClaims.ContractsDelete, PermissionClaims.FilesRead,
    ];

    // ── Authorization matrix (criterion #8) ───────────────────────────────────

    [Fact]
    public async Task List_Unauthenticated_ReturnsUnauthorized()
    {
        await using var factory = new ApiFactory(permissions: null);
        using var client = factory.CreateClient();

        var response = await client.GetAsync(Path);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task List_WithoutReadPermission_ReturnsForbidden()
    {
        await using var factory = new ApiFactory(permissions: []);
        using var client = factory.CreateClient();

        var response = await client.GetAsync(Path);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Mutations_WithReadOnlyPermission_ReturnForbidden()
    {
        await using var factory = new ApiFactory(ReadOnly);
        using var client = factory.CreateClient();

        var post = await client.PostAsJsonAsync(Path, NewContract());
        Assert.Equal(HttpStatusCode.Forbidden, post.StatusCode);
    }

    [Fact]
    public async Task AttachFile_WithoutFilesReadClaim_ReturnsForbidden()
    {
        // Confused-deputy guard: contracts.update alone is not enough to attach a file.
        await using var factory = new ApiFactory(ReadWrite);
        var pdfId = await SeedFileAsync(factory, "contract.pdf", "application/pdf");
        using var client = factory.CreateClient();

        var id = await CreateAsync(client);

        var attach = await client.PostAsJsonAsync($"{Path}/{id}/files", AttachRequest(pdfId));
        Assert.Equal(HttpStatusCode.Forbidden, attach.StatusCode);
    }

    // ── Create + read round-trip (criterion #1) ────────────────────────────────

    [Fact]
    public async Task Create_Then_Get_RoundTrips()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();

        var id = await CreateAsync(client);
        var fetched = await client.GetFromJsonAsync<ExistingContract>($"{Path}/{id}");

        Assert.NotNull(fetched);
        Assert.Equal("Employment agreement", fetched!.Name);
        Assert.Equal(ContractType.Employment, fetched.Type);
        Assert.Equal(ContractStatus.Active, fetched.Status);
    }

    [Fact]
    public async Task Create_MissingName_ReturnsBadRequest()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();

        // Name omitted entirely → model validation 400.
        var post = await client.PostAsJsonAsync(Path, new
        {
            type = ContractType.Other,
            startDate = FixedToday,
        });
        Assert.Equal(HttpStatusCode.BadRequest, post.StatusCode);
    }

    [Fact]
    public async Task Create_EndBeforeStart_ReturnsBadRequest()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();

        var post = await client.PostAsJsonAsync(Path, new NewContract
        {
            Name = "Bad dates",
            Type = ContractType.Service,
            StartDate = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            EndDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        });
        Assert.Equal(HttpStatusCode.BadRequest, post.StatusCode);
    }

    // ── Derived status (criterion #2) ──────────────────────────────────────────

    [Fact]
    public async Task DerivedStatus_ReflectsDatesAndArchive()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();

        var upcoming = await CreateAsync(client, start: FixedToday.AddDays(10));
        var expired = await CreateAsync(client, start: FixedToday.AddDays(-100), end: FixedToday.AddDays(-1));
        var active = await CreateAsync(client, start: FixedToday.AddDays(-10), end: FixedToday.AddDays(10));

        Assert.Equal(ContractStatus.Upcoming, (await GetAsync(client, upcoming)).Status);
        Assert.Equal(ContractStatus.Expired, (await GetAsync(client, expired)).Status);
        Assert.Equal(ContractStatus.Active, (await GetAsync(client, active)).Status);

        // Archive via PUT → status becomes Archived, outranking the status the dates would derive.
        // The same request ends the term, since only an ended contract can be archived.
        (await client.PutAsJsonAsync($"{Path}/{active}", UpdateContract(isArchived: true, endDate: Lapsed)))
            .EnsureSuccessStatusCode();
        Assert.Equal(ContractStatus.Archived, (await GetAsync(client, active)).Status);
    }

    // ── Archive ≠ delete, via update (criterion #9) ────────────────────────────

    [Fact]
    public async Task Put_ArchiveThenUnarchive_TogglesListAndSummary()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();

        var id = await CreateAsync(client);

        var before = await client.GetFromJsonAsync<ContractSummary>($"{Path}/summary");
        Assert.Equal(1, before!.TotalContracts);
        Assert.Equal(1, before.CountsByStatus.Active);

        // Archive via PUT — the contract is kept and still shown in the default list (the design
        // system no longer hides archived rows); a status filter still narrows to just Archived.
        var archive = await client.PutAsJsonAsync($"{Path}/{id}", UpdateContract(isArchived: true, endDate: Lapsed));
        Assert.Equal(HttpStatusCode.OK, archive.StatusCode);

        var fetched = await GetAsync(client, id);
        Assert.NotNull(fetched.Archived);                                  // kept, not deleted
        var defaultList = await client.GetPagedItemsAsync<ContractListItem>(Path);
        Assert.Contains(defaultList!, c => c.ContractId == id                       // shown by default,
            && c.Archived is not null && c.Status == ContractStatus.Archived);      // reading Archived
        var withArchived = await client.GetPagedItemsAsync<ContractListItem>($"{Path}?statuses=Archived");
        Assert.Contains(withArchived!, c => c.ContractId == id);           // and via the status filter

        // Summary rollups drop it from the live totals but surface it in the by-status breakdown.
        var afterSummary = await client.GetFromJsonAsync<ContractSummary>($"{Path}/summary");
        Assert.Equal(0, afterSummary!.CountsByStatus.Active);
        Assert.Equal(1, afterSummary.CountsByStatus.Archived);

        // A non-Archived status filter keeps a genuinely-active contract and still excludes the archived
        // one — the invariant that moved from the removed SQL pre-filter onto the derived-status filter.
        var activeId = await CreateAsync(client);
        var activeOnly = await client.GetPagedItemsAsync<ContractListItem>($"{Path}?statuses=Active");
        Assert.Contains(activeOnly!, c => c.ContractId == activeId);
        Assert.DoesNotContain(activeOnly!, c => c.ContractId == id);

        // Unarchive via PUT.
        var unarchive = await client.PutAsJsonAsync($"{Path}/{id}", UpdateContract(isArchived: false));
        Assert.Equal(HttpStatusCode.OK, unarchive.StatusCode);
        Assert.Null((await GetAsync(client, id)).Archived);
    }

    // ── XOR party invariant (criterion #3) ─────────────────────────────────────

    [Fact]
    public async Task AddParty_RequiresExactlyOneTarget()
    {
        await using var factory = new ApiFactory(ReadWrite);
        var (accountId, contactId) = await SeedTargetsAsync(factory);
        using var client = factory.CreateClient();

        var id = await CreateAsync(client);

        // Zero targets → 400. The role is supplied so this stays the XOR rejection: without it the
        // body would fail model validation on Role first and prove nothing about the target rule.
        var none = await client.PostAsJsonAsync($"{Path}/{id}/parties",
            new ContractPartyRequest { Role = ContractPartyRole.Employer });
        Assert.Equal(HttpStatusCode.BadRequest, none.StatusCode);

        // Two targets → 400.
        var two = await client.PostAsJsonAsync($"{Path}/{id}/parties",
            new ContractPartyRequest { AccountId = accountId, ContactId = contactId, Role = ContractPartyRole.Employer });
        Assert.Equal(HttpStatusCode.BadRequest, two.StatusCode);

        // Exactly one → 201.
        var one = await client.PostAsJsonAsync($"{Path}/{id}/parties",
            new ContractPartyRequest { AccountId = accountId, Role = ContractPartyRole.Employer });
        Assert.Equal(HttpStatusCode.Created, one.StatusCode);
        var party = await one.Content.ReadFromJsonAsync<ExistingContractParty>();
        Assert.Equal(ContractPartyKind.Account, party!.Kind);
        Assert.Equal(accountId, party.Account!.AccountId);
    }

    [Fact]
    public async Task AddParty_MissingTarget_Returns404_DistinctFromContractNotFound()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();

        var id = await CreateAsync(client);

        // Contract exists, target does not → 404 (target not found).
        var missingTarget = await client.PostAsJsonAsync($"{Path}/{id}/parties",
            new ContractPartyRequest { AccountId = Guid.NewGuid(), Role = ContractPartyRole.Employer });
        Assert.Equal(HttpStatusCode.NotFound, missingTarget.StatusCode);

        // Contract does not exist → 404 (contract not found).
        var missingContract = await client.PostAsJsonAsync($"{Path}/{Guid.NewGuid()}/parties",
            new ContractPartyRequest { AccountId = Guid.NewGuid(), Role = ContractPartyRole.Employer });
        Assert.Equal(HttpStatusCode.NotFound, missingContract.StatusCode);
    }

    [Fact]
    public async Task AddParty_Duplicate_Returns409()
    {
        await using var factory = new ApiFactory(ReadWrite);
        var (accountId, _) = await SeedTargetsAsync(factory);
        using var client = factory.CreateClient();

        var id = await CreateAsync(client);
        var first = await client.PostAsJsonAsync($"{Path}/{id}/parties", new ContractPartyRequest { AccountId = accountId, Role = ContractPartyRole.Employer });
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        var duplicate = await client.PostAsJsonAsync($"{Path}/{id}/parties", new ContractPartyRequest { AccountId = accountId, Role = ContractPartyRole.Employer });
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
    }

    [Fact]
    public async Task AddParty_ExceedingCap_Returns422()
    {
        // Shrink the per-contract party cap so the boundary is cheap to hit. The cap moved out of
        // `ContractOptions` and into the settings store (issue #421 Wave 3), so it has to be set as a
        // row: the old `["Contracts:MaxPartiesPerContract"] = "1"` config entry is read by nothing and
        // left this test asserting a 422 that the shipped default of 20 would never produce.
        await using var factory = new ApiFactory(ReadWrite);
        await SystemSettingsSeed.SetAsync(factory.Services, SystemSettingsKeys.ContractMaxPartiesPerContract, "1");
        var (accountId, contactId) = await SeedTargetsAsync(factory);
        using var client = factory.CreateClient();

        var id = await CreateAsync(client);
        Assert.Equal(HttpStatusCode.Created,
            (await client.PostAsJsonAsync($"{Path}/{id}/parties", new ContractPartyRequest { AccountId = accountId, Role = ContractPartyRole.Employer })).StatusCode);

        var overCap = await client.PostAsJsonAsync($"{Path}/{id}/parties", new ContractPartyRequest { ContactId = contactId, Role = ContractPartyRole.Employer });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, overCap.StatusCode);
    }

    /// <summary>
    /// <b>Archiving does not lock a contract.</b> A party is added to an archived contract exactly as
    /// to a live one — archival hides it from the default list, it does not freeze it — and the
    /// archive stamp survives the write.
    /// </summary>
    [Fact]
    public async Task AddParty_OnArchivedContract_Succeeds()
    {
        await using var factory = new ApiFactory(ReadWrite);
        var (accountId, _) = await SeedTargetsAsync(factory);
        using var client = factory.CreateClient();

        var id = await CreateAsync(client);
        (await client.PutAsJsonAsync($"{Path}/{id}", UpdateContract(isArchived: true, endDate: Lapsed)))
            .EnsureSuccessStatusCode();

        var add = await client.PostAsJsonAsync($"{Path}/{id}/parties", new ContractPartyRequest { AccountId = accountId, Role = ContractPartyRole.Employer });
        Assert.Equal(HttpStatusCode.Created, add.StatusCode);

        var contract = await GetAsync(client, id);
        Assert.Single(contract.Parties);
        Assert.NotNull(contract.Archived);
    }

    // ── Roles and terms (issue #121) ──────────────────────────────────────────

    /// <summary>AC 1 — the three new fields round-trip on the add and on the contract read.</summary>
    [Fact]
    public async Task AddParty_WithRoleAndTerm_EchoesBoth_AndAppearsOnTheContract()
    {
        await using var factory = new ApiFactory(ReadWrite);
        var (accountId, _) = await SeedTargetsAsync(factory);
        using var client = factory.CreateClient();

        var id = await CreateAsync(client);
        var from = FixedToday.AddDays(-10);
        var to = FixedToday.AddDays(10);

        var add = await client.PostAsJsonAsync($"{Path}/{id}/parties", new ContractPartyRequest
        {
            AccountId = accountId,
            Role = ContractPartyRole.Employer,
            FromDate = from,
            ToDate = to,
        });
        Assert.Equal(HttpStatusCode.Created, add.StatusCode);

        var created = await add.Content.ReadFromJsonAsync<ExistingContractParty>();
        Assert.Equal(ContractPartyRole.Employer, created!.Role);
        Assert.Equal(from.Date, created.FromDate!.Value.Date);
        Assert.Equal(to.Date, created.ToDate!.Value.Date);

        var onContract = Assert.Single((await GetAsync(client, id)).Parties);
        Assert.Equal(ContractPartyRole.Employer, onContract.Role);
        Assert.Equal(from.Date, onContract.FromDate!.Value.Date);
        Assert.Equal(to.Date, onContract.ToDate!.Value.Date);
    }

    /// <summary>
    /// Issue #157 AC 5 — a role-less write is now REFUSED with a <c>400</c> naming <c>Role</c> as
    /// required. It used to be valid and resolve to <c>Unspecified</c>; that member is retired, so
    /// there is nothing left for it to resolve to.
    /// </summary>
    /// <remarks>
    /// The message matters as much as the status: <c>Role</c> is <c>ContractPartyRole?</c> precisely
    /// so an omitted value reads as "required" rather than as an invalid enum value, which would
    /// misdescribe what the caller did wrong (§8.1).
    /// </remarks>
    [Fact]
    public async Task AddParty_WithoutRole_Returns400NamingRoleAsRequired()
    {
        await using var factory = new ApiFactory(ReadWrite);
        var (accountId, _) = await SeedTargetsAsync(factory);
        using var client = factory.CreateClient();

        var id = await CreateAsync(client);
        var add = await client.PostAsJsonAsync($"{Path}/{id}/parties", new { accountId });

        Assert.Equal(HttpStatusCode.BadRequest, add.StatusCode);
        Assert.True(await HasErrorKeyAsync(add, nameof(ContractPartyRequest.Role)));

        var body = await add.Content.ReadAsStringAsync();
        Assert.Contains("required", body, StringComparison.OrdinalIgnoreCase);
        // NOT the invalid-enum-value message, which is what a non-nullable Role would have produced.
        Assert.DoesNotContain("is invalid", body, StringComparison.OrdinalIgnoreCase);

        Assert.Empty((await GetAsync(client, id)).Parties);
    }

    /// <summary>
    /// AC 3 — the row is updated IN PLACE, so the party stays one party across a role change. The
    /// returned id equalling the route's is the whole assertion: a delete-and-insert would pass a
    /// "the role changed" check just as well.
    /// </summary>
    [Fact]
    public async Task UpdateParty_ChangingOnlyRole_KeepsTheSamePartyId()
    {
        await using var factory = new ApiFactory(ReadWrite);
        var (accountId, _) = await SeedTargetsAsync(factory);
        using var client = factory.CreateClient();

        var id = await CreateAsync(client);
        var add = await client.PostAsJsonAsync($"{Path}/{id}/parties",
            new ContractPartyRequest { AccountId = accountId, Role = ContractPartyRole.Employer });
        var party = await add.Content.ReadFromJsonAsync<ExistingContractParty>();

        // Both roles are legal on an Employment contract — the matrix decides which they can be,
        // and this test is about the row's IDENTITY surviving a role change (issue #157).
        var put = await client.PutAsJsonAsync($"{Path}/{id}/parties/{party!.ContractPartyId}",
            new ContractPartyRequest { AccountId = accountId, Role = ContractPartyRole.Employee });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        var updated = await put.Content.ReadFromJsonAsync<ExistingContractParty>();
        Assert.Equal(party.ContractPartyId, updated!.ContractPartyId);
        Assert.Equal(ContractPartyRole.Employee, updated.Role);
        Assert.Single((await GetAsync(client, id)).Parties);
    }

    /// <summary>
    /// AC 4 — the PUT is a full replacement: an omitted date CLEARS it, never "leave as is". The role
    /// is no longer part of that demonstration, because issue #157 made it required — an omitted role
    /// is now a <c>400</c> rather than a silent reset, which is its own test above.
    /// </summary>
    [Fact]
    public async Task UpdateParty_OmittingToDate_ClearsIt()
    {
        await using var factory = new ApiFactory(ReadWrite);
        var (accountId, _) = await SeedTargetsAsync(factory);
        using var client = factory.CreateClient();

        var id = await CreateAsync(client);
        var add = await client.PostAsJsonAsync($"{Path}/{id}/parties", new ContractPartyRequest
        {
            AccountId = accountId,
            Role = ContractPartyRole.Employer,
            ToDate = FixedToday.AddDays(5),
        });
        var party = await add.Content.ReadFromJsonAsync<ExistingContractParty>();
        Assert.NotNull(party!.ToDate);

        // The body omits toDate — it clears, which is exactly what §7.7 logs.
        var put = await client.PutAsJsonAsync($"{Path}/{id}/parties/{party.ContractPartyId}",
            new { accountId, role = (int)ContractPartyRole.Employer });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        var updated = await put.Content.ReadFromJsonAsync<ExistingContractParty>();
        Assert.Null(updated!.ToDate);
        Assert.Equal(ContractPartyRole.Employer, updated.Role);
    }

    /// <summary>
    /// AC 5 — uniqueness is (contract, target, ROLE). A contact that is both an Employer and a
    /// Broker is one contract with two parties, not two contracts.
    /// </summary>
    [Fact]
    public async Task AddParty_SameTargetInTwoRoles_Succeeds_SameRoleTwiceConflicts()
    {
        await using var factory = new ApiFactory(ReadWrite);
        var (_, contactId) = await SeedTargetsAsync(factory);
        using var client = factory.CreateClient();

        var id = await CreateAsync(client);

        var employer = await client.PostAsJsonAsync($"{Path}/{id}/parties",
            new ContractPartyRequest { ContactId = contactId, Role = ContractPartyRole.Employer });
        Assert.Equal(HttpStatusCode.Created, employer.StatusCode);

        var broker = await client.PostAsJsonAsync($"{Path}/{id}/parties",
            new ContractPartyRequest { ContactId = contactId, Role = ContractPartyRole.Broker });
        Assert.Equal(HttpStatusCode.Created, broker.StatusCode);

        var again = await client.PostAsJsonAsync($"{Path}/{id}/parties",
            new ContractPartyRequest { ContactId = contactId, Role = ContractPartyRole.Employer });
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);

        // The 409 is keyed on the id the caller sent, so the record picker can mark it.
        Assert.True(await HasErrorKeyAsync(again, nameof(ContractPartyRequest.ContactId)));
    }

    /// <summary>
    /// AC 6 — every party load is scoped by BOTH ids, so a valid party id from another contract is a
    /// 404 rather than a silent cross-contract edit (§7.5).
    /// </summary>
    [Fact]
    public async Task UpdateAndDeleteParty_WithForeignPartyId_Return404_AndLeaveThatPartyUntouched()
    {
        await using var factory = new ApiFactory(ReadWrite);
        var (accountId, _) = await SeedTargetsAsync(factory);
        using var client = factory.CreateClient();

        var owner = await CreateAsync(client);
        var other = await CreateAsync(client);
        var add = await client.PostAsJsonAsync($"{Path}/{owner}/parties",
            new ContractPartyRequest { AccountId = accountId, Role = ContractPartyRole.Employer });
        var party = await add.Content.ReadFromJsonAsync<ExistingContractParty>();

        var put = await client.PutAsJsonAsync($"{Path}/{other}/parties/{party!.ContractPartyId}",
            new ContractPartyRequest { AccountId = accountId, Role = ContractPartyRole.Employee });
        Assert.Equal(HttpStatusCode.NotFound, put.StatusCode);

        var delete = await client.DeleteAsync($"{Path}/{other}/parties/{party.ContractPartyId}");
        Assert.Equal(HttpStatusCode.NotFound, delete.StatusCode);

        var untouched = Assert.Single((await GetAsync(client, owner)).Parties);
        Assert.Equal(ContractPartyRole.Employer, untouched.Role);
    }

    /// <summary>AC 7 — both term rules reject with the field key the dialog renders on.</summary>
    [Fact]
    public async Task AddParty_TermRules_Return400_WithTheOffendingFieldKey()
    {
        await using var factory = new ApiFactory(ReadWrite);
        var (accountId, _) = await SeedTargetsAsync(factory);
        using var client = factory.CreateClient();

        var start = FixedToday.AddDays(-30);
        var id = await CreateAsync(client, start);

        var outOfOrder = await client.PostAsJsonAsync($"{Path}/{id}/parties", new ContractPartyRequest
        {
            AccountId = accountId,
            Role = ContractPartyRole.Employer,
            FromDate = FixedToday,
            ToDate = FixedToday.AddDays(-1),
        });
        Assert.Equal(HttpStatusCode.BadRequest, outOfOrder.StatusCode);
        Assert.True(await HasErrorKeyAsync(outOfOrder, nameof(ContractPartyRequest.ToDate)));

        var beforeStart = await client.PostAsJsonAsync($"{Path}/{id}/parties", new ContractPartyRequest
        {
            AccountId = accountId,
            Role = ContractPartyRole.Employer,
            FromDate = start.AddDays(-1),
        });
        Assert.Equal(HttpStatusCode.BadRequest, beforeStart.StatusCode);
        Assert.True(await HasErrorKeyAsync(beforeStart, nameof(ContractPartyRequest.FromDate)));
    }

    /// <summary>
    /// AC 8 — no StartDate means no anchor. A one-off (completion date only) and an open-started term
    /// contract both take any FromDate; only the lower bound was ever tied, and only when there is one.
    /// </summary>
    [Fact]
    public async Task AddParty_OnContractWithNoStartDate_AcceptsAnyFromDate()
    {
        await using var factory = new ApiFactory(ReadWrite);
        var (accountId, _) = await SeedTargetsAsync(factory);
        using var client = factory.CreateClient();

        var post = await client.PostAsJsonAsync(Path, new NewContract
        {
            Name = "Property purchase",
            Type = ContractType.Other,
            CompletionDate = FixedToday.AddYears(-3),
            Ready = ReadyOn,
            Signed = SignedOn,
        });
        post.EnsureSuccessStatusCode();
        var id = (await post.Content.ReadFromJsonAsync<ExistingContract>())!.ContractId;

        var add = await client.PostAsJsonAsync($"{Path}/{id}/parties", new ContractPartyRequest
        {
            AccountId = accountId,
            Role = ContractPartyRole.Employee,
            FromDate = FixedToday.AddYears(-10),
        });
        Assert.Equal(HttpStatusCode.Created, add.StatusCode);
    }

    /// <summary>AC 12 — an out-of-range role is refused by model validation, before the service runs.</summary>
    [Fact]
    public async Task AddParty_UnbindableRole_Returns400FromModelValidation()
    {
        await using var factory = new ApiFactory(ReadWrite);
        var (accountId, _) = await SeedTargetsAsync(factory);
        using var client = factory.CreateClient();

        var id = await CreateAsync(client);
        var add = await client.PostAsJsonAsync($"{Path}/{id}/parties", new { accountId, role = 99 });

        Assert.Equal(HttpStatusCode.BadRequest, add.StatusCode);
        // Rejected BEFORE the service: nothing was written.
        Assert.Empty((await GetAsync(client, id)).Parties);
    }

    /// <summary>
    /// Editing and detaching a party both work on an archived contract, the same as adding one. The
    /// three used to split — add and edit refused with a 422 while detach was permitted — and that
    /// asymmetry is gone rather than harmonised in the refusing direction.
    /// </summary>
    [Fact]
    public async Task UpdateAndDetachParty_OnArchivedContract_BothSucceed()
    {
        await using var factory = new ApiFactory(ReadWrite);
        var (accountId, _) = await SeedTargetsAsync(factory);
        using var client = factory.CreateClient();

        var id = await CreateAsync(client);
        var add = await client.PostAsJsonAsync($"{Path}/{id}/parties",
            new ContractPartyRequest { AccountId = accountId, Role = ContractPartyRole.Employer });
        var party = await add.Content.ReadFromJsonAsync<ExistingContractParty>();

        (await client.PutAsJsonAsync($"{Path}/{id}", UpdateContract(isArchived: true, endDate: Lapsed)))
            .EnsureSuccessStatusCode();

        var put = await client.PutAsJsonAsync($"{Path}/{id}/parties/{party!.ContractPartyId}",
            new ContractPartyRequest { AccountId = accountId, Role = ContractPartyRole.Employee });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        Assert.Equal(ContractPartyRole.Employee, Assert.Single((await GetAsync(client, id)).Parties).Role);

        Assert.Equal(HttpStatusCode.NoContent,
            (await client.DeleteAsync($"{Path}/{id}/parties/{party.ContractPartyId}")).StatusCode);
    }

    /// <summary>
    /// AC 14 — the FIELD KEY is the discriminator, never the message text. Three failure classes share
    /// status 404; only PartyTargetNotFound is rendered inline, on the record picker.
    /// </summary>
    [Fact]
    public async Task PartyNotFoundClasses_AreDistinguishableByFieldKey()
    {
        await using var factory = new ApiFactory(ReadWrite);
        var (accountId, contactId) = await SeedTargetsAsync(factory);
        using var client = factory.CreateClient();

        var id = await CreateAsync(client);

        // PartyTargetNotFound — keyed on whichever id was actually sent.
        var missingAccount = await client.PostAsJsonAsync($"{Path}/{id}/parties",
            new ContractPartyRequest { AccountId = Guid.NewGuid(), Role = ContractPartyRole.Employer });
        Assert.Equal(HttpStatusCode.NotFound, missingAccount.StatusCode);
        Assert.True(await HasErrorKeyAsync(missingAccount, nameof(ContractPartyRequest.AccountId)));

        var missingContact = await client.PostAsJsonAsync($"{Path}/{id}/parties",
            new ContractPartyRequest { ContactId = Guid.NewGuid(), Role = ContractPartyRole.Employer });
        Assert.True(await HasErrorKeyAsync(missingContact, nameof(ContractPartyRequest.ContactId)));

        // ContractNotFound — no field key at all.
        var noContract = await client.PutAsJsonAsync($"{Path}/{Guid.NewGuid()}/parties/{Guid.NewGuid()}",
            new ContractPartyRequest { AccountId = accountId, Role = ContractPartyRole.Employer });
        Assert.Equal(HttpStatusCode.NotFound, noContract.StatusCode);
        Assert.False(await HasAnyErrorKeyAsync(noContract));

        // PartyNotOnContract — also no field key.
        var noParty = await client.PutAsJsonAsync($"{Path}/{id}/parties/{Guid.NewGuid()}",
            new ContractPartyRequest { ContactId = contactId, Role = ContractPartyRole.Employer });
        Assert.Equal(HttpStatusCode.NotFound, noParty.StatusCode);
        Assert.False(await HasAnyErrorKeyAsync(noParty));
    }

    /// <summary>
    /// AC 9 (the edit half) — the PUT inherits the add's mass-assignment invariant unchanged: a body
    /// carrying a populated nested object alongside the scalar id neither creates nor mutates a record.
    /// </summary>
    [Fact]
    public async Task UpdateParty_NestedTargetObject_IsIgnored_NoMutation()
    {
        await using var factory = new ApiFactory(ReadWrite);
        var (_, contactId) = await SeedTargetsAsync(factory);
        using var client = factory.CreateClient();

        var id = await CreateAsync(client);
        var add = await client.PostAsJsonAsync($"{Path}/{id}/parties", new ContractPartyRequest { ContactId = contactId, Role = ContractPartyRole.Employer });
        var party = await add.Content.ReadFromJsonAsync<ExistingContractParty>();

        var put = await client.PutAsJsonAsync($"{Path}/{id}/parties/{party!.ContractPartyId}", new
        {
            contactId,
            // The ordinal, not the name: the API serializes enums numerically, which is exactly why
            // issue #121 §4 pins the ORDINALS rather than the member names as the wire contract.
            role = (int)ContractPartyRole.Employee,
            institution = new { contactId, name = "HACKED", organizationNumber = "EVIL" },
        });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        var cp = await context.Contacts.Include(c => c.OrganizationDetails).FirstAsync(c => c.ContactId == contactId);
        Assert.Equal("Acme Corp", cp.OrganizationDetails!.LegalName);
        Assert.Equal("ORG-12345", cp.OrganizationDetails.OrganizationNumber);
    }

    /// <summary>
    /// AC 10 — the edit is gated on contracts.update like its two siblings, and on nothing else: a
    /// role and a term are attributes of a link a contracts.update holder can already create outright.
    /// </summary>
    [Fact]
    public async Task UpdateParty_WithoutUpdatePermission_ReturnsForbidden()
    {
        await using var factory = new ApiFactory(ReadOnly);
        using var client = factory.CreateClient();

        var put = await client.PutAsJsonAsync($"{Path}/{Guid.NewGuid()}/parties/{Guid.NewGuid()}",
            new ContractPartyRequest { AccountId = Guid.NewGuid(), Role = ContractPartyRole.Employer });

        Assert.Equal(HttpStatusCode.Forbidden, put.StatusCode);
    }

    /// <summary>
    /// A cap is row-count-neutral on an edit, so a contract already AT its cap can still have its
    /// parties re-dated and re-roled (§8 rule 8) — the state a lowered cap produces.
    /// </summary>
    [Fact]
    public async Task UpdateParty_OnContractAtItsCap_IsNotRefused()
    {
        await using var factory = new ApiFactory(ReadWrite);
        await SystemSettingsSeed.SetAsync(factory.Services, SystemSettingsKeys.ContractMaxPartiesPerContract, "1");
        var (accountId, _) = await SeedTargetsAsync(factory);
        using var client = factory.CreateClient();

        var id = await CreateAsync(client);
        var add = await client.PostAsJsonAsync($"{Path}/{id}/parties",
            new ContractPartyRequest { AccountId = accountId, Role = ContractPartyRole.Employer });
        var party = await add.Content.ReadFromJsonAsync<ExistingContractParty>();

        var put = await client.PutAsJsonAsync($"{Path}/{id}/parties/{party!.ContractPartyId}",
            new ContractPartyRequest { AccountId = accountId, Role = ContractPartyRole.Employee });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
    }

    /// <summary>
    /// A date-only edit is not a self-conflict: the row being edited is excluded from its own
    /// duplicate check (§8 rule 6).
    /// </summary>
    [Fact]
    public async Task UpdateParty_ChangingOnlyDates_IsNotASelfConflict()
    {
        await using var factory = new ApiFactory(ReadWrite);
        var (accountId, _) = await SeedTargetsAsync(factory);
        using var client = factory.CreateClient();

        var id = await CreateAsync(client);
        var add = await client.PostAsJsonAsync($"{Path}/{id}/parties",
            new ContractPartyRequest { AccountId = accountId, Role = ContractPartyRole.Employer });
        var party = await add.Content.ReadFromJsonAsync<ExistingContractParty>();

        var put = await client.PutAsJsonAsync($"{Path}/{id}/parties/{party!.ContractPartyId}",
            new ContractPartyRequest
            {
                AccountId = accountId,
                Role = ContractPartyRole.Employer,
                FromDate = FixedToday.AddDays(-5),
            });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        Assert.Equal(FixedToday.AddDays(-5).Date,
            (await put.Content.ReadFromJsonAsync<ExistingContractParty>())!.FromDate!.Value.Date);
    }

    /// <summary>
    /// AC 19 — two concurrent edits of one party both succeed and the later wins. Pinned as the
    /// DOCUMENTED behaviour (§8 rule 9): ContractParty deliberately carries no concurrency token, so a
    /// future one is a considered change rather than an accidental one.
    /// </summary>
    [Fact]
    public async Task UpdateParty_TwiceInSequence_LastWriteWins_NoConflict()
    {
        await using var factory = new ApiFactory(ReadWrite);
        var (accountId, _) = await SeedTargetsAsync(factory);
        using var client = factory.CreateClient();

        var id = await CreateAsync(client);
        var add = await client.PostAsJsonAsync($"{Path}/{id}/parties",
            new ContractPartyRequest { AccountId = accountId, Role = ContractPartyRole.Employer });
        var party = await add.Content.ReadFromJsonAsync<ExistingContractParty>();
        var route = $"{Path}/{id}/parties/{party!.ContractPartyId}";

        var first = await client.PutAsJsonAsync(route,
            new ContractPartyRequest { AccountId = accountId, Role = ContractPartyRole.Employee });
        var second = await client.PutAsJsonAsync(route,
            new ContractPartyRequest { AccountId = accountId, Role = ContractPartyRole.Broker });

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(ContractPartyRole.Broker, Assert.Single((await GetAsync(client, id)).Parties).Role);
    }

    // ── Over-posting blocked (criterion #4) ────────────────────────────────────

    [Fact]
    public async Task AddParty_NestedTargetObject_IsIgnored_NoMutation()
    {
        await using var factory = new ApiFactory(ReadWrite);
        var (_, contactId) = await SeedTargetsAsync(factory);
        using var client = factory.CreateClient();

        var id = await CreateAsync(client);

        // Body carries the scalar id plus a populated nested contact object with edited fields.
        // The nested object must be ignored — only the link is created, the contact is unchanged.
        var add = await client.PostAsJsonAsync($"{Path}/{id}/parties", new
        {
            contactId,
            role = (int)ContractPartyRole.Employer,
            contact = new { contactId, name = "HACKED", organizationNumber = "EVIL" },
        });
        Assert.Equal(HttpStatusCode.Created, add.StatusCode);

        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        var cp = await context.Contacts.Include(c => c.OrganizationDetails).FirstAsync(c => c.ContactId == contactId);
        Assert.Equal("Acme Corp", cp.OrganizationDetails!.LegalName); // not "HACKED"
        Assert.Equal("ORG-12345", cp.OrganizationDetails.OrganizationNumber); // not "EVIL"
    }

    // ── Cross-claim minimisation (criterion #5) ────────────────────────────────

    [Fact]
    public async Task Get_PartyReferences_OmitCrossClaimFields()
    {
        await using var factory = new ApiFactory(ReadWrite);
        var (accountId, contactId) = await SeedTargetsAsync(factory);
        using var client = factory.CreateClient();

        var id = await CreateAsync(client);
        await client.PostAsJsonAsync($"{Path}/{id}/parties", new ContractPartyRequest { AccountId = accountId, Role = ContractPartyRole.Employer });
        await client.PostAsJsonAsync($"{Path}/{id}/parties", new ContractPartyRequest { ContactId = contactId, Role = ContractPartyRole.Employer });

        // Inspect the raw JSON: none of the cross-claim sensitive values should be present.
        var raw = await client.GetStringAsync($"{Path}/{id}");
        Assert.DoesNotContain("ACC-SECRET-001", raw);   // Account.AccountNumber
        Assert.DoesNotContain("ORG-12345", raw);        // Contact.OrganizationNumber
        Assert.DoesNotContain("secret contact notes", raw); // Contact.Description

        // But the minimal reference fields (names) are present.
        Assert.Contains("Acme Corp", raw);
        Assert.Contains("Salary account", raw);
    }

    // ── Files: round-trip + scope (criterion #7) ───────────────────────────────

    [Fact]
    public async Task AttachFile_DownloadDetach_BlobSurvives_ParentChainEnforced()
    {
        await using var factory = new ApiFactory(ReadWriteWithFiles);
        var pdfId = await SeedFileAsync(factory, "contract.pdf", "application/pdf");
        var textId = await SeedFileAsync(factory, "notes.txt", "text/plain");
        using var client = factory.CreateClient();

        var contractA = await CreateAsync(client);
        var contractB = await CreateAsync(client);

        var attach = await client.PostAsJsonAsync($"{Path}/{contractA}/files", AttachRequest(pdfId));
        Assert.Equal(HttpStatusCode.Created, attach.StatusCode);

        // Disallowed content type → 400.
        var bad = await client.PostAsJsonAsync($"{Path}/{contractA}/files", AttachRequest(textId));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        // Duplicate (contract, file) → 409.
        var duplicate = await client.PostAsJsonAsync($"{Path}/{contractA}/files", AttachRequest(pdfId));
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);

        // Download via the parent-scoped route sets safe headers.
        var download = await client.GetAsync($"{Path}/{contractA}/files/{pdfId}");
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal("nosniff", download.Headers.TryGetValues("X-Content-Type-Options", out var v) ? string.Join(",", v) : null);
        Assert.Equal("attachment", download.Content.Headers.ContentDisposition?.DispositionType);

        // A file attached to A is not downloadable through B.
        var crossContract = await client.GetAsync($"{Path}/{contractB}/files/{pdfId}");
        Assert.Equal(HttpStatusCode.NotFound, crossContract.StatusCode);

        // Detach removes the join only; the underlying blob survives.
        var detach = await client.DeleteAsync($"{Path}/{contractA}/files/{pdfId}");
        Assert.Equal(HttpStatusCode.NoContent, detach.StatusCode);
        var afterDetach = await client.GetAsync($"{Path}/{contractA}/files/{pdfId}");
        Assert.Equal(HttpStatusCode.NotFound, afterDetach.StatusCode);

        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        Assert.True(await context.FileMetadata.AnyAsync(f => f.Id == pdfId));
    }

    // ── Delete semantics (criterion #10) ───────────────────────────────────────

    [Fact]
    public async Task Delete_CascadesLinks_LeavesTargetsAndFiles()
    {
        await using var factory = new ApiFactory(ReadWriteWithFiles);
        var (accountId, _) = await SeedTargetsAsync(factory);
        var pdfId = await SeedFileAsync(factory, "contract.pdf", "application/pdf");
        using var client = factory.CreateClient();

        var id = await CreateAsync(client);
        await client.PostAsJsonAsync($"{Path}/{id}/parties", new ContractPartyRequest { AccountId = accountId, Role = ContractPartyRole.Employer });
        await client.PostAsJsonAsync($"{Path}/{id}/files", AttachRequest(pdfId));

        var delete = await client.DeleteAsync($"{Path}/{id}");
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);

        // Permanent: the contract is gone (an archive would still GET 200).
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"{Path}/{id}")).StatusCode);

        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        Assert.False(await context.ContractParties.AnyAsync(p => p.ContractId == id)); // links cascaded
        Assert.False(await context.ContractFiles.AnyAsync(f => f.ContractId == id));
        Assert.True(await context.Accounts.AnyAsync(a => a.AccountId == accountId));    // target survives
        Assert.True(await context.FileMetadata.AnyAsync(f => f.Id == pdfId));           // file survives
    }

    [Fact]
    public async Task DeleteParty_DetachesOnly()
    {
        await using var factory = new ApiFactory(ReadWrite);
        var (accountId, _) = await SeedTargetsAsync(factory);
        using var client = factory.CreateClient();

        var id = await CreateAsync(client);
        var add = await client.PostAsJsonAsync($"{Path}/{id}/parties", new ContractPartyRequest { AccountId = accountId, Role = ContractPartyRole.Employer });
        var party = await add.Content.ReadFromJsonAsync<ExistingContractParty>();

        var delete = await client.DeleteAsync($"{Path}/{id}/parties/{party!.ContractPartyId}");
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);

        var fetched = await GetAsync(client, id);
        Assert.Empty(fetched.Parties);

        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        Assert.True(await context.Accounts.AnyAsync(a => a.AccountId == accountId)); // target survives
    }

    // ── Summary aggregation ────────────────────────────────────────────────────

    [Fact]
    public async Task Summary_AggregatesByStatus_AndBreaksDownNonArchivedByType()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();

        await CreateTypedAsync(client, ContractType.Employment, start: FixedToday.AddDays(-30));                          // Active
        await CreateTypedAsync(client, ContractType.Employment, start: FixedToday.AddDays(-10), end: FixedToday.AddDays(10)); // Active
        await CreateTypedAsync(client, ContractType.Service, start: FixedToday.AddDays(10));                             // Upcoming
        await CreateTypedAsync(client, ContractType.Rental, start: FixedToday.AddDays(-100), end: FixedToday.AddDays(-1)); // Expired
        var archived = await CreateTypedAsync(client, ContractType.Other, start: FixedToday.AddDays(-30));
        (await client.PutAsJsonAsync($"{Path}/{archived}", new UpdateContract
        {
            Name = "Other contract", Type = ContractType.Other,
            StartDate = FixedToday.AddDays(-30), EndDate = Lapsed, IsArchived = true,
            Ready = ReadyOn, Signed = SignedOn,
        })).EnsureSuccessStatusCode();

        var summary = (await client.GetFromJsonAsync<ContractSummary>($"{Path}/summary"))!;

        Assert.Equal(5, summary.TotalContracts);
        Assert.Equal(2, summary.CountsByStatus.Active);
        Assert.Equal(1, summary.CountsByStatus.Upcoming);
        Assert.Equal(1, summary.CountsByStatus.Expired);
        Assert.Equal(1, summary.CountsByStatus.Archived);

        // By-type covers only the active (non-archived) set — the archived Other contract is excluded.
        var byType = summary.CountsByType.ToDictionary(t => t.Type, t => t.Count);
        Assert.Equal(2, byType.GetValueOrDefault(ContractType.Employment));
        Assert.Equal(1, byType.GetValueOrDefault(ContractType.Service));
        Assert.Equal(1, byType.GetValueOrDefault(ContractType.Rental));
        Assert.False(byType.ContainsKey(ContractType.Other));
    }

    /// <summary>
    /// The conversion half of the run rate, which the unit tier deliberately leaves alone: a rate row
    /// is a real table, not a stub. A currency WITH a rate is folded into the total; one without is
    /// NAMED and excluded, because a silent 1:1 would under-report a strong currency and over-report a
    /// weak one — and either reads as a real figure rather than a partial one.
    /// </summary>
    [Fact]
    public async Task Summary_RunRate_ConvertsWhatItCan_AndNamesWhatItCannot()
    {
        await using var factory = new ApiFactory(ReadWrite);
        await SeedRateAsync(factory, "EUR", "USD", 1.10m);
        using var client = factory.CreateClient();

        var domestic = await CreateTypedAsync(client, ContractType.Rental, start: FixedToday.AddDays(-30));
        var european = await CreateTypedAsync(client, ContractType.Membership, start: FixedToday.AddDays(-30));
        var unrated = await CreateTypedAsync(client, ContractType.Service, start: FixedToday.AddDays(-30));
        await AddMonthlyFeeAsync(client, domestic, 100m, "USD");
        await AddMonthlyFeeAsync(client, european, 200m, "EUR");
        await AddMonthlyFeeAsync(client, unrated, 999m, "SEK");

        var summary = (await client.GetFromJsonAsync<ContractSummary>($"{Path}/summary?baseCurrency=USD"))!;

        Assert.Equal("USD", summary.RunRate.BaseCurrency);
        Assert.Equal(320m, summary.RunRate.Monthly);   // 100 + 200 × 1.10; the SEK fee is left out.
        Assert.Equal(3840m, summary.RunRate.Yearly);
        Assert.Equal(["SEK"], summary.RunRate.UnconvertedCurrencies);

        // The excluded currency is excluded from the per-type split too, so the rows still sum.
        Assert.Equal(summary.RunRate.Monthly, summary.RunRate.ByType.Sum(r => r.Monthly));
        Assert.DoesNotContain(summary.RunRate.ByType, r => r.Type == ContractType.Service);
    }

    /// <summary>
    /// The four types added after the original set carry ordinals 4–7 while <c>Other</c> keeps 3. An
    /// ordinal is a wire and persistence contract, so a round trip has to preserve the member — a
    /// renumbering would silently reinterpret every stored row.
    /// </summary>
    [Theory]
    [InlineData(ContractType.Insurance, 4)]
    [InlineData(ContractType.Subscription, 5)]
    [InlineData(ContractType.Purchase, 6)]
    [InlineData(ContractType.Membership, 7)]
    [InlineData(ContractType.Other, 3)]
    public async Task Create_RoundTripsTheLaterTypes_AtTheirOwnOrdinals(ContractType type, int ordinal)
    {
        Assert.Equal(ordinal, (int)type);

        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();

        var id = await CreateTypedAsync(client, type);

        var fetched = (await client.GetFromJsonAsync<ExistingContract>($"{Path}/{id}"))!;
        Assert.Equal(type, fetched.Type);

        var summary = (await client.GetFromJsonAsync<ContractSummary>($"{Path}/summary"))!;
        Assert.Contains(summary.CountsByType, t => t.Type == type && t.Count == 1);
    }

    /// <summary>
    /// The <c>[StringLength(3)]</c> on <c>baseCurrency</c> is model validation, so it runs before the
    /// service and returns a ProblemDetails rather than reaching the FX lookup with a junk code.
    /// </summary>
    [Fact]
    public async Task Summary_RejectsAnOverlongBaseCurrency()
    {
        await using var factory = new ApiFactory(ReadOnly);
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"{Path}/summary?baseCurrency=USDD");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private static async Task AddMonthlyFeeAsync(
        HttpClient client, Guid contractId, decimal value, string currency)
    {
        var post = await client.PostAsJsonAsync($"{Path}/{contractId}/terms", new NewTerm
        {
            TermKind = Odyssey.Dtos.Finance.TermKind.Fee,
            Label = $"{currency} fee",
            ValueUnit = Odyssey.Dtos.Finance.TermValueUnit.Amount,
            Value = value,
            CurrencyCode = currency,
            Interval = Odyssey.Dtos.Finance.Interval.Monthly,
            IntervalCount = 1,
            EffectiveFrom = FixedToday.AddDays(-30),
        });
        post.EnsureSuccessStatusCode();
    }

    private static async Task SeedRateAsync(
        WebApplicationFactory<Program> factory, string from, string to, decimal rate)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        await context.Database.EnsureCreatedAsync();
        context.ExchangeRates.Add(new ExchangeRate
        {
            ExchangeRateId = Guid.NewGuid(),
            FromCurrencyCode = from,
            ToCurrencyCode = to,
            Rate = rate,
            AsOf = FixedToday.AddDays(-1),
        });
        await context.SaveChangesAsync();
    }

    // ── Lean-list projection: InstitutionName ───────────────────────────────────

    [Fact]
    public async Task List_InstitutionName_IsTheFirstContactPartyOrNull()
    {
        await using var factory = new ApiFactory(ReadWrite);
        var (accountId, contactId) = await SeedTargetsAsync(factory);
        using var client = factory.CreateClient();

        var withInstitution = await CreateAsync(client);
        (await client.PostAsJsonAsync($"{Path}/{withInstitution}/parties",
            new ContractPartyRequest { ContactId = contactId, Role = ContractPartyRole.Employer })).EnsureSuccessStatusCode();

        var accountOnly = await CreateAsync(client);
        (await client.PostAsJsonAsync($"{Path}/{accountOnly}/parties",
            new ContractPartyRequest { AccountId = accountId, Role = ContractPartyRole.Employer })).EnsureSuccessStatusCode();

        var noParties = await CreateAsync(client);

        var list = (await client.GetPagedItemsAsync<ContractListItem>(Path))!;

        Assert.Equal("Acme Corp", list.Single(c => c.ContractId == withInstitution).InstitutionName);
        Assert.Null(list.Single(c => c.ContractId == accountOnly).InstitutionName);   // account party, no contact
        Assert.Null(list.Single(c => c.ContractId == noParties).InstitutionName);
    }

    // ── List filters: type / status / search ────────────────────────────────────

    [Fact]
    public async Task List_FiltersBy_Type_Status_And_Search()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();

        var employment = await CreateTypedAsync(client, ContractType.Employment, start: FixedToday.AddDays(-30), name: "Globex Employment"); // Active
        var service = await CreateTypedAsync(client, ContractType.Service, start: FixedToday.AddDays(10), name: "Fiber Service");             // Upcoming
        var rental = await CreateTypedAsync(client, ContractType.Rental, start: FixedToday.AddDays(-100), end: FixedToday.AddDays(-1), name: "Old Lease"); // Expired

        // type
        var serviceOnly = (await client.GetPagedItemsAsync<ContractListItem>($"{Path}?types=Service"))!;
        Assert.Equal(service, Assert.Single(serviceOnly).ContractId);

        // status (derived, filtered after projection)
        var upcoming = (await client.GetPagedItemsAsync<ContractListItem>($"{Path}?statuses=Upcoming"))!;
        Assert.Equal(service, Assert.Single(upcoming).ContractId);
        var expired = (await client.GetPagedItemsAsync<ContractListItem>($"{Path}?statuses=Expired"))!;
        Assert.Equal(rental, Assert.Single(expired).ContractId);

        // search over name — case-insensitive
        var globex = (await client.GetPagedItemsAsync<ContractListItem>($"{Path}?search=globex"))!;
        Assert.Equal(employment, Assert.Single(globex).ContractId);
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private static async Task<Guid> CreateTypedAsync(
        HttpClient client, ContractType type, DateTime? start = null, DateTime? end = null, string? name = null)
    {
        var post = await client.PostAsJsonAsync(Path, new NewContract
        {
            Name = name ?? $"{type} contract",
            Type = type,
            StartDate = start ?? FixedToday.AddDays(-30),
            EndDate = end,
            Ready = ReadyOn,
            Signed = SignedOn,
        });
        post.EnsureSuccessStatusCode();
        return (await post.Content.ReadFromJsonAsync<ExistingContract>())!.ContractId;
    }

    /// <summary>
    /// Whether the problem-details <c>errors</c> extension names <paramref name="field"/>. The FIELD
    /// KEY is what tells same-status failure classes apart (issue #121 §9), so the assertions read it
    /// straight off the wire rather than through a typed body no production code has.
    /// </summary>
    private static async Task<bool> HasErrorKeyAsync(HttpResponseMessage response, string field)
    {
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return problem.RootElement.TryGetProperty("errors", out var errors)
            && errors.EnumerateObject().Any(error => string.Equals(error.Name, field, StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<bool> HasAnyErrorKeyAsync(HttpResponseMessage response)
    {
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return problem.RootElement.TryGetProperty("errors", out var errors)
            && errors.EnumerateObject().Any();
    }


    // ── Pause (issue #140) ────────────────────────────────────────────────────

    /// <summary>
    /// AC 1–3: the flag rides the regular PUT, the stamp round-trips through a fresh GET, a repeat
    /// keeps the original value and clearing returns the contract to Active.
    /// </summary>
    [Fact]
    public async Task Put_PauseThenResume_RoundTripsTheStampAndTheStatus()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();
        var id = await CreateAsync(client);

        var pause = await client.PutAsJsonAsync($"{Path}/{id}", UpdateContract(isPaused: true, isArchived: false));
        Assert.Equal(HttpStatusCode.OK, pause.StatusCode);
        var paused = (await pause.Content.ReadFromJsonAsync<ExistingContract>())!;
        Assert.NotNull(paused.Paused);
        Assert.Equal(ContractStatus.Paused, paused.Status);

        var fetched = await GetAsync(client, id);
        Assert.Equal(paused.Paused, fetched.Paused);
        Assert.Equal(ContractStatus.Paused, fetched.Status);

        // A repeated pause preserves the original stamp — never "paused since now".
        var again = await client.PutAsJsonAsync($"{Path}/{id}", UpdateContract(isPaused: true, isArchived: false));
        Assert.Equal(paused.Paused, (await again.Content.ReadFromJsonAsync<ExistingContract>())!.Paused);

        var resume = await client.PutAsJsonAsync($"{Path}/{id}", UpdateContract(isPaused: false, isArchived: false));
        Assert.Equal(HttpStatusCode.OK, resume.StatusCode);
        var resumed = (await resume.Content.ReadFromJsonAsync<ExistingContract>())!;
        Assert.Null(resumed.Paused);
        Assert.Equal(ContractStatus.Active, resumed.Status);
    }

    /// <summary>
    /// AC 4–5: the transition is refused from every non-Active status with a stable code and a
    /// field-keyed errors entry, and the contract is left untouched.
    /// </summary>
    [Theory]
    [InlineData(ContractStatus.Upcoming)]
    [InlineData(ContractStatus.Expired)]
    [InlineData(ContractStatus.Archived)]
    public async Task Put_Pause_FromANonActiveStatus_ReturnsBadRequest(ContractStatus from)
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();
        var id = await CreateAsync(client);

        var (start, end) = from == ContractStatus.Upcoming
            ? ((DateTime?)FixedToday.AddDays(10), (DateTime?)null)
            : (FixedToday.AddDays(-30), Lapsed);
        var archive = from == ContractStatus.Archived;
        (await client.PutAsJsonAsync($"{Path}/{id}",
            UpdateContract(isPaused: false, isArchived: archive, startDate: start, endDate: end)))
            .EnsureSuccessStatusCode();
        Assert.Equal(from, (await GetAsync(client, id)).Status);

        var refused = await client.PutAsJsonAsync($"{Path}/{id}",
            UpdateContract(isPaused: true, isArchived: archive, startDate: start, endDate: end));

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        using var problem = JsonDocument.Parse(await refused.Content.ReadAsStringAsync());
        // The stable code AND the field key: the code is what a client branches on, the errors entry
        // is what lets a form render the message on the control rather than only in a toast.
        Assert.Equal("contract_pause_requires_active", problem.RootElement.GetProperty("code").GetString());
        Assert.Contains(
            problem.RootElement.GetProperty("errors").EnumerateObject().Select(e => e.Name),
            name => string.Equals(name, nameof(Odyssey.Dtos.Finance.UpdateContract.IsPaused),
                StringComparison.OrdinalIgnoreCase));
        // The message names the contract's own derived status and no other record.
        Assert.Contains(from.ToString(),
            problem.RootElement.GetProperty("detail").GetString() ?? string.Empty, StringComparison.Ordinal);

        // A re-read shows the contract unchanged and still not paused.
        var unchanged = await GetAsync(client, id);
        Assert.Null(unchanged.Paused);
        Assert.Equal(from, unchanged.Status);
    }

    /// <summary>AC 6: clearing is never refused, even on an archived contract still carrying a stamp.</summary>
    [Fact]
    public async Task Put_Resume_OnAnArchivedContract_ClearsTheStamp()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();
        var id = await CreateAsync(client);

        (await client.PutAsJsonAsync($"{Path}/{id}", UpdateContract(isPaused: true, isArchived: false)))
            .EnsureSuccessStatusCode();
        // End it and archive it in one write, keeping the pause stamp: nothing clears one stamp when
        // the other is set, and the derivation simply reports the terminal fact.
        var archived = (await (await client.PutAsJsonAsync($"{Path}/{id}",
            UpdateContract(isPaused: true, isArchived: true, endDate: Lapsed)))
            .Content.ReadFromJsonAsync<ExistingContract>())!;
        Assert.Equal(ContractStatus.Archived, archived.Status);
        Assert.NotNull(archived.Paused);

        var cleared = await client.PutAsJsonAsync($"{Path}/{id}",
            UpdateContract(isPaused: false, isArchived: true, endDate: Lapsed));

        Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);
        Assert.Null((await cleared.Content.ReadFromJsonAsync<ExistingContract>())!.Paused);
    }


    /// <summary>
    /// §8's "Interaction with archive", over HTTP and from a contract carrying NEITHER stamp. Every
    /// other combined-flag test starts from an already-paused contract, where the pause guard's early
    /// return makes that half a no-op — so this is the only one that actually exercises both guards
    /// against a fresh record.
    ///
    /// <para>
    /// At the API tier rather than only in the service, because the "nothing persisted" half is the
    /// point and each request here gets its own <c>DbContext</c>: a re-read through a shared,
    /// still-tracking context could report a rejected write as absent whether or not it committed.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Put_ArchiveAndPause_Together_IsRefused_AndPersistsNeitherStamp()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();

        // The contract ends in this same request, so the archive guard is satisfied and the PAUSE
        // guard is the one that refuses — an ended contract does not derive as Active.
        var ended = await CreateAsync(client);
        var byPause = await client.PutAsJsonAsync($"{Path}/{ended}",
            UpdateContract(isPaused: true, isArchived: true, endDate: Lapsed));
        Assert.Equal(HttpStatusCode.BadRequest, byPause.StatusCode);
        using (var problem = JsonDocument.Parse(await byPause.Content.ReadAsStringAsync()))
        {
            Assert.Equal("contract_pause_requires_active", problem.RootElement.GetProperty("code").GetString());
        }

        // Still running, so the ARCHIVE guard refuses first and the pause guard is never reached.
        var running = await CreateAsync(client);
        var byArchive = await client.PutAsJsonAsync($"{Path}/{running}",
            UpdateContract(isPaused: true, isArchived: true, endDate: FixedToday.AddDays(30)));
        Assert.Equal(HttpStatusCode.BadRequest, byArchive.StatusCode);
        using (var problem = JsonDocument.Parse(await byArchive.Content.ReadAsStringAsync()))
        {
            Assert.NotEqual("contract_pause_requires_active",
                problem.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null);
        }

        // Neither refusal left half of the compound write applied: both guards run before any
        // mutation, so a rejected body persists no stamp at all.
        foreach (var id in new[] { ended, running })
        {
            var reread = await GetAsync(client, id);
            Assert.Null(reread.Paused);
            Assert.Null(reread.Archived);
        }
    }

    /// <summary>
    /// AC 9–10: the new member is bindable on the status filter, an unfiltered list still includes
    /// paused rows, and sorting by status places Paused (ordinal 4) after Archived (3).
    /// </summary>
    [Fact]
    public async Task List_FiltersAndSortsByThePausedStatus()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();

        var paused = await CreateAsync(client);
        (await client.PutAsJsonAsync($"{Path}/{paused}", UpdateContract(isPaused: true, isArchived: false)))
            .EnsureSuccessStatusCode();
        var archived = await CreateAsync(client);
        (await client.PutAsJsonAsync($"{Path}/{archived}",
            UpdateContract(isPaused: false, isArchived: true, endDate: Lapsed))).EnsureSuccessStatusCode();
        var active = await CreateAsync(client);

        var filtered = await client.GetPagedItemsAsync<ContractListItem>($"{Path}?statuses=Paused");
        var only = Assert.Single(filtered!);
        Assert.Equal(paused, only.ContractId);
        Assert.NotNull(only.Paused);

        // An unfiltered list still includes it.
        var all = await client.GetPagedItemsAsync<ContractListItem>(Path);
        Assert.Contains(all!, c => c.ContractId == paused && c.Status == ContractStatus.Paused);

        // Two members bind together.
        var pair = await client.GetPagedItemsAsync<ContractListItem>($"{Path}?statuses=Paused&statuses=Active");
        Assert.Equal(2, pair!.Count);

        // LIFECYCLE order (issue #145 §8): Active · Paused · Archived. Not the enum ordinal — Draft
        // and Ready are appended members, so an ordinal sort would put the two earliest lifecycle
        // states last. This reads Active · Archived · Paused before that change.
        var sorted = await client.GetPagedItemsAsync<ContractListItem>($"{Path}?sortBy=status&sortDir=asc");
        Assert.Equal([active, paused, archived], sorted!.Select(c => c.ContractId).ToArray());
    }

    /// <summary>
    /// AC 19: pause is not a lock — a paused contract still accepts a party, a term and a file. Nor,
    /// since the design system's archive rule, is archiving: the same three writes are exercised on
    /// an archived contract below, where two of them used to be refused.
    /// </summary>
    [Fact]
    public async Task NeitherPauseNorArchive_BlocksPartyTermOrFileWrites()
    {
        await using var factory = new ApiFactory(ReadWriteWithFiles);
        var (accountId, _) = await SeedTargetsAsync(factory);
        var fileId = await SeedFileAsync(factory, "contract.pdf", "application/pdf");
        using var client = factory.CreateClient();

        var id = await CreateAsync(client);
        (await client.PutAsJsonAsync($"{Path}/{id}", UpdateContract(isPaused: true, isArchived: false)))
            .EnsureSuccessStatusCode();
        Assert.Equal(ContractStatus.Paused, (await GetAsync(client, id)).Status);

        Assert.Equal(HttpStatusCode.Created,
            (await client.PostAsJsonAsync($"{Path}/{id}/parties",
                new ContractPartyRequest { AccountId = accountId, Role = ContractPartyRole.Employer })).StatusCode);
        Assert.Equal(HttpStatusCode.Created,
            (await client.PostAsJsonAsync($"{Path}/{id}/files", AttachRequest(fileId))).StatusCode);
        await AddMonthlyFeeAsync(client, id, 100m, "USD");

        // And an archived contract takes all three too — archival hides a contract from the default
        // list, it does not freeze it, so the closing rent and the handover document still land.
        var archived = await CreateAsync(client);
        (await client.PutAsJsonAsync($"{Path}/{archived}",
            UpdateContract(isPaused: false, isArchived: true, endDate: Lapsed))).EnsureSuccessStatusCode();

        var archivedFileId = await SeedFileAsync(factory, "closing.pdf", "application/pdf");
        Assert.Equal(HttpStatusCode.Created,
            (await client.PostAsJsonAsync($"{Path}/{archived}/parties",
                new ContractPartyRequest { AccountId = accountId, Role = ContractPartyRole.Employer })).StatusCode);
        Assert.Equal(HttpStatusCode.Created,
            (await client.PostAsJsonAsync($"{Path}/{archived}/files", AttachRequest(archivedFileId))).StatusCode);
        await AddMonthlyFeeAsync(client, archived, 100m, "USD");

        Assert.Equal(ContractStatus.Archived, (await GetAsync(client, archived)).Status);
    }

    /// <summary>
    /// AC 20 — mass assignment: a body carrying nested collections alongside the pause flag creates
    /// and mutates none of them. <c>UpdateContract</c> has no such members, so the unrecognised
    /// properties are simply dropped by model binding; this pins that rather than assuming it.
    /// </summary>
    [Fact]
    public async Task Put_WithNestedCollectionsAlongsideIsPaused_MutatesNone()
    {
        await using var factory = new ApiFactory(ReadWrite);
        var (accountId, contactId) = await SeedTargetsAsync(factory);
        using var client = factory.CreateClient();
        var id = await CreateAsync(client);

        var response = await client.PutAsJsonAsync($"{Path}/{id}", new
        {
            name = "Employment agreement",
            type = ContractType.Employment,
            startDate = FixedToday.AddDays(-30),
            isArchived = false,
            isPaused = true,
            // Carried forward: a body omitting these clears both stamps, which would make the
            // contract a Draft and the pause a 400 — the very regression issue #145 §5.2 describes.
            ready = ReadyOn,
            signed = SignedOn,
            parties = new[] { new { accountId, role = ContractPartyRole.Employee } },
            files = new[] { new { fileMetadataId = Guid.NewGuid() } },
            currentTerms = new[] { new { value = 999m, currencyCode = "USD" } },
            contactId,
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var updated = (await response.Content.ReadFromJsonAsync<ExistingContract>())!;
        Assert.Equal(ContractStatus.Paused, updated.Status);
        Assert.Empty(updated.Parties);
        Assert.Empty(updated.Files);
        Assert.Empty(updated.CurrentTerms);
    }

    /// <summary>
    /// AC 21 — a DECISION, not a side effect: <c>PUT</c> is a full replacement, so a body that omits
    /// <c>isPaused</c> reads it as false and therefore RESUMES a paused contract. This is the
    /// existing behaviour of <c>isArchived</c> and is pinned here so a client that hand-builds a
    /// partial body finds it documented rather than surprising.
    /// </summary>
    [Fact]
    public async Task Put_OmittingIsPaused_ResumesAPausedContract()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();
        var id = await CreateAsync(client);
        (await client.PutAsJsonAsync($"{Path}/{id}", UpdateContract(isPaused: true, isArchived: false)))
            .EnsureSuccessStatusCode();

        var response = await client.PutAsJsonAsync($"{Path}/{id}", new
        {
            name = "Employment agreement",
            type = ContractType.Employment,
            startDate = FixedToday.AddDays(-30),
            // The signature stamps carry the SAME full-replacement convention (issue #145), so they
            // are restated here to keep this test about isPaused alone. Their own omission case is
            // ContractSignatureApiTests' AC 15.
            ready = ReadyOn,
            signed = SignedOn,
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var updated = (await response.Content.ReadFromJsonAsync<ExistingContract>())!;
        Assert.Null(updated.Paused);
        Assert.Equal(ContractStatus.Active, updated.Status);
    }

    /// <summary>
    /// AC 16: the write is gated on <c>contracts.update</c> alone — no new claim — and a read-only
    /// caller neither pauses nor leaves a trace.
    /// </summary>
    [Fact]
    public async Task Put_Pause_WithoutUpdateClaim_ReturnsForbidden_AndWritesNothing()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();
        var id = await CreateAsync(client);

        await using var readOnlyFactory = new ApiFactory(ReadOnly);
        using var readOnly = readOnlyFactory.CreateClient();
        var refused = await readOnly.PutAsJsonAsync($"{Path}/{id}", UpdateContract(isPaused: true, isArchived: false));

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Null((await GetAsync(client, id)).Paused);
    }

    // SIGNED by default (issue #145). The signature layer sits ABOVE the date chain, so an unsigned
    // contract derives as Draft/Ready whatever its dates say — everything the helpers below feed is a
    // test of the date chain, the archive stamp or the pause stamp, so the fixture clears that layer
    // first. The signature lifecycle has its own file, ContractSignatureApiTests.
    private static readonly DateTime ReadyOn = FixedToday.AddDays(-200);
    private static readonly DateTime SignedOn = FixedToday.AddDays(-199);

    private static NewContract NewContractRequest(DateTime? start = null, DateTime? end = null) => new()
    {
        Name = "Employment agreement",
        Type = ContractType.Employment,
        Description = "Full-time role",
        StartDate = start ?? FixedToday.AddDays(-30),
        EndDate = end,
        Ready = ReadyOn,
        Signed = SignedOn,
    };

    private static NewContract NewContract() => NewContractRequest();

    /// <summary>Archiving requires an ended contract, so a caller that archives passes the lapsed
    /// end date in the same request — one PUT may end and archive together.</summary>
    private static UpdateContract UpdateContract(bool isArchived, DateTime? endDate = null) => new()
    {
        Name = "Employment agreement",
        Type = ContractType.Employment,
        Description = "Full-time role",
        StartDate = FixedToday.AddDays(-30),
        EndDate = endDate,
        IsArchived = isArchived,
        // Carried forward exactly as a real client must (issue #145): PUT is a full replacement, so
        // omitting these would clear both stamps and flip the contract to Draft — which would also
        // make the archive guard's "has to end first" refusal unreachable, since an unsigned contract
        // is archivable under the widened rule.
        Ready = ReadyOn,
        Signed = SignedOn,
    };

    /// <summary>
    /// The same shape for the pause flag (issue #140). Pause is enterable only from Active, so the
    /// default dates leave the contract running.
    /// </summary>
    private static UpdateContract UpdateContract(
        bool isPaused, bool isArchived, DateTime? startDate = null, DateTime? endDate = null,
        DateTime? completionDate = null) => new()
    {
        Name = "Employment agreement",
        Type = ContractType.Employment,
        Description = "Full-time role",
        StartDate = completionDate is null ? startDate ?? FixedToday.AddDays(-30) : null,
        EndDate = completionDate is null ? endDate : null,
        CompletionDate = completionDate,
        IsArchived = isArchived,
        IsPaused = isPaused,
        // Carried forward for the same reason (issue #145) — and additionally because an unsigned
        // contract cannot be paused at all, so omitting these would make every pause below a 400.
        Ready = ReadyOn,
        Signed = SignedOn,
    };

    /// <summary>An end date already lapsed against the fixed clock — the shorthand for "archivable".</summary>
    private static DateTime Lapsed => FixedToday.AddDays(-1);

    private static AttachContractFileRequest AttachRequest(Guid fileId) => new()
    {
        FileMetadataId = fileId,
        FileType = ContractFileType.Signed,
    };

    private static async Task<Guid> CreateAsync(HttpClient client, DateTime? start = null, DateTime? end = null)
    {
        var post = await client.PostAsJsonAsync(Path, NewContractRequest(start, end));
        post.EnsureSuccessStatusCode();
        var created = await post.Content.ReadFromJsonAsync<ExistingContract>();
        return created!.ContractId;
    }

    private static async Task<ExistingContract> GetAsync(HttpClient client, Guid id) =>
        (await client.GetFromJsonAsync<ExistingContract>($"{Path}/{id}"))!;

    private static async Task<(Guid AccountId, Guid ContactId)> SeedTargetsAsync(
        WebApplicationFactory<Program> factory)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        await context.Database.EnsureCreatedAsync();
        var journalContext = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        await journalContext.Database.EnsureCreatedAsync();

        var accountId = Guid.NewGuid();
        context.Accounts.Add(new Account
        {
            AccountId = accountId,
            Name = "Salary account",
            Description = "Primary",
            Opened = DateTime.UtcNow,
            AccountType = ContextAccountType.CheckingAccount,
            AccountNumber = "ACC-SECRET-001",
            CurrencyCode = "USD",
        });

        var contactId = Guid.NewGuid();
        journalContext.Contacts.Add(new Contact
        {
            ContactId = contactId,
            ExternalUid = $"urn:uuid:{Guid.NewGuid()}",
            NormalizedName = "acme corp",
            Type = ContactType.Organization,
            Notes = "secret contact notes",
            OrganizationDetails = new() { LegalName = "Acme Corp", OrganizationNumber = "ORG-12345" },
        });

        await context.SaveChangesAsync();
        await journalContext.SaveChangesAsync();
        return (accountId, contactId);
    }

    private static async Task<Guid> SeedFileAsync(WebApplicationFactory<Program> factory, string fileName, string contentType)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        await context.Database.EnsureCreatedAsync();

        var blob = new FileBlob { Id = Guid.NewGuid(), Content = [1, 2, 3] };
        var metadataId = Guid.NewGuid();
        context.FileBlob.Add(blob);
        context.FileMetadata.Add(new FileMetadata
        {
            Id = metadataId,
            UploadedByUserId = ActorUserId,
            FileName = fileName,
            ContentType = contentType,
            SizeBytes = 3,
            Sha256Hash = Guid.NewGuid().ToString("N"),
            UploadedAtUtc = DateTime.UtcNow,
            FileBlobId = blob.Id,
            FileBlob = blob,
        });
        await context.SaveChangesAsync();
        return metadataId;
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
