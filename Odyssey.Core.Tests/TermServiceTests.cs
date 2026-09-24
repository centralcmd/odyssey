using Odyssey.Core;
using Odyssey.Context;
using Odyssey.Dtos.Finance;
using Microsoft.EntityFrameworkCore;
using Xunit;
using TermValueUnit = Odyssey.Dtos.Finance.TermValueUnit;
using Interval = Odyssey.Dtos.Finance.Interval;
using TermDirection = Odyssey.Dtos.Finance.TermDirection;
using Odyssey.Core.Finance;
using System.ComponentModel.DataAnnotations;

namespace Odyssey.Core.Tests;

/// <summary>
/// The single term validation path, exercised through its only owner — a contract, since issue #190.
/// Contract-specific rules (cap, archived refusal, event staging) live in
/// <see cref="ContractTermServiceTests"/>.
/// </summary>
public class TermServiceTests
{
    /// <summary>A percentage term named like the former rate kind — now an ordinary labelled series.</summary>
    private static NewTerm InterestRate(decimal value, DateTime effectiveFrom) => new()
    {
        Label = "Interest rate",
        ValueUnit = TermValueUnit.Percentage,
        Value = value,
        EffectiveFrom = effectiveFrom,
    };

    /// <summary>A money-valued term, in the explicit currency a contract term requires.</summary>
    private static NewTerm Fee(string? label, decimal value, DateTime effectiveFrom) => new()
    {
        Label = label,
        ValueUnit = TermValueUnit.Amount,
        Value = value,
        CurrencyCode = "USD",
        EffectiveFrom = effectiveFrom,
    };

    [Fact]
    public async Task Create_InterestRate_Persists()
    {
        await using var context = TestContextFactory.Create();
        var contractId = await SeedContractAsync(context);
        var service = new TermService(context);

        var created = await service.CreateForContract(contractId, InterestRate(0.0325m, new DateTime(2026, 1, 1)), userId: null);

        Assert.Equal("Interest rate", created.Label);
        Assert.Equal(0.0325m, created.Value);
        Assert.Null(created.CurrencyCode);
        Assert.NotEqual(Guid.Empty, created.TermId);

        var history = await service.GetContractHistory(contractId);
        Assert.NotNull(history);
        Assert.Single(history!);
    }

    [Fact]
    public async Task Create_AmountFee_RejectsUnsupportedCurrency()
    {
        await using var context = TestContextFactory.Create();
        var contractId = await SeedContractAsync(context);
        var service = new TermService(context);

        await Assert.ThrowsAsync<DomainValidationException>(() => service.CreateForContract(contractId, new NewTerm
        {
            Label = "Account fee",
            ValueUnit = TermValueUnit.Amount,
            Value = 5m,
            CurrencyCode = "ZZZ",
            EffectiveFrom = new DateTime(2026, 1, 1),
        }, userId: null));
    }

    [Fact]
    public async Task Create_PercentageOutOfRange_Throws()
    {
        await using var context = TestContextFactory.Create();
        var contractId = await SeedContractAsync(context);
        var service = new TermService(context);

        await Assert.ThrowsAsync<DomainValidationException>(
            () => service.CreateForContract(contractId, InterestRate(1.5m, new DateTime(2026, 1, 1)), userId: null));
    }

    [Fact]
    public async Task Create_NegativeAmount_Throws()
    {
        await using var context = TestContextFactory.Create();
        var contractId = await SeedContractAsync(context);
        var service = new TermService(context);

        await Assert.ThrowsAsync<DomainValidationException>(() => service.CreateForContract(contractId, new NewTerm
        {
            Label = "Account fee",
            ValueUnit = TermValueUnit.Amount,
            Value = -1m,
            EffectiveFrom = new DateTime(2026, 1, 1),
        }, userId: null));
    }

    [Fact]
    public async Task Create_NegativeInterestRate_Allowed()
    {
        await using var context = TestContextFactory.Create();
        var contractId = await SeedContractAsync(context);
        var service = new TermService(context);

        var created = await service.CreateForContract(contractId, InterestRate(-0.005m, new DateTime(2026, 1, 1)), userId: null);

        Assert.Equal(-0.005m, created.Value);
    }

    [Fact]
    public async Task Create_FeeWithInterval_RoundTrips()
    {
        await using var context = TestContextFactory.Create();
        var contractId = await SeedContractAsync(context);
        var service = new TermService(context);

        await service.CreateForContract(contractId, new NewTerm
        {
            Label = "Account fee",
            ValueUnit = TermValueUnit.Amount,
            Value = 2m,
            CurrencyCode = "USD",
            Interval = Interval.Daily,
            EffectiveFrom = new DateTime(2026, 1, 1),
        }, userId: null);

        var history = await service.GetContractHistory(contractId);
        var term = Assert.Single(history!);
        Assert.Equal(Interval.Daily, term.Interval);
    }

    [Fact]
    public async Task Create_DuplicateSeriesAndEffectiveFrom_Throws()
    {
        await using var context = TestContextFactory.Create();
        var contractId = await SeedContractAsync(context);
        var service = new TermService(context);

        var date = new DateTime(2026, 1, 1);
        await service.CreateForContract(contractId, InterestRate(0.03m, date), userId: null);

        await Assert.ThrowsAsync<DomainConflictException>(
            () => service.CreateForContract(contractId, InterestRate(0.04m, date), userId: null));
    }

    [Fact]
    public async Task Create_OnMissingContract_Throws()
    {
        await using var context = TestContextFactory.Create();
        var service = new TermService(context);

        await Assert.ThrowsAsync<DomainNotFoundException>(
            () => service.CreateForContract(Guid.NewGuid(), InterestRate(0.03m, new DateTime(2026, 1, 1)), userId: null));
    }

    [Fact]
    public async Task GetCurrent_ReturnsLatestEntryPerSeriesOnOrBeforeAsOf()
    {
        await using var context = TestContextFactory.Create();
        var contractId = await SeedContractAsync(context);
        var service = new TermService(context);

        await service.CreateForContract(contractId, InterestRate(0.03m, new DateTime(2026, 1, 1)), userId: null);
        await service.CreateForContract(contractId, InterestRate(0.025m, new DateTime(2026, 3, 1)), userId: null);
        await service.CreateForContract(contractId, InterestRate(0.02m, new DateTime(2026, 6, 1)), userId: null);

        var current = await service.GetContractCurrent(contractId, new DateTime(2026, 4, 1));
        var entry = Assert.Single(current!);
        Assert.Equal(0.025m, entry.Value);
        Assert.Equal(new DateTime(2026, 3, 1), entry.EffectiveFrom);

        // History still shows all three.
        var history = await service.GetContractHistory(contractId);
        Assert.Equal(3, history!.Count);
    }

    [Fact]
    public async Task GetCurrent_NewerEntrySupersedesPrevious()
    {
        await using var context = TestContextFactory.Create();
        var contractId = await SeedContractAsync(context);
        var service = new TermService(context);

        await service.CreateForContract(contractId, InterestRate(0.03m, new DateTime(2026, 1, 1)), userId: null);
        await service.CreateForContract(contractId, InterestRate(0.02m, new DateTime(2026, 6, 1)), userId: null);

        var current = await service.GetContractCurrent(contractId, new DateTime(2026, 12, 1));
        var entry = Assert.Single(current!);
        Assert.Equal(0.02m, entry.Value);
    }

    [Fact]
    public async Task GetCurrent_OnePerSeries_AcrossLabels()
    {
        await using var context = TestContextFactory.Create();
        var contractId = await SeedContractAsync(context);
        var service = new TermService(context);

        await service.CreateForContract(contractId, InterestRate(0.03m, new DateTime(2026, 1, 1)), userId: null);
        await service.CreateForContract(contractId, new NewTerm
        {
            Label = "Account fee",
            ValueUnit = TermValueUnit.Amount,
            Value = 5m,
            CurrencyCode = "USD",
            EffectiveFrom = new DateTime(2026, 1, 1),
        }, userId: null);

        var current = await service.GetContractCurrent(contractId);
        Assert.Equal(2, current!.Count);
        Assert.Equal(new[] { "Account fee", "Interest rate" }, current.Select(t => t.Label));
    }

    [Fact]
    public async Task GetHistory_OnMissingContract_ReturnsNull()
    {
        await using var context = TestContextFactory.Create();
        var service = new TermService(context);

        Assert.Null(await service.GetContractHistory(Guid.NewGuid()));
        Assert.Null(await service.GetContractCurrent(Guid.NewGuid()));
    }

    [Fact]
    public async Task Update_TermNotOnContract_ReturnsFalse()
    {
        await using var context = TestContextFactory.Create();
        var contractId = await SeedContractAsync(context);
        var service = new TermService(context);

        var updated = await service.UpdateForContract(contractId, Guid.NewGuid(), InterestRate(0.01m, new DateTime(2026, 1, 1)), userId: null);
        Assert.False(updated);
    }

    [Fact]
    public async Task Update_ChangesValue()
    {
        await using var context = TestContextFactory.Create();
        var contractId = await SeedContractAsync(context);
        var service = new TermService(context);

        var created = await service.CreateForContract(contractId, InterestRate(0.03m, new DateTime(2026, 1, 1)), userId: null);
        var updated = await service.UpdateForContract(contractId, created.TermId, InterestRate(0.04m, new DateTime(2026, 1, 1)), userId: null);

        Assert.True(updated);
        var history = await service.GetContractHistory(contractId);
        Assert.Equal(0.04m, Assert.Single(history!).Value);
    }

    [Fact]
    public async Task Delete_TermNotOnContract_ReturnsFalse()
    {
        await using var context = TestContextFactory.Create();
        var contractId = await SeedContractAsync(context);
        var service = new TermService(context);

        Assert.False(await service.DeleteForContract(contractId, Guid.NewGuid(), userId: null));
    }

    [Fact]
    public async Task Delete_RemovesTerm()
    {
        await using var context = TestContextFactory.Create();
        var contractId = await SeedContractAsync(context);
        var service = new TermService(context);

        var created = await service.CreateForContract(contractId, InterestRate(0.03m, new DateTime(2026, 1, 1)), userId: null);
        Assert.True(await service.DeleteForContract(contractId, created.TermId, userId: null));
        Assert.Empty((await service.GetContractHistory(contractId))!);
    }

    // ── Series labels ────────────────────────────────────────────────────────

    [Fact]
    public async Task Create_TwoLabelledFeesOnOneDate_BothAreInForce()
    {
        await using var context = TestContextFactory.Create();
        var contractId = await SeedContractAsync(context);
        var service = new TermService(context);

        var date = new DateTime(2026, 1, 1);
        await service.CreateForContract(contractId, Fee("ATM · abroad", 25m, date), userId: null);
        await service.CreateForContract(contractId, Fee("ATM · domestic", 5m, date), userId: null);

        var current = await service.GetContractCurrent(contractId, date);
        Assert.Equal(2, current!.Count);
        Assert.Equal(new[] { "ATM · abroad", "ATM · domestic" }, current.Select(t => t.Label));
    }

    [Fact]
    public async Task GetCurrent_SupersessionHappensWithinOneLabelOnly()
    {
        await using var context = TestContextFactory.Create();
        var contractId = await SeedContractAsync(context);
        var service = new TermService(context);

        await service.CreateForContract(contractId, Fee("ATM · abroad", 25m, new DateTime(2026, 1, 1)), userId: null);
        await service.CreateForContract(contractId, Fee("ATM · domestic", 5m, new DateTime(2026, 1, 1)), userId: null);
        await service.CreateForContract(contractId, Fee("ATM · abroad", 30m, new DateTime(2026, 6, 1)), userId: null);

        var current = await service.GetContractCurrent(contractId, new DateTime(2026, 12, 1));
        Assert.Equal(2, current!.Count);
        Assert.Equal(30m, current.Single(t => t.Label == "ATM · abroad").Value);
        Assert.Equal(5m, current.Single(t => t.Label == "ATM · domestic").Value);
    }

    [Theory]
    [InlineData("atm · abroad")]
    [InlineData("  ATM   ·   abroad  ")]
    [InlineData("AtM · AbRoAd")]
    public async Task Create_LabelDifferingOnlyByCaseOrSpacing_Conflicts(string collidingLabel)
    {
        await using var context = TestContextFactory.Create();
        var contractId = await SeedContractAsync(context);
        var service = new TermService(context);

        var date = new DateTime(2026, 1, 1);
        await service.CreateForContract(contractId, Fee("ATM · abroad", 25m, date), userId: null);

        await Assert.ThrowsAsync<DomainConflictException>(
            () => service.CreateForContract(contractId, Fee(collidingLabel, 30m, date), userId: null));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Create_FeeWithoutLabel_Throws(string? label)
    {
        await using var context = TestContextFactory.Create();
        var contractId = await SeedContractAsync(context);
        var service = new TermService(context);

        await Assert.ThrowsAsync<DomainValidationException>(
            () => service.CreateForContract(contractId, Fee(label, 25m, new DateTime(2026, 1, 1)), userId: null));
    }

    [Fact]
    public async Task Create_OverlongLabel_Throws()
    {
        await using var context = TestContextFactory.Create();
        var contractId = await SeedContractAsync(context);
        var service = new TermService(context);

        await Assert.ThrowsAsync<DomainValidationException>(
            () => service.CreateForContract(contractId, Fee(new string('x', TermLabel.MaxLength + 1), 5m, new DateTime(2026, 1, 1)), userId: null));
    }

    [Fact]
    public async Task Create_NormalizesLabelAndRoundTripsThroughHistory()
    {
        await using var context = TestContextFactory.Create();
        var contractId = await SeedContractAsync(context);
        var service = new TermService(context);

        var created = await service.CreateForContract(contractId, Fee("  ATM   Abroad  ", 25m, new DateTime(2026, 1, 1)), userId: null);

        Assert.Equal("ATM Abroad", created.Label);
        var history = await service.GetContractHistory(contractId);
        Assert.Equal("ATM Abroad", Assert.Single(history!).Label);
    }

    [Fact]
    public async Task Create_DerivesLabelKeyFromLabelAlone()
    {
        // Mass assignment: LabelKey is on no request DTO, so the only thing that can set it is the
        // service's own derivation from Label.
        await using var context = TestContextFactory.Create();
        var contractId = await SeedContractAsync(context);
        var service = new TermService(context);

        var created = await service.CreateForContract(contractId, Fee("  ATM   Abroad  ", 25m, new DateTime(2026, 1, 1)), userId: null);

        var stored = await context.Terms.AsNoTracking()
            .SingleAsync(t => t.TermId == created.TermId);
        Assert.Equal("ATM Abroad", stored.Label);
        Assert.Equal("atm abroad", stored.LabelKey);
    }

    [Fact]
    public async Task Update_ChangingOnlyTheLabel_MovesTheTermToItsOwnSeries()
    {
        await using var context = TestContextFactory.Create();
        var contractId = await SeedContractAsync(context);
        var service = new TermService(context);

        await service.CreateForContract(contractId, Fee("ATM", 25m, new DateTime(2026, 1, 1)), userId: null);
        var second = await service.CreateForContract(contractId, Fee("ATM", 30m, new DateTime(2026, 6, 1)), userId: null);

        // One series, so the later entry supersedes: one current entry.
        Assert.Single((await service.GetContractCurrent(contractId, new DateTime(2026, 12, 1)))!);

        Assert.True(await service.UpdateForContract(contractId, second.TermId, Fee("ATM · abroad", 30m, new DateTime(2026, 6, 1)), userId: null));

        var current = await service.GetContractCurrent(contractId, new DateTime(2026, 12, 1));
        Assert.Equal(2, current!.Count);
        Assert.Equal(new[] { "ATM", "ATM · abroad" }, current.Select(t => t.Label));
    }

    [Fact]
    public async Task GetCurrent_AnUnlabelledLegacyRowResolvesAsItsOwnSeries()
    {
        // A row written before labels were required carries a null label, which IS the unnamed series.
        await using var context = TestContextFactory.Create();
        var contractId = await SeedContractAsync(context);
        var service = new TermService(context);

        context.Terms.AddRange(
            new Term
            {
                ContractId = contractId,
                ValueUnit = Odyssey.Context.TermValueUnit.Percentage,
                Value = 0.03m,
                EffectiveFrom = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                CreatedAtUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            },
            new Term
            {
                ContractId = contractId,
                ValueUnit = Odyssey.Context.TermValueUnit.Percentage,
                Value = 0.02m,
                EffectiveFrom = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc),
                CreatedAtUtc = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            });
        await context.SaveChangesAsync();

        var current = await service.GetContractCurrent(contractId, new DateTime(2026, 12, 1));
        var entry = Assert.Single(current!);
        Assert.Equal(0.02m, entry.Value);
        Assert.Null(entry.Label);
    }

    // ── Defense in depth: the bounds a DIRECT (non-HTTP) caller must still hit ──
    //
    // These call TermService itself, with no WebApplicationFactory and no model binding in the path.
    // An API-level test cannot reach them at all: [ApiController] model validation rejects the same
    // input first, so the only way to prove the service check exists is to bypass the pipeline
    // exactly as a background job, a seeder or a future internal caller would (issue #120, AC 41-43).

    [Theory]
    [InlineData(4)]   // the retired Quarterly ordinal — the value this rule exists for
    [InlineData(99)]
    public async Task Create_CalledDirectlyWithAnUndefinedInterval_ThrowsAndPersistsNothing(int ordinal)
    {
        await using var context = TestContextFactory.Create();
        var contractId = await SeedContractAsync(context);
        var service = new TermService(context);

        var term = Fee("ATM · abroad", 25m, new DateTime(2026, 1, 1));
        term.Interval = (Interval)ordinal;

        await Assert.ThrowsAsync<DomainValidationException>(() => service.CreateForContract(contractId, term, userId: null));

        // Before this rule the Mapster converter's `_ => OneTime` fallthrough silently PERSISTED the
        // value as OneTime — a write-path fail-open, not a read-path degradation.
        Assert.Empty(await context.Terms.AsNoTracking().ToListAsync());
    }

    [Theory]
    [InlineData(4)]
    [InlineData(99)]
    public async Task Update_CalledDirectlyWithAnUndefinedInterval_ThrowsAndLeavesTheRowUntouched(int ordinal)
    {
        await using var context = TestContextFactory.Create();
        var contractId = await SeedContractAsync(context);
        var service = new TermService(context);

        var original = Fee("ATM · abroad", 25m, new DateTime(2026, 1, 1));
        original.Interval = Interval.PerOccurrence;
        var created = await service.CreateForContract(contractId, original, userId: null);

        var edit = Fee("ATM · abroad", 30m, new DateTime(2026, 1, 1));
        edit.Interval = (Interval)ordinal;

        await Assert.ThrowsAsync<DomainValidationException>(() => service.UpdateForContract(contractId, created.TermId, edit, userId: null));

        var stored = await context.Terms.AsNoTracking().SingleAsync();
        Assert.Equal(Odyssey.Context.Interval.PerOccurrence, stored.Interval);
        Assert.Equal(25m, stored.Value);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(TermIntervalCount.Max + 1)]
    public async Task Create_CalledDirectlyWithACountOutsideItsBound_ThrowsAndPersistsNothing(int count)
    {
        await using var context = TestContextFactory.Create();
        var contractId = await SeedContractAsync(context);
        var service = new TermService(context);

        var term = Fee("Account maintenance", 45m, new DateTime(2026, 1, 1));
        term.Interval = Interval.Monthly;
        term.IntervalCount = count;

        await Assert.ThrowsAsync<DomainValidationException>(() => service.CreateForContract(contractId, term, userId: null));
        Assert.Empty(await context.Terms.AsNoTracking().ToListAsync());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(TermIntervalCount.Max + 1)]
    public async Task Update_CalledDirectlyWithACountOutsideItsBound_ThrowsAndLeavesTheRowUntouched(int count)
    {
        await using var context = TestContextFactory.Create();
        var contractId = await SeedContractAsync(context);
        var service = new TermService(context);

        var original = Fee("Account maintenance", 45m, new DateTime(2026, 1, 1));
        original.Interval = Interval.Monthly;
        original.IntervalCount = 3;
        var created = await service.CreateForContract(contractId, original, userId: null);

        var edit = Fee("Account maintenance", 45m, new DateTime(2026, 1, 1));
        edit.Interval = Interval.Monthly;
        edit.IntervalCount = count;

        await Assert.ThrowsAsync<DomainValidationException>(() => service.UpdateForContract(contractId, created.TermId, edit, userId: null));

        var stored = await context.Terms.AsNoTracking().SingleAsync();
        Assert.Equal(3, stored.IntervalCount);
    }

    [Fact]
    public void TheIntervalCountBound_IsTheOnePairTheRangeAttributeNames()
    {
        // Asserted by reflecting the attribute rather than by inspection, so the [Range] on the DTO
        // and the service check in ApplyAndValidate cannot drift into two different bounds.
        var range = typeof(NewTerm)
            .GetProperty(nameof(NewTerm.IntervalCount))!
            .GetCustomAttributes(typeof(RangeAttribute), inherit: false)
            .Cast<RangeAttribute>()
            .Single();

        Assert.Equal(TermIntervalCount.Min, range.Minimum);
        Assert.Equal(TermIntervalCount.Max, range.Maximum);
        Assert.Equal(1, TermIntervalCount.Min);
        Assert.Equal(1000, TermIntervalCount.Max);
    }

    [Fact]
    public async Task Create_NonPeriodicInterval_StoresANullCountRatherThanAMeaningless1()
    {
        await using var context = TestContextFactory.Create();
        var contractId = await SeedContractAsync(context);
        var service = new TermService(context);

        var term = Fee("Card replacement", 15m, new DateTime(2026, 1, 1));
        term.Interval = Interval.OneTime;
        await service.CreateForContract(contractId, term, userId: null);

        var stored = await context.Terms.AsNoTracking().SingleAsync();
        Assert.Null(stored.IntervalCount);
    }

    // ── Direction (issue #159) ───────────────────────────────────────────────

    /// <summary>
    /// The direct (non-HTTP) caller never reaches <c>[EnumDataType]</c> model validation, so the
    /// service checks the ordinal itself. Without this, an undefined value would fall through the
    /// Mapster converter and be PERSISTED as whichever member it defaults to — a write-path fail-open
    /// rather than a read-path degradation. Same reasoning as the <c>Interval</c> check beside it.
    /// </summary>
    [Fact]
    public async Task Create_AnUndefinedDirectionOrdinal_IsRefused_EvenOffTheHttpPath()
    {
        await using var context = TestContextFactory.Create();
        var contractId = await SeedContractAsync(context);
        var service = new TermService(context);

        var term = Fee("Card fee", 4m, new DateTime(2026, 1, 1));
        term.Direction = (TermDirection)99;

        await Assert.ThrowsAsync<DomainValidationException>(() => service.CreateForContract(contractId, term, userId: null));
        Assert.Empty(context.Terms);
    }

    /// <summary>
    /// A contract's percentage term carries a direction like any other — an arrears rate charges, a
    /// deposit rate pays. Tested directly against the service, not only over HTTP: this file's convention is
    /// one unit test per eligibility rule, and the API tier proves the STATUS CODE rather than that
    /// the rule lives in the service every non-HTTP caller also goes through.
    /// </summary>
    [Fact]
    public async Task Create_IncomingOnAContractPercentageTerm_IsStored()
    {
        await using var context = TestContextFactory.Create();
        var contractId = await SeedContractAsync(context);
        var service = new TermService(context);

        var term = InterestRate(0.0325m, new DateTime(2026, 1, 1));
        term.Direction = TermDirection.Incoming;

        await service.CreateForContract(contractId, term, userId: null);

        Assert.Equal(Odyssey.Context.TermDirection.Incoming,
            (await context.Terms.AsNoTracking().SingleAsync()).Direction);
    }

    /// <summary>
    /// V2 — direction is accepted on any FEE, including the ones the roll-up ignores. A one-off signing
    /// bonus is legitimate incoming record-keeping: the roll-up's exclusions are about having no rate
    /// to PROJECT, not about direction.
    /// </summary>
    [Theory]
    [InlineData(Interval.OneTime)]
    [InlineData(Interval.PerOccurrence)]
    [InlineData(Interval.PerUnit)]
    [InlineData(null)]
    public async Task Create_ACadencelessFee_StillCarriesItsDirection(Interval? interval)
    {
        await using var context = TestContextFactory.Create();
        var contractId = await SeedContractAsync(context);
        var service = new TermService(context);

        var term = Fee("Signing bonus", 5000m, new DateTime(2026, 1, 1));
        term.CurrencyCode = "USD";
        term.Interval = interval;
        term.Direction = TermDirection.Incoming;

        await service.CreateForContract(contractId, term, userId: null);

        Assert.Equal(Odyssey.Context.TermDirection.Incoming,
            (await context.Terms.AsNoTracking().SingleAsync()).Direction);
    }

    /// <summary>
    /// V3 — direction joins neither the series key nor the duplicate guard. Two entries sharing a
    /// <c>(kind, label)</c> and an effective date still collide however they are directed; if direction
    /// forked the series, correcting a mis-directed term would grow a second concurrent history
    /// instead of superseding the first.
    /// </summary>
    [Fact]
    public async Task Create_TheDuplicateGuard_IsBlindToDirection()
    {
        await using var context = TestContextFactory.Create();
        var contractId = await SeedContractAsync(context);
        var service = new TermService(context);

        var outgoing = Fee("Base salary", 4000m, new DateTime(2026, 1, 1));
        outgoing.CurrencyCode = "USD";
        await service.CreateForContract(contractId, outgoing, userId: null);

        var incoming = Fee("Base salary", 4000m, new DateTime(2026, 1, 1));
        incoming.CurrencyCode = "USD";
        incoming.Direction = TermDirection.Incoming;

        await Assert.ThrowsAsync<DomainConflictException>(
            () => service.CreateForContract(contractId, incoming, userId: null));
    }

    private static async Task<Guid> SeedContractAsync(OdysseyContext context)
    {
        var contract = new Contract
        {
            ContractId = Guid.NewGuid(),
            Name = "Employment Agreement",
            Type = Odyssey.Context.ContractType.Employment,
            StartDate = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            CreatedAtUtc = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        context.Contracts.Add(contract);
        await context.SaveChangesAsync();
        return contract.ContractId;
    }
}
