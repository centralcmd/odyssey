using Microsoft.Extensions.Logging.Abstractions;
using Odyssey.Context;
using Odyssey.Core.Finance;
using Odyssey.Dtos.Finance;
using Xunit;
using DtoContractType = Odyssey.Dtos.Finance.ContractType;
using ContextInterval = Odyssey.Context.Interval;
using ContextTermKind = Odyssey.Context.TermKind;
using ContextTermValueUnit = Odyssey.Context.TermValueUnit;
using ContextTermDirection = Odyssey.Context.TermDirection;

namespace Odyssey.Core.Tests;

/// <summary>
/// The direction-aware half of the page-header roll-up (issue #159): which side of the household's
/// money each in-force term lands on, what the net is, and which rows reach neither.
///
/// <para>
/// Run against EF InMemory and a fixed "today", with no exchange rates seeded, so every figure here is
/// single-currency and the bucketing arithmetic stays the subject. The conversion half — one base
/// elected across both directions, and a currency named rather than folded in at 1:1 on either side —
/// needs real rate rows and is covered over HTTP in <c>ContractTermDirectionApiTests</c>.
/// </para>
/// </summary>
public class ContractSummaryDirectionTests
{
    private static readonly DateTime FixedToday = new(2026, 6, 15, 0, 0, 0, DateTimeKind.Utc);

    private readonly OdysseyContext journal = TestContextFactory.CreateJournal();

    private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow);
    }

    // ── The compatibility guarantee ──────────────────────────────────────────

    /// <summary>
    /// AC 13 — on a file whose priced contracts only record costs, every figure the summary returned
    /// before the migration is unchanged, and the new ones are absent rather than zero. That is what
    /// lets <c>Monthly</c>/<c>Yearly</c>/<c>ByType</c> keep their names AND their meaning: every
    /// pre-#159 term backfills to <c>Outgoing</c>, so "the run rate" and "the outgoing run rate" are
    /// the same number on every existing file.
    /// </summary>
    [Fact]
    public async Task AnOutgoingOnlyFile_IsUnchanged_AndCarriesNoIncomingSide()
    {
        await using var context = TestContextFactory.Create();
        var lease = SeedContract(context, "Lease", DtoContractType.Rental);
        SeedFee(context, lease, 1000m, label: "Rent");

        var summary = await Summarise(context);

        Assert.Equal(1000m, summary.RunRate.Monthly);
        Assert.Equal(12000m, summary.RunRate.Yearly);
        Assert.Equal(DtoContractType.Rental, Assert.Single(summary.RunRate.ByType).Type);

        Assert.Null(summary.RunRate.IncomingMonthly);
        Assert.Null(summary.RunRate.IncomingYearly);
        Assert.Empty(summary.RunRate.IncomingByType);
        Assert.Empty(summary.UpcomingReceipts);

        // The net of a cost-only file is the negated gross — not null, and not the gross itself.
        Assert.Equal(-1000m, summary.RunRate.NetMonthly);
        Assert.Equal(-12000m, summary.RunRate.NetYearly);
    }

    // ── Both sides at once ───────────────────────────────────────────────────

    /// <summary>
    /// AC 14 — the case the feature exists for: one employment contract paying a salary IN and
    /// deducting a fee OUT, neither cancelling the other. A single flag on the contract could not
    /// express it, and a negative amount would make the sign mean two different things.
    /// </summary>
    [Fact]
    public async Task OneContract_CanCarryBothDirections_WithoutEitherCancellingTheOther()
    {
        await using var context = TestContextFactory.Create();
        var employment = SeedContract(context, "Globex", DtoContractType.Employment);
        SeedFee(context, employment, 600000m, label: "Base salary",
            interval: ContextInterval.Annually, direction: ContextTermDirection.Incoming);
        SeedFee(context, employment, 450m, label: "Union membership");

        var summary = await Summarise(context);

        Assert.Equal(450m, summary.RunRate.Monthly);
        Assert.Equal(50000m, summary.RunRate.IncomingMonthly);
        Assert.Equal(49550m, summary.RunRate.NetMonthly);
        Assert.Equal(594600m, summary.RunRate.NetYearly);

        var outgoingRow = Assert.Single(summary.RunRate.ByType);
        var incomingRow = Assert.Single(summary.RunRate.IncomingByType);
        Assert.Equal(DtoContractType.Employment, outgoingRow.Type);
        Assert.Equal(DtoContractType.Employment, incomingRow.Type);
        Assert.Equal(450m, outgoingRow.Monthly);
        Assert.Equal(50000m, incomingRow.Monthly);
        Assert.Equal(1, outgoingRow.Count);
        Assert.Equal(1, incomingRow.Count);
    }

    /// <summary>
    /// AC 15 — <c>IntervalCount</c> is a DIVISOR on both sides: 300 every three months is 100 a month,
    /// never 900. Reading it as a factor triples every quarterly line, and the incoming side must not
    /// acquire its own copy of that mistake.
    /// </summary>
    [Fact]
    public async Task IntervalCount_Divides_OnBothSides()
    {
        await using var context = TestContextFactory.Create();
        var costs = SeedContract(context, "Storage", DtoContractType.Subscription);
        var earns = SeedContract(context, "Sublet", DtoContractType.Rental);
        SeedFee(context, costs, 300m, label: "Plan fee", intervalCount: 3);
        SeedFee(context, earns, 900m, label: "Sublet rent", intervalCount: 3,
            direction: ContextTermDirection.Incoming);

        var summary = await Summarise(context);

        Assert.Equal(100m, summary.RunRate.Monthly);
        Assert.Equal(300m, summary.RunRate.IncomingMonthly);
        Assert.Equal(200m, summary.RunRate.NetMonthly);
    }

    // ── Null semantics ───────────────────────────────────────────────────────

    /// <summary>
    /// AC 16 — each gross is null when its OWN side contributes nothing, and the net is not null just
    /// because one side is. A household with income and no recorded costs has a perfectly good net.
    /// </summary>
    [Fact]
    public async Task WithIncomeAndNoCosts_TheOutgoingGrossIsNull_AndTheNetIsNot()
    {
        await using var context = TestContextFactory.Create();
        var employment = SeedContract(context, "Globex", DtoContractType.Employment);
        SeedFee(context, employment, 4000m, label: "Base salary",
            direction: ContextTermDirection.Incoming);

        var summary = await Summarise(context);

        Assert.Null(summary.RunRate.Monthly);
        Assert.Null(summary.RunRate.Yearly);
        Assert.Empty(summary.RunRate.ByType);
        Assert.Equal(4000m, summary.RunRate.IncomingMonthly);
        Assert.Equal(4000m, summary.RunRate.NetMonthly);
        Assert.Equal(48000m, summary.RunRate.NetYearly);
    }

    /// <summary>
    /// AC 17 — with nothing priced on either side all six figures are null and both movement lists are
    /// empty. A file with no recorded price is healthy, not degraded, so no figure resolves to zero.
    /// </summary>
    [Fact]
    public async Task WithNothingPriced_AllSixFiguresAreNull_AndBothListsAreEmpty()
    {
        await using var context = TestContextFactory.Create();
        SeedContract(context, "Unpriced", DtoContractType.Other);

        var summary = await Summarise(context);

        Assert.Null(summary.RunRate.Monthly);
        Assert.Null(summary.RunRate.Yearly);
        Assert.Null(summary.RunRate.IncomingMonthly);
        Assert.Null(summary.RunRate.IncomingYearly);
        Assert.Null(summary.RunRate.NetMonthly);
        Assert.Null(summary.RunRate.NetYearly);
        Assert.Empty(summary.UpcomingCharges);
        Assert.Empty(summary.UpcomingReceipts);
    }

    // ── The movement lists ───────────────────────────────────────────────────

    /// <summary>
    /// AC 20 — the soonest-per-contract collapse is per <c>(contract, direction)</c>. A contract that
    /// pays a salary on the 25th and deducts a fee on the 1st has two next movements, and collapsing
    /// on the contract alone would silently discard whichever fell later.
    /// </summary>
    [Fact]
    public async Task AContractWithBothDirections_ReportsOneMovementInEachList()
    {
        await using var context = TestContextFactory.Create();
        var employment = SeedContract(context, "Globex", DtoContractType.Employment);
        SeedFee(context, employment, 4000m, label: "Base salary",
            anchor: new DateTime(2026, 6, 25, 0, 0, 0, DateTimeKind.Utc),
            direction: ContextTermDirection.Incoming);
        SeedFee(context, employment, 450m, label: "Union membership",
            anchor: new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc));

        var summary = await Summarise(context);

        var receipt = Assert.Single(summary.UpcomingReceipts);
        var charge = Assert.Single(summary.UpcomingCharges);
        Assert.Equal("Base salary", receipt.Label);
        Assert.Equal(new DateTime(2026, 6, 25), receipt.ChargeDate);
        Assert.Equal("Union membership", charge.Label);
        Assert.Equal(new DateTime(2026, 7, 1), charge.ChargeDate);
    }

    /// <summary>
    /// The cap applies PER LIST, so a file with many outgoing charges cannot starve the receipts.
    /// Sharing one budget would make the incoming half of the panel disappear on exactly the files
    /// that need it most — the busy ones.
    /// </summary>
    [Fact]
    public async Task TheMovementCap_AppliesToEachListSeparately()
    {
        await using var context = TestContextFactory.Create();
        for (var i = 0; i < 4; i++)
        {
            var costs = SeedContract(context, $"Cost {i}", DtoContractType.Subscription);
            SeedFee(context, costs, 10m + i, label: "Plan fee", anchor: FixedToday.AddDays(i));
        }

        var earns = SeedContract(context, "Sublet", DtoContractType.Rental);
        SeedFee(context, earns, 900m, label: "Sublet rent", anchor: FixedToday.AddDays(20),
            direction: ContextTermDirection.Incoming);

        var lookup = new FakeSystemSettingsLookup { ContractSummary = new ContractSummarySettings(45, 45, 2) };
        var summary = await CreateService(context, lookup).GetSummary("USD");

        // Two charges — the cap — AND the receipt, which a shared budget would have crowded out.
        Assert.Equal(2, summary.UpcomingCharges.Count);
        Assert.Equal([0, 1], summary.UpcomingCharges.Select(c => c.DaysUntil));
        Assert.Equal("Sublet rent", Assert.Single(summary.UpcomingReceipts).Label);
    }

    // ── What direction does NOT change ───────────────────────────────────────

    /// <summary>
    /// AC 21 — the status gate is unchanged, and direction neither widens nor narrows it. Each of the
    /// four excluded states carries a fully priced INCOMING term here, so a parallel "is it incoming"
    /// test bypassing the one gate would show up as money appearing on the wrong side of a file that
    /// is not running at all.
    /// </summary>
    [Theory]
    [InlineData("paused")]
    [InlineData("archived")]
    [InlineData("draft")]
    [InlineData("ready")]
    public async Task ANonRunningContract_ContributesToNeitherSide(string state)
    {
        await using var context = TestContextFactory.Create();
        var id = SeedContract(context, "Not running", DtoContractType.Employment,
            paused: state == "paused",
            archived: state == "archived",
            signature: state switch { "draft" => Signature.Draft, "ready" => Signature.Ready, _ => Signature.Signed });
        SeedFee(context, id, 4000m, label: "Base salary", direction: ContextTermDirection.Incoming);

        var summary = await Summarise(context);

        Assert.Null(summary.RunRate.IncomingMonthly);
        Assert.Null(summary.RunRate.Monthly);
        Assert.Null(summary.RunRate.NetMonthly);
        Assert.Empty(summary.RunRate.IncomingByType);
        Assert.Empty(summary.UpcomingReceipts);
        Assert.Empty(summary.UpcomingCharges);
    }

    /// <summary>
    /// AC 22 — bucketing happens AFTER the series collapse, on the winning entry's direction. A
    /// superseded entry never reaches a total, and correcting a mis-directed term therefore moves the
    /// money rather than counting it twice.
    /// </summary>
    [Fact]
    public async Task ASupersededEntrysDirection_NeverReachesATotal()
    {
        await using var context = TestContextFactory.Create();
        var employment = SeedContract(context, "Globex", DtoContractType.Employment);
        // Filed the wrong way round a year ago…
        SeedFee(context, employment, 4000m, label: "Base salary",
            effectiveFrom: FixedToday.AddYears(-1));
        // …and corrected since. Same series — the later entry wins outright.
        SeedFee(context, employment, 4000m, label: "Base salary",
            effectiveFrom: FixedToday.AddMonths(-1), direction: ContextTermDirection.Incoming);

        var summary = await Summarise(context);

        Assert.Equal(4000m, summary.RunRate.IncomingMonthly);
        Assert.Null(summary.RunRate.Monthly);
        Assert.Equal(1, Assert.Single(summary.RunRate.IncomingByType).Count);
    }

    /// <summary>
    /// AC 23 — the headcounts are direction-blind. <c>CountsByStatus</c>, <c>CountsByType</c>,
    /// <c>TotalContracts</c> and the two windows answer "what is on file", not "what does it cost", so
    /// an incoming term changes none of them.
    /// </summary>
    [Fact]
    public async Task TheHeadcounts_AreUnchangedByAnIncomingTerm()
    {
        await using var withIncome = TestContextFactory.Create();
        var a = SeedContract(withIncome, "Globex", DtoContractType.Employment);
        SeedFee(withIncome, a, 4000m, label: "Base salary", direction: ContextTermDirection.Incoming);

        await using var withoutIncome = TestContextFactory.Create();
        var b = SeedContract(withoutIncome, "Globex", DtoContractType.Employment);
        SeedFee(withoutIncome, b, 4000m, label: "Base salary");

        var income = await Summarise(withIncome);
        var cost = await Summarise(withoutIncome);

        Assert.Equal(cost.TotalContracts, income.TotalContracts);
        Assert.Equal(cost.CountsByStatus, income.CountsByStatus);
        Assert.Equal(cost.CountsByType, income.CountsByType);
        Assert.Equal(cost.EndingWindowDays, income.EndingWindowDays);
        Assert.Equal(cost.ChargeWindowDays, income.ChargeWindowDays);
    }

    // ── Rounding ─────────────────────────────────────────────────────────────

    /// <summary>
    /// AC 24 — the net is computed from the UNROUNDED sums and rounded once. Differencing two
    /// already-rounded figures compounds the rounding rather than cancelling it, and this is the case
    /// that tells the two apart: 10.01 every two months is 5.005 a month, which rounds UP on its own,
    /// so 20.02 − 5.01 = 15.01 while the correct answer is round(20.02 − 5.005) = 15.02.
    /// </summary>
    [Fact]
    public async Task TheNet_IsRoundedOnce_FromTheUnroundedSums()
    {
        await using var context = TestContextFactory.Create();
        var costs = SeedContract(context, "Storage", DtoContractType.Subscription);
        var earns = SeedContract(context, "Sublet", DtoContractType.Rental);
        SeedFee(context, costs, 10.01m, label: "Plan fee", intervalCount: 2);
        SeedFee(context, earns, 20.02m, label: "Sublet rent", direction: ContextTermDirection.Incoming);

        var summary = await Summarise(context);

        Assert.Equal(5.01m, summary.RunRate.Monthly);        // 5.005, rounded away from zero
        Assert.Equal(20.02m, summary.RunRate.IncomingMonthly);
        Assert.Equal(15.02m, summary.RunRate.NetMonthly);    // NOT 20.02 − 5.01
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private enum Signature { Signed, Draft, Ready }

    private ContractService CreateService(OdysseyContext context, FakeSystemSettingsLookup? lookup = null) =>
        new(context, TestContextFactory.ContactLookup(journal), new FixedTimeProvider(FixedToday),
            lookup ?? new FakeSystemSettingsLookup(), NullLogger<ContractService>.Instance);

    private Task<ContractSummary> Summarise(OdysseyContext context) =>
        CreateService(context).GetSummary("USD");

    /// <summary>
    /// A contract that derives as Active unless the caller asks otherwise. Signed by default, because
    /// the signature layer outranks the date chain — leaving the stamps null would make every seeded
    /// row a Draft and collapse every figure this file is about.
    /// </summary>
    private static Guid SeedContract(
        OdysseyContext context,
        string name,
        DtoContractType type,
        bool archived = false,
        bool paused = false,
        Signature signature = Signature.Signed)
    {
        var contract = new Contract
        {
            ContractId = Guid.NewGuid(),
            Name = name,
            Type = (Odyssey.Context.ContractType)(int)type,
            StartDate = FixedToday.AddYears(-1),
            Archived = archived ? FixedToday.AddDays(-1) : null,
            Paused = paused ? FixedToday.AddDays(-2) : null,
            Ready = signature is Signature.Signed or Signature.Ready ? FixedToday.AddYears(-1).AddDays(-1) : null,
            Signed = signature == Signature.Signed ? FixedToday.AddYears(-1) : null,
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
        string label,
        ContextInterval interval = ContextInterval.Monthly,
        int intervalCount = 1,
        DateTime? anchor = null,
        DateTime? effectiveFrom = null,
        ContextTermDirection direction = ContextTermDirection.Outgoing)
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
            CurrencyCode = "USD",
            Interval = interval,
            IntervalCount = intervalCount,
            AnchorDate = anchor,
            Direction = direction,
            EffectiveFrom = effectiveFrom ?? FixedToday.AddMonths(-6),
            CreatedAtUtc = effectiveFrom ?? FixedToday.AddMonths(-6),
        });
        context.SaveChanges();
    }
}
