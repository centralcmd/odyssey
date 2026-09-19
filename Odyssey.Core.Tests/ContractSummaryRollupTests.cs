using Microsoft.Extensions.Logging.Abstractions;
using Odyssey.Context;
using Odyssey.Core.Finance;
using Odyssey.Dtos.Finance;
using Xunit;
using DtoContractType = Odyssey.Dtos.Finance.ContractType;
using ContextInterval = Odyssey.Context.Interval;
using ContextTermKind = Odyssey.Context.TermKind;
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
        bool archived = false)
    {
        var contract = new Contract
        {
            ContractId = Guid.NewGuid(),
            Name = name,
            Type = (Odyssey.Context.ContractType)(int)type,
            StartDate = start,
            EndDate = end,
            Archived = archived ? FixedToday.AddDays(-1) : null,
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
            TermKind = ContextTermKind.Fee,
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

    /// <summary>The per-type rows are the same read split, so they must sum to the totals.</summary>
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

    // ── Ending soon ──────────────────────────────────────────────────────────

    /// <summary>
    /// Ending soon is a SLICE of Active, so the four real buckets still sum to the total. Counting it
    /// as a fifth status would double-count every contract approaching its end date.
    /// </summary>
    [Fact]
    public async Task EndingSoon_IsASliceOfActive_NotAFifthBucket()
    {
        await using var context = TestContextFactory.Create();
        SeedContract(context, "Ending", FixedToday.AddYears(-1), FixedToday.AddDays(10));
        SeedContract(context, "Running on", FixedToday.AddYears(-1), FixedToday.AddDays(200));

        var summary = await Summarise(context);
        var counts = summary.CountsByStatus;

        Assert.Equal(2, counts.Active);
        Assert.Equal(1, counts.EndingSoon);
        Assert.Equal(
            summary.TotalContracts,
            counts.Active + counts.Upcoming + counts.Expired + counts.Archived);
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
