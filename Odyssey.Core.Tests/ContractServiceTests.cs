using Odyssey.Core;
using Odyssey.Dtos;
using Microsoft.Extensions.Options;
using Odyssey.Context;
using Odyssey.Dtos.Finance;
using Xunit;
using DtoContractType = Odyssey.Dtos.Finance.ContractType;
using Odyssey.Core.Finance;
using Context = Odyssey.Context;

namespace Odyssey.Core.Tests;

/// <summary>
/// Unit coverage for the derived-status ordering and the XOR / target-existence / duplicate party
/// branches that <c>ContractsApiTests</c> only reaches over HTTP (issue #240 H2).
/// </summary>
public class ContractServiceTests
{
    // Fixed UTC "today" so DeriveStatus is deterministic.
    private static readonly DateTime FixedToday = new(2026, 6, 15, 0, 0, 0, DateTimeKind.Utc);

    // Contact moved to OdysseyContext; one journal per test backs both the seeded contact and the
    // IContactLookup the service resolves through (xUnit creates a fresh test-class instance per test).
    private readonly OdysseyContext journal = TestContextFactory.CreateJournal();

    private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow);
    }

    /// <summary>
    /// The contract caps moved into the settings store (issue #421 Wave 3), so the service takes a
    /// lookup rather than <c>IOptions&lt;ContractOptions&gt;</c> — that class is gone.
    /// </summary>
    private ContractService CreateService(OdysseyContext context, ISystemSettingsLookup? caps = null) =>
        new(context, TestContextFactory.ContactLookup(journal), new FixedTimeProvider(FixedToday),
            caps ?? new StubFinanceCaps());

    /// <summary>Shipped cap values; literals because this project cannot reference the key catalogue.</summary>
    private sealed class StubFinanceCaps : ISystemSettingsLookup
    {
        public FinanceRequestCaps Caps { get; set; } = new(25, 50, 1000, 100, 50);

        public Task<InsurancePolicySettings> GetInsurancePolicySettingsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new InsurancePolicySettings(30, 1000));

        public Task<FinanceRequestCaps> GetRequestCapsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Caps);

        // The subscriptions limits joined the interface in issue #437. Not used by ContractService —
        // present because the interface is one seam for the whole finance domain.
        public Task<SubscriptionSettings> GetSubscriptionSettingsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new SubscriptionSettings(45, 6, 1000));
    }

    private static NewContract NewContract(DateTime start, DateTime? end = null) => new()
    {
        Name = "Agreement",
        Type = DtoContractType.Service,
        StartDate = start,
        EndDate = end,
    };

    private static NewContract OneOffContract(DateTime completion) => new()
    {
        Name = "One-off agreement",
        Type = DtoContractType.Service,
        CompletionDate = completion,
    };

    // ── Derived status ordering & boundaries (§6) ───────────────────────────────

    [Fact]
    public async Task Create_StartInPast_NoEnd_IsActive()
    {
        await using var context = TestContextFactory.Create();
        var service = CreateService(context);

        var created = await service.Create(NewContract(FixedToday.AddDays(-10)));

        Assert.Equal(ContractStatus.Active, created.Status);
    }

    [Fact]
    public async Task Create_StartEqualsToday_IsActive_NotUpcoming()
    {
        await using var context = TestContextFactory.Create();
        var service = CreateService(context);

        // Boundary: startDate.Date > today is false when start == today.
        var created = await service.Create(NewContract(FixedToday));

        Assert.Equal(ContractStatus.Active, created.Status);
    }

    [Fact]
    public async Task Create_StartTomorrow_IsUpcoming()
    {
        await using var context = TestContextFactory.Create();
        var service = CreateService(context);

        var created = await service.Create(NewContract(FixedToday.AddDays(1)));

        Assert.Equal(ContractStatus.Upcoming, created.Status);
    }

    [Fact]
    public async Task Create_EndEqualsToday_IsActive_NotExpired()
    {
        await using var context = TestContextFactory.Create();
        var service = CreateService(context);

        // Boundary: endDate.Date < today is false when end == today.
        var created = await service.Create(NewContract(FixedToday.AddDays(-10), FixedToday));

        Assert.Equal(ContractStatus.Active, created.Status);
    }

    [Fact]
    public async Task Create_EndYesterday_IsExpired()
    {
        await using var context = TestContextFactory.Create();
        var service = CreateService(context);

        var created = await service.Create(NewContract(FixedToday.AddDays(-100), FixedToday.AddDays(-1)));

        Assert.Equal(ContractStatus.Expired, created.Status);
    }

    // ── One-off (completion date) contracts ─────────────────────────────────────

    [Fact]
    public async Task Create_TermStartOmitted_IsActive()
    {
        await using var context = TestContextFactory.Create();
        var service = CreateService(context);

        // A term contract may be open-started (no start date) — that is Active, not Upcoming.
        var created = await service.Create(new NewContract { Name = "Open", Type = DtoContractType.Service });

        Assert.Null(created.StartDate);
        Assert.Equal(ContractStatus.Active, created.Status);
    }

    [Fact]
    public async Task Create_OneOff_ClearsTermDates_AndPersistsCompletion()
    {
        await using var context = TestContextFactory.Create();
        var service = CreateService(context);

        // Even if a caller sends stray term dates, a one-off (completion set) clears them.
        var request = OneOffContract(FixedToday.AddDays(-5));
        request.StartDate = FixedToday.AddDays(-30);
        request.EndDate = FixedToday.AddDays(30);

        var created = await service.Create(request);

        Assert.Null(created.StartDate);
        Assert.Null(created.EndDate);
        Assert.Equal(FixedToday.AddDays(-5), created.CompletionDate);
    }

    [Fact]
    public async Task Create_OneOff_CompletionInPast_IsActive()
    {
        await using var context = TestContextFactory.Create();
        var service = CreateService(context);

        var created = await service.Create(OneOffContract(FixedToday.AddDays(-1)));

        Assert.Equal(ContractStatus.Active, created.Status);
    }

    [Fact]
    public async Task Create_OneOff_CompletionInFuture_IsUpcoming()
    {
        await using var context = TestContextFactory.Create();
        var service = CreateService(context);

        var created = await service.Create(OneOffContract(FixedToday.AddDays(1)));

        Assert.Equal(ContractStatus.Upcoming, created.Status);
    }

    [Fact]
    public async Task Update_Archived_OverridesDateDerivedStatus()
    {
        await using var context = TestContextFactory.Create();
        var service = CreateService(context);

        // Archiving requires an ended term, so the contract that gets archived is a lapsed one — and
        // the point of the test survives: Archived outranks the status the dates would derive.
        var created = await service.Create(NewContract(FixedToday.AddDays(-100), FixedToday.AddDays(-1)));
        Assert.Equal(ContractStatus.Expired, created.Status);

        var archived = await service.Update(created.ContractId, new UpdateContract
        {
            Name = created.Name,
            Type = created.Type,
            StartDate = created.StartDate,
            EndDate = created.EndDate,
            IsArchived = true,
        });

        Assert.Equal(ContractStatus.Archived, archived!.Status);
    }

    [Fact]
    public async Task Update_Archive_RequiresAnEndedContract()
    {
        await using var context = TestContextFactory.Create();
        var service = CreateService(context);
        var created = await service.Create(NewContract(FixedToday.AddDays(-10), FixedToday.AddDays(10)));

        UpdateContract Request(DateTime? end, DateTime? completion = null) => new()
        {
            Name = created.Name,
            Type = created.Type,
            StartDate = completion is null ? created.StartDate : null,
            EndDate = end,
            CompletionDate = completion,
            IsArchived = true,
        };

        // Running: refused — the lifecycle is ordered, so Archived implies ended.
        await Assert.ThrowsAsync<DomainValidationException>(() => service.Update(created.ContractId, Request(FixedToday.AddDays(10))));

        // Today is not "ended" either: DeriveStatus calls a term Expired only once end < today.
        await Assert.ThrowsAsync<DomainValidationException>(() => service.Update(created.ContractId, Request(FixedToday)));

        // One PUT may end and archive together — the check reads the request's dates.
        var archived = await service.Update(created.ContractId, Request(FixedToday.AddDays(-1)));
        Assert.NotNull(archived!.Archived);
        Assert.Equal(ContractStatus.Archived, archived.Status);
    }

    [Fact]
    public async Task Update_Archive_AcceptsASettledOneOff()
    {
        await using var context = TestContextFactory.Create();
        var service = CreateService(context);

        // A delivered one-off is over, but its derived status is Active, not Expired — it is a settled
        // record rather than a lapsed term. The gate has to accept it, which is why it checks the
        // dates rather than comparing against ContractStatus.Expired.
        var created = await service.Create(OneOffContract(FixedToday));
        Assert.Equal(ContractStatus.Active, created.Status);

        var archived = await service.Update(created.ContractId, new UpdateContract
        {
            Name = created.Name,
            Type = created.Type,
            CompletionDate = FixedToday,
            IsArchived = true,
        });

        Assert.NotNull(archived!.Archived);
    }

    [Fact]
    public async Task Update_ArchivedContract_StaysEditableAndRestorable()
    {
        await using var context = TestContextFactory.Create();
        var service = CreateService(context);
        var created = await service.Create(NewContract(FixedToday.AddDays(-100), FixedToday.AddDays(-1)));
        await service.Update(created.ContractId, new UpdateContract
        {
            Name = created.Name, Type = created.Type,
            StartDate = created.StartDate, EndDate = created.EndDate, IsArchived = true,
        });

        // Only the TRANSITION into archived is gated. Re-saving an already-archived row must not
        // re-validate, or a row archived before this rule existed could never be edited or restored.
        UpdateContract Running(bool isArchived) => new()
        {
            Name = created.Name, Type = created.Type,
            StartDate = FixedToday.AddDays(-10), EndDate = FixedToday.AddDays(10),
            IsArchived = isArchived,
        };

        Assert.NotNull((await service.Update(created.ContractId, Running(isArchived: true)))!.Archived);
        Assert.Null((await service.Update(created.ContractId, Running(isArchived: false)))!.Archived);
    }

    // ── One-off / term update transitions & boundaries ──────────────────────────

    [Fact]
    public async Task Update_TermToOneOff_ClearsTermDates_AndSetsCompletion()
    {
        await using var context = TestContextFactory.Create();
        var service = CreateService(context);

        var created = await service.Create(NewContract(FixedToday.AddDays(-10), FixedToday.AddDays(10)));
        Assert.NotNull(created.StartDate);

        var updated = await service.Update(created.ContractId, new UpdateContract
        {
            Name = created.Name,
            Type = created.Type,
            CompletionDate = FixedToday.AddDays(-2),
        });

        Assert.NotNull(updated);
        Assert.Null(updated!.StartDate);
        Assert.Null(updated.EndDate);
        Assert.Equal(FixedToday.AddDays(-2), updated.CompletionDate);
        Assert.Equal(ContractStatus.Active, updated.Status);
    }

    [Fact]
    public async Task Update_OneOffToTerm_ClearsCompletion()
    {
        await using var context = TestContextFactory.Create();
        var service = CreateService(context);

        var created = await service.Create(OneOffContract(FixedToday.AddDays(-2)));
        Assert.NotNull(created.CompletionDate);

        var updated = await service.Update(created.ContractId, new UpdateContract
        {
            Name = created.Name,
            Type = created.Type,
            StartDate = FixedToday.AddDays(-5),
            EndDate = FixedToday.AddDays(5),
        });

        Assert.NotNull(updated);
        Assert.Null(updated!.CompletionDate);
        Assert.Equal(FixedToday.AddDays(-5), updated.StartDate);
        Assert.Equal(FixedToday.AddDays(5), updated.EndDate);
        Assert.Equal(ContractStatus.Active, updated.Status);
    }

    [Fact]
    public async Task Create_OneOff_CompletionEqualsToday_IsActive_NotUpcoming()
    {
        await using var context = TestContextFactory.Create();
        var service = CreateService(context);

        // Boundary: completion.Date > today is false when completion == today (mirrors the term boundaries).
        var created = await service.Create(OneOffContract(FixedToday));

        Assert.Equal(ContractStatus.Active, created.Status);
    }

    [Fact]
    public async Task Create_TermEndOnly_StartOmitted_IsAllowed_AndDerivesFromEnd()
    {
        await using var context = TestContextFactory.Create();
        var service = CreateService(context);

        // A term may be open-started: an end with no start is valid (the end≥start check only applies
        // when both are present) and derives its status from the end date alone.
        var created = await service.Create(new NewContract
        {
            Name = "Open-started",
            Type = DtoContractType.Service,
            EndDate = FixedToday.AddDays(-1),
        });

        Assert.Null(created.StartDate);
        Assert.Equal(FixedToday.AddDays(-1), created.EndDate);
        Assert.Equal(ContractStatus.Expired, created.Status);
    }

    [Fact]
    public async Task GetSummary_CountsOneOffByDerivedStatus()
    {
        await using var context = TestContextFactory.Create();
        var service = CreateService(context);

        await service.Create(OneOffContract(FixedToday.AddDays(5))); // Upcoming
        await service.Create(NewContract(FixedToday.AddDays(-3)));   // Active term

        var summary = await service.GetSummary();

        Assert.Equal(1, summary.CountsByStatus.Upcoming);
        Assert.Equal(1, summary.CountsByStatus.Active);
    }

    [Fact]
    public async Task SearchFor_CarriesCompletionDate_ForOneOff()
    {
        await using var context = TestContextFactory.Create();
        var service = CreateService(context);

        await service.Create(OneOffContract(FixedToday.AddDays(3)));

        var items = (await service.ListAsync(new ContractsQueryParams())).Items;

        var item = Assert.Single(items);
        Assert.Equal(FixedToday.AddDays(3), item.CompletionDate);
        Assert.Null(item.StartDate);
    }

    // ── XOR party invariant: setCount 0/1/2/3 ───────────────────────────────────

    [Fact]
    public async Task AddParty_ZeroTargets_Throws()
    {
        await using var context = TestContextFactory.Create();
        var service = CreateService(context);
        var contract = await service.Create(NewContract(FixedToday));

        await Assert.ThrowsAsync<DomainValidationException>(() =>
            service.AddParty(contract.ContractId, new AddContractPartyRequest()));
    }

    [Fact]
    public async Task AddParty_TwoTargets_Throws()
    {
        await using var context = TestContextFactory.Create();
        var service = CreateService(context);
        var (accountId, contactId, _) = await SeedTargets(context);
        var contract = await service.Create(NewContract(FixedToday));

        await Assert.ThrowsAsync<DomainValidationException>(() =>
            service.AddParty(contract.ContractId,
                new AddContractPartyRequest { AccountId = accountId, ContactId = contactId }));
    }

    [Fact]
    public async Task AddParty_ThreeTargets_Throws()
    {
        await using var context = TestContextFactory.Create();
        var service = CreateService(context);
        var (accountId, contactId, policyId) = await SeedTargets(context);
        var contract = await service.Create(NewContract(FixedToday));

        await Assert.ThrowsAsync<DomainValidationException>(() =>
            service.AddParty(contract.ContractId, new AddContractPartyRequest
            {
                AccountId = accountId,
                ContactId = contactId,
                InsurancePolicyId = policyId,
            }));
    }

    [Fact]
    public async Task AddParty_ExactlyOneTarget_Succeeds()
    {
        await using var context = TestContextFactory.Create();
        var service = CreateService(context);
        var (accountId, _, _) = await SeedTargets(context);
        var contract = await service.Create(NewContract(FixedToday));

        var party = await service.AddParty(contract.ContractId,
            new AddContractPartyRequest { AccountId = accountId });

        Assert.NotNull(party);
        Assert.Equal(ContractPartyKind.Account, party!.Kind);
        Assert.Equal(accountId, party.Account!.AccountId);
    }

    // ── Target existence & duplicate branches ───────────────────────────────────

    [Fact]
    public async Task AddParty_UnknownAccount_ThrowsNotFound()
    {
        await using var context = TestContextFactory.Create();
        var service = CreateService(context);
        var contract = await service.Create(NewContract(FixedToday));

        await Assert.ThrowsAsync<DomainNotFoundException>(() =>
            service.AddParty(contract.ContractId, new AddContractPartyRequest { AccountId = Guid.NewGuid() }));
    }

    [Fact]
    public async Task AddParty_UnknownContact_ThrowsNotFound()
    {
        await using var context = TestContextFactory.Create();
        var service = CreateService(context);
        var contract = await service.Create(NewContract(FixedToday));

        await Assert.ThrowsAsync<DomainNotFoundException>(() =>
            service.AddParty(contract.ContractId, new AddContractPartyRequest { ContactId = Guid.NewGuid() }));
    }

    [Fact]
    public async Task AddParty_UnknownInsurancePolicy_ThrowsNotFound()
    {
        await using var context = TestContextFactory.Create();
        var service = CreateService(context);
        var contract = await service.Create(NewContract(FixedToday));

        await Assert.ThrowsAsync<DomainNotFoundException>(() =>
            service.AddParty(contract.ContractId, new AddContractPartyRequest { InsurancePolicyId = Guid.NewGuid() }));
    }

    [Fact]
    public async Task AddParty_DuplicateTarget_ThrowsConflict()
    {
        await using var context = TestContextFactory.Create();
        var service = CreateService(context);
        var (accountId, _, _) = await SeedTargets(context);
        var contract = await service.Create(NewContract(FixedToday));

        await service.AddParty(contract.ContractId, new AddContractPartyRequest { AccountId = accountId });

        await Assert.ThrowsAsync<DomainConflictException>(() =>
            service.AddParty(contract.ContractId, new AddContractPartyRequest { AccountId = accountId }));
    }

    [Fact]
    public async Task AddParty_OnArchivedContract_Throws()
    {
        await using var context = TestContextFactory.Create();
        var service = CreateService(context);
        var (accountId, _, _) = await SeedTargets(context);
        var contract = await service.Create(NewContract(FixedToday.AddDays(-100), FixedToday.AddDays(-1)));
        await service.Update(contract.ContractId, new UpdateContract
        {
            Name = contract.Name,
            Type = contract.Type,
            StartDate = contract.StartDate,
            EndDate = contract.EndDate,
            IsArchived = true,
        });

        await Assert.ThrowsAsync<DomainValidationException>(() =>
            service.AddParty(contract.ContractId, new AddContractPartyRequest { AccountId = accountId }));
    }

    private async Task<(Guid AccountId, Guid ContactId, Guid PolicyId)> SeedTargets(OdysseyContext context)
    {
        var account = new Account
        {
            Name = "Salary account",
            Description = "Primary",
            Opened = DateTime.UtcNow,
            AccountType = Context.AccountType.CheckingAccount,
            CurrencyCode = "USD",
        };
        context.Accounts.Add(account);

        var contact = new Contact
        {
            ExternalUid = $"urn:uuid:{Guid.NewGuid()}",
            NormalizedName = "acme corp",
            Type = ContactType.Organization,
            OrganizationDetails = new() { LegalName = "Acme Corp" },
        };
        journal.Contacts.Add(contact);
        await journal.SaveChangesAsync();
        await context.SaveChangesAsync();

        var policy = new InsurancePolicy
        {
            Name = "Liability cover",
            Type = Context.InsurancePolicyType.Liability,
            InsurerId = contact.ContactId,
            CreatedAtUtc = DateTime.UtcNow,
        };
        context.InsurancePolicies.Add(policy);
        await context.SaveChangesAsync();

        return (account.AccountId, contact.ContactId, policy.InsurancePolicyId);
    }
}
