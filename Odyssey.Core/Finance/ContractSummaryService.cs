using Odyssey.Core;
using Mapster;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Odyssey.Context;
using ContextContractType = Odyssey.Context.ContractType;
using ContextContractFileType = Odyssey.Context.ContractFileType;
using DtoAccountType = Odyssey.Dtos.Finance.AccountType;
using DtoContractType = Odyssey.Dtos.Finance.ContractType;
using DtoContractFileType = Odyssey.Dtos.Finance.ContractFileType;
using Odyssey.Dtos.Finance;
using Odyssey.Core.Pagination;
using Odyssey.Dtos;
using Microsoft.Extensions.Logging;
using ContextContractPartyRole = Odyssey.Context.ContractPartyRole;
using ContextInterval = Odyssey.Context.Interval;
using ContextTermValueUnit = Odyssey.Context.TermValueUnit;
using ContextTermDirection = Odyssey.Context.TermDirection;
using DtoInterval = Odyssey.Dtos.Finance.Interval;
using DtoContractPartyRole = Odyssey.Dtos.Finance.ContractPartyRole;

namespace Odyssey.Core.Finance;

/// <summary>
/// The contracts roll-up (issue #174 §5): status buckets, the run rate of the Active contracts' in-force
/// fees in the caller's display currency, and the upcoming charges and receipts. Split out of
/// <see cref="ContractService"/> (issue #287 M1), along the <c>PropertySummaryService</c> precedent.
/// </summary>
public class ContractSummaryService
{
    private readonly OdysseyContext context;
    private readonly TimeProvider timeProvider;
    private readonly ISystemSettingsLookup systemSettingsLookup;
    private readonly CurrencyConversionService conversion;

    public ContractSummaryService(
        OdysseyContext context,
        TimeProvider timeProvider,
        ISystemSettingsLookup systemSettingsLookup,
        CurrencyConversionService? conversion = null)
    {
        this.context = context;
        this.timeProvider = timeProvider;
        this.systemSettingsLookup = systemSettingsLookup;
        // Optional-defaulted so a direct construction in a unit test need not supply one.
        this.conversion = conversion ?? new CurrencyConversionService(context);
    }

    private DateTime Today => timeProvider.GetUtcNow().UtcDateTime.Date;

    /// <summary>
    /// The page-header roll-up: counts by status and by type, what the agreements cost to run, and the
    /// recurring charges falling due inside the look-ahead window.
    ///
    /// <para>
    /// The run rate and the movements are the SAME read of the same rows — the in-force <c>Fee</c>
    /// terms of the Active contracts — once summed and once projected forward. Neither schedules
    /// anything: a term's cadence anchor is read, never advanced or written.
    /// </para>
    ///
    /// <para>
    /// Since issue #159 each of those rows also says which way its money moves, so the run rate reports
    /// the two sides separately plus an explicit net, and the projection emits into two lists. Direction
    /// plays NO part in the status derivation, in <c>CountsByStatus</c>, in <c>CountsByType</c> or in
    /// the "ending soon" slice; it is not a query predicate and not part of the series key, so the query
    /// plan is unchanged and the split is an in-memory bucketing of a set already materialised.
    /// </para>
    ///
    /// <para>
    /// <paramref name="baseCurrency"/> is the caller's display currency; blank falls back to the most
    /// common currency among the in-force fees, so a single-currency household never sees a converted
    /// figure at all.
    /// </para>
    /// </summary>
    public async Task<ContractSummary> GetSummary(
        string? baseCurrency, CancellationToken cancellationToken = default)
    {
        var today = Today;
        var caps = await systemSettingsLookup.GetRequestCapsAsync(cancellationToken);
        var windows = await systemSettingsLookup.GetContractSummarySettingsAsync(cancellationToken);

        var contracts = await context.Contracts
            .AsNoTracking()
            .OrderByDescending(c => c.CreatedAtUtc)
            .ThenBy(c => c.ContractId)
            .Take(caps.MaxSummaryContracts)
            .Select(c => new SummaryRow(
                c.ContractId, c.Name, c.Type, c.StartDate, c.EndDate, c.CompletionDate,
                c.Archived, c.Paused, c.Ready, c.Signed))
            .ToListAsync(cancellationToken);

        var counts = new ContractStatusCounts();
        var byType = new Dictionary<DtoContractType, int>();
        var priceable = new List<SummaryRow>();

        foreach (var c in contracts)
        {
            var status = ContractStatusRules.DeriveStatus(
                c.StartDate, c.EndDate, c.CompletionDate, c.Archived, c.Paused, c.Ready, c.Signed, today);
            switch (status)
            {
                case ContractStatus.Active: counts.Active++; break;
                case ContractStatus.Upcoming: counts.Upcoming++; break;
                case ContractStatus.Expired: counts.Expired++; break;
                case ContractStatus.Archived: counts.Archived++; break;
                case ContractStatus.Paused: counts.Paused++; break;
                // Two more REAL buckets (issue #145 §5.5), not a slice: the seven are mutually
                // exclusive derived statuses and still sum to TotalContracts.
                case ContractStatus.Draft: counts.Draft++; break;
                case ContractStatus.Ready: counts.Ready++; break;
            }

            // A SLICE of Active, not a sixth bucket: it is already counted above, so the five still sum
            // to the total. Only a dated term can run out — an open-ended one never reaches a cliff.
            // A paused contract derives as Paused, so it drops out of this slice with no extra test.
            if (status == ContractStatus.Active && c.EndDate is { } end
                && DaysUntil(end, today) is var days && days >= 0 && days <= windows.EndingWindowDays)
            {
                counts.EndingSoon++;
            }

            // The by-type breakdown covers only the active (non-archived) set — archived contracts are
            // counted in the status pills but excluded from "By type" (matches the design's summary).
            // Deliberately PAUSE-AGNOSTIC (issue #140 §5.4) and, for the same reason,
            // SIGNATURE-AGNOSTIC (issue #145 §5.5): this is a headcount of the contracts on file, not
            // a cost split, and a paused agreement — or an unsigned draft — is still a contract of its
            // type. The cost split is RunRate.ByType, which excludes both — the two by-type reads
            // answer different questions and this is the one place they are answered differently.
            if (c.Archived is null)
            {
                var dtoType = c.Type.Adapt<DtoContractType>();
                byType[dtoType] = byType.GetValueOrDefault(dtoType) + 1;
            }

            // Two different sets, and they are deliberately not the same one. The run rate is what the
            // file costs to run RIGHT NOW, so only Active contracts carry one. A next charge is a
            // question about the future, so an Upcoming contract belongs there too — one signed today
            // with a price already in force has a first charge to report, clamped to its start date.
            //
            // A paused contract is excluded from the run rate, its by-type split AND the charges by
            // this one gate, because it no longer derives as Active — never by a parallel
            // "Paused is not null" test, which is how the "counts one set, prices another" defect
            // class gets in (issue #140 §3). An UNSIGNED contract (Draft / Ready) leaves through the
            // very same gate for the very same reason (issue #145 §5.5): it may carry a fully priced
            // fee, but a price nobody has agreed to is a quote, and there is nothing to run-rate or to
            // expect a charge from. No second "Signed is null" test is added here.
            if (status is ContractStatus.Active or ContractStatus.Upcoming)
            {
                priceable.Add(c with { IsActive = status == ContractStatus.Active });
            }
        }

        var priced = await LoadInForceFeesAsync(priceable, today, cancellationToken);
        var movements = BuildUpcomingMovements(
            priced, today, windows.ChargeWindowDays, windows.MaxSummaryCharges);

        return new ContractSummary
        {
            TotalContracts = contracts.Count,
            CountsByStatus = counts,
            CountsByType = byType
                .OrderBy(kv => kv.Key)
                .Select(kv => new ContractTypeCount { Type = kv.Key, Count = kv.Value })
                .ToList(),
            RunRate = await BuildRunRateAsync(priced, baseCurrency, cancellationToken),
            UpcomingCharges = movements.Charges,
            UpcomingReceipts = movements.Receipts,
            EndingWindowDays = windows.EndingWindowDays,
            ChargeWindowDays = windows.ChargeWindowDays,
        };
    }

    /// <summary>
    /// The in-force amount terms of the Active and Upcoming contracts, in one query.
    ///
    /// <para>
    /// Narrowed in SQL to those contracts and to <c>EffectiveFrom &lt;= today</c>, then collapsed per
    /// series in memory by <see cref="TermSeries"/> — the same winner rule the record card and the
    /// <c>…/terms/current</c> endpoint use, so "in force" cannot mean three different things.
    /// </para>
    /// </summary>
    private async Task<List<PricedTerm>> LoadInForceFeesAsync(
        List<SummaryRow> contracts, DateTime today, CancellationToken cancellationToken)
    {
        if (contracts.Count == 0)
        {
            return [];
        }

        var byId = contracts.ToDictionary(c => c.ContractId);
        var ids = byId.Keys.ToList();

        // Every kind is a candidate, not just Amount: the in-force entry of each series is resolved
        // FIRST and only then filtered to Amount (issue #192 §8). Filtering in SQL would let a series
        // whose amount was superseded by a Text or Percentage entry keep contributing that amount.
        var candidates = await context.Terms
            .AsNoTracking()
            .Where(t => ids.Contains(t.ContractId) && t.EffectiveFrom <= today)
            .ToListAsync(cancellationToken);

        var priced = new List<PricedTerm>();
        foreach (var group in candidates.GroupBy(t => t.ContractId))
        {
            foreach (var term in TermSeries.Current(group))
            {
                if (term.ValueUnit != ContextTermValueUnit.Amount || term.Value is not { } amount)
                {
                    continue;
                }

                // A fee with no cadence names an occasion (OneTime, PerOccurrence, PerUnit) rather than
                // a rhythm, so it carries neither a rate to project nor a next occurrence to predict.
                if (term.Interval is not { } interval || !interval.IsPeriodic())
                {
                    continue;
                }

                priced.Add(new PricedTerm(
                    byId[group.Key], term.Label, amount,
                    CurrencyValidationService.Normalize(term.CurrencyCode ?? string.Empty),
                    interval, Math.Max(1, term.IntervalCount ?? 1),
                    (term.AnchorDate ?? term.EffectiveFrom).Date,
                    // R1 (issue #159) — the direction of the WINNING entry, read after the series
                    // collapse, so a superseded entry's direction never reaches a total. Direction is
                    // not a query predicate and not part of the series key, so nothing above changes.
                    term.Direction));
            }
        }

        return priced;
    }

    /// <summary>
    /// What the file costs to run and what it brings in: each in-force periodic fee projected by its
    /// cadence (<c>Value ÷ IntervalCount × periods</c>), bucketed by the direction of the in-force
    /// entry, and converted to base.
    ///
    /// <para>
    /// A currency with no rate to base is NAMED rather than folded in at 1:1 — a silent 1:1 would
    /// under-report a strong currency and over-report a weak one, and either reads as a real figure.
    /// The same exclusion applies to the per-type split and to BOTH directions, so the rows, the two
    /// grosses and the net always cover the same set of terms.
    /// </para>
    ///
    /// <para>
    /// The base-currency vote (issue #159 §5.7 rule 6) counts both directions and runs BEFORE the
    /// bucketing. Splitting first and voting per bucket would elect two bases, and the net would then
    /// be a difference of two different currencies.
    /// </para>
    /// </summary>
    private async Task<ContractRunRate> BuildRunRateAsync(
        List<PricedTerm> priced, string? baseCurrency, CancellationToken cancellationToken)
    {
        // Only the Active rows feed the run rate, so only they name its currencies and vote on its base:
        // a currency used solely by a contract that has not started could otherwise win a base-currency
        // vote it then contributes nothing to.
        var running = priced.Where(p => p.Contract.IsActive).ToList();
        var currencies = running
            .Select(p => p.CurrencyCode)
            .Where(code => code.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        // Blank base → the currency the most in-force fees are priced in; the code tie-break keeps the
        // pick deterministic. A term with no currency of its own is read as being in base.
        var baseCode = string.IsNullOrWhiteSpace(baseCurrency)
            ? running.Where(p => p.CurrencyCode.Length > 0)
                .GroupBy(p => p.CurrencyCode, StringComparer.Ordinal)
                .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal)
                .FirstOrDefault()?.Key ?? "USD"
            : CurrencyValidationService.Normalize(baseCurrency);

        var rates = await conversion.GetLatestRatesToAsync(baseCode, currencies, cancellationToken: cancellationToken);

        var runRate = new ContractRunRate { BaseCurrency = baseCode };
        var unconverted = new SortedSet<string>(StringComparer.Ordinal);

        // One accumulator per direction. The two are summed independently and NEVER mixed: the only
        // figure that crosses them is the net, which says so in its name.
        var outgoing = new DirectionTotals();
        var incoming = new DirectionTotals();

        foreach (var p in priced)
        {
            // Narrowed back to Active here rather than at the query: a next charge legitimately looks
            // ahead to a contract that has not started, but nothing that has not started is costing
            // anything yet, so it carries no run rate. R2 — direction neither widens nor narrows this
            // gate, and no parallel status test is added beside it.
            if (!p.Contract.IsActive)
            {
                continue;
            }

            var code = p.CurrencyCode.Length == 0 ? baseCode : p.CurrencyCode;
            if (!TryRateToBase(code, baseCode, rates, out var rate))
            {
                // Named once, on whichever side it appeared, and excluded from both grosses and the
                // net — so the net is partial exactly when the grosses are, which is the existing
                // legibility contract extended rather than a new one.
                unconverted.Add(code);
                continue;
            }

            var (moFactor, yrFactor) = CadenceFactors(p.Interval);
            var mo = p.Amount * moFactor / p.IntervalCount * rate;
            var yr = p.Amount * yrFactor / p.IntervalCount * rate;

            var side = p.Direction == ContextTermDirection.Incoming ? incoming : outgoing;
            side.Add(p.Contract.Type.Adapt<DtoContractType>(), mo, yr);
        }

        // Display-only estimates (the daily/weekly cadence factors are not exact in decimal), so round
        // to a clean money figure — after summing, never per term. The per-type rows and the totals are
        // rounded independently, so with enough types their sum can differ from the total by a cent;
        // what "the rows sum to the totals" guarantees is CURRENCY PARITY — a currency excluded from
        // the total is excluded from every row too — not post-rounding arithmetic equality.
        runRate.Monthly = outgoing.Monthly is { } om ? Round2(om) : null;
        runRate.Yearly = outgoing.Yearly is { } oy ? Round2(oy) : null;
        runRate.ByType = outgoing.Rows();

        runRate.IncomingMonthly = incoming.Monthly is { } im ? Round2(im) : null;
        runRate.IncomingYearly = incoming.Yearly is { } iy ? Round2(iy) : null;
        runRate.IncomingByType = incoming.Rows();

        // Computed from the UNROUNDED sums and rounded once: differencing two already-rounded figures
        // compounds the rounding rather than cancelling it. Null only when BOTH sides are null — a
        // household with income and no recorded costs has a perfectly good net.
        runRate.NetMonthly = Net(incoming.Monthly, outgoing.Monthly);
        runRate.NetYearly = Net(incoming.Yearly, outgoing.Yearly);

        runRate.UnconvertedCurrencies = [.. unconverted];

        return runRate;
    }

    /// <summary>
    /// <c>(incoming ?? 0) − (outgoing ?? 0)</c>, rounded once, or <c>null</c> when neither side
    /// contributed anything convertible.
    /// </summary>
    private static decimal? Net(decimal? incoming, decimal? outgoing) =>
        incoming is null && outgoing is null ? null : Round2((incoming ?? 0m) - (outgoing ?? 0m));

    /// <summary>
    /// One direction's running totals and per-type split. Two instances rather than a parameterised
    /// pass, so the two sides are summed by the SAME arithmetic and cannot drift — and so a total can
    /// never be assembled from rows of mixed direction.
    /// </summary>
    private sealed class DirectionTotals
    {
        private readonly Dictionary<DtoContractType, ContractRunRateTypeRow> byType = [];

        /// <summary>Null until something is added: absent means "no convertible terms on this side".</summary>
        public decimal? Monthly { get; private set; }

        public decimal? Yearly { get; private set; }

        public void Add(DtoContractType type, decimal monthly, decimal yearly)
        {
            Monthly = (Monthly ?? 0m) + monthly;
            Yearly = (Yearly ?? 0m) + yearly;

            if (!byType.TryGetValue(type, out var row))
            {
                row = new ContractRunRateTypeRow { Type = type };
                byType[type] = row;
            }

            row.Monthly += monthly;
            row.Yearly += yearly;
            row.Count++;
        }

        public List<ContractRunRateTypeRow> Rows() => byType
            .OrderBy(kv => kv.Key)
            .Select(kv =>
            {
                kv.Value.Monthly = Round2(kv.Value.Monthly);
                kv.Value.Yearly = Round2(kv.Value.Yearly);
                return kv.Value;
            })
            .ToList();
    }

    /// <summary>
    /// Each contract's SOONEST next movement inside the window, per DIRECTION — one outgoing row and
    /// one incoming row per contract at most, so a contract pricing four fees does not crowd out three
    /// others, and a contract that pays a salary on the 25th and deducts a fee on the 1st reports both.
    ///
    /// <para>
    /// Collapsing on the contract alone would silently discard whichever movement fell later, which is
    /// why the key is <c>(contract, direction)</c> since issue #159. The cap applies PER LIST, so a
    /// file with many outgoing charges cannot starve the receipts.
    /// </para>
    ///
    /// <para>
    /// A movement never falls outside the agreement it is priced under, so an occurrence past the
    /// contract's end date is dropped rather than shown.
    /// </para>
    /// </summary>
    private static (List<ContractUpcomingCharge> Charges, List<ContractUpcomingCharge> Receipts) BuildUpcomingMovements(
        List<PricedTerm> priced, DateTime today, int windowDays, int maxCharges)
    {
        var soonest = new Dictionary<(Guid ContractId, ContextTermDirection Direction), ContractUpcomingCharge>();

        foreach (var p in priced)
        {
            // A term whose contract has not started yet cannot be charged before it does.
            var from = p.Contract.StartDate is { } start && start.Date > today ? start.Date : today;
            if (NextOccurrence(p.Anchor, p.Interval, p.IntervalCount, from) is not { } date)
            {
                continue;
            }

            if (p.Contract.EndDate is { } end && date > end.Date)
            {
                continue;
            }

            var days = DaysUntil(date, today);
            if (days < 0 || days > windowDays)
            {
                continue;
            }

            var key = (p.Contract.ContractId, p.Direction);
            if (soonest.TryGetValue(key, out var held) && held.ChargeDate <= date)
            {
                continue;
            }

            soonest[key] = new ContractUpcomingCharge
            {
                ContractId = p.Contract.ContractId,
                Name = p.Contract.Name,
                Type = p.Contract.Type.Adapt<DtoContractType>(),
                Label = p.Label,
                Amount = p.Amount,
                CurrencyCode = p.CurrencyCode,
                Interval = p.Interval.Adapt<DtoInterval>(),
                IntervalCount = p.IntervalCount,
                ChargeDate = date,
                DaysUntil = days,
            };
        }

        List<ContractUpcomingCharge> Ordered(ContextTermDirection direction) => soonest
            .Where(kv => kv.Key.Direction == direction)
            .Select(kv => kv.Value)
            .OrderBy(c => c.ChargeDate)
            .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(c => c.ContractId)
            .Take(maxCharges)
            .ToList();

        return (Ordered(ContextTermDirection.Outgoing), Ordered(ContextTermDirection.Incoming));
    }

    /// <summary>Cadence → (monthly, yearly) multiplier for a single charge (the design's own factors).</summary>
    private static (decimal Monthly, decimal Yearly) CadenceFactors(ContextInterval interval) => interval switch
    {
        ContextInterval.Daily => (365.25m / 12m, 365.25m),
        ContextInterval.Weekly => (52.1775m / 12m, 52.1775m),
        ContextInterval.Annually => (1m / 12m, 1m),
        _ => (1m, 12m), // Monthly
    };

    /// <summary>
    /// The first occurrence of a periodic cadence falling on or after <paramref name="from"/>, stepped
    /// from the anchor.
    ///
    /// <para>
    /// Month and year steps are always measured from the ORIGINAL anchor rather than from a prior
    /// clamped result, so an anchor on the 31st recovers its day-of-month in longer months instead of
    /// drifting permanently to the 28th after one short February — the same rule
    /// <c>RecurrenceOccurrenceGenerator</c> follows on the journal side.
    /// </para>
    /// </summary>
    private static DateTime? NextOccurrence(DateTime anchor, ContextInterval interval, int intervalCount, DateTime from)
    {
        var count = Math.Max(1, intervalCount);
        var cur = anchor.Date;
        if (cur >= from)
        {
            return cur;
        }

        switch (interval)
        {
            case ContextInterval.Daily:
            case ContextInterval.Weekly:
            {
                var stepDays = (interval == ContextInterval.Weekly ? 7 : 1) * count;
                var diff = (from - cur).Days;
                var steps = (diff + stepDays - 1) / stepDays; // ceil to the first occurrence >= from
                return cur.AddDays((long)steps * stepDays);
            }
            case ContextInterval.Annually:
            case ContextInterval.Monthly:
            {
                var months = interval == ContextInterval.Annually ? 12 * count : count;
                // Bounded rather than a bare while: an anchor far in the past with a huge count must not
                // spin, and beyond the bound there is no occurrence worth reporting anyway.
                for (var k = 1; k <= MaxCadenceSteps; k++)
                {
                    cur = anchor.Date.AddMonths(months * k);
                    if (cur >= from)
                    {
                        return cur;
                    }
                }

                return null;
            }
            default:
                return null;
        }
    }

    /// <summary>
    /// The step ceiling on a monthly/annual projection. 6000 monthly steps is 500 years — far past any
    /// window an administrator can set — so reaching it means the anchor is nonsense, not that a real
    /// charge was missed.
    /// </summary>
    private const int MaxCadenceSteps = 6000;

    private static bool TryRateToBase(
        string currency, string baseCode, IReadOnlyDictionary<string, decimal> rates, out decimal rate)
    {
        if (string.Equals(currency, baseCode, StringComparison.Ordinal))
        {
            rate = 1m;
            return true;
        }

        return rates.TryGetValue(currency, out rate);
    }

    private static int DaysUntil(DateTime date, DateTime today) => (date.Date - today).Days;

    private static decimal Round2(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);

    /// <summary>Slim projection row for the summary computation (all contracts, one batch query).</summary>
    private sealed record SummaryRow(
        Guid ContractId, string Name, ContextContractType Type,
        DateTime? StartDate, DateTime? EndDate, DateTime? CompletionDate,
        DateTime? Archived, DateTime? Paused, DateTime? Ready, DateTime? Signed)
    {
        /// <summary>
        /// Set once from the single <c>DeriveStatus</c> call per contract, so the run rate and the
        /// charge projection cannot disagree about which contracts are running.
        /// </summary>
        public bool IsActive { get; init; }
    }

    /// <summary>One in-force periodic fee, resolved against its contract — the run rate's unit of work
    /// and the next-charge projection's, so both read exactly the same set.</summary>
    private sealed record PricedTerm(
        SummaryRow Contract, string? Label, decimal Amount, string CurrencyCode,
        ContextInterval Interval, int IntervalCount, DateTime Anchor,
        ContextTermDirection Direction);
}
