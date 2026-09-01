using Odyssey.Core;
using Microsoft.Extensions.Options;
using Odyssey.Context;
using Odyssey.Dtos.Finance;
using Odyssey.Dtos;
using Xunit;
using DtoInsurancePolicyType = Odyssey.Dtos.Finance.InsurancePolicyType;
using DtoPolicyFileType = Odyssey.Dtos.Finance.PolicyFileType;
using Odyssey.Core.Finance;
using Context = Odyssey.Context;

namespace Odyssey.Core.Tests;

/// <summary>
/// Unit coverage for the InsuranceService branches that <c>InsuranceApiTests</c> only reaches over HTTP
/// (issue #175): the deterministic coverage-status derivation and its boundaries, the current-renewal
/// tie-break (latest FromDate, then latest CreatedAtUtc), independent premium/coverage currency
/// validation, insurer/insured-account reference validation, the defensive caps, and the summary's
/// live/archived split and base-currency conversion. The largest finance service otherwise has no
/// unit-level coverage; driving the caps and boundaries here is deterministic and far cheaper than
/// end-to-end.
/// </summary>
public class InsuranceServiceTests
{
    // Fixed UTC "today" so coverage derivation is deterministic (mirrors ContractServiceTests).
    private static readonly DateTime FixedToday = new(2026, 6, 15, 0, 0, 0, DateTimeKind.Utc);

    // Contact moved to OdysseyContext; one journal per test backs both the seeded insurers and the
    // IContactLookup the service resolves through (xUnit creates a fresh test-class instance per test).
    private readonly OdysseyContext journal = TestContextFactory.CreateJournal();

    private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow);
    }

    // Fixed at the same values InsuranceOptions used to default to (issue #349 moved these two out
    // of static config and into the database-backed system-settings store) — no existing test
    // exercises a non-default window/cap, so this keeps every one of them behaving identically.
    private sealed class FixedSystemSettingsLookup(int expiringSoonWindowDays = 30, int maxSummaryPolicies = 1000)
        : ISystemSettingsLookup
    {
        public Task<InsurancePolicySettings> GetInsurancePolicySettingsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new InsurancePolicySettings(expiringSoonWindowDays, maxSummaryPolicies));

        /// <summary>
        /// The per-request caps moved into the store too (issue #421 Wave 3). Defaults mirror the
        /// shipped values, so a test that does not care about them reads production behaviour; the
        /// literals are inline rather than referencing SystemSettingsKeys because this project has no
        /// dependency on Odyssey.Context — the reason the interface lives in Odyssey.Core.Finance.
        /// </summary>
        public FinanceRequestCaps Caps { get; set; } = new(25, 50, 1000, 100, 50, 50);

        public Task<FinanceRequestCaps> GetRequestCapsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Caps);

        // The subscriptions limits joined the interface in issue #437. Not used by InsuranceService —
        // present because the interface is one seam for the whole finance domain.
        public Task<SubscriptionSettings> GetSubscriptionSettingsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new SubscriptionSettings(45, 6, 1000));
    }

    // InsuranceOptions is gone: both of its properties moved into the settings store (issue #421
    // Wave 3), so the lookup is the only source now.
    private InsuranceService CreateService(
        OdysseyContext context, ISystemSettingsLookup? systemSettingsLookup = null) =>
        new(
            context,
            new CurrencyConversionService(context),
            TestContextFactory.ContactLookup(journal),
            new FixedTimeProvider(FixedToday),
            systemSettingsLookup ?? new FixedSystemSettingsLookup());

    // ── Seed helpers ────────────────────────────────────────────────────────────

    private async Task<Guid> SeedInsurer(DateTime? archived = null)
    {
        var insurer = new Contact
        {
            ExternalUid = $"urn:uuid:{Guid.NewGuid()}",
            NormalizedName = "acme insurance",
            Type = ContactType.Organization,
            Archived = archived,
            OrganizationDetails = new() { LegalName = "Acme Insurance" },
        };
        journal.Contacts.Add(insurer);
        await journal.SaveChangesAsync();
        return insurer.ContactId;
    }

    private static async Task<Guid> SeedAccount(OdysseyContext context, DateTime? archived = null)
    {
        var account = new Account
        {
            Name = "Insured home",
            Description = "Primary residence",
            Opened = FixedToday,
            AccountType = Context.AccountType.CheckingAccount,
            CurrencyCode = "USD",
            Archived = archived,
        };
        context.Accounts.Add(account);
        await context.SaveChangesAsync();
        return account.AccountId;
    }

    private static NewInsurancePolicy NewPolicy(Guid insurerId, Guid? accountId = null) => new()
    {
        Name = "Home cover",
        Type = DtoInsurancePolicyType.Home,
        InsurerIds = [insurerId],
        InsuredAccountIds = accountId is { } id ? [id] : null,
    };

    private static NewPolicyRenewal NewRenewal(
        DateTime from,
        DateTime to,
        decimal premium = 100m,
        string premiumCurrency = "USD",
        decimal coverage = 10_000m,
        string coverageCurrency = "USD") => new()
    {
        FromDate = from,
        ToDate = to,
        Premium = premium,
        PremiumCurrencyCode = premiumCurrency,
        CoverageAmount = coverage,
        CoverageCurrencyCode = coverageCurrency,
    };

    // Seed a renewal directly so CreatedAtUtc can be controlled for the overlap tie-break (AddRenewal
    // always stamps CreatedAtUtc from the fixed clock, so two service-added renewals would tie).
    private static async Task SeedRenewal(
        OdysseyContext context, Guid policyId, DateTime from, DateTime to, DateTime createdAtUtc, decimal premium)
    {
        context.PolicyRenewals.Add(new PolicyRenewal
        {
            InsurancePolicyId = policyId,
            FromDate = from,
            ToDate = to,
            Premium = premium,
            CoverageAmount = 1_000m,
            CreatedAtUtc = createdAtUtc,
        });
        await context.SaveChangesAsync();
    }

    // ── List boundary dates (the collapsed row's headline figure) ───────────────

    [Fact]
    public async Task List_CarriesTheBoundaryDates_ARowHeadlinesOn()
    {
        await using var context = TestContextFactory.Create();
        var service = CreateService(context);
        var insurerId = await SeedInsurer();
        var lapsed = await service.Create(NewPolicy(insurerId));
        await service.AddRenewal(lapsed.InsurancePolicyId, NewRenewal(FixedToday.AddDays(-800), FixedToday.AddDays(-450)));
        await service.AddRenewal(lapsed.InsurancePolicyId, NewRenewal(FixedToday.AddDays(-440), FixedToday.AddDays(-100)));

        var upcoming = await service.Create(NewPolicy(insurerId));
        await service.AddRenewal(upcoming.InsurancePolicyId, NewRenewal(FixedToday.AddDays(30), FixedToday.AddDays(400)));
        await service.AddRenewal(upcoming.InsurancePolicyId, NewRenewal(FixedToday.AddDays(410), FixedToday.AddDays(700)));

        var never = await service.Create(NewPolicy(insurerId));

        var page = await service.ListAsync(new InsurancePoliciesQueryParams());
        var byId = page.Items.ToDictionary(i => i.InsurancePolicyId);

        // Lapsed headlines on the LATEST period's end — the most recent cover, not the first.
        Assert.Equal(CoverageStatus.Lapsed, byId[lapsed.InsurancePolicyId].CoverageStatus);
        Assert.Equal(FixedToday.AddDays(-100), byId[lapsed.InsurancePolicyId].LatestRenewalEndDate);

        // Upcoming headlines on the EARLIEST period's start — when cover begins.
        Assert.Equal(CoverageStatus.Upcoming, byId[upcoming.InsurancePolicyId].CoverageStatus);
        Assert.Equal(FixedToday.AddDays(30), byId[upcoming.InsurancePolicyId].EarliestRenewalStartDate);

        // No period ever recorded: both are null, which is exactly the NoCoverage case — the one
        // state with no date to show, so the row reads "No coverage" rather than a bare status word.
        Assert.Equal(CoverageStatus.NoCoverage, byId[never.InsurancePolicyId].CoverageStatus);
        Assert.Null(byId[never.InsurancePolicyId].LatestRenewalEndDate);
        Assert.Null(byId[never.InsurancePolicyId].EarliestRenewalStartDate);
    }

    // ── Accrued premium (the record card's Total premium tile) ──────────────────

    [Fact]
    public async Task Get_AccruedPremium_SumsEveryPeriodThroughTheCurrentOne()
    {
        await using var context = TestContextFactory.Create();
        var service = CreateService(context);
        var insurerId = await SeedInsurer();
        var created = await service.Create(NewPolicy(insurerId));

        // Two past periods and the current one all count; a period starting after the current one
        // ends does not — the figure accrues THROUGH the current period, not beyond it.
        await service.AddRenewal(created.InsurancePolicyId, NewRenewal(FixedToday.AddDays(-800), FixedToday.AddDays(-450), premium: 100m));
        await service.AddRenewal(created.InsurancePolicyId, NewRenewal(FixedToday.AddDays(-440), FixedToday.AddDays(-100), premium: 110m));
        await service.AddRenewal(created.InsurancePolicyId, NewRenewal(FixedToday.AddDays(-90), FixedToday.AddDays(90), premium: 120m));
        await service.AddRenewal(created.InsurancePolicyId, NewRenewal(FixedToday.AddDays(120), FixedToday.AddDays(400), premium: 130m));

        var policy = await service.Get(created.InsurancePolicyId);

        Assert.Equal(330m, policy!.AccruedPremium);
        Assert.Equal("USD", policy.AccruedPremiumCurrencyCode);
        Assert.Equal(3, policy.AccruedPremiumPeriods);
    }

    [Fact]
    public async Task Get_AccruedPremium_SkipsAPeriodItCannotConvert()
    {
        await using var context = TestContextFactory.Create();
        var service = CreateService(context);
        var insurerId = await SeedInsurer();
        var created = await service.Create(NewPolicy(insurerId));

        // No exchange rate is seeded, so the EUR period cannot be expressed in the current period's
        // USD. It is LEFT OUT rather than added at face value — a silently mixed-currency sum is
        // worse than a smaller one — and the period count reports what was actually summed, so the
        // figure and the tile's "N periods to date" caption can never disagree.
        await service.AddRenewal(created.InsurancePolicyId, NewRenewal(FixedToday.AddDays(-800), FixedToday.AddDays(-450), premium: 100m, premiumCurrency: "EUR"));
        await service.AddRenewal(created.InsurancePolicyId, NewRenewal(FixedToday.AddDays(-90), FixedToday.AddDays(90), premium: 120m));

        var policy = await service.Get(created.InsurancePolicyId);

        Assert.Equal(120m, policy!.AccruedPremium);
        Assert.Equal(1, policy.AccruedPremiumPeriods);
    }

    [Fact]
    public async Task Get_AccruedPremium_IsNull_WithoutACurrentPeriod()
    {
        await using var context = TestContextFactory.Create();
        var service = CreateService(context);
        var insurerId = await SeedInsurer();
        var created = await service.Create(NewPolicy(insurerId));

        // Nothing to accrue "through": the tile is absent rather than showing a zero that would read
        // as a real total.
        await service.AddRenewal(created.InsurancePolicyId, NewRenewal(FixedToday.AddDays(-800), FixedToday.AddDays(-450)));

        var policy = await service.Get(created.InsurancePolicyId);

        Assert.Null(policy!.AccruedPremium);
        Assert.Null(policy.AccruedPremiumCurrencyCode);
        Assert.Equal(0, policy.AccruedPremiumPeriods);
    }

    // ── Coverage-status derivation & boundaries (§5) ─────────────────────────────

    [Fact]
    public async Task Get_NoRenewals_IsNoCoverage()
    {
        await using var context = TestContextFactory.Create();
        var service = CreateService(context);
        var insurerId = await SeedInsurer();

        var created = await service.Create(NewPolicy(insurerId));
        var policy = await service.Get(created.InsurancePolicyId);

        Assert.Equal(CoverageStatus.NoCoverage, policy!.CoverageStatus);
        Assert.Null(policy.CurrentRenewal);
    }

    [Fact]
    public async Task Get_RenewalContainsToday_EndsBeyondWindow_IsActive()
    {
        await using var context = TestContextFactory.Create();
        var service = CreateService(context);
        var insurerId = await SeedInsurer();
        var created = await service.Create(NewPolicy(insurerId));

        await service.AddRenewal(created.InsurancePolicyId, NewRenewal(FixedToday.AddDays(-100), FixedToday.AddDays(100)));

        var policy = await service.Get(created.InsurancePolicyId);
        Assert.Equal(CoverageStatus.Active, policy!.CoverageStatus);
        Assert.NotNull(policy.CurrentRenewal);
    }

    [Fact]
    public async Task Get_RenewalEndsWithinWindow_IsExpiringSoon()
    {
        await using var context = TestContextFactory.Create();
        var service = CreateService(context); // default window = 30 days
        var insurerId = await SeedInsurer();
        var created = await service.Create(NewPolicy(insurerId));

        await service.AddRenewal(created.InsurancePolicyId, NewRenewal(FixedToday.AddDays(-100), FixedToday.AddDays(15)));

        var policy = await service.Get(created.InsurancePolicyId);
        Assert.Equal(CoverageStatus.ExpiringSoon, policy!.CoverageStatus);
    }

    [Fact]
    public async Task Get_EndEqualsTodayPlusWindow_IsExpiringSoon_Boundary()
    {
        await using var context = TestContextFactory.Create();
        var service = CreateService(context); // window = 30
        var insurerId = await SeedInsurer();
        var created = await service.Create(NewPolicy(insurerId));

        // Boundary: ToDate <= today + window is inclusive, so end exactly at the window edge is ExpiringSoon.
        await service.AddRenewal(created.InsurancePolicyId, NewRenewal(FixedToday.AddDays(-10), FixedToday.AddDays(30)));

        var policy = await service.Get(created.InsurancePolicyId);
        Assert.Equal(CoverageStatus.ExpiringSoon, policy!.CoverageStatus);
    }

    [Fact]
    public async Task Get_EndOneDayBeyondWindow_IsActive_Boundary()
    {
        await using var context = TestContextFactory.Create();
        var service = CreateService(context); // window = 30
        var insurerId = await SeedInsurer();
        var created = await service.Create(NewPolicy(insurerId));

        // One day past the window edge flips back to Active.
        await service.AddRenewal(created.InsurancePolicyId, NewRenewal(FixedToday.AddDays(-10), FixedToday.AddDays(31)));

        var policy = await service.Get(created.InsurancePolicyId);
        Assert.Equal(CoverageStatus.Active, policy!.CoverageStatus);
    }

    [Fact]
    public async Task Get_FromDateEqualsToday_IsCovered_Boundary()
    {
        await using var context = TestContextFactory.Create();
        var service = CreateService(context);
        var insurerId = await SeedInsurer();
        var created = await service.Create(NewPolicy(insurerId));

        // Inclusive start: FromDate == today still counts as covering today (Active, not Upcoming).
        await service.AddRenewal(created.InsurancePolicyId, NewRenewal(FixedToday, FixedToday.AddDays(100)));

        var policy = await service.Get(created.InsurancePolicyId);
        Assert.Equal(CoverageStatus.Active, policy!.CoverageStatus);
    }

    [Fact]
    public async Task Get_ToDateEqualsToday_IsExpiringSoon_Boundary()
    {
        await using var context = TestContextFactory.Create();
        var service = CreateService(context);
        var insurerId = await SeedInsurer();
        var created = await service.Create(NewPolicy(insurerId));

        // Inclusive end: FromDate <= today <= ToDate holds when ToDate == today, so it is still covered
        // (and, ending today, within the expiring-soon window) rather than Lapsed.
        await service.AddRenewal(created.InsurancePolicyId, NewRenewal(FixedToday.AddDays(-100), FixedToday));

        var policy = await service.Get(created.InsurancePolicyId);
        Assert.Equal(CoverageStatus.ExpiringSoon, policy!.CoverageStatus);
    }

    [Fact]
    public async Task Get_EarliestStartInFuture_IsUpcoming()
    {
        await using var context = TestContextFactory.Create();
        var service = CreateService(context);
        var insurerId = await SeedInsurer();
        var created = await service.Create(NewPolicy(insurerId));

        await service.AddRenewal(created.InsurancePolicyId, NewRenewal(FixedToday.AddDays(5), FixedToday.AddDays(40)));

        var policy = await service.Get(created.InsurancePolicyId);
        Assert.Equal(CoverageStatus.Upcoming, policy!.CoverageStatus);
        Assert.Null(policy.CurrentRenewal);
    }

    [Fact]
    public async Task Get_LatestEndedInPast_IsLapsed()
    {
        await using var context = TestContextFactory.Create();
        var service = CreateService(context);
        var insurerId = await SeedInsurer();
        var created = await service.Create(NewPolicy(insurerId));

        await service.AddRenewal(created.InsurancePolicyId, NewRenewal(FixedToday.AddDays(-100), FixedToday.AddDays(-1)));

        var policy = await service.Get(created.InsurancePolicyId);
        Assert.Equal(CoverageStatus.Lapsed, policy!.CoverageStatus);
        Assert.Null(policy.CurrentRenewal);
    }

    [Fact]
    public async Task Update_Archived_OverridesDerivedStatus_IsArchived()
    {
        await using var context = TestContextFactory.Create();
        var service = CreateService(context);
        var insurerId = await SeedInsurer();
        var created = await service.Create(NewPolicy(insurerId));
        await service.AddRenewal(created.InsurancePolicyId, NewRenewal(FixedToday.AddDays(-10), FixedToday.AddDays(100)));

        var archived = await service.Update(created.InsurancePolicyId, new UpdateInsurancePolicy
        {
            Name = created.Name,
            Type = created.Type,
            InsurerIds = [insurerId],
            Archived = true,
        });

        // Archived is terminal and wins over the otherwise-Active derivation; the current renewal is
        // still surfaced for context.
        Assert.Equal(CoverageStatus.Archived, archived!.CoverageStatus);
        Assert.NotNull(archived.CurrentRenewal);
        Assert.NotNull(archived.Archived);
    }

    // ── Current-renewal selection & ordering (overlap tie-break) ─────────────────

    [Fact]
    public async Task Get_OverlappingRenewals_CurrentIsLatestFromDate()
    {
        await using var context = TestContextFactory.Create();
        var service = CreateService(context);
        var insurerId = await SeedInsurer();
        var created = await service.Create(NewPolicy(insurerId));

        // Both cover today; the current renewal is the one with the later FromDate.
        await service.AddRenewal(created.InsurancePolicyId, NewRenewal(FixedToday.AddDays(-30), FixedToday.AddDays(100), premium: 111m));
        await service.AddRenewal(created.InsurancePolicyId, NewRenewal(FixedToday.AddDays(-5), FixedToday.AddDays(100), premium: 222m));

        var policy = await service.Get(created.InsurancePolicyId);
        Assert.Equal(222m, policy!.CurrentRenewal!.Premium);
    }

    [Fact]
    public async Task Get_OverlappingRenewals_SameFromDate_CurrentIsLatestCreatedAt()
    {
        await using var context = TestContextFactory.Create();
        var service = CreateService(context);
        var insurerId = await SeedInsurer();
        var created = await service.Create(NewPolicy(insurerId));

        var from = FixedToday.AddDays(-10);
        var to = FixedToday.AddDays(100);
        // Identical windows; the CreatedAtUtc tie-break picks the later-created renewal (premium 222).
        await SeedRenewal(context, created.InsurancePolicyId, from, to, createdAtUtc: FixedToday.AddDays(-2), premium: 111m);
        await SeedRenewal(context, created.InsurancePolicyId, from, to, createdAtUtc: FixedToday.AddDays(-1), premium: 222m);

        var policy = await service.Get(created.InsurancePolicyId);
        Assert.Equal(222m, policy!.CurrentRenewal!.Premium);
    }

    [Fact]
    public async Task Get_Renewals_OrderedByFromDateDescending()
    {
        await using var context = TestContextFactory.Create();
        var service = CreateService(context);
        var insurerId = await SeedInsurer();
        var created = await service.Create(NewPolicy(insurerId));

        await service.AddRenewal(created.InsurancePolicyId, NewRenewal(FixedToday.AddDays(-300), FixedToday.AddDays(-200), premium: 1m));
        await service.AddRenewal(created.InsurancePolicyId, NewRenewal(FixedToday.AddDays(-100), FixedToday.AddDays(100), premium: 3m));
        await service.AddRenewal(created.InsurancePolicyId, NewRenewal(FixedToday.AddDays(-200), FixedToday.AddDays(-100), premium: 2m));

        var policy = await service.Get(created.InsurancePolicyId);
        Assert.Equal([3m, 2m, 1m], policy!.Renewals.Select(r => r.Premium));
    }

    // ── Renewal date validation ──────────────────────────────────────────────────

    [Fact]
    public async Task AddRenewal_ToBeforeFrom_Throws()
    {
        await using var context = TestContextFactory.Create();
        var service = CreateService(context);
        var insurerId = await SeedInsurer();
        var created = await service.Create(NewPolicy(insurerId));

        await Assert.ThrowsAsync<DomainValidationException>(() =>
            service.AddRenewal(created.InsurancePolicyId, NewRenewal(FixedToday, FixedToday.AddDays(-1))));
    }

    [Fact]
    public async Task AddRenewal_ToEqualsFrom_Succeeds_Boundary()
    {
        await using var context = TestContextFactory.Create();
        var service = CreateService(context);
        var insurerId = await SeedInsurer();
        var created = await service.Create(NewPolicy(insurerId));

        // ToDate == FromDate is a single-day window, which is valid (the check rejects only to < from).
        var renewal = await service.AddRenewal(created.InsurancePolicyId, NewRenewal(FixedToday, FixedToday));

        Assert.NotNull(renewal);
    }

    [Fact]
    public async Task UpdateRenewal_ToBeforeFrom_Throws()
    {
        await using var context = TestContextFactory.Create();
        var service = CreateService(context);
        var insurerId = await SeedInsurer();
        var created = await service.Create(NewPolicy(insurerId));
        var renewal = await service.AddRenewal(created.InsurancePolicyId, NewRenewal(FixedToday.AddDays(-5), FixedToday.AddDays(5)));

        await Assert.ThrowsAsync<DomainValidationException>(() =>
            service.UpdateRenewal(created.InsurancePolicyId, renewal!.PolicyRenewalId,
                new UpdatePolicyRenewal
                {
                    FromDate = FixedToday,
                    ToDate = FixedToday.AddDays(-1),
                    Premium = 1m,
                    CoverageAmount = 1m,
                }));
    }

    // ── Independent premium/coverage currency validation ─────────────────────────

    [Fact]
    public async Task AddRenewal_UnsupportedPremiumCurrency_Throws()
    {
        await using var context = TestContextFactory.Create();
        var service = CreateService(context);
        var insurerId = await SeedInsurer();
        var created = await service.Create(NewPolicy(insurerId));

        await Assert.ThrowsAsync<DomainValidationException>(() =>
            service.AddRenewal(created.InsurancePolicyId,
                NewRenewal(FixedToday, FixedToday.AddDays(1), premiumCurrency: "XXX")));
    }

    [Fact]
    public async Task AddRenewal_UnsupportedCoverageCurrency_Throws()
    {
        await using var context = TestContextFactory.Create();
        var service = CreateService(context);
        var insurerId = await SeedInsurer();
        var created = await service.Create(NewPolicy(insurerId));

        // Premium currency is valid; coverage currency is validated independently and still rejects.
        await Assert.ThrowsAsync<DomainValidationException>(() =>
            service.AddRenewal(created.InsurancePolicyId,
                NewRenewal(FixedToday, FixedToday.AddDays(1), premiumCurrency: "USD", coverageCurrency: "XXX")));
    }

    [Fact]
    public async Task AddRenewal_ArchivedCurrency_Throws()
    {
        await using var context = TestContextFactory.Create();
        var sek = await context.Currencies.FindAsync("SEK");
        sek!.Archived = FixedToday;
        await context.SaveChangesAsync();

        var service = CreateService(context);
        var insurerId = await SeedInsurer();
        var created = await service.Create(NewPolicy(insurerId));

        // Supported-and-active: an archived (but format-valid) currency is rejected.
        await Assert.ThrowsAsync<DomainValidationException>(() =>
            service.AddRenewal(created.InsurancePolicyId,
                NewRenewal(FixedToday, FixedToday.AddDays(1), premiumCurrency: "SEK")));
    }

    // ── Renewal cap ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task AddRenewal_ExceedingCap_ThrowsUnprocessable()
    {
        await using var context = TestContextFactory.Create();
        // The cap comes from the settings store now, so the boundary is set through the lookup.
        var service = CreateService(context, new FixedSystemSettingsLookup
        {
            Caps = new FinanceRequestCaps(
                MaxPartiesPerContract: 25, MaxFilesPerContract: 50, MaxSummaryContracts: 1000,
                MaxRenewalsPerPolicy: 2, MaxFilesPerParent: 50, MaxLinksPerPolicy: 50),
        });
        var insurerId = await SeedInsurer();
        var created = await service.Create(NewPolicy(insurerId));

        await service.AddRenewal(created.InsurancePolicyId, NewRenewal(FixedToday.AddDays(-2), FixedToday.AddDays(-1)));
        await service.AddRenewal(created.InsurancePolicyId, NewRenewal(FixedToday, FixedToday.AddDays(1)));

        await Assert.ThrowsAsync<DomainUnprocessableException>(() =>
            service.AddRenewal(created.InsurancePolicyId, NewRenewal(FixedToday.AddDays(2), FixedToday.AddDays(3))));
    }

    [Fact]
    public async Task AddRenewal_UnknownPolicy_ReturnsNull()
    {
        await using var context = TestContextFactory.Create();
        var service = CreateService(context);

        var result = await service.AddRenewal(Guid.NewGuid(), NewRenewal(FixedToday, FixedToday.AddDays(1)));

        Assert.Null(result);
    }

    // ── Insurer / insured-account reference validation ───────────────────────────

    [Fact]
    public async Task Create_UnknownInsurer_Throws()
    {
        await using var context = TestContextFactory.Create();
        var service = CreateService(context);

        await Assert.ThrowsAsync<DomainValidationException>(() => service.Create(NewPolicy(Guid.NewGuid())));
    }

    [Fact]
    public async Task Create_ArchivedInsurer_Throws()
    {
        await using var context = TestContextFactory.Create();
        var service = CreateService(context);
        var insurerId = await SeedInsurer(archived: FixedToday);

        await Assert.ThrowsAsync<DomainValidationException>(() => service.Create(NewPolicy(insurerId)));
    }

    [Fact]
    public async Task Create_UnknownInsuredAccount_Throws()
    {
        await using var context = TestContextFactory.Create();
        var service = CreateService(context);
        var insurerId = await SeedInsurer();

        await Assert.ThrowsAsync<DomainValidationException>(() =>
            service.Create(NewPolicy(insurerId, accountId: Guid.NewGuid())));
    }

    [Fact]
    public async Task Create_ArchivedInsuredAccount_Throws()
    {
        await using var context = TestContextFactory.Create();
        var service = CreateService(context);
        var insurerId = await SeedInsurer();
        var accountId = await SeedAccount(context, archived: FixedToday);

        await Assert.ThrowsAsync<DomainValidationException>(() =>
            service.Create(NewPolicy(insurerId, accountId)));
    }

    [Fact]
    public async Task Create_ValidInsuredAccount_Succeeds()
    {
        await using var context = TestContextFactory.Create();
        var service = CreateService(context);
        var insurerId = await SeedInsurer();
        var accountId = await SeedAccount(context);

        var created = await service.Create(NewPolicy(insurerId, accountId));

        Assert.Equal(accountId, Assert.Single(created.InsuredAccounts).AccountId);
    }

    [Fact]
    public async Task Update_UnchangedInsurer_DoesNotRevalidate_WhenInsurerLaterArchived()
    {
        await using var context = TestContextFactory.Create();
        var service = CreateService(context);
        var insurerId = await SeedInsurer();
        var created = await service.Create(NewPolicy(insurerId));

        // Archive the still-linked insurer after the policy was created. An unrelated edit that keeps
        // the same InsurerId must not re-validate (and therefore not 400) — only a changed reference is
        // re-checked (InsuranceService.cs:180-187).
        var insurer = await journal.Contacts.FindAsync(insurerId);
        insurer!.Archived = FixedToday;
        await journal.SaveChangesAsync();

        var updated = await service.Update(created.InsurancePolicyId, new UpdateInsurancePolicy
        {
            Name = "Renamed cover",
            Type = created.Type,
            InsurerIds = [insurerId],
        });

        Assert.Equal("Renamed cover", updated!.Name);
    }

    [Fact]
    public async Task Update_ChangedInsurerToUnknown_Throws()
    {
        await using var context = TestContextFactory.Create();
        var service = CreateService(context);
        var insurerId = await SeedInsurer();
        var created = await service.Create(NewPolicy(insurerId));

        await Assert.ThrowsAsync<DomainValidationException>(() =>
            service.Update(created.InsurancePolicyId, new UpdateInsurancePolicy
            {
                Name = created.Name,
                Type = created.Type,
                InsurerIds = [Guid.NewGuid()],
            }));
    }

    // ── File attach: cap & duplicate ─────────────────────────────────────────────

    /// <summary>The cap is per PERIOD — the policy is no longer a parent it can apply to (issue #26).</summary>
    [Fact]
    public async Task AttachRenewalFile_ExceedingCap_ThrowsUnprocessable()
    {
        await using var context = TestContextFactory.Create();
        var service = CreateService(context, new FixedSystemSettingsLookup
        {
            Caps = new FinanceRequestCaps(
                MaxPartiesPerContract: 25, MaxFilesPerContract: 50, MaxSummaryContracts: 1000,
                MaxRenewalsPerPolicy: 100, MaxFilesPerParent: 1, MaxLinksPerPolicy: 50),
        });
        var insurerId = await SeedInsurer();
        var created = await service.Create(NewPolicy(insurerId));
        var renewal = await service.AddRenewal(created.InsurancePolicyId, NewRenewal(FixedToday, FixedToday.AddDays(1)));

        // Seed the cap directly (the join row exists without loading FileMetadata; the count check
        // short-circuits before the file is materialized).
        context.PolicyRenewalFiles.Add(new PolicyRenewalFile
        {
            PolicyRenewalId = renewal!.PolicyRenewalId,
            FileMetadataId = Guid.NewGuid(),
            AttachedByUserId = "seed",
            AttachedAtUtc = FixedToday,
        });
        await context.SaveChangesAsync();

        await Assert.ThrowsAsync<DomainUnprocessableException>(() =>
            service.AttachRenewalFile(renewal.PolicyRenewalId, Guid.NewGuid(), "user", DtoPolicyFileType.PolicyDocument, null));
    }

    [Fact]
    public async Task AttachRenewalFile_Duplicate_ThrowsConflict()
    {
        await using var context = TestContextFactory.Create();
        var service = CreateService(context);
        var insurerId = await SeedInsurer();
        var created = await service.Create(NewPolicy(insurerId));
        var renewal = await service.AddRenewal(created.InsurancePolicyId, NewRenewal(FixedToday, FixedToday.AddDays(1)));
        var fileId = Guid.NewGuid();

        context.PolicyRenewalFiles.Add(new PolicyRenewalFile
        {
            PolicyRenewalId = renewal!.PolicyRenewalId,
            FileMetadataId = fileId,
            AttachedByUserId = "seed",
            AttachedAtUtc = FixedToday,
        });
        await context.SaveChangesAsync();

        await Assert.ThrowsAsync<DomainConflictException>(() =>
            service.AttachRenewalFile(renewal.PolicyRenewalId, fileId, "user", DtoPolicyFileType.PolicyDocument, null));
    }

    // ── Summary ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetSummary_CountsLivePoliciesByDerivedStatus()
    {
        await using var context = TestContextFactory.Create();
        var service = CreateService(context);
        var insurerId = await SeedInsurer();

        var active = await service.Create(NewPolicy(insurerId));
        await service.AddRenewal(active.InsurancePolicyId, NewRenewal(FixedToday.AddDays(-10), FixedToday.AddDays(100)));

        var upcoming = await service.Create(NewPolicy(insurerId));
        await service.AddRenewal(upcoming.InsurancePolicyId, NewRenewal(FixedToday.AddDays(5), FixedToday.AddDays(40)));

        await service.Create(NewPolicy(insurerId)); // NoCoverage (no renewals)

        var summary = await service.GetSummary(null);

        Assert.Equal(3, summary.TotalPolicies);
        Assert.Equal(1, summary.CountsByStatus.Active);
        Assert.Equal(1, summary.CountsByStatus.Upcoming);
        Assert.Equal(1, summary.CountsByStatus.NoCoverage);
    }

    [Fact]
    public async Task GetSummary_ArchivedCountedInStatus_ButExcludedFromTotalsAndByType()
    {
        await using var context = TestContextFactory.Create();
        var service = CreateService(context);
        var insurerId = await SeedInsurer();

        var live = await service.Create(NewPolicy(insurerId));
        await service.AddRenewal(live.InsurancePolicyId, NewRenewal(FixedToday.AddDays(-10), FixedToday.AddDays(100)));

        var archivedPolicy = await service.Create(NewPolicy(insurerId));
        await service.AddRenewal(archivedPolicy.InsurancePolicyId, NewRenewal(FixedToday.AddDays(-10), FixedToday.AddDays(100)));
        await service.Update(archivedPolicy.InsurancePolicyId, new UpdateInsurancePolicy
        {
            Name = archivedPolicy.Name,
            Type = archivedPolicy.Type,
            InsurerIds = [insurerId],
            Archived = true,
        });

        var summary = await service.GetSummary(null);

        Assert.Equal(1, summary.TotalPolicies);          // live only
        Assert.Equal(1, summary.CountsByStatus.Archived); // archived still surfaces in the status breakdown
        Assert.Equal(1, summary.CountsByStatus.Active);
        Assert.Equal(1, summary.CountsByType.Sum(t => t.Count)); // by-type over the live set only
        var premium = Assert.Single(summary.PremiumByCurrency);
        Assert.Equal(100m, premium.Amount);              // archived premium excluded
    }

    [Fact]
    public async Task GetSummary_WithBaseCurrency_ConvertsAndReportsUnconverted()
    {
        await using var context = TestContextFactory.Create();
        context.ExchangeRates.Add(new ExchangeRate
        {
            FromCurrencyCode = "USD",
            ToCurrencyCode = "EUR",
            Rate = 0.5m,
            AsOf = FixedToday,
            CreatedAt = FixedToday,
        });
        await context.SaveChangesAsync();

        var service = CreateService(context);
        var insurerId = await SeedInsurer();

        var usdPolicy = await service.Create(NewPolicy(insurerId));
        await service.AddRenewal(usdPolicy.InsurancePolicyId,
            NewRenewal(FixedToday.AddDays(-10), FixedToday.AddDays(100), premium: 200m, premiumCurrency: "USD"));

        var sekPolicy = await service.Create(NewPolicy(insurerId));
        await service.AddRenewal(sekPolicy.InsurancePolicyId,
            NewRenewal(FixedToday.AddDays(-10), FixedToday.AddDays(100), premium: 300m, premiumCurrency: "SEK"));

        var summary = await service.GetSummary("EUR");

        Assert.Equal("EUR", summary.BaseCurrency);
        Assert.Equal(100m, summary.ConvertedTotalPremium); // 200 USD * 0.5; SEK has no rate so it is skipped
        Assert.Contains("SEK", summary.UnconvertedCurrencies);
    }

    [Fact]
    public async Task GetSummary_UnknownBaseCurrency_Throws()
    {
        await using var context = TestContextFactory.Create();
        var service = CreateService(context);

        await Assert.ThrowsAsync<DomainValidationException>(() => service.GetSummary("XXX"));
    }

    // ── List sort keys: Name (default), Type, RenewalEnd, Premium ────────────────
    // The insurance list sorts on derived (in-memory) values, so every key is exercised here rather
    // than at the API layer (issue #277).

    [Fact]
    public async Task List_EverySortKey_IsHonoured()
    {
        await using var context = TestContextFactory.Create();
        var service = CreateService(context);
        var insurerId = await SeedInsurer();

        async Task<Guid> Seed(string name, DtoInsurancePolicyType type, decimal premium, DateTime end)
        {
            var policy = await service.Create(new NewInsurancePolicy { Name = name, Type = type, InsurerIds = [insurerId] });
            await service.AddRenewal(policy.InsurancePolicyId, NewRenewal(FixedToday.AddDays(-10), end, premium: premium));
            return policy.InsurancePolicyId;
        }

        var p1 = await Seed("Charlie", DtoInsurancePolicyType.Home, 200m, FixedToday.AddDays(5));
        var p2 = await Seed("Alpha", DtoInsurancePolicyType.Vehicle, 100m, FixedToday.AddDays(7));
        var p3 = await Seed("Bravo", DtoInsurancePolicyType.Health, 300m, FixedToday.AddDays(6));

        async Task<List<Guid>> List(InsuranceSortBy key, SortDirection dir) =>
            (await service.ListAsync(new InsurancePoliciesQueryParams { SortBy = key, SortDir = dir }))
            .Items.Select(i => i.InsurancePolicyId).ToList();

        // Name (default): Alpha(p2), Bravo(p3), Charlie(p1)
        Assert.Equal([p2, p3, p1], await List(InsuranceSortBy.Name, SortDirection.Asc));
        Assert.Equal([p1, p3, p2], await List(InsuranceSortBy.Name, SortDirection.Desc));
        // Type: Home(0)=p1, Vehicle(3)=p2, Health(6)=p3 — differs from Name.
        Assert.Equal([p1, p2, p3], await List(InsuranceSortBy.Type, SortDirection.Asc));
        // RenewalEnd: +5(p1), +6(p3), +7(p2) — differs from Name.
        Assert.Equal([p1, p3, p2], await List(InsuranceSortBy.RenewalEnd, SortDirection.Asc));
        // Premium: 100(p2), 200(p1), 300(p3) — differs from Name.
        Assert.Equal([p2, p1, p3], await List(InsuranceSortBy.Premium, SortDirection.Asc));
    }
}
