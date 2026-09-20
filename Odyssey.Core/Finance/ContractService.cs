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
using DtoInsurancePolicyType = Odyssey.Dtos.Finance.InsurancePolicyType;
using Odyssey.Dtos.Finance;
using Odyssey.Core.Pagination;
using Odyssey.Dtos;
using Microsoft.Extensions.Logging;
using ContextContractPartyRole = Odyssey.Context.ContractPartyRole;
using ContextInterval = Odyssey.Context.Interval;
using ContextTermKind = Odyssey.Context.TermKind;
using ContextTermValueUnit = Odyssey.Context.TermValueUnit;
using DtoInterval = Odyssey.Dtos.Finance.Interval;
using DtoContractPartyRole = Odyssey.Dtos.Finance.ContractPartyRole;

namespace Odyssey.Core.Finance;

/// <summary>
/// CRUD for contracts plus party- and file-link management, derived-status computation and the summary
/// rollup (issue #174). Owns all business validation — the one-of-two (XOR) party invariant,
/// defensive caps and the data-minimised read projections; the controller owns claim authorization
/// and the file content-type allow-list. Note there is no archive guard on the write paths: archival
/// hides a contract from the default list, it does not lock it, and only <see cref="EnsureArchivable"/>
/// (the transition INTO archived) still refuses anything on that account.
///
/// All time-relative computation uses a single UTC "today" captured once per request from the injected
/// <see cref="TimeProvider"/>, so a contract cannot evaluate to different statuses within one request.
/// </summary>
public class ContractService
{
    private readonly OdysseyContext context;
    private readonly IContactLookup contactLookup;
    private readonly TimeProvider timeProvider;
    private readonly ISystemSettingsLookup systemSettingsLookup;
    private readonly ILogger<ContractService> logger;
    private readonly CurrencyConversionService conversion;

    public ContractService(
        OdysseyContext context,
        IContactLookup contactLookup,
        TimeProvider timeProvider,
        ISystemSettingsLookup systemSettingsLookup,
        ILogger<ContractService> logger,
        CurrencyConversionService? conversion = null)
    {
        this.context = context;
        this.contactLookup = contactLookup;
        this.timeProvider = timeProvider;
        this.systemSettingsLookup = systemSettingsLookup;
        this.logger = logger;
        // Optional-defaulted like SubscriptionService's: the run rate is the only thing that needs it,
        // and a direct construction in a unit test should not have to supply one to exercise the rest.
        this.conversion = conversion ?? new CurrencyConversionService(context);
    }

    private DateTime Today => timeProvider.GetUtcNow().UtcDateTime.Date;

    // ── Contracts ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Server-side paged list (issue #277): SQL search + multi-type filter, then derive status, filter
    /// (multi-select) and sort in memory (status is a derived value that cannot be expressed in SQL),
    /// then slice. Archived contracts are shown by default (matching the design system) and only
    /// excluded when an explicit status filter omits the Archived status.
    /// </summary>
    public async Task<PagedResult<ContractListItem>> ListAsync(
        ContractsQueryParams query,
        CancellationToken cancellationToken = default)
    {
        var today = Today;
        var q = context.Contracts.AsNoTracking().AsQueryable();

        // Archived contracts are shown by default and only excluded when an explicit status filter
        // omits Archived (applied post-projection below) — matching the design system, which no
        // longer hides archived rows.
        var statusFilter = query.Statuses ?? [];

        var typeFilter = (query.Types ?? [])
            .Select(t => t.Adapt<ContextContractType>())
            .ToList();
        if (typeFilter.Count > 0)
        {
            q = q.Where(c => typeFilter.Contains(c.Type));
        }

        var term = ListQuery.NormalizeSearch(query.Search);
        if (term is not null)
        {
            var pattern = ListQuery.ContainsPattern(term);
            // Contact now lives in OdysseyContext — a SQL JOIN to the Contacts table is impossible across
            // the context boundary, so pre-resolve matching contact ids and filter parties by membership.
            var contactMatchIds = (await contactLookup.SearchIdsByNameAsync(term, cancellationToken)).ToHashSet();
            q = q.Where(c =>
                EF.Functions.Like(c.Name, pattern) ||
                (c.Description != null && EF.Functions.Like(c.Description, pattern)) ||
                c.Parties.Any(p => p.ContactId != null && contactMatchIds.Contains(p.ContactId.Value)));
        }

        var projected = await q
            .Select(c => new
            {
                Contract = c,
                PartyCount = c.Parties.Count,
                FileCount = c.Files.Count,
                // A correlated subquery in the one list query, exactly like the two counts above —
                // never a second grouped read, so a page of 50 costs the same as a page of 1.
                TermCount = c.Terms.Count,
                // The event log's size, on the same terms: a correlated subquery in the one list
                // query. The log itself is unbounded and has its own paged endpoint — nothing on this
                // path loads its rows.
                EventCount = c.Events.Count,
                // Contact id of the first institution party (issue #325); its display name is resolved
                // after materialisation via the contact lookup (Contact now lives in OdysseyContext).
                InstitutionContactId = c.Parties
                    .Where(p => p.ContactId != null)
                    .OrderBy(p => p.ContractPartyId)
                    .Select(p => p.ContactId)
                    .FirstOrDefault(),
            })
            .ToListAsync(cancellationToken);

        var institutionContactIds = projected
            .Where(x => x.InstitutionContactId != null)
            .Select(x => x.InstitutionContactId!.Value)
            .Distinct()
            .ToList();
        var institutionRefs = institutionContactIds.Count == 0
            ? new Dictionary<Guid, ContactRef>()
            : await contactLookup.ResolveRefsAsync(institutionContactIds, cancellationToken);

        var items = projected.Select(x => new ContractListItem
        {
            ContractId = x.Contract.ContractId,
            Name = x.Contract.Name,
            Type = x.Contract.Type.Adapt<DtoContractType>(),
            Description = x.Contract.Description,
            StartDate = x.Contract.StartDate,
            EndDate = x.Contract.EndDate,
            CompletionDate = x.Contract.CompletionDate,
            Status = DeriveStatus(x.Contract, today),
            InstitutionName = x.InstitutionContactId is { } cid && institutionRefs.TryGetValue(cid, out var institution)
                ? institution.Name
                : null,
            PartyCount = x.PartyCount,
            FileCount = x.FileCount,
            TermCount = x.TermCount,
            EventCount = x.EventCount,
            Archived = x.Contract.Archived,
            Paused = x.Contract.Paused,
            Ready = x.Contract.Ready,
            Signed = x.Contract.Signed,
        });

        if (statusFilter.Length > 0)
        {
            items = items.Where(i => statusFilter.Contains(i.Status));
        }

        var ascending = ListQuery.Ascending(query.SortDir, naturalDefaultAscending: query.SortBy is null or ContractSortBy.Name or ContractSortBy.Type or ContractSortBy.Status);
        IOrderedEnumerable<ContractListItem> sorted = query.SortBy switch
        {
            ContractSortBy.StartDate => ascending
                ? items.OrderBy(i => i.StartDate is null).ThenBy(i => i.StartDate)
                : items.OrderBy(i => i.StartDate is null).ThenByDescending(i => i.StartDate),
            ContractSortBy.EndDate => ascending
                ? items.OrderBy(i => i.EndDate is null).ThenBy(i => i.EndDate)
                : items.OrderBy(i => i.EndDate is null).ThenByDescending(i => i.EndDate),
            ContractSortBy.Type => ascending ? items.OrderBy(i => i.Type) : items.OrderByDescending(i => i.Type),
            // The shared LIFECYCLE rank, not the enum ordinal (issue #145 §8): Draft = 5 and
            // Ready = 6 are APPENDED members — an ordinal is a wire and persistence contract and is
            // never renumbered — so ordering on it would sort the two EARLIEST lifecycle states last,
            // behind Archived. Only the reading order changes, and it lives in one place the client
            // reads too.
            ContractSortBy.Status => ascending
                ? items.OrderBy(i => ContractStatusOrder.Rank(i.Status))
                : items.OrderByDescending(i => ContractStatusOrder.Rank(i.Status)),
            _ => ascending ? items.OrderBy(i => i.Name) : items.OrderByDescending(i => i.Name),
        };
        var ordered = sorted.ThenBy(i => i.ContractId).ToList();
        return ListQuery.ToPagedResult(ordered, query.Offset, query.Limit);
    }

    /// <summary>
    /// The page-header roll-up: counts by status and by type, what the agreements cost to run, and the
    /// recurring charges falling due inside the look-ahead window.
    ///
    /// <para>
    /// The run rate and the charges are the SAME read of the same rows — the in-force <c>Fee</c>
    /// terms of the Active contracts — once summed and once projected forward. Neither schedules
    /// anything: a term's cadence anchor is read, never advanced or written.
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
            var status = DeriveStatus(
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

        return new ContractSummary
        {
            TotalContracts = contracts.Count,
            CountsByStatus = counts,
            CountsByType = byType
                .OrderBy(kv => kv.Key)
                .Select(kv => new ContractTypeCount { Type = kv.Key, Count = kv.Value })
                .ToList(),
            RunRate = await BuildRunRateAsync(priced, baseCurrency, cancellationToken),
            UpcomingCharges = BuildUpcomingCharges(
                priced, today, windows.ChargeWindowDays, windows.MaxSummaryCharges),
            EndingWindowDays = windows.EndingWindowDays,
            ChargeWindowDays = windows.ChargeWindowDays,
        };
    }

    /// <summary>
    /// The in-force fee terms of the Active and Upcoming contracts, in one query.
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

        var candidates = await context.Terms
            .AsNoTracking()
            .Where(t => t.ContractId != null
                && ids.Contains(t.ContractId.Value)
                && t.TermKind == ContextTermKind.Fee
                && t.ValueUnit == ContextTermValueUnit.Amount
                && t.EffectiveFrom <= today)
            .ToListAsync(cancellationToken);

        var priced = new List<PricedTerm>();
        foreach (var group in candidates.GroupBy(t => t.ContractId!.Value))
        {
            foreach (var term in TermSeries.Current(group))
            {
                // A fee with no cadence names an occasion (OneTime, PerOccurrence, PerUnit) rather than
                // a rhythm, so it carries neither a rate to project nor a next occurrence to predict.
                if (term.Interval is not { } interval || !interval.IsPeriodic())
                {
                    continue;
                }

                priced.Add(new PricedTerm(
                    byId[group.Key], term.Label, term.Value,
                    CurrencyValidationService.Normalize(term.CurrencyCode ?? string.Empty),
                    interval, Math.Max(1, term.IntervalCount ?? 1),
                    (term.AnchorDate ?? term.EffectiveFrom).Date));
            }
        }

        return priced;
    }

    /// <summary>
    /// What the file costs to run: each in-force periodic fee projected by its cadence
    /// (<c>Value ÷ IntervalCount × periods</c>) and converted to base.
    ///
    /// <para>
    /// A currency with no rate to base is NAMED rather than folded in at 1:1 — a silent 1:1 would
    /// under-report a strong currency and over-report a weak one, and either reads as a real figure.
    /// The same exclusion applies to the per-type split, so the rows and the totals always cover the
    /// same set of terms.
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
        var byType = new Dictionary<DtoContractType, ContractRunRateTypeRow>();
        decimal? monthly = null;
        decimal? yearly = null;

        foreach (var p in priced)
        {
            // Narrowed back to Active here rather than at the query: a next charge legitimately looks
            // ahead to a contract that has not started, but nothing that has not started is costing
            // anything yet, so it carries no run rate.
            if (!p.Contract.IsActive)
            {
                continue;
            }

            var code = p.CurrencyCode.Length == 0 ? baseCode : p.CurrencyCode;
            if (!TryRateToBase(code, baseCode, rates, out var rate))
            {
                unconverted.Add(code);
                continue;
            }

            var (moFactor, yrFactor) = CadenceFactors(p.Interval);
            var mo = p.Amount * moFactor / p.IntervalCount * rate;
            var yr = p.Amount * yrFactor / p.IntervalCount * rate;

            monthly = (monthly ?? 0m) + mo;
            yearly = (yearly ?? 0m) + yr;

            var dtoType = p.Contract.Type.Adapt<DtoContractType>();
            if (!byType.TryGetValue(dtoType, out var row))
            {
                row = new ContractRunRateTypeRow { Type = dtoType };
                byType[dtoType] = row;
            }

            row.Monthly += mo;
            row.Yearly += yr;
            row.Count++;
        }

        // Display-only estimates (the daily/weekly cadence factors are not exact in decimal), so round
        // to a clean money figure — after summing, never per term. The per-type rows and the totals are
        // rounded independently, so with enough types their sum can differ from the total by a cent;
        // what "the rows sum to the totals" guarantees is CURRENCY PARITY — a currency excluded from
        // the total is excluded from every row too — not post-rounding arithmetic equality.
        runRate.Monthly = monthly is { } m ? Round2(m) : null;
        runRate.Yearly = yearly is { } y ? Round2(y) : null;
        runRate.UnconvertedCurrencies = [.. unconverted];
        runRate.ByType = byType
            .OrderBy(kv => kv.Key)
            .Select(kv =>
            {
                kv.Value.Monthly = Round2(kv.Value.Monthly);
                kv.Value.Yearly = Round2(kv.Value.Yearly);
                return kv.Value;
            })
            .ToList();

        return runRate;
    }

    /// <summary>
    /// Each contract's SOONEST next charge inside the window — one row per contract, not one per term,
    /// so a contract pricing four fees does not crowd out three others.
    ///
    /// <para>
    /// A charge never falls outside the agreement it is priced under, so an occurrence past the
    /// contract's end date is dropped rather than shown.
    /// </para>
    /// </summary>
    private static List<ContractUpcomingCharge> BuildUpcomingCharges(
        List<PricedTerm> priced, DateTime today, int windowDays, int maxCharges)
    {
        var soonest = new Dictionary<Guid, ContractUpcomingCharge>();

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

            if (soonest.TryGetValue(p.Contract.ContractId, out var held) && held.ChargeDate <= date)
            {
                continue;
            }

            soonest[p.Contract.ContractId] = new ContractUpcomingCharge
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

        return soonest.Values
            .OrderBy(c => c.ChargeDate)
            .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(c => c.ContractId)
            .Take(maxCharges)
            .ToList();
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
    /// <c>SubscriptionService.NextBilling</c> follows.
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
        ContextInterval Interval, int IntervalCount, DateTime Anchor);

    public async Task<ExistingContract?> Get(Guid id, CancellationToken cancellationToken = default)
    {
        var contract = await LoadWithDetails(id, cancellationToken);
        return contract is null ? null : await ToDto(contract, Today, cancellationToken);
    }

    /// <summary>
    /// Creates a contract. <paramref name="userId"/> is the acting user, for the signature-transition
    /// log line (issue #145 §7.7) — the same position and nullability the party writes already use. A
    /// <c>Signed</c> transition can happen on <c>POST</c> as well as <c>PUT</c>, so both call chains
    /// carry it; plumbing one and not the other would leave contracts entered already-signed with an
    /// unattributed line.
    /// </summary>
    public async Task<ExistingContract> Create(
        NewContract request, string? userId, CancellationToken cancellationToken = default)
    {
        var (startDate, endDate, completionDate) = NormalizeDates(request.StartDate, request.EndDate, request.CompletionDate);
        // The same helper PUT runs, so a rule enforced on one write path and not the other cannot
        // happen — the defect class this codebase keeps closing.
        var (ready, signed) = NormalizeSignature(request.Ready, request.Signed);

        var contract = new Contract
        {
            Name = request.Name,
            Type = request.Type.Adapt<ContextContractType>(),
            Description = request.Description,
            StartDate = startDate,
            EndDate = endDate,
            CompletionDate = completionDate,
            Archived = null,
            Paused = null,
            // Both omitted — the normal path — creates the contract in Draft.
            Ready = ready,
            Signed = signed,
            CreatedAtUtc = timeProvider.GetUtcNow().UtcDateTime,
        };

        context.Contracts.Add(contract);
        await context.SaveChangesAsync(cancellationToken);

        LogSignatureWrites(contract.ContractId, previousReady: null, previousSigned: null, ready, signed, userId);

        var loaded = await LoadWithDetails(contract.ContractId, cancellationToken);
        return await ToDto(loaded!, Today, cancellationToken);
    }

    /// <summary>
    /// Full-replacement update. <paramref name="userId"/> is the acting user, for the
    /// signature-transition log line (issue #145 §7.7).
    /// </summary>
    public async Task<ExistingContract?> Update(
        Guid id, UpdateContract request, string? userId, CancellationToken cancellationToken = default)
    {
        var contract = await LoadWithDetails(id, cancellationToken);
        if (contract is null)
        {
            return null;
        }

        var (startDate, endDate, completionDate) = NormalizeDates(request.StartDate, request.EndDate, request.CompletionDate);
        // PUT is a full replacement, so a present value SETS each stamp and an omitted one CLEARS it.
        // Guarded before anything is written back, so "stored" below still means "as of before this
        // call".
        var (ready, signed) = NormalizeSignature(request.Ready, request.Signed);
        var previousReady = contract.Ready;
        var previousSigned = contract.Signed;

        contract.Name = request.Name;
        contract.Type = request.Type.Adapt<ContextContractType>();
        contract.Description = request.Description;
        contract.StartDate = startDate;
        contract.EndDate = endDate;
        contract.CompletionDate = completionDate;
        // The lifecycle is ORDERED, not orthogonal: archiving retires a contract that is already
        // over, so only an ended one can be archived. Validated against the request's dates, not the
        // stored ones, so a single PUT may end and archive in one go.
        // Widened by issue #145: an UNSIGNED contract is archivable whatever its dates, because
        // abandoning a negotiation is the single likeliest reason to archive a draft and a draft
        // typically has no end date at all — the un-widened rule would strand it forever.
        EnsureArchivable(contract, request.IsArchived, endDate, completionDate, signed);
        // Then the pause guard, against the same request dates plus the STORED archive stamp — so a
        // body asserting both on a contract that has ended is refused here, and one on a contract that
        // has not is refused above — and against the REQUEST'S signature stamps (issue #145 §8), so a
        // single PUT that signs a Draft contract and pauses it in the same body succeeds.
        EnsurePausable(contract, request.IsPaused, startDate, endDate, completionDate, ready, signed);

        // Archive (preserving the original archive stamp) or unarchive per the request.
        contract.Archived = request.IsArchived
            ? contract.Archived ?? timeProvider.GetUtcNow().UtcDateTime
            : null;

        // Pause or resume, same idempotence rule: a repeated or replayed PUT keeps the ORIGINAL stamp,
        // so "paused since" never resets. Neither stamp is auto-cleared by the other — archiving a
        // paused contract retains the pause, losslessly, and the derivation simply reports Archived.
        contract.Paused = request.IsPaused
            ? contract.Paused ?? timeProvider.GetUtcNow().UtcDateTime
            : null;

        // Full replacement, unlike the two stamps above: these carry a caller-supplied MOMENT, not a
        // boolean intent, so there is no original value to preserve and no idempotence rule to apply.
        contract.Ready = ready;
        contract.Signed = signed;

        await context.SaveChangesAsync(cancellationToken);

        LogSignatureWrites(id, previousReady, previousSigned, ready, signed, userId);

        var reloaded = await LoadWithDetails(id, cancellationToken);
        return await ToDto(reloaded!, Today, cancellationToken);
    }

    public async Task<bool> Delete(Guid id, CancellationToken cancellationToken = default)
    {
        // Hard delete: removes the contract and cascades its party + file link rows, its term history
        // (issue #135) and its event log (issue #138). The underlying accounts/contacts/policies and
        // FileMetadata/blobs are left intact. Children are loaded so the cascade also applies under the
        // EF InMemory provider (used by tests), which does not enforce database-level cascade — without
        // the Terms/Events includes a contract delete would orphan every such row on exactly the tier
        // meant to catch it.
        var contract = await context.Contracts
            .Include(c => c.Parties)
            .Include(c => c.Files)
            .Include(c => c.Terms)
            .Include(c => c.Events)
            .FirstOrDefaultAsync(c => c.ContractId == id, cancellationToken);
        if (contract is null)
        {
            return false;
        }

        context.Contracts.Remove(contract);
        await context.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<bool> Exists(Guid id, CancellationToken cancellationToken = default) =>
        await context.Contracts.AnyAsync(c => c.ContractId == id, cancellationToken);

    // ── Parties ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Links one account or contact to a contract, in a role, optionally for a term (issue #121 §5).
    /// Returns <see langword="null"/> when the contract does not exist.
    /// </summary>
    public async Task<ExistingContractParty?> AddParty(
        Guid contractId, ContractPartyRequest request, string? userId, CancellationToken cancellationToken = default)
    {
        var contract = await context.Contracts.FirstOrDefaultAsync(c => c.ContractId == contractId, cancellationToken);
        if (contract is null)
        {
            return null;
        }

        EnsurePartyTargetXor(request);

        await EnsureTargetExists(request, cancellationToken);
        var role = request.Role.Adapt<ContextContractPartyRole>();
        var (fromDate, toDate) = NormalizePartyTerm(contract, request);
        await EnsureNotDuplicateParty(contractId, request, role, excludingPartyId: null, cancellationToken);

        var caps = await systemSettingsLookup.GetRequestCapsAsync(cancellationToken);
        var count = await context.ContractParties.CountAsync(p => p.ContractId == contractId, cancellationToken);
        if (count >= caps.MaxPartiesPerContract)
        {
            throw new DomainUnprocessableException(
                $"Contract {contractId} already has the maximum of {caps.MaxPartiesPerContract} parties.",
                PartyTargetField(request));
        }

        var party = new ContractParty
        {
            ContractId = contractId,
            AccountId = request.AccountId,
            ContactId = request.ContactId,
            Role = role,
            FromDate = fromDate,
            ToDate = toDate,
        };

        context.ContractParties.Add(party);
        await context.SaveChangesAsync(cancellationToken);

        LogPartyWrite("added", party, previousRole: null, userId);
        return await ProjectPartyAsync(party.ContractPartyId, cancellationToken);
    }

    /// <summary>
    /// Re-writes one party: its role, its target, its dates, or any combination (issue #121 §5). The
    /// row is updated <b>in place</b>, so <c>ContractPartyId</c> is stable across a role or target
    /// change and the party stays one party. Returns <see langword="null"/> when the party is not on
    /// <i>this</i> contract; throws <see cref="DomainNotFoundException"/> when the contract itself is
    /// gone.
    /// </summary>
    /// <remarks>
    /// The body is a <b>full replacement</b>, not a patch: an omitted <c>role</c> resets the role to
    /// <c>Unspecified</c> and an omitted date clears it. That is why every write is logged (§7.7).
    /// The party cap is deliberately not re-checked — an in-place update is row-count-neutral, so it is
    /// never refused by a cap, including on a contract already at or above one a later edit lowered.
    /// </remarks>
    public async Task<ExistingContractParty?> UpdateParty(
        Guid contractId, Guid partyId, ContractPartyRequest request, string? userId,
        CancellationToken cancellationToken = default)
    {
        var contract = await context.Contracts.FirstOrDefaultAsync(c => c.ContractId == contractId, cancellationToken);
        if (contract is null)
        {
            // ContractNotFound and PartyNotOnContract are distinct failure classes (§9) that happen to
            // share a status: this one names only the contract, the null return below names both ids.
            // Neither carries a field key, which is what tells them apart from the inline target 404.
            throw new DomainNotFoundException($"Contract ID {contractId} not found.");
        }

        // Scoped by BOTH ids, exactly as DeleteParty is: a valid party id from another contract is a
        // 404, never a silent cross-contract edit (§7.5).
        var party = await context.ContractParties
            .FirstOrDefaultAsync(p => p.ContractPartyId == partyId && p.ContractId == contractId, cancellationToken);
        if (party is null)
        {
            return null;
        }

        EnsurePartyTargetXor(request);

        // Only a NEW target is validated for existence, so re-dating a party whose contact was deleted
        // meanwhile does not fail — the same rule the insurance party edit applies.
        if (party.AccountId != request.AccountId || party.ContactId != request.ContactId)
        {
            await EnsureTargetExists(request, cancellationToken);
        }

        var role = request.Role.Adapt<ContextContractPartyRole>();
        var (fromDate, toDate) = NormalizePartyTerm(contract, request);
        await EnsureNotDuplicateParty(contractId, request, role, excludingPartyId: partyId, cancellationToken);

        var previousRole = party.Role;
        party.AccountId = request.AccountId;
        party.ContactId = request.ContactId;
        party.Role = role;
        party.FromDate = fromDate;
        party.ToDate = toDate;

        await context.SaveChangesAsync(cancellationToken);

        LogPartyWrite("updated", party, previousRole, userId);
        return await ProjectPartyAsync(party.ContractPartyId, cancellationToken);
    }

    public async Task<bool> DeleteParty(
        Guid contractId, Guid partyId, string? userId, CancellationToken cancellationToken = default)
    {
        var party = await context.ContractParties
            .FirstOrDefaultAsync(p => p.ContractPartyId == partyId && p.ContractId == contractId, cancellationToken);
        if (party is null)
        {
            return false;
        }

        context.ContractParties.Remove(party);
        await context.SaveChangesAsync(cancellationToken);

        // A detach has no role AFTER — the row is gone. Writing Unspecified there would make the line
        // byte-identical to a PUT that downgraded the role to Unspecified, which is precisely the event
        // this log exists to make visible; the two would then differ only by the action word, so a query
        // for the downgrade would match every detach as well.
        LogPartyWrite("detached", party, party.Role, userId, roleAfter: NoRole);
        return true;
    }

    /// <summary>What the "after" slot reads when there is no role after the write, i.e. on a detach.</summary>
    private const string NoRole = "(none)";

    /// <summary>
    /// One structured <c>Information</c> line per party write (issue #121 §7.7). <c>ContractParty</c>
    /// deliberately carries no <c>CreatedByUserId</c> column — no v1 role confers or transfers an
    /// entitlement the way an insurance beneficiary designation does — but the <c>PUT</c> is a full
    /// replacement in which an omitted <c>role</c> silently resets to <c>Unspecified</c>, so without
    /// this line an accidental employment-relationship downgrade would leave no trace anywhere.
    /// </summary>
    /// <remarks>
    /// Every value is an opaque identifier or a closed enum — never a name, an address or any free
    /// text — so the line identifies the rows a reader would then have to hold <c>contracts.read</c>
    /// to resolve, and discloses nothing by itself. The target is read back off the persisted
    /// <paramref name="party"/> rather than from the request, so it records what was actually written.
    ///
    /// <para>
    /// The one-of-two target collapses to a single <c>targetId</c> here because that is what the line
    /// means: which record this link points at. Which of the two columns held it is already implied by
    /// the party row, and naming it per-column would make the log shape depend on the target kind.
    /// </para>
    /// </remarks>
    private void LogPartyWrite(
        string action, ContractParty party, ContextContractPartyRole? previousRole, string? userId,
        string? roleAfter = null)
    {
        // A Guid, so it cannot carry the CR/LF a forged log line would need, and an opaque row id
        // rather than a credential. Both are why this is safe to record verbatim.
        Guid? targetId = party.AccountId ?? party.ContactId;

        logger.LogInformation(
            "Contract party {Action}: contract {ContractId}, party {ContractPartyId}, target {TargetId}, " +
            "role {RoleBefore} -> {RoleAfter}, by user {UserId}.",
            action,
            party.ContractId,
            party.ContractPartyId,
            targetId,
            previousRole ?? ContextContractPartyRole.Unspecified,
            roleAfter ?? party.Role.ToString(),
            userId ?? "(unknown)");
    }

    private async Task<ExistingContractParty> ProjectPartyAsync(Guid partyId, CancellationToken cancellationToken)
    {
        var loaded = await LoadPartyWithTargets(partyId, cancellationToken);
        IReadOnlyDictionary<Guid, ContactRef> contacts = loaded!.ContactId is { } contactId
            ? await contactLookup.ResolveRefsAsync([contactId], cancellationToken)
            : new Dictionary<Guid, ContactRef>();
        return ToPartyDto(loaded, contacts);
    }

    // One-of-two (XOR): exactly one target id must be set.
    private static void EnsurePartyTargetXor(ContractPartyRequest request)
    {
        var setCount =
            (request.AccountId is not null ? 1 : 0) +
            (request.ContactId is not null ? 1 : 0);
        if (setCount != 1)
        {
            throw new DomainValidationException(
                "Exactly one of accountId or contactId must be set.");
        }
    }

    /// <summary>
    /// The field key the inline-rendered party failures are attributed to: whichever of the two target
    /// ids the caller actually sent, since that is the control the client rendered.
    /// </summary>
    private static string PartyTargetField(ContractPartyRequest request) =>
        request.AccountId is not null
            ? nameof(ContractPartyRequest.AccountId)
            : nameof(ContractPartyRequest.ContactId);

    /// <summary>
    /// A party's term is the party's own fact, with one tie to the contract: it cannot begin before the
    /// contract did. Both dates are optional and null is the <b>default term</b> — the contract's own
    /// extent — not an unset value. Only the lower bound is tied, and only when the contract has a
    /// <c>StartDate</c>: an open-started term contract and a one-off (completion date only) have no
    /// anchor. <c>ToDate</c> is deliberately <b>not</b> bounded by the contract's <c>EndDate</c>, since
    /// a term contract's end moves when it is extended and bounding here would make an existing party's
    /// validity depend on the order two edits happened in.
    /// </summary>
    /// <remarks>
    /// The anchor is checked at party-write time only: editing the contract's <c>StartDate</c> later
    /// neither re-validates nor re-dates its parties, mirroring insurance, where a renewal never
    /// re-dates a party.
    /// </remarks>
    private static (DateTime? FromDate, DateTime? ToDate) NormalizePartyTerm(
        Contract contract, ContractPartyRequest request)
    {
        var fromDate = request.FromDate is { } from ? DateTimeNormalization.NormalizeToUtc(from) : (DateTime?)null;
        var toDate = request.ToDate is { } to ? DateTimeNormalization.NormalizeToUtc(to) : (DateTime?)null;

        if (fromDate is { } start && toDate is { } end && end.Date < start.Date)
        {
            throw new DomainValidationException(
                "ToDate must be on or after FromDate.",
                code: null,
                field: nameof(ContractPartyRequest.ToDate));
        }

        if (fromDate is { } began && contract.StartDate is { } contractStart && began.Date < contractStart.Date)
        {
            throw new DomainValidationException(
                $"This contract began {contractStart:yyyy-MM-dd} — a party cannot be in the role before that.",
                code: null,
                field: nameof(ContractPartyRequest.FromDate));
        }

        return (fromDate, toDate);
    }

    // ── Files ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Attaches an already-uploaded file to the contract, recording the optional validity metadata
    /// the request carries (issue #146). Takes the whole request rather than loose parameters so a
    /// later field addition does not grow the parameter list again.
    /// </summary>
    public async Task<ExistingContractFile?> AttachFile(
        Guid contractId, AttachContractFileRequest request, string userId, CancellationToken cancellationToken = default)
    {
        var contract = await context.Contracts.FirstOrDefaultAsync(c => c.ContractId == contractId, cancellationToken);
        if (contract is null)
        {
            return null;
        }

        var (validFrom, validTo, issuedAt) = DocumentValidity.Normalize(
            request.ValidFrom, request.ValidTo, request.IssuedAt);
        await EnsureIssuerExists(request.IssuedBy, cancellationToken);

        var duplicate = await context.ContractFiles
            .AnyAsync(f => f.ContractId == contractId && f.FileMetadataId == request.FileMetadataId, cancellationToken);
        if (duplicate)
        {
            throw new DomainConflictException(
                $"File {request.FileMetadataId} is already attached to contract {contractId}.");
        }

        var caps = await systemSettingsLookup.GetRequestCapsAsync(cancellationToken);
        var count = await context.ContractFiles.CountAsync(f => f.ContractId == contractId, cancellationToken);
        if (count >= caps.MaxFilesPerContract)
        {
            throw new DomainUnprocessableException(
                $"Contract {contractId} already has the maximum of {caps.MaxFilesPerContract} attached files.");
        }

        var link = new ContractFile
        {
            ContractId = contractId,
            FileMetadataId = request.FileMetadataId,
            FileType = request.FileType.Adapt<ContextContractFileType>(),
            AttachedByUserId = userId,
            AttachedAtUtc = timeProvider.GetUtcNow().UtcDateTime,
            ValidFrom = validFrom,
            ValidTo = validTo,
            IssuedAt = issuedAt,
            IssuedBy = request.IssuedBy,
        };

        context.ContractFiles.Add(link);
        await context.SaveChangesAsync(cancellationToken);

        var loaded = await context.ContractFiles
            .Include(f => f.FileMetadata)
            .FirstAsync(f => f.ContractFileId == link.ContractFileId);
        return ToFileDto(loaded);
    }

    /// <summary>
    /// Replaces an attached document's type and validity metadata (issue #146 §5.2). The link is
    /// addressed by <c>(ContractId, FileMetadataId)</c> — the unique index, and the same pair the
    /// download and detach routes use — so no link-row id is exposed. Returns <c>false</c> when that
    /// file is not attached to that contract, which the controller turns into a <c>404</c>.
    /// </summary>
    /// <remarks>
    /// The per-contract file cap is deliberately <b>not</b> evaluated: this creates no row, so a
    /// contract already at its cap can still have a document's dates corrected.
    /// </remarks>
    public async Task<bool> UpdateFile(
        Guid contractId, Guid fileMetadataId, UpdateContractFileRequest request, CancellationToken cancellationToken = default)
    {
        var contract = await context.Contracts.FirstOrDefaultAsync(c => c.ContractId == contractId, cancellationToken);
        if (contract is null)
        {
            throw new DomainNotFoundException($"Contract ID {contractId} not found.");
        }

        var link = await context.ContractFiles
            .FirstOrDefaultAsync(f => f.ContractId == contractId && f.FileMetadataId == fileMetadataId, cancellationToken);
        if (link is null)
        {
            return false;
        }

        var (validFrom, validTo, issuedAt) = DocumentValidity.Normalize(
            request.ValidFrom, request.ValidTo, request.IssuedAt);
        await EnsureIssuerExists(request.IssuedBy, cancellationToken);

        link.FileType = request.FileType.Adapt<ContextContractFileType>();
        link.ValidFrom = validFrom;
        link.ValidTo = validTo;
        link.IssuedAt = issuedAt;
        link.IssuedBy = request.IssuedBy;

        await context.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <summary>
    /// The documents attached to one contract (issue #146 §5.3), or <c>null</c> when the contract does
    /// not exist — an empty list is the correct answer for a contract with no documents, so the two
    /// cases stay distinguishable. Unpaged and bounded by <c>MaxFilesPerContract</c>, mirroring
    /// <c>GET /api/accounts/{accountId}/files</c>.
    /// </summary>
    public async Task<List<ExistingContractFile>?> GetFiles(Guid contractId, CancellationToken cancellationToken = default)
    {
        if (!await context.Contracts.AnyAsync(c => c.ContractId == contractId, cancellationToken))
        {
            return null;
        }

        var files = await context.ContractFiles
            .AsNoTracking()
            .Include(f => f.FileMetadata)
            .Where(f => f.ContractId == contractId && f.FileMetadata != null)
            .OrderBy(f => f.AttachedAtUtc)
            .ToListAsync(cancellationToken);

        return [.. files.Select(ToFileDto)];
    }

    /// <summary>
    /// Asserts the issuing contact exists. The only thing either contract-document write path does
    /// with the id: no request DTO accepts a nested contact object, so no write path here can create,
    /// rename or otherwise mutate a <c>Contact</c> (issue #146 §4.3).
    /// </summary>
    private async Task EnsureIssuerExists(Guid? issuedBy, CancellationToken cancellationToken)
    {
        if (issuedBy is not { } id)
        {
            return;
        }

        if (!(await contactLookup.ExistingIdsAsync([id], cancellationToken)).Contains(id))
        {
            throw new DomainValidationException(
                $"Contact with ID {id} was not found.",
                code: null,
                field: nameof(UpdateContractFileRequest.IssuedBy));
        }
    }

    public async Task<bool> IsFileAttachedToContract(Guid contractId, Guid fileMetadataId, CancellationToken cancellationToken = default) =>
        await context.ContractFiles.AnyAsync(f => f.ContractId == contractId && f.FileMetadataId == fileMetadataId, cancellationToken);

    public async Task<bool> DetachFile(Guid contractId, Guid fileMetadataId, CancellationToken cancellationToken = default)
    {
        var link = await context.ContractFiles
            .FirstOrDefaultAsync(f => f.ContractId == contractId && f.FileMetadataId == fileMetadataId, cancellationToken);
        if (link is null)
        {
            return false;
        }

        context.ContractFiles.Remove(link);
        await context.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <summary>
    /// Archiving requires a contract that is over — the lifecycle is ordered, so Archived implies
    /// ended and the status chip renders one state rather than a stack of flags.
    ///
    /// <para>
    /// "Over" is not the same as the derived <see cref="ContractStatus.Expired"/>: a one-off whose
    /// completion date has passed stays <c>Active</c> in the status derivation (it is a settled
    /// record, not a lapsed term), and it is archivable. Hence the two-branch check rather than a
    /// status comparison.
    /// </para>
    ///
    /// <para>
    /// Only the <b>transition</b> into archived is checked. A row archived before this rule existed
    /// stays editable and restorable: re-validating it on every save would strand it, since the only
    /// way out is a PUT that carries <c>IsArchived = true</c> right up until the one that clears it.
    /// Restoring is always allowed.
    /// </para>
    /// </summary>
    private void EnsureArchivable(
        Contract contract, bool isArchived, DateTime? endDate, DateTime? completionDate, DateTime? signed)
    {
        if (!isArchived || contract.Archived is not null)
        {
            return;
        }

        // Widened by issue #145: archiving is permitted when the contract has ended OR when the
        // request leaves it UNSIGNED. Abandoning a negotiation is the likeliest reason to archive a
        // draft, and a draft typically has no end date at all — without this branch the un-widened
        // rule would leave an abandoned draft un-archivable forever, with no step the reader could
        // take to satisfy it.
        if (signed is null)
        {
            return;
        }

        if (!ContractLifecycle.HasEnded(endDate, completionDate, Today))
        {
            throw new DomainValidationException(
                "A contract can only be archived once it has ended. Set an EndDate before today, or a CompletionDate on or before today, first.");
        }
    }

    /// <summary>
    /// The mirror of <see cref="EnsureArchivable"/> for the pause stamp (issue #140 §8): a
    /// <b>transition into</b> paused is permitted only from <c>Active</c>.
    ///
    /// <para>
    /// Evaluated against the <b>request's</b> dates and the <b>stored</b> archive stamp, exactly as the
    /// archive guard is, so one PUT may move a start date into the past and pause in the same write.
    /// </para>
    ///
    /// <para>
    /// Only the transition is checked. A contract already paused is never re-validated, so one that
    /// later expires or is archived is never stranded in a state it cannot be written out of — and
    /// <b>clearing a pause is never refused</b>, on any contract in any state. A guard on the way out
    /// is how a row gets stranded.
    /// </para>
    /// </summary>
    private void EnsurePausable(
        Contract contract, bool isPaused, DateTime? startDate, DateTime? endDate, DateTime? completionDate,
        DateTime? ready, DateTime? signed)
    {
        if (!isPaused || contract.Paused is not null)
        {
            return;
        }

        // The base status, not the full one: the contract is not paused yet, so there is nothing for
        // the Paused member to replace, and asking for it back would be circular. It is now
        // SIGNATURE-AWARE, so pausing a Draft or Ready contract refuses under the existing code with a
        // message naming the actual state — a pause is a stamp with nothing to suspend on a contract
        // nobody has signed, and it would put the row into a state the derivation never reports.
        //
        // It reads the REQUEST'S signature stamps, not the stored ones (issue #145 §8), and that is a
        // deliberate asymmetry with the STORED archive stamp beside it. EnsureArchivable has already
        // adjudicated the archive transition one line above, so re-reading the request's archive
        // intent here would double-judge it; no such prior guard exists for the signature stamps, so
        // the same stale read would be a defect rather than a mirror of one — a single PUT that signs
        // a Draft contract AND pauses it would be judged against the still-null stored Signed and
        // refused, for a contract the very same body makes Active.
        var status = DeriveBaseStatus(startDate, endDate, completionDate, contract.Archived, ready, signed, Today);
        if (status != ContractStatus.Active)
        {
            throw new DomainValidationException(
                $"Only an active contract can be paused. This contract is {status} — clear its archive, or move its dates so it is running today, first.",
                "contract_pause_requires_active",
                nameof(UpdateContract.IsPaused));
        }
    }

    // ── Derived status (deterministic, ordered — §6) ──────────────────────────────

    private static ContractStatus DeriveStatus(Contract contract, DateTime today) =>
        DeriveStatus(
            contract.StartDate, contract.EndDate, contract.CompletionDate,
            contract.Archived, contract.Paused, contract.Ready, contract.Signed, today);

    /// <summary>
    /// The full derivation: the pause-blind base status, then <c>Paused</c> applied <b>once, to its
    /// result</b> (issue #140 §8).
    ///
    /// <para>
    /// <b>Paused replaces Active and nothing else</b> — it is deliberately not a sixth step in the
    /// chain below. <see cref="DeriveBaseStatus"/> contains an early return for one-off contracts that
    /// resolves <i>both</i> of its outcomes before any later branch runs, so a pause check written
    /// inside that chain would be unreachable for a settled one-off: the stamp would be stored and
    /// every read would keep reporting <c>Active</c> while the contract kept contributing to the run
    /// rate. Applying it to the result closes that by construction rather than by careful placement.
    /// </para>
    ///
    /// <para>
    /// Read as precedence:
    /// <c>Archived &gt; Draft/Ready &gt; Upcoming &gt; Expired &gt; Paused &gt; Active</c>. A
    /// terminal fact outranks a temporary one, so a paused contract whose term has since run out reads
    /// <c>Expired</c> — its stamp is retained, so resuming it after fixing its dates is one write.
    /// </para>
    /// </summary>
    private static ContractStatus DeriveStatus(
        DateTime? startDate, DateTime? endDate, DateTime? completionDate,
        DateTime? archived, DateTime? paused, DateTime? ready, DateTime? signed, DateTime today)
    {
        var status = DeriveBaseStatus(startDate, endDate, completionDate, archived, ready, signed, today);
        return status == ContractStatus.Active && paused is not null
            ? ContractStatus.Paused
            : status;
    }

    /// <summary>
    /// The pause-blind derivation: the archive check, then the <b>signature layer</b>, then the date
    /// chain (issue #174 §6, issue #145 §3).
    ///
    /// <para>
    /// <b>The signature layer sits between the archive check and the date chain, and short-circuits
    /// it.</b> An unsigned contract with a future start date reads <c>Draft</c>/<c>Ready</c>, not
    /// <c>Upcoming</c>: its dates describe a term nobody has agreed to, and reporting <c>Upcoming</c>
    /// would assert a commitment that does not exist — and would put it back into the upcoming
    /// charges. An unsigned contract whose end date has passed reads <c>Draft</c>/<c>Ready</c>, not
    /// <c>Expired</c>: a term cannot lapse before it begins, and describing a negotiation that stalled
    /// as an agreement that ran its course would make the row look retired rather than abandoned —
    /// which matters, because abandonment is the thing the reader has to act on.
    /// </para>
    ///
    /// <para>
    /// <c>Archived</c> still wins over both: a retired contract's signature history is no longer the
    /// thing a reader is acting on.
    /// </para>
    /// </summary>
    private static ContractStatus DeriveBaseStatus(
        DateTime? startDate, DateTime? endDate, DateTime? completionDate, DateTime? archived,
        DateTime? ready, DateTime? signed, DateTime today)
    {
        if (archived is not null)
        {
            return ContractStatus.Archived;
        }
        // The signature layer. Nothing below runs for an unsigned contract, by design.
        if (signed is null)
        {
            return ready is not null ? ContractStatus.Ready : ContractStatus.Draft;
        }
        // One-off: a point-in-time agreement — Upcoming until its completion date, a settled record after.
        if (completionDate is { } completion)
        {
            return completion.Date > today ? ContractStatus.Upcoming : ContractStatus.Active;
        }
        if (startDate is { } start && start.Date > today)
        {
            return ContractStatus.Upcoming;
        }
        if (endDate is { } end && end.Date < today)
        {
            return ContractStatus.Expired;
        }
        return ContractStatus.Active;
    }

    // ── Validation helpers ─────────────────────────────────────────────────────────

    /// <summary>
    /// Validates the term/one-off dates and returns the normalized triple: a one-off (completion set)
    /// clears the term dates; a term validates <c>end ≥ start</c> when both are present.
    /// </summary>
    private static (DateTime? StartDate, DateTime? EndDate, DateTime? CompletionDate) NormalizeDates(
        DateTime? startDate, DateTime? endDate, DateTime? completionDate)
    {
        if (completionDate is not null)
        {
            return (null, null, completionDate);
        }
        if (endDate is { } end && startDate is { } start && end.Date < start.Date)
        {
            throw new DomainValidationException("EndDate must be on or after StartDate.");
        }
        return (startDate, endDate, null);
    }

    /// <summary>
    /// Normalizes both signature stamps to UTC and runs the three signature guards (issue #145 §8).
    /// Shared verbatim by <c>Create</c> and <c>Update</c>: a rule enforced on one write path and not
    /// the other is the defect class this codebase keeps closing.
    ///
    /// <para>
    /// <b>Clearing is never refused.</b> Both null, or a cleared <c>Signed</c> on a signed contract in
    /// any state, passes every guard — a guard on the way out is how a row gets stranded, which is the
    /// rule <c>EnsurePausable</c> already states in so many words.
    /// </para>
    ///
    /// <para>
    /// The two future checks run first so the field a client highlights matches the field the server
    /// names for the same body: the shared client-side helper (<c>conSignatureError</c> in the design
    /// system) tests them in this order.
    /// </para>
    /// </summary>
    private (DateTime? Ready, DateTime? Signed) NormalizeSignature(DateTime? ready, DateTime? signed)
    {
        // Through the same funnel every client-supplied date on this surface already passes, so a
        // Local or Unspecified kind cannot store a value off by a timezone offset.
        var readyUtc = ready is { } r ? DateTimeNormalization.NormalizeToUtc(r) : (DateTime?)null;
        var signedUtc = signed is { } g ? DateTimeNormalization.NormalizeToUtc(g) : (DateTime?)null;

        var today = Today;

        // G3 — DATE granularity, not instant: a client clock a few minutes ahead of the server must
        // not turn an ordinary "signed just now" into a 400, while a value dated tomorrow or later is
        // still refused. Both stamps record something that has HAPPENED, which is what makes Signed a
        // fact rather than a schedule.
        if (readyUtc is { } readyValue && readyValue.Date > today)
        {
            throw new DomainValidationException(
                "A ready date records something that has happened — it cannot be in the future.",
                "contract_signature_date_in_future",
                nameof(UpdateContract.Ready));
        }

        if (signedUtc is { } signedValue && signedValue.Date > today)
        {
            throw new DomainValidationException(
                "A signed date records something that has happened — it cannot be in the future.",
                "contract_signature_date_in_future",
                nameof(UpdateContract.Signed));
        }

        // G1 — ONE rule, not two. Under the full-replacement PUT, "clearing Ready on a signed
        // contract" and "signing a contract that was never marked ready" are the same request shape
        // (signed present, ready absent), so they take one guard and one code.
        if (signedUtc is not null && readyUtc is null)
        {
            throw new DomainValidationException(
                "A signed contract needs a ready date too. Set when it was ready for signature, or clear the signed date.",
                "contract_signed_requires_ready",
                nameof(UpdateContract.Signed));
        }

        // G2 — INSTANT granularity, unlike G3. Both values come from the same request body, so there
        // is no clock to be skewed against: a caller that sends a signed one second before its own
        // ready has contradicted itself, and rounding that away to date granularity would silently
        // accept it.
        if (signedUtc is { } s2 && readyUtc is { } r2 && s2 < r2)
        {
            throw new DomainValidationException(
                "A contract cannot be signed before it was ready for signature.",
                "contract_signed_before_ready",
                nameof(UpdateContract.Signed));
        }

        return (readyUtc, signedUtc);
    }

    /// <summary>
    /// One structured <c>Information</c> line per <c>Ready</c>/<c>Signed</c> transition (issue #145
    /// §7.7), mirroring <see cref="LogPartyWrite"/> and for the same reason: <c>PUT</c> is a full
    /// replacement in which an omitted stamp silently clears, so the line is the record of who cleared
    /// it. A write that changes neither stamp emits nothing.
    /// </summary>
    /// <remarks>
    /// Neither field carries a <c>MarkedReadyByUserId</c> / <c>SignedRecordedByUserId</c> attribution
    /// COLUMN, deliberately: no contract field carries attribution today, and any holder of
    /// <c>contracts.update</c> can already rewrite the name, counterparty, dates and price history
    /// unattributed. The trigger to revisit is stated in issue #145 §7.7 — if contract mutations
    /// become audited, or if <c>Signed</c> is ever treated as evidence of a legal fact rather than a
    /// record-keeping convenience, BOTH fields take <c>SET NULL</c> FK columns in the same change.
    ///
    /// <para>
    /// Every value is an opaque id, a fixed literal or a timestamp — never a contract name, a party
    /// name or any free text — so the line names rows a reader would still need <c>contracts.read</c>
    /// to resolve, exactly as the party line's does, and carries nothing a forged log line could use.
    /// </para>
    /// </remarks>
    private void LogSignatureWrites(
        Guid contractId, DateTime? previousReady, DateTime? previousSigned,
        DateTime? ready, DateTime? signed, string? userId)
    {
        LogSignatureWrite(contractId, nameof(Contract.Ready), previousReady, ready, userId);
        LogSignatureWrite(contractId, nameof(Contract.Signed), previousSigned, signed, userId);
    }

    private void LogSignatureWrite(
        Guid contractId, string stamp, DateTime? before, DateTime? after, string? userId)
    {
        if (Nullable.Equals(before, after))
        {
            return;
        }

        logger.LogInformation(
            "Contract signature {Stamp} {Action}: contract {ContractId}, at {StampValue}, by user {UserId}.",
            stamp,
            after is null ? "cleared" : "set",
            contractId,
            after,
            userId ?? "(unknown)");
    }

    // The two target 404s carry the field key of the id that was sent; the whole-request 404s (contract
    // gone, party not on this contract) deliberately carry none, which is how a client tells the three
    // apart without matching on message text (§9).
    private async Task EnsureTargetExists(ContractPartyRequest request, CancellationToken cancellationToken = default)
    {
        if (request.AccountId is { } accountId)
        {
            if (!await context.Accounts.AnyAsync(a => a.AccountId == accountId, cancellationToken))
            {
                throw new DomainNotFoundException(
                    $"Account ID {accountId} not found.", nameof(ContractPartyRequest.AccountId));
            }
        }
        else if (request.ContactId is { } contactId)
        {
            if (!(await contactLookup.ExistingIdsAsync([contactId], cancellationToken)).Contains(contactId))
            {
                throw new DomainNotFoundException(
                    $"Contact ID {contactId} not found.", nameof(ContractPartyRequest.ContactId));
            }
        }
    }

    /// <summary>
    /// The <i>(contract, target, role)</i> uniqueness pre-check (issue #121 §8 rule 6). Widened from
    /// <i>(contract, target)</i>: the same record may be named twice in two genuinely different
    /// capacities. <paramref name="excludingPartyId"/> takes the row being edited out of its own check,
    /// so a date-only edit is not a self-conflict.
    /// </summary>
    /// <remarks>
    /// This is a check-then-act with no transaction around it, so it is not what makes the rule
    /// <i>true</i> — the two unique indexes are, and a race surfaces through
    /// <c>GlobalExceptionHandler</c> as a generic 409. The pre-check is kept for the explaining message
    /// and because it is the only implementation the EF InMemory tiers see: that provider enforces no
    /// indexes at all.
    /// </remarks>
    private async Task EnsureNotDuplicateParty(
        Guid contractId, ContractPartyRequest request, ContextContractPartyRole role, Guid? excludingPartyId,
        CancellationToken cancellationToken = default)
    {
        var duplicate = await context.ContractParties.AnyAsync(p =>
            p.ContractId == contractId &&
            p.Role == role &&
            (excludingPartyId == null || p.ContractPartyId != excludingPartyId) &&
            ((request.AccountId != null && p.AccountId == request.AccountId) ||
             (request.ContactId != null && p.ContactId == request.ContactId)), cancellationToken);
        if (duplicate)
        {
            throw new DomainConflictException(
                "That party is already linked to the contract in that role.",
                PartyTargetField(request));
        }
    }

    // ── Loading & mapping ───────────────────────────────────────────────────────────

    private async Task<Contract?> LoadWithDetails(Guid id, CancellationToken cancellationToken = default)
    {
        return await context.Contracts
            .Include(c => c.Parties).ThenInclude(p => p.Account)
            .Include(c => c.Files).ThenInclude(f => f.FileMetadata)
            .FirstOrDefaultAsync(c => c.ContractId == id, cancellationToken);
    }

    /// <summary>
    /// The in-force entry of each of the contract's term series (issue #135) — <b>one</b> additional
    /// indexed read on the detail path, filtered on the contract and the cutoff in SQL and collapsed
    /// per series in memory by the shared <see cref="TermSeries.Current"/> rule. Deliberately not an
    /// <c>Include</c> on <see cref="LoadWithDetails"/>: that would materialise the whole history
    /// (bounded only by the per-contract cap) to return at most one row per series.
    /// </summary>
    private async Task<List<AccountCurrentTerm>> LoadCurrentTermsAsync(
        Guid contractId, DateTime asOf, CancellationToken cancellationToken)
    {
        var candidates = await context.Terms
            .AsNoTracking()
            .Where(t => t.ContractId == contractId && t.EffectiveFrom <= asOf)
            .ToListAsync(cancellationToken);

        return TermSeries.Current(candidates).Adapt<List<AccountCurrentTerm>>();
    }

    private async Task<ContractParty?> LoadPartyWithTargets(Guid partyId, CancellationToken cancellationToken = default)
    {
        return await context.ContractParties
            .Include(p => p.Account)
            .FirstOrDefaultAsync(p => p.ContractPartyId == partyId, cancellationToken);
    }

    private async Task<ExistingContract> ToDto(Contract contract, DateTime today, CancellationToken cancellationToken)
    {
        // Batch-resolve the distinct, non-null party contact ids in one call (Contact now lives in
        // OdysseyContext — no cross-context navigation include).
        var contactIds = contract.Parties
            .Where(p => p.ContactId is not null)
            .Select(p => p.ContactId!.Value)
            .Distinct()
            .ToList();
        IReadOnlyDictionary<Guid, ContactRef> contacts = contactIds.Count == 0
            ? new Dictionary<Guid, ContactRef>()
            : await contactLookup.ResolveRefsAsync(contactIds, cancellationToken);

        // Resolved against the same "today" the derived status uses, so one request cannot report a
        // contract as expired while pricing it as in force.
        var currentTerms = await LoadCurrentTermsAsync(contract.ContractId, today, cancellationToken);

        return new ExistingContract
        {
            ContractId = contract.ContractId,
            Name = contract.Name,
            Type = contract.Type.Adapt<DtoContractType>(),
            Description = contract.Description,
            StartDate = contract.StartDate,
            EndDate = contract.EndDate,
            CompletionDate = contract.CompletionDate,
            Status = DeriveStatus(contract, today),
            Parties = contract.Parties
                .OrderBy(p => p.ContractPartyId)
                .Select(p => ToPartyDto(p, contacts))
                .ToList(),
            Files = contract.Files
                .Where(f => f.FileMetadata is not null)
                .OrderBy(f => f.AttachedAtUtc)
                .Select(ToFileDto)
                .ToList(),
            CurrentTerms = currentTerms,
            Archived = contract.Archived,
            Paused = contract.Paused,
            Ready = contract.Ready,
            Signed = contract.Signed,
            CreatedAtUtc = contract.CreatedAtUtc,
        };
    }

    // Explicit member mapping (never a permissive Adapt) so a future field added to Account or
    // Contact cannot silently re-leak into this cross-claim projection (§9/§10 #2).
    private static ExistingContractParty ToPartyDto(ContractParty party, IReadOnlyDictionary<Guid, ContactRef> contacts)
    {
        if (party.AccountId is not null)
        {
            return new ExistingContractParty
            {
                ContractPartyId = party.ContractPartyId,
                ContractId = party.ContractId,
                Kind = ContractPartyKind.Account,
                Account = party.Account is null ? null : new ContractAccountReference
                {
                    AccountId = party.Account.AccountId,
                    Name = party.Account.Name,
                    Type = party.Account.AccountType.Adapt<DtoAccountType>(),
                },
                Role = party.Role.Adapt<DtoContractPartyRole>(),
                FromDate = party.FromDate,
                ToDate = party.ToDate,
            };
        }

        {
            // Resolve via the batched lookup (Contact lives in OdysseyContext). An unresolved link
            // (contact deleted across the context boundary) nulls the reference, as the read path
            // does for any missing link.
            var contact = party.ContactId is { } contactId ? contacts.GetValueOrDefault(contactId) : null;
            return new ExistingContractParty
            {
                ContractPartyId = party.ContractPartyId,
                ContractId = party.ContractId,
                Kind = ContractPartyKind.Institution,
                Institution = contact is null ? null : new ContractContactReference
                {
                    ContactId = contact.ContactId,
                    Name = contact.Name,
                    // No .Adapt here (unlike Account): ContactRef already declares Type as the Dtos
                    // ContactType, so this is a same-type assignment.
                    Type = contact.Type,
                },
                // A top-level field on the party, so it survives an unresolved target reference.
                Role = party.Role.Adapt<DtoContractPartyRole>(),
                FromDate = party.FromDate,
                ToDate = party.ToDate,
            };
        }
    }

    private static ExistingContractFile ToFileDto(ContractFile file) => new()
    {
        ContractFileId = file.ContractFileId,
        ContractId = file.ContractId,
        FileMetadata = file.FileMetadata!.Adapt<ExistingFileMetadata>(),
        FileType = file.FileType.Adapt<DtoContractFileType>(),
        AttachedByUserId = file.AttachedByUserId,
        AttachedAtUtc = file.AttachedAtUtc,
        ValidFrom = file.ValidFrom,
        ValidTo = file.ValidTo,
        IssuedAt = file.IssuedAt,
        IssuedBy = file.IssuedBy,
    };
}
