using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Odyssey.Api.Tests.Infrastructure;
using Odyssey.Context;
using Odyssey.Dtos;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos.Finance;
using Xunit;
using static Odyssey.Api.Tests.PropertyApiTestSupport;
using ContextAccountType = Odyssey.Context.AccountType;
using ContextContractPartyRole = Odyssey.Context.ContractPartyRole;
using ContextContractType = Odyssey.Context.ContractType;
using ContractEventType = Odyssey.Dtos.Finance.ContractEventType;
using ContractPartyRole = Odyssey.Dtos.Finance.ContractPartyRole;
using ContractType = Odyssey.Dtos.Finance.ContractType;

namespace Odyssey.Api.Tests;

/// <summary>
/// A property as a contract party (issue #208): the party write path, the contract read projection, the
/// property record's Contracts section, <see cref="ExistingProperty.ContractCount"/>, and the property
/// delete's evented cascade — all under EF InMemory, which is why the delete removes the party rows by
/// hand rather than trusting the FK.
/// </summary>
public class PropertyContractPartiesApiTests
{
    private const string ActorUserId = "property-parties-actor";
    private const string ContractsPath = "/api/contracts";

    private static readonly DateTime FixedToday = new(2026, 6, 15, 8, 0, 0, DateTimeKind.Utc);

    private static readonly string[] FullAccess =
    [
        PermissionClaims.ContractsRead, PermissionClaims.ContractsCreate,
        PermissionClaims.ContractsUpdate, PermissionClaims.ContractsDelete,
        PermissionClaims.PropertiesRead, PermissionClaims.PropertiesDelete,
    ];

    private static readonly string[] Reader = [PermissionClaims.PropertiesRead, PermissionClaims.ContractsRead];

    private static string PartiesPath(Guid contractId) => $"{ContractsPath}/{contractId}/parties";

    private static string PropertyContractsPath(Guid propertyId) => $"{PropertyPath(propertyId)}/contracts";

    // ── Party write + read projection (AC 1, 2) ───────────────────────────────

    [Fact]
    public async Task AddParty_WithAProperty_Returns201_WithTheMinimalPropertyReference()
    {
        await using var factory = new ApiFactory(FullAccess);
        var propertyId = await SeedPropertyAsync(factory, "Cabin at Hafjell", city: "Øyer", notes: "secret notes");
        var contractId = await SeedContractAsync(factory, "Cabin insurance", ContextContractType.Insurance);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(PartiesPath(contractId),
            new ContractPartyRequest { PropertyId = propertyId, Role = ContractPartyRole.Insured });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var party = (await response.Content.ReadFromJsonAsync<ExistingContractParty>())!;
        Assert.Equal(ContractPartyKind.Property, party.Kind);
        Assert.Equal(3, (int)party.Kind);
        Assert.Null(party.Account);
        Assert.Null(party.Institution);
        Assert.Equal(propertyId, party.Property!.PropertyId);
        Assert.Equal("Cabin at Hafjell", party.Property.Name);
        Assert.Equal(PropertyType.RealEstate, party.Property.Type);
    }

    [Fact]
    public async Task GetContract_ProjectsThePropertyParty_AndLeaksNoDetailField()
    {
        await using var factory = new ApiFactory(FullAccess);
        var propertyId = await SeedPropertyAsync(factory, "Cabin at Hafjell", city: "Øyer", notes: "secret notes");
        var contractId = await SeedContractAsync(factory, "Cabin insurance", ContextContractType.Insurance,
            (propertyId, ContextContractPartyRole.Insured));
        using var client = factory.CreateClient();

        var raw = await client.GetStringAsync($"{ContractsPath}/{contractId}");
        var contract = JsonSerializer.Deserialize<ExistingContract>(raw, JsonSerializerOptions.Web)!;

        var party = Assert.Single(contract.Parties);
        Assert.Equal(ContractPartyKind.Property, party.Kind);
        Assert.Equal("Cabin at Hafjell", party.Property!.Name);

        using var document = JsonDocument.Parse(raw);
        var property = document.RootElement.GetProperty("parties")[0].GetProperty("property");
        Assert.Equal(
            ["propertyId", "name", "type"],
            property.EnumerateObject().Select(p => p.Name));
        Assert.DoesNotContain("Øyer", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("secret notes", raw, StringComparison.Ordinal);
    }

    // ── One-of-three (AC 3) ───────────────────────────────────────────────────

    [Fact]
    public async Task AddParty_WithAPropertyAndAnotherTarget_OrNoTarget_Returns400()
    {
        await using var factory = new ApiFactory(FullAccess);
        var propertyId = await SeedPropertyAsync(factory);
        var accountId = await SeedAccountAsync(factory);
        var contactId = await SeedContactAsync(factory);
        var contractId = await SeedContractAsync(factory, "Other", ContextContractType.Other);
        using var client = factory.CreateClient();

        var withAccount = await client.PostAsJsonAsync(PartiesPath(contractId),
            new ContractPartyRequest { PropertyId = propertyId, AccountId = accountId, Role = ContractPartyRole.Other });
        var withContact = await client.PostAsJsonAsync(PartiesPath(contractId),
            new ContractPartyRequest { PropertyId = propertyId, ContactId = contactId, Role = ContractPartyRole.Other });
        var none = await client.PostAsJsonAsync(PartiesPath(contractId),
            new ContractPartyRequest { Role = ContractPartyRole.Other });

        Assert.Equal(HttpStatusCode.BadRequest, withAccount.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, withContact.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, none.StatusCode);
        Assert.Contains(await ReadErrorKeysAsync(withAccount), k => k.Equals("propertyId", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(await ReadAsync(factory, c => c.ContractParties.ToListAsync()));
    }

    // ── Existence (AC 4) ──────────────────────────────────────────────────────

    /// <summary>
    /// Same status and shape as the account and contact existence checks — keyed on the id that was
    /// sent, echoing only that GUID.
    /// </summary>
    [Fact]
    public async Task AddParty_WithAnUnknownProperty_IsRefused_KeyedOnPropertyId_EchoingOnlyTheGuid()
    {
        await using var factory = new ApiFactory(FullAccess);
        var contractId = await SeedContractAsync(factory, "Other", ContextContractType.Other);
        using var client = factory.CreateClient();
        var unknown = Guid.NewGuid();

        var response = await client.PostAsJsonAsync(PartiesPath(contractId),
            new ContractPartyRequest { PropertyId = unknown, Role = ContractPartyRole.Other });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains(await ReadErrorKeysAsync(response), k => k.Equals("propertyId", StringComparison.OrdinalIgnoreCase));
        Assert.Equal($"Property ID {unknown} not found.", await ReadDetailAsync(response));
    }

    // ── Duplicate (AC 5) ──────────────────────────────────────────────────────

    [Fact]
    public async Task AddParty_SamePropertyAndRoleTwice_Returns409_ButADifferentRoleSucceeds()
    {
        await using var factory = new ApiFactory(FullAccess);
        var propertyId = await SeedPropertyAsync(factory);
        var contractId = await SeedContractAsync(factory, "Mortgage", ContextContractType.Loan);
        using var client = factory.CreateClient();

        var first = await client.PostAsJsonAsync(PartiesPath(contractId),
            new ContractPartyRequest { PropertyId = propertyId, Role = ContractPartyRole.Collateral });
        var again = await client.PostAsJsonAsync(PartiesPath(contractId),
            new ContractPartyRequest { PropertyId = propertyId, Role = ContractPartyRole.Collateral });
        var otherRole = await client.PostAsJsonAsync(PartiesPath(contractId),
            new ContractPartyRequest { PropertyId = propertyId, Role = ContractPartyRole.Guarantor });

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Contains(await ReadErrorKeysAsync(again), k => k.Equals("propertyId", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(HttpStatusCode.Created, otherRole.StatusCode);
    }

    // ── Role matrix (AC 6) ────────────────────────────────────────────────────

    [Fact]
    public async Task AddParty_WithARoleIllegalForTheType_Returns422_KeyedOnRole()
    {
        await using var factory = new ApiFactory(FullAccess);
        var propertyId = await SeedPropertyAsync(factory);
        var contractId = await SeedContractAsync(factory, "Mortgage", ContextContractType.Loan);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(PartiesPath(contractId),
            new ContractPartyRequest { PropertyId = propertyId, Role = ContractPartyRole.Tenant });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains(await ReadErrorKeysAsync(response), k => k.Equals("role", StringComparison.OrdinalIgnoreCase));
    }

    // ── Retargeting (AC 7) ────────────────────────────────────────────────────

    [Fact]
    public async Task UpdateParty_FromAnAccountToAProperty_KeepsThePartyId_AndRecordsRemovedThenAdded()
    {
        await using var factory = new ApiFactory(FullAccess);
        var propertyId = await SeedPropertyAsync(factory);
        var accountId = await SeedAccountAsync(factory);
        var contractId = await SeedContractAsync(factory, "Other", ContextContractType.Other);
        using var client = factory.CreateClient();

        var added = await (await client.PostAsJsonAsync(PartiesPath(contractId),
            new ContractPartyRequest { AccountId = accountId, Role = ContractPartyRole.Other }))
            .Content.ReadFromJsonAsync<ExistingContractParty>();

        var put = await client.PutAsJsonAsync($"{PartiesPath(contractId)}/{added!.ContractPartyId}",
            new ContractPartyRequest { PropertyId = propertyId, Role = ContractPartyRole.Property });

        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var updated = (await put.Content.ReadFromJsonAsync<ExistingContractParty>())!;
        Assert.Equal(added.ContractPartyId, updated.ContractPartyId);
        Assert.Equal(ContractPartyKind.Property, updated.Kind);
        Assert.Null(updated.Account);
        Assert.Equal(propertyId, updated.Property!.PropertyId);

        var events = await client.GetPagedItemsAsync<ExistingContractEvent>($"{ContractsPath}/{contractId}/events");
        // The fixed clock stamps all three alike, so the pair is asserted by count rather than order:
        // the add, then the retarget's removed + added.
        Assert.Equal(1, events.Count(e => e.Type == ContractEventType.PartyRemoved));
        Assert.Equal(2, events.Count(e => e.Type == ContractEventType.PartyAdded));
    }

    // ── Write claim (AC 8) ────────────────────────────────────────────────────

    [Fact]
    public async Task PartyWrites_WithoutContractsUpdate_Return403()
    {
        await using var owner = new ApiFactory(FullAccess);
        var propertyId = await SeedPropertyAsync(owner);
        var contractId = await SeedContractAsync(owner, "Cabin insurance", ContextContractType.Insurance,
            (propertyId, ContextContractPartyRole.Insured));
        var partyId = await ReadAsync(owner, c => c.ContractParties.Select(p => p.ContractPartyId).SingleAsync());
        await using var reader = new ApiFactory(
            [PermissionClaims.ContractsRead, PermissionClaims.PropertiesRead], owner);
        using var client = reader.CreateClient();
        var body = new ContractPartyRequest { PropertyId = propertyId, Role = ContractPartyRole.Object };

        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync(PartiesPath(contractId), body)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await client.PutAsJsonAsync($"{PartiesPath(contractId)}/{partyId}", body)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await client.DeleteAsync($"{PartiesPath(contractId)}/{partyId}")).StatusCode);
    }

    // ── Mass assignment (AC 9) ────────────────────────────────────────────────

    [Fact]
    public async Task AddParty_WithANestedPropertyObject_NeitherRenamesNorCreatesAProperty()
    {
        await using var factory = new ApiFactory(FullAccess);
        var propertyId = await SeedPropertyAsync(factory, "Cabin at Hafjell");
        var contractId = await SeedContractAsync(factory, "Cabin insurance", ContextContractType.Insurance);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(PartiesPath(contractId), new
        {
            propertyId,
            role = ContractPartyRole.Insured,
            property = new { propertyId = Guid.NewGuid(), name = "X", type = PropertyType.Vehicle },
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var properties = await ReadAsync(factory, c => c.Properties.AsNoTracking().ToListAsync());
        var property = Assert.Single(properties);
        Assert.Equal("Cabin at Hafjell", property.Name);
        Assert.Equal(PropertyType.RealEstate, property.Type);
    }

    // ── Property Contracts section (AC 10, 11, 12) ────────────────────────────

    [Fact]
    public async Task GetPropertyContracts_ReturnsOneRowPerContract_RolesInPartyOrder_OrderedByName_ArchivedIncluded()
    {
        await using var factory = new ApiFactory(Reader);
        var propertyId = await SeedPropertyAsync(factory);
        var otherPropertyId = await SeedPropertyAsync(factory, "Other house");
        var mortgageId = await SeedContractAsync(factory, "Mortgage", ContextContractType.Loan,
            [
                (Guid.Parse("00000000-0000-0000-0000-000000000002"), propertyId, ContextContractPartyRole.Property),
                (Guid.Parse("00000000-0000-0000-0000-000000000001"), propertyId, ContextContractPartyRole.Collateral),
                (Guid.Parse("00000000-0000-0000-0000-000000000003"), otherPropertyId, ContextContractPartyRole.Collateral),
            ]);
        var archivedId = await SeedContractAsync(factory, "alpha cover", ContextContractType.Insurance,
            [(Guid.NewGuid(), propertyId, ContextContractPartyRole.Insured)], archived: FixedToday.AddDays(-1));
        await SeedContractAsync(factory, "Unrelated", ContextContractType.Other, (otherPropertyId, ContextContractPartyRole.Other));
        using var client = factory.CreateClient();

        var rows = (await client.GetFromJsonAsync<List<PropertyContractLink>>(PropertyContractsPath(propertyId)))!;

        Assert.Equal([archivedId, mortgageId], rows.Select(r => r.ContractId));
        Assert.Equal(ContractStatus.Archived, rows[0].Status);
        Assert.Equal(ContractType.Loan, rows[1].Type);
        Assert.Equal(ContractStatus.Active, rows[1].Status);
        Assert.Equal([ContractPartyRole.Collateral, ContractPartyRole.Property], rows[1].Roles);
    }

    [Fact]
    public async Task GetPropertyContracts_Returns404ForAnUnknownProperty_AndEmptyForAPropertyOnNoContract()
    {
        await using var factory = new ApiFactory(Reader);
        var loneId = await SeedPropertyAsync(factory);
        using var client = factory.CreateClient();
        var unknown = Guid.NewGuid();

        var missing = await client.GetAsync(PropertyContractsPath(unknown));
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal($"Property ID {unknown} not found.", await ReadDetailAsync(missing));

        Assert.Empty((await client.GetFromJsonAsync<List<PropertyContractLink>>(PropertyContractsPath(loneId)))!);
    }

    [Theory]
    [InlineData(PermissionClaims.PropertiesRead)]
    [InlineData(PermissionClaims.ContractsRead)]
    public async Task GetPropertyContracts_WithOnlyOneOfTheTwoClaims_Returns403(string onlyClaim)
    {
        await using var owner = new ApiFactory(Reader);
        var propertyId = await SeedPropertyAsync(owner);
        await using var factory = new ApiFactory([onlyClaim], owner);
        using var client = factory.CreateClient();

        var response = await client.GetAsync(PropertyContractsPath(propertyId));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ── ContractCount (AC 13) ─────────────────────────────────────────────────

    [Fact]
    public async Task ContractCount_CountsDistinctContracts_OnListAndGet()
    {
        await using var factory = new ApiFactory(Reader);
        var propertyId = await SeedPropertyAsync(factory);
        var loneId = await SeedPropertyAsync(factory, "Lonely");
        await SeedContractAsync(factory, "Mortgage", ContextContractType.Loan,
            (propertyId, ContextContractPartyRole.Collateral), (propertyId, ContextContractPartyRole.Property));
        await SeedContractAsync(factory, "Cover", ContextContractType.Insurance, (propertyId, ContextContractPartyRole.Insured));
        using var client = factory.CreateClient();

        var page = await client.GetFromJsonAsync<PagedResult<ExistingProperty>>(PropertiesPath);
        Assert.Equal(2, page!.Items.Single(p => p.PropertyId == propertyId).ContractCount);
        Assert.Equal(0, page.Items.Single(p => p.PropertyId == loneId).ContractCount);

        Assert.Equal(2, (await GetPropertyAsync(client, propertyId)).ContractCount);
    }

    [Fact]
    public async Task ContractCount_WithoutContractsRead_IsNull_OnListAndGet()
    {
        await using var owner = new ApiFactory(Reader);
        var propertyId = await SeedPropertyAsync(owner);
        await SeedContractAsync(owner, "Mortgage", ContextContractType.Loan, (propertyId, ContextContractPartyRole.Collateral));
        await using var guest = new ApiFactory([PermissionClaims.PropertiesRead], owner);
        using var client = guest.CreateClient();

        var page = await client.GetFromJsonAsync<PagedResult<ExistingProperty>>(PropertiesPath);
        Assert.Null(page!.Items.Single(p => p.PropertyId == propertyId).ContractCount);
        Assert.Null((await GetPropertyAsync(client, propertyId)).ContractCount);
    }

    // ── Property delete cascade + audit (AC 16, 21) ───────────────────────────

    [Fact]
    public async Task DeleteProperty_RemovesItsPartyRows_KeepsTheContractsAndTheirOtherParties()
    {
        await using var factory = new ApiFactory(FullAccess);
        var propertyId = await SeedPropertyAsync(factory);
        var accountId = await SeedAccountAsync(factory);
        var contractId = await SeedContractAsync(factory, "Mortgage", ContextContractType.Loan,
            (propertyId, ContextContractPartyRole.Collateral));
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync(PartiesPath(contractId),
            new ContractPartyRequest { AccountId = accountId, Role = ContractPartyRole.Borrower })).StatusCode);

        var response = await client.DeleteAsync(PropertyPath(propertyId));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var parties = await ReadAsync(factory, c => c.ContractParties.AsNoTracking().ToListAsync());
        var survivor = Assert.Single(parties);
        Assert.Equal(accountId, survivor.AccountId);
        Assert.DoesNotContain(parties, p => p.PropertyId == propertyId);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"{ContractsPath}/{contractId}")).StatusCode);
    }

    [Fact]
    public async Task DeleteProperty_OnTwoContracts_EventsEachRemoval_AndLogsEachRow_WithTheCallerAndOneTimestamp()
    {
        var logs = new CapturingLoggerProvider();
        await using var factory = new ApiFactory(FullAccess, services => services.AddSingleton<ILoggerProvider>(logs));
        var propertyId = await SeedPropertyAsync(factory, "Cabin at Hafjell");
        var loanId = await SeedContractAsync(factory, "Mortgage", ContextContractType.Loan,
            (propertyId, ContextContractPartyRole.Collateral), (propertyId, ContextContractPartyRole.Property));
        var coverId = await SeedContractAsync(factory, "Cover", ContextContractType.Insurance,
            (propertyId, ContextContractPartyRole.Insured));
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync(PropertyPath(propertyId))).StatusCode);

        var events = await ReadAsync(factory, c => c.ContractEvents.AsNoTracking()
            .Where(e => e.Type == Odyssey.Context.ContractEventType.PartyRemoved).ToListAsync());
        Assert.Equal(2, events.Count(e => e.ContractId == loanId));
        Assert.Single(events, e => e.ContractId == coverId);
        Assert.All(events, e =>
        {
            Assert.Equal(ActorUserId, e.CreatedByUserId);
            Assert.Equal(FixedToday, e.CreatedAtUtc);
            Assert.Equal(FixedToday, e.OccurredAt);
        });

        // Visible through the contract events endpoint, not just in the table.
        var visible = await client.GetPagedItemsAsync<ExistingContractEvent>($"{ContractsPath}/{coverId}/events");
        Assert.Contains(visible, e => e.Type == ContractEventType.PartyRemoved);

        var lines = logs.Entries
            .Where(e => e.Message.Contains(ContractPartyAuditAction, StringComparison.Ordinal))
            .ToList();
        Assert.Equal(3, lines.Count);
        Assert.All(lines, line =>
        {
            Assert.Equal(LogLevel.Information, line.Level);
            Assert.Contains($"target Property {propertyId}", line.Message, StringComparison.Ordinal);
            Assert.Contains($"by user {ActorUserId}", line.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("Cabin at Hafjell", line.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("Bergen", line.Message, StringComparison.Ordinal);
        });
    }

    private const string ContractPartyAuditAction = "detached-by-property-delete";

    // ── Party-write log line (AC 20) ──────────────────────────────────────────

    [Fact]
    public async Task PartyWriteLogLine_ForAPropertyParty_NamesTheKindAndGuid_AndNoPropertyField()
    {
        var logs = new CapturingLoggerProvider();
        await using var factory = new ApiFactory(FullAccess, services => services.AddSingleton<ILoggerProvider>(logs));
        var propertyId = await SeedPropertyAsync(factory, "Cabin at Hafjell", city: "Øyer");
        var contractId = await SeedContractAsync(factory, "Cover", ContextContractType.Insurance);
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync(PartiesPath(contractId),
            new ContractPartyRequest { PropertyId = propertyId, Role = ContractPartyRole.Insured })).StatusCode);

        var line = Assert.Single(logs.Entries, e => e.Message.StartsWith("Contract party added", StringComparison.Ordinal));
        Assert.Contains($"target Property {propertyId}", line.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Cabin at Hafjell", line.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Øyer", line.Message, StringComparison.Ordinal);
    }

    // ── Type-change blockers (AC 19) ──────────────────────────────────────────

    [Fact]
    public async Task TypeChange_BlockedByAPropertyParty_NamesThePropertyInTheBlockers()
    {
        await using var factory = new ApiFactory(FullAccess);
        var propertyId = await SeedPropertyAsync(factory, "Cabin at Hafjell");
        var contractId = await SeedContractAsync(factory, "Cover", ContextContractType.Insurance,
            (propertyId, ContextContractPartyRole.Insured));
        using var client = factory.CreateClient();
        var contract = await client.GetFromJsonAsync<ExistingContract>($"{ContractsPath}/{contractId}");

        var response = await client.PutAsJsonAsync($"{ContractsPath}/{contractId}", new UpdateContract
        {
            Name = contract!.Name,
            Type = ContractType.Employment,
            StartDate = contract.StartDate,
        });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var blockers = document.RootElement.EnumerateObject()
            .Single(p => p.Name.Equals("typeChange", StringComparison.OrdinalIgnoreCase)).Value
            .Deserialize<ContractTypeChangeBlockers>(JsonSerializerOptions.Web)!;
        var blocker = Assert.Single(blockers.Parties);
        Assert.Equal("Cabin at Hafjell", blocker.DisplayName);
        Assert.Equal(ContractPartyRole.Insured, blocker.Role);
    }

    // ── Fixture ───────────────────────────────────────────────────────────────

    private sealed class ApiFactory : OdysseyApiFactory
    {
        public ApiFactory(IReadOnlyCollection<string>? permissions, Action<IServiceCollection>? extra = null)
            : base(permissions, ActorUserId, configuration: null, configureServices: services => Configure(services, extra))
        {
        }

        public ApiFactory(IReadOnlyCollection<string>? permissions, OdysseyApiFactory sharing)
            : base(permissions, ActorUserId, configuration: null, configureServices: services => Configure(services, null),
                sharingStoreWith: sharing)
        {
        }

        private static void Configure(IServiceCollection services, Action<IServiceCollection>? extra)
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(new FixedTimeProvider(FixedToday));
            extra?.Invoke(services);
        }
    }

    private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow);
    }

    private static async Task<Guid> SeedAccountAsync(OdysseyApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        await context.Database.EnsureCreatedAsync();
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

    private static async Task<Guid> SeedContactAsync(OdysseyApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        await context.Database.EnsureCreatedAsync();
        var contact = new Contact
        {
            ContactId = Guid.NewGuid(),
            ExternalUid = $"urn:uuid:{Guid.NewGuid()}",
            NormalizedName = "acme corp",
            Type = ContactType.Organization,
            OrganizationDetails = new() { LegalName = "Acme Corp" },
        };
        context.Contacts.Add(contact);
        await context.SaveChangesAsync();
        return contact.ContactId;
    }

    /// <summary>A signed contract that started a month ago — so it derives as Active.</summary>
    private static Task<Guid> SeedContractAsync(
        OdysseyApiFactory factory, string name, ContextContractType type,
        params (Guid PropertyId, ContextContractPartyRole Role)[] parties) =>
        SeedContractAsync(factory, name, type, [.. parties.Select(p => (Guid.NewGuid(), p.PropertyId, p.Role))]);

    private static async Task<Guid> SeedContractAsync(
        OdysseyApiFactory factory, string name, ContextContractType type,
        (Guid PartyId, Guid PropertyId, ContextContractPartyRole Role)[] parties, DateTime? archived = null)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        await context.Database.EnsureCreatedAsync();

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
        foreach (var (partyId, propertyId, role) in parties)
        {
            contract.Parties.Add(new ContractParty
            {
                ContractPartyId = partyId,
                ContractId = contract.ContractId,
                PropertyId = propertyId,
                Role = role,
            });
        }

        context.Contracts.Add(contract);
        await context.SaveChangesAsync();
        return contract.ContractId;
    }
}
