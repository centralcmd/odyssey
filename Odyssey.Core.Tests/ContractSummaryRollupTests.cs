using Microsoft.Extensions.Logging.Abstractions;
using Odyssey.Context;
using Odyssey.Core.Finance;
using Odyssey.Dtos.Finance;
using Xunit;
using DtoContractType = Odyssey.Dtos.Finance.ContractType;
using ContextInterval = Odyssey.Context.Interval;
using ContextTermValueUnit = Odyssey.Context.TermValueUnit;

namespace Odyssey.Core.Tests;

/// <summary>
/// The page-header roll-up's two derived halves: what the agreements cost to run, and what falls due
/// next. Both read the same set — the in-force <c>Fee</c>/<c>Amount</c> terms of the <b>Active</b>
/// contracts — so most of what is worth pinning is which rows that set excludes and why.
///
/// <para>
/// Run against EF InMemory and a fixed "today", with no exchange rates seeded: every figure here is
/// single-currency, which keeps the cadence arithmetic the subject. The conversion path itself —
/// including the unconvertible currency that must be named rather than folded in at 1:1 — needs real
/// rate rows and is covered over HTTP in <c>ContractsApiTests</c>.
/// </para>
/// </summary>
public class ContractSummaryRollupTests
{
    private static readonly DateTime FixedToday = new(2026, 6, 15, 0, 0, 0, DateTimeKind.Utc);

    private readonly OdysseyContext journal = TestContextFactory.CreateJournal();

    private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow);
    }

    private ContractService CreateService(OdysseyContext context) =>
        new(context, TestContextFactory.ContactLookup(journal), new FixedTimeProvider(FixedToday),
            new FakeSystemSettingsLookup(), NullLogger<ContractService>.Instance);

    private static Guid SeedContract(
        OdysseyContext context,
        string name,
        DateTime? start,
        DateTime? end = null,
        DtoContractType type = DtoContractType.Service,
        bool archived = false,
        bool paused = false,
        DateTime? completion = null,
        bool signed = true)
    {
        var contract = new Contract
        {
            ContractId = Guid.NewGuid(),
            Name = name,
            Type = (Odyssey.Context.ContractType)(int)type,
            StartDate = completion is null ? start : null,
            EndDate = completion is null ? end : null,
            CompletionDate = completion,
            Archived = archived ? FixedToday.AddDays(-1) : null,
            Paused = paused ? FixedToday.AddDays(-2) : null,
            // SIGNED unless the caller asks otherwise (issue #145). The signature layer outranks the
            // date chain, so leaving these null would make every seeded row a Draft and collapse every
            // status this file exercises. The unsigned case has its own tests, which use `signed:
            // false` to prove a Draft leaves the run rate, the by-type cost split and the charges.
            Ready = signed ? FixedToday.AddYears(-1).AddDays(-1) : null,
            Signed = signed ? FixedToday.AddYears(-1) : null,
            CreatedAtUtc = FixedToday.AddYears(-1),
        };
        context.Contracts.Add(contract);
        context.SaveChanges();
        return contract.ContractId;
    }

    private static void SeedFee(
        OdysseyContext context,
        Guid contractId,
        decimal value,
        DateTime effectiveFrom,
        ContextInterval? interval = ContextInterval.Monthly,
        int? intervalCount = 1,
        string label = "Rent",
        string currency = "USD",
        DateTime? anchor = null)
    {
        context.Terms.Add(new Term
        {
            TermId = Guid.NewGuid(),
            ContractId = contractId,
            Label = label,
            LabelKey = label.ToLowerInvariant(),
            ValueUnit = ContextTermValueUnit.Amount,
            Value = value,
            CurrencyCode = currency,
            Interval = interval,
            IntervalCount = interval is { } i && i.IsPeriodic() ? intervalCount : null,
            AnchorDate = anchor,
            EffectiveFrom = effectiveFrom,
            CreatedAtUtc = effectiveFrom,
        });
        context.SaveChanges();
    }

    private Task<ContractSummary> Summarise(OdysseyContext context) =>
        CreateService(context).GetSummary("USD");

    // ── Run rate ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task RunRate_ProjectsAMonthlyFeeToTwelveTimesItsYearlyValue()
    {
        await using var context = TestContextFactory.Create();
        var id = SeedContract(context, "Lease", FixedToday.AddMonths(-6), FixedToday.AddMonths(6));
        SeedFee(context, id, 1000m, FixedToday.AddMonths(-6));

        var summary = await Summarise(context);

        Assert.Equal(1000m, summary.RunRate.Monthly);
        Assert.Equal(12000m, summary.RunRate.Yearly);
        Assert.Equal("USD", summary.RunRate.BaseCurrency);
    }

    /// <summary>
    /// The cadence multiplier is a DIVISOR, not a factor: a 300 charge every three months is 100 a
    /// month, not 900. Reading it the other way triples every quarterly line in the roll-up.
    /// </summary>
    [Fact]
    public async Task RunRate_DividesByTheIntervalCount()
    {
        await using var context = TestContextFactory.Create();
        var id = SeedContract(context, "Storage", FixedToday.AddMonths(-12));
        SeedFee(context, id, 300m, FixedToday.AddMonths(-12), intervalCount: 3);

        var summary = await Summarise(context);

        Assert.Equal(100m, summary.RunRate.Monthly);
        Assert.Equal(1200m, summary.RunRate.Yearly);
    }

    /// <summary>
    /// A fee with no cadence names an occasion, not a rhythm, so there is no rate to project. Counting
    /// one would make a single completed purchase read as a permanent monthly cost.
    /// </summary>
    [Fact]
    public async Task RunRate_ExcludesAOneTimeFee()
    {
        await using var context = TestContextFactory.Create();
        var id = SeedContract(context, "Parking", FixedToday.AddMonths(-3));
        SeedFee(context, id, 40m, FixedToday.AddMonths(-3), interval: ContextInterval.OneTime, label: "Access fob");

        var summary = await Summarise(context);

        Assert.Null(summary.RunRate.Monthly);
        Assert.Null(summary.RunRate.Yearly);
        Assert.Empty(summary.RunRate.ByType);
    }

    /// <summary>
    /// A series is a run of supersessions: only the latest entry on or before today is in force. Summing
    /// the history instead would inflate the run rate by every price the contract has ever had.
    /// </summary>
    [Fact]
    public async Task RunRate_CountsOnlyTheInForceEntryOfASeries()
    {
        await using var context = TestContextFactory.Create();
        var id = SeedContract(context, "Storage", FixedToday.AddYears(-2));
        SeedFee(context, id, 95m, FixedToday.AddYears(-2));
        SeedFee(context, id, 105m, FixedToday.AddMonths(-2));

        var summary = await Summarise(context);

        Assert.Equal(105m, summary.RunRate.Monthly);
    }

    /// <summary>A price that takes effect later is not in force yet, whatever it will cost.</summary>
    [Fact]
    public async Task RunRate_IgnoresATermEffectiveInTheFuture()
    {
        await using var context = TestContextFactory.Create();
        var id = SeedContract(context, "Storage", FixedToday.AddYears(-2));
        SeedFee(context, id, 95m, FixedToday.AddYears(-2));
        SeedFee(context, id, 150m, FixedToday.AddMonths(1));

        var summary = await Summarise(context);

        Assert.Equal(95m, summary.RunRate.Monthly);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RunRate_CountsOnlyActiveContracts(bool archived)
    {
        await using var context = TestContextFactory.Create();
        // Expired when not archived: either way it is not Active, and neither carries a running cost.
        var id = SeedContract(context, "Old broadband", FixedToday.AddYears(-3), FixedToday.AddMonths(-1), archived: archived);
        SeedFee(context, id, 45m, FixedToday.AddYears(-3));

        var summary = await Summarise(context);

        Assert.Null(summary.RunRate.Monthly);
    }

    /// <summary>
    /// The per-type rows are the same read split. What is pinned is that they cover the same terms as
    /// the totals — the rounding is applied independently to each, so exact equality is a property of
    /// these figures rather than a guarantee the code makes.
    /// </summary>
    [Fact]
    public async Task RunRate_ByTypeRowsSumToTheTotals()
    {
        await using var context = TestContextFactory.Create();
        var lease = SeedContract(context, "Lease", FixedToday.AddMonths(-6), type: DtoContractType.Rental);
        var gym = SeedContract(context, "Gym", FixedToday.AddMonths(-3), type: DtoContractType.Membership);
        SeedFee(context, lease, 1000m, FixedToday.AddMonths(-6));
        SeedFee(context, gym, 60m, FixedToday.AddMonths(-3), label: "Membership");

        var summary = await Summarise(context);

        Assert.Equal(2, summary.RunRate.ByType.Count);
        Assert.Equal(summary.RunRate.Monthly, summary.RunRate.ByType.Sum(r => r.Monthly));
        Assert.Equal(summary.RunRate.Yearly, summary.RunRate.ByType.Sum(r => r.Yearly));
        Assert.Contains(summary.RunRate.ByType, r => r.Type == DtoContractType.Membership && r.Monthly == 60m);
    }

    // ── Upcoming charges ─────────────────────────────────────────────────────

    /// <summary>
    /// The anchor is stepped forward, never advanced or stored: a monthly fee anchored on the 1st is
    /// next due on the coming 1st, whatever year it started.
    /// </summary>
    [Fact]
    public async Task UpcomingCharges_ProjectsFromTheCadenceAnchor()
    {
        await using var context = TestContextFactory.Create();
        var id = SeedContract(context, "Parking", FixedToday.AddYears(-1));
        SeedFee(context, id, 165m, FixedToday.AddYears(-1), anchor: new DateTime(2025, 11, 1, 0, 0, 0, DateTimeKind.Utc));

        var summary = await Summarise(context);

        var charge = Assert.Single(summary.UpcomingCharges);
        Assert.Equal(new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc), charge.ChargeDate);
        Assert.Equal(16, charge.DaysUntil);
        Assert.Equal(165m, charge.Amount);
    }

    /// <summary>
    /// A charge never falls outside the agreement it is priced under. Without this the panel would
    /// invite a reader to expect a payment on a contract that has already ended.
    /// </summary>
    [Fact]
    public async Task UpcomingCharges_DropsAnOccurrencePastTheContractsEndDate()
    {
        await using var context = TestContextFactory.Create();
        // Cover ends 2026-06-20; the next occurrence of a fee anchored on the 25th is 2026-06-25.
        var id = SeedContract(context, "Expiring lease", FixedToday.AddYears(-1), FixedToday.AddDays(5));
        SeedFee(context, id, 500m, FixedToday.AddYears(-1),
            anchor: new DateTime(2025, 6, 25, 0, 0, 0, DateTimeKind.Utc));

        var summary = await Summarise(context);

        Assert.Empty(summary.UpcomingCharges);
    }

    /// <summary>One row per CONTRACT, not per term: a contract pricing four fees must not crowd out three others.</summary>
    [Fact]
    public async Task UpcomingCharges_ReportsOnlyTheSoonestChargePerContract()
    {
        await using var context = TestContextFactory.Create();
        var id = SeedContract(context, "Parking", FixedToday.AddYears(-1));
        SeedFee(context, id, 165m, FixedToday.AddYears(-1), label: "Licence",
            anchor: new DateTime(2026, 6, 20, 0, 0, 0, DateTimeKind.Utc));
        SeedFee(context, id, 12m, FixedToday.AddYears(-1), label: "Cleaning",
            anchor: new DateTime(2026, 6, 18, 0, 0, 0, DateTimeKind.Utc));

        var summary = await Summarise(context);

        var charge = Assert.Single(summary.UpcomingCharges);
        Assert.Equal("Cleaning", charge.Label);
    }

    [Fact]
    public async Task UpcomingCharges_ExcludesAChargeBeyondTheWindow()
    {
        await using var context = TestContextFactory.Create();
        var id = SeedContract(context, "Annual dues", FixedToday.AddYears(-1));
        // 45-day window; an annual fee anchored 6 months out is next due well past it.
        SeedFee(context, id, 900m, FixedToday.AddYears(-1),
            interval: ContextInterval.Annually, anchor: FixedToday.AddMonths(-6));

        var summary = await Summarise(context);

        Assert.Empty(summary.UpcomingCharges);
        // …but it is still a running cost, which is the distinction between the two halves.
        Assert.Equal(900m, summary.RunRate.Yearly);
    }

    /// <summary>
    /// The two halves read DIFFERENT sets, deliberately. A contract that has not started is not
    /// costing anything yet, so it carries no run rate — but if its price is already in force it has a
    /// first charge to report, and that charge cannot fall before the agreement begins.
    /// </summary>
    [Fact]
    public async Task UpcomingContract_HasAChargeButNoRunRate()
    {
        await using var context = TestContextFactory.Create();
        var id = SeedContract(context, "Fixed tariff", FixedToday.AddDays(20), FixedToday.AddYears(1));
        // Priced on signing, a month before the switch completes.
        SeedFee(context, id, 28.50m, FixedToday.AddMonths(-1), label: "Standing charge",
            anchor: FixedToday.AddMonths(-1));

        var summary = await Summarise(context);

        Assert.Null(summary.RunRate.Monthly);
        Assert.Empty(summary.RunRate.ByType);

        var charge = Assert.Single(summary.UpcomingCharges);
        Assert.True(charge.ChargeDate >= FixedToday.AddDays(20),
            "A charge cannot fall before the agreement it is priced under begins.");
    }

    /// <summary>
    /// The other side of that split: an Upcoming contract's currency must not win the base-currency
    /// vote, since it contributes nothing to the total it would then denominate.
    /// </summary>
    [Fact]
    public async Task BaseCurrency_IsVotedOnByTheRunningContractsOnly()
    {
        await using var context = TestContextFactory.Create();
        var running = SeedContract(context, "Lease", FixedToday.AddMonths(-6));
        var notYet = SeedContract(context, "Tariff", FixedToday.AddDays(20));
        SeedFee(context, running, 1000m, FixedToday.AddMonths(-6), currency: "USD");
        SeedFee(context, notYet, 30m, FixedToday.AddMonths(-1), currency: "EUR", label: "Standing charge");
        SeedFee(context, notYet, 30m, FixedToday.AddMonths(-1), currency: "EUR", label: "Unit rate");

        // Blank base: the pick is the most common currency among the RUNNING fees, so the two EUR
        // rows on the not-yet-started contract must not outvote the single USD one.
        var summary = await CreateService(context).GetSummary(baseCurrency: null);

        Assert.Equal("USD", summary.RunRate.BaseCurrency);
    }

    /// <summary>
    /// The daily and weekly factors, which no seeded fee exercises: a week is 52.1775/12 months, not
    /// 4, and a day is 365.25/12 — the leap-year-aware figures the design system uses. A "close
    /// enough" 30/7 here would misreport a weekly fee by about 4% a year.
    /// </summary>
    [Theory]
    [InlineData(ContextInterval.Daily, 10, 304.38, 3652.50)]  // 304.375 rounds away from zero
    [InlineData(ContextInterval.Weekly, 100, 434.81, 5217.75)]
    public async Task RunRate_UsesTheLeapAwareDailyAndWeeklyFactors(
        ContextInterval interval, decimal value, decimal monthly, decimal yearly)
    {
        await using var context = TestContextFactory.Create();
        var id = SeedContract(context, "Cadence", FixedToday.AddMonths(-6));
        SeedFee(context, id, value, FixedToday.AddMonths(-6), interval: interval);

        var summary = await Summarise(context);

        Assert.Equal(monthly, summary.RunRate.Monthly);
        Assert.Equal(yearly, summary.RunRate.Yearly);
    }

    /// <summary>
    /// A percentage fee carries no due amount, so it can be neither projected nor charged. The filter
    /// is on the unit, not the kind, and dropping it would put a bare <c>2.5</c> in the run rate as
    /// though it were money.
    /// </summary>
    [Fact]
    public async Task PercentageFee_ContributesToNeitherHalf()
    {
        await using var context = TestContextFactory.Create();
        var id = SeedContract(context, "Commission", FixedToday.AddMonths(-6));
        context.Terms.Add(new Term
        {
            TermId = Guid.NewGuid(),
            ContractId = id,
            Label = "Commission",
            LabelKey = "commission",
            ValueUnit = ContextTermValueUnit.Percentage,
            Value = 0.025m,
            Interval = ContextInterval.Monthly,
            IntervalCount = 1,
            EffectiveFrom = FixedToday.AddMonths(-6),
            CreatedAtUtc = FixedToday.AddMonths(-6),
        });
        context.SaveChanges();

        var summary = await Summarise(context);

        Assert.Null(summary.RunRate.Monthly);
        Assert.Empty(summary.UpcomingCharges);
    }

    /// <summary>
    /// The whole point of <c>ContractMaxSummaryCharges</c>: above the cap the panel lists the SOONEST
    /// charges, not an arbitrary subset. Truncating before sorting would drop the imminent ones.
    /// </summary>
    [Fact]
    public async Task UpcomingCharges_TruncateToTheCap_KeepingTheSoonest()
    {
        await using var context = TestContextFactory.Create();
        for (var i = 0; i < 5; i++)
        {
            var id = SeedContract(context, $"Contract {i}", FixedToday.AddYears(-1));
            // Anchored i days out, so the expected survivors are unambiguous.
            SeedFee(context, id, 10m + i, FixedToday.AddYears(-1), anchor: FixedToday.AddDays(i));
        }

        var lookup = new FakeSystemSettingsLookup { ContractSummary = new ContractSummarySettings(45, 45, 2) };
        var service = new ContractService(context, TestContextFactory.ContactLookup(journal),
            new FixedTimeProvider(FixedToday), lookup, NullLogger<ContractService>.Instance);

        var summary = await service.GetSummary("USD");

        Assert.Equal(2, summary.UpcomingCharges.Count);
        Assert.Equal([0, 1], summary.UpcomingCharges.Select(c => c.DaysUntil));
    }

    /// <summary>
    /// Both windows are inclusive, and the edge is where an off-by-one lives: a term ending on the
    /// last day of the window is ending soon, and one a day later is not.
    /// </summary>
    [Fact]
    public async Task Windows_AreInclusiveAtTheirLastDay()
    {
        await using var context = TestContextFactory.Create();
        SeedContract(context, "On the edge", FixedToday.AddYears(-1), FixedToday.AddDays(45));
        SeedContract(context, "One day past", FixedToday.AddYears(-1), FixedToday.AddDays(46));

        var onEdge = SeedContract(context, "Charge on the edge", FixedToday.AddYears(-1));
        var past = SeedContract(context, "Charge one day past", FixedToday.AddYears(-1));
        SeedFee(context, onEdge, 10m, FixedToday.AddYears(-1), anchor: FixedToday.AddDays(45));
        SeedFee(context, past, 10m, FixedToday.AddYears(-1), anchor: FixedToday.AddDays(46));

        var summary = await Summarise(context);

        Assert.Equal(1, summary.CountsByStatus.EndingSoon);
        var charge = Assert.Single(summary.UpcomingCharges);
        Assert.Equal(45, charge.DaysUntil);
    }

    // ── Ending soon ──────────────────────────────────────────────────────────

    /// <summary>
    /// Ending soon is a SLICE of Active, so the five real buckets still sum to the total. Counting it
    /// as a sixth status would double-count every contract approaching its end date. Paused, by
    /// contrast, IS a real bucket — the five are mutually exclusive derived statuses (issue #140).
    /// </summary>
    [Fact]
    public async Task EndingSoon_IsASliceOfActive_NotASixthBucket()
    {
        await using var context = TestContextFactory.Create();
        SeedContract(context, "Ending", FixedToday.AddYears(-1), FixedToday.AddDays(10));
        SeedContract(context, "Running on", FixedToday.AddYears(-1), FixedToday.AddDays(200));
        SeedContract(context, "Frozen", FixedToday.AddYears(-1), paused: true);
        SeedContract(context, "Not yet", FixedToday.AddDays(20));
        SeedContract(context, "Lapsed", FixedToday.AddYears(-2), FixedToday.AddDays(-1));
        SeedContract(context, "Retired", FixedToday.AddYears(-3), FixedToday.AddYears(-1), archived: true);

        var summary = await Summarise(context);
        var counts = summary.CountsByStatus;

        Assert.Equal(2, counts.Active);
        Assert.Equal(1, counts.Paused);
        Assert.Equal(1, counts.EndingSoon);
        Assert.Equal(
            summary.TotalContracts,
            counts.Active + counts.Upcoming + counts.Expired + counts.Archived + counts.Paused);
    }

    // ── Paused (issue #140) ──────────────────────────────────────────────────

    /// <summary>
    /// A paused contract contributes nothing to the run rate or its by-type split, and its type row
    /// disappears entirely when it was the only contributor. The exclusion is not a second predicate:
    /// the contract simply stops deriving as Active, which is the one gate the priceable set uses.
    /// </summary>
    [Fact]
    public async Task Paused_LeavesTheRunRateAndItsByTypeRow()
    {
        await using var context = TestContextFactory.Create();
        var lease = SeedContract(context, "Lease", FixedToday.AddMonths(-6), type: DtoContractType.Rental);
        var gym = SeedContract(context, "Gym", FixedToday.AddMonths(-3), type: DtoContractType.Membership);
        SeedFee(context, lease, 1000m, FixedToday.AddMonths(-6));
        SeedFee(context, gym, 60m, FixedToday.AddMonths(-3), label: "Membership");

        var before = await Summarise(context);
        Assert.Equal(1060m, before.RunRate.Monthly);
        Assert.Contains(before.RunRate.ByType, r => r.Type == DtoContractType.Membership);

        context.Contracts.Single(c => c.ContractId == gym).Paused = FixedToday;
        await context.SaveChangesAsync();

        var after = await Summarise(context);

        // Down by exactly the paused contract's monthly equivalent…
        Assert.Equal(1000m, after.RunRate.Monthly);
        Assert.Equal(12000m, after.RunRate.Yearly);
        // …and its type row is gone, because it was the only contributor.
        Assert.DoesNotContain(after.RunRate.ByType, r => r.Type == DtoContractType.Membership);
    }

    [Fact]
    public async Task Paused_LeavesTheUpcomingCharges()
    {
        await using var context = TestContextFactory.Create();
        var id = SeedContract(context, "Parking", FixedToday.AddYears(-1));
        SeedFee(context, id, 165m, FixedToday.AddYears(-1), anchor: FixedToday.AddDays(3));

        Assert.Single((await Summarise(context)).UpcomingCharges);

        context.Contracts.Single(c => c.ContractId == id).Paused = FixedToday;
        await context.SaveChangesAsync();

        Assert.Empty((await Summarise(context)).UpcomingCharges);
    }

    /// <summary>
    /// "Ending soon" is a slice of Active, so a paused contract inside the window falls out of it as
    /// well — counted in neither, and in Paused instead. The cliff is real but nothing is being
    /// billed against it, so reporting it as an imminent cost would be wrong.
    /// </summary>
    [Fact]
    public async Task Paused_IsCountedInNeitherEndingSoonNorActive()
    {
        await using var context = TestContextFactory.Create();
        var id = SeedContract(context, "Ending", FixedToday.AddYears(-1), FixedToday.AddDays(10));

        var before = await Summarise(context);
        Assert.Equal(1, before.CountsByStatus.Active);
        Assert.Equal(1, before.CountsByStatus.EndingSoon);

        context.Contracts.Single(c => c.ContractId == id).Paused = FixedToday;
        await context.SaveChangesAsync();

        var after = await Summarise(context);
        Assert.Equal(0, after.CountsByStatus.Active);
        Assert.Equal(0, after.CountsByStatus.EndingSoon);
        Assert.Equal(1, after.CountsByStatus.Paused);
    }

    /// <summary>
    /// The by-type HEADCOUNT is pause-agnostic while the cost split is not — the two by-type reads
    /// answer different questions. A paused agreement is still a contract of its type; it simply is
    /// not costing anything.
    /// </summary>
    [Fact]
    public async Task CountsByType_CountsAPausedContract_UnlikeTheCostSplit()
    {
        await using var context = TestContextFactory.Create();
        var id = SeedContract(context, "Gym", FixedToday.AddMonths(-3), type: DtoContractType.Membership);
        SeedFee(context, id, 60m, FixedToday.AddMonths(-3), label: "Membership");

        Assert.Equal(1, Assert.Single((await Summarise(context)).CountsByType).Count);

        context.Contracts.Single(c => c.ContractId == id).Paused = FixedToday;
        await context.SaveChangesAsync();

        var after = await Summarise(context);
        var row = Assert.Single(after.CountsByType);
        Assert.Equal(DtoContractType.Membership, row.Type);
        Assert.Equal(1, row.Count);
        // …while the cost split, which asks a different question, has dropped it.
        Assert.Empty(after.RunRate.ByType);
    }

    /// <summary>
    /// The shape the derivation is easiest to get wrong on (issue #140 AC 24). A settled one-off can
    /// carry a periodic fee like any other contract — <c>Term.ContractId</c> does not discriminate by
    /// contract shape — so if the pause check is unreachable from the one-off branch, this contract
    /// keeps its run rate and its next charge while the stamp sits stored and ignored.
    /// </summary>
    [Fact]
    public async Task PausedSettledOneOff_WithAnInForcePeriodicFee_ContributesNothing()
    {
        await using var context = TestContextFactory.Create();
        var id = SeedContract(context, "Maple St purchase", start: null,
            type: DtoContractType.Purchase, completion: FixedToday.AddDays(-30));
        SeedFee(context, id, 420m, FixedToday.AddMonths(-6), label: "Servicing", anchor: FixedToday.AddDays(4));

        // Before: a settled one-off derives as Active, so it is priced like any other running contract.
        var before = await Summarise(context);
        Assert.Equal(420m, before.RunRate.Monthly);
        Assert.Contains(before.RunRate.ByType, r => r.Type == DtoContractType.Purchase);
        Assert.Single(before.UpcomingCharges);
        Assert.Equal(1, before.CountsByStatus.Active);

        context.Contracts.Single(c => c.ContractId == id).Paused = FixedToday;
        await context.SaveChangesAsync();

        var after = await Summarise(context);
        Assert.Null(after.RunRate.Monthly);
        Assert.Null(after.RunRate.Yearly);
        Assert.Empty(after.RunRate.ByType);
        Assert.Empty(after.UpcomingCharges);
        Assert.Equal(0, after.CountsByStatus.Active);
        Assert.Equal(1, after.CountsByStatus.Paused);
    }

    /// <summary>An open-ended agreement never reaches a cliff, so it is never ending soon.</summary>
    [Fact]
    public async Task EndingSoon_ExcludesAnOpenEndedContract()
    {
        await using var context = TestContextFactory.Create();
        SeedContract(context, "Open-ended", FixedToday.AddYears(-1));

        var summary = await Summarise(context);

        Assert.Equal(1, summary.CountsByStatus.Active);
        Assert.Equal(0, summary.CountsByStatus.EndingSoon);
    }

    /// <summary>
    /// The windows travel to the client because the page renders them — the "Ending soon · Nd" row
    /// interpolates the number. A client-side copy would label the row with one value while the server
    /// counted by another.
    /// </summary>
    [Fact]
    public async Task Summary_CarriesTheEffectiveWindows()
    {
        await using var context = TestContextFactory.Create();

        var summary = await Summarise(context);

        Assert.Equal(45, summary.EndingWindowDays);
        Assert.Equal(45, summary.ChargeWindowDays);
    }
}
