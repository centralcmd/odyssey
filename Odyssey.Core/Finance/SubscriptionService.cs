using Odyssey.Core;
using Mapster;
using Microsoft.EntityFrameworkCore;
using Odyssey.Context;
using Odyssey.Dtos.Finance;
using Odyssey.Core.Pagination;
using Odyssey.Dtos;
using ContextBillingInterval = Odyssey.Context.BillingInterval;
using DtoBillingInterval = Odyssey.Dtos.Finance.BillingInterval;

namespace Odyssey.Core.Finance;

/// <summary>
/// CRUD + server-side list for manually tracked subscriptions (issue #293). Owns all business
/// validation, mapping, and the paged list query; the controller owns claim authorization. A
/// subscription is a pure record-keeping row — no transactions, no accounts, no scheduler — so this
/// service is deliberately simpler than <see cref="InsuranceService"/> (a single amount/currency/
/// interval live directly on the entity, with no child renewals and no derived-status engine).
///
/// The <c>Paused</c>/<c>Archived</c> state stamps are owned here (set from the injected
/// <see cref="TimeProvider"/>); the write DTOs carry only boolean toggles, so a client cannot inject
/// an arbitrary state timestamp.
/// </summary>
public class SubscriptionService
{
    private readonly OdysseyContext context;
    private readonly IContactLookup contactLookup;
    private readonly TimeProvider timeProvider;
    private readonly CurrencyConversionService conversion;
    private readonly ISystemSettingsLookup systemSettingsLookup;

    /// <summary>"Today" in UTC, matching every other derivation on this service (Ended, the status
    /// partition, the renewals window) so they cannot disagree across the date boundary.</summary>
    private DateOnly Today => DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime);

    /// <summary>
    /// The look-ahead window, the renewal-row cap and the summary fetch bound are admin-editable
    /// settings (issue #437), not constants. The lookup is a <strong>required</strong> parameter,
    /// matching <see cref="InsuranceService"/> and unlike this class's own optional
    /// <c>TimeProvider</c>/<c>CurrencyConversionService</c>: an optional null-defaulted lookup would
    /// let a direct construction silently revert to compiled defaults, which is exactly the drift the
    /// migration exists to remove.
    /// </summary>
    public SubscriptionService(
        OdysseyContext context,
        IContactLookup contactLookup,
        ISystemSettingsLookup systemSettingsLookup,
        TimeProvider? timeProvider = null,
        CurrencyConversionService? conversion = null)
    {
        this.context = context;
        this.contactLookup = contactLookup;
        this.systemSettingsLookup = systemSettingsLookup;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.conversion = conversion ?? new CurrencyConversionService(context);
    }

    /// <summary>
    /// Server-side paged list (issue #277/#293): SQL search (name / external id / contact name),
    /// interval filter, derived lifecycle-status filter (Active/Paused/Ended/Archived), allowlisted
    /// sort, then a single windowed batch. The contact name is projected via a join in the same
    /// query — no per-row lookup.
    /// </summary>
    public async Task<PagedResult<SubscriptionListItem>> ListAsync(
        SubscriptionsQueryParams query,
        CancellationToken cancellationToken = default)
    {
        var q = context.Subscriptions
            .AsNoTracking()
            .AsQueryable();

        var term = ListQuery.NormalizeSearch(query.Search);
        if (term is not null)
        {
            var pattern = ListQuery.ContainsPattern(term);
            // A name match is pre-resolved to contact ids via the lookup, then combined with the
            // subscription-field matches. Same note as InsuranceService: a JOIN is possible again now that
            // the contexts are merged, and swapping to one is its own change.
            var contactMatchIds = (await contactLookup.SearchIdsByNameAsync(term, cancellationToken)).ToHashSet();
            q = q.Where(s =>
                EF.Functions.Like(s.Name, pattern) ||
                (s.ExternalId != null && EF.Functions.Like(s.ExternalId, pattern)) ||
                (s.ContactId != null && contactMatchIds.Contains(s.ContactId.Value)));
        }

        if (query.Intervals is { Length: > 0 } intervals)
        {
            var contextIntervals = intervals
                .Select(i => i.Adapt<ContextBillingInterval>())
                .ToList();
            q = q.Where(s => contextIntervals.Contains(s.Interval));
        }

        // Filter by the derived single lifecycle status (Archived > Ended > Paused > Active). Each
        // status is a mutually exclusive partition expressible in SQL, so an OR over the selected set
        // keeps paging server-side. Empty = all (archived included, per the default list contract).
        if (query.Statuses is { Length: > 0 } statuses)
        {
            var today = Today;
            var wantActive = statuses.Contains(SubscriptionStatusFilter.Active);
            var wantPaused = statuses.Contains(SubscriptionStatusFilter.Paused);
            var wantEnded = statuses.Contains(SubscriptionStatusFilter.Ended);
            var wantArchived = statuses.Contains(SubscriptionStatusFilter.Archived);

            q = q.Where(s =>
                (wantArchived && s.Archived != null) ||
                (wantEnded && s.Archived == null && s.EndDate != null && s.EndDate <= today) ||
                (wantPaused && s.Archived == null && (s.EndDate == null || s.EndDate > today) && s.Paused != null) ||
                (wantActive && s.Archived == null && (s.EndDate == null || s.EndDate > today) && s.Paused == null));
        }

        var ascending = ListQuery.Ascending(
            query.SortDir,
            naturalDefaultAscending: query.SortBy is null or SubscriptionSortBy.Name or SubscriptionSortBy.StartDate or SubscriptionSortBy.Interval);
        var sorted = query.SortBy switch
        {
            SubscriptionSortBy.Amount => ascending ? q.OrderBy(s => s.Amount) : q.OrderByDescending(s => s.Amount),
            SubscriptionSortBy.StartDate => ascending ? q.OrderBy(s => s.StartDate) : q.OrderByDescending(s => s.StartDate),
            // Interval is stored as its int value, so ordering by the column yields the enum's
            // numeric order (Daily < Weekly < Monthly < Yearly).
            SubscriptionSortBy.Interval => ascending ? q.OrderBy(s => s.Interval) : q.OrderByDescending(s => s.Interval),
            _ => ascending ? q.OrderBy(s => s.Name) : q.OrderByDescending(s => s.Name),
        };
        q = sorted.ThenBy(s => s.SubscriptionId);

        var page = await q.ToPagedResultAsync(query.Offset, query.Limit, cancellationToken);
        var refs = await ResolveContactRefs(page.Items.Select(s => s.ContactId), cancellationToken);

        return new PagedResult<SubscriptionListItem>
        {
            Items = page.Items.Select(s => ToListItem(s, LookupRef(refs, s.ContactId))).ToList(),
            Offset = page.Offset,
            Limit = page.Limit,
            TotalCount = page.TotalCount,
        };
    }

    public async Task<ExistingSubscription?> Get(Guid id, CancellationToken cancellationToken = default)
    {
        var subscription = await context.Subscriptions
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.SubscriptionId == id, cancellationToken);

        if (subscription is null)
        {
            return null;
        }

        var refs = await ResolveContactRefs([subscription.ContactId], cancellationToken);
        return ToDto(subscription, LookupRef(refs, subscription.ContactId), Today);
    }

    public async Task<ExistingSubscription> Create(NewSubscription request, CancellationToken cancellationToken = default)
    {
        await EnsureContactValid(request.ContactId, cancellationToken);
        var currency = await NormalizeAndValidateCurrency(request.CurrencyCode, cancellationToken);
        ValidateDates(request.StartDate, request.EndDate);
        ValidateIntervalCount(request.IntervalCount);

        var subscription = new Subscription
        {
            Name = request.Name.Trim(),
            ExternalId = NormalizeExternalId(request.ExternalId),
            ContactId = request.ContactId,
            StartDate = request.StartDate,
            EndDate = request.EndDate,
            Amount = request.Amount,
            CurrencyCode = currency,
            Interval = request.Interval.Adapt<ContextBillingInterval>(),
            IntervalCount = request.IntervalCount,
            FirstBillingDate = request.FirstBillingDate,
            Notes = request.Notes,
            Paused = null,
            Archived = null,
            CreatedAtUtc = timeProvider.GetUtcNow().UtcDateTime,
        };

        context.Subscriptions.Add(subscription);
        await context.SaveChangesAsync(cancellationToken);

        return (await Get(subscription.SubscriptionId, cancellationToken))!;
    }

    public async Task<ExistingSubscription?> Update(Guid id, UpdateSubscription request, CancellationToken cancellationToken = default)
    {
        var subscription = await context.Subscriptions
            .FirstOrDefaultAsync(s => s.SubscriptionId == id, cancellationToken);
        if (subscription is null)
        {
            return null;
        }

        // Re-validate the contact only when it actually changes, so an unrelated edit doesn't
        // 400 just because a still-linked contact was archived in the meantime.
        if (request.ContactId != subscription.ContactId)
        {
            await EnsureContactValid(request.ContactId, cancellationToken);
        }

        var currency = await NormalizeAndValidateCurrency(request.CurrencyCode, cancellationToken);
        ValidateDates(request.StartDate, request.EndDate);
        ValidateIntervalCount(request.IntervalCount);

        subscription.Name = request.Name.Trim();
        subscription.ExternalId = NormalizeExternalId(request.ExternalId);
        subscription.ContactId = request.ContactId;
        subscription.StartDate = request.StartDate;
        subscription.EndDate = request.EndDate;
        subscription.Amount = request.Amount;
        subscription.CurrencyCode = currency;
        subscription.Interval = request.Interval.Adapt<ContextBillingInterval>();
        subscription.IntervalCount = request.IntervalCount;
        subscription.FirstBillingDate = request.FirstBillingDate;
        subscription.Notes = request.Notes;

        // The lifecycle is ORDERED, not orthogonal: archiving retires a subscription that has already
        // stopped billing, so only an ended one can be archived. Validated against the request's
        // EndDate, not the stored one, so a single PUT may end and archive in one go.
        EnsureArchivable(subscription, request);

        // The service owns the timestamps. Setting true when already set preserves the original stamp;
        // setting false clears it. Pause stays orthogonal — a live subscription can be paused.
        var now = timeProvider.GetUtcNow().UtcDateTime;
        subscription.Paused = request.Paused ? subscription.Paused ?? now : null;
        subscription.Archived = request.Archived ? subscription.Archived ?? now : null;

        await context.SaveChangesAsync(cancellationToken);

        return (await Get(id, cancellationToken))!;
    }

    public async Task<bool> Delete(Guid id, CancellationToken cancellationToken = default)
    {
        var subscription = await context.Subscriptions
            .FirstOrDefaultAsync(s => s.SubscriptionId == id, cancellationToken);
        if (subscription is null)
        {
            return false;
        }

        context.Subscriptions.Remove(subscription);
        await context.SaveChangesAsync(cancellationToken);
        return true;
    }

    // ── Summary rollup (issue #293 follow-up) ─────────────────────────────────────────

    /// <summary>
    /// Server-computed page-header rollup: status/interval counts, the cadence-normalized multi-currency
    /// run-rate (blended into <paramref name="baseCurrency"/> where a rate exists), and the derived
    /// upcoming renewals. All figures are display-only — the billing anchor is never advanced or stored.
    /// When <paramref name="baseCurrency"/> is blank, the most common live billing currency is used.
    /// <para>
    /// "Live" means non-archived throughout: the status/interval <b>counts</b>, the <b>run-rate</b> and
    /// the <b>upcoming renewals</b> all treat a non-archived subscription as live. A lapsed term
    /// (<c>EndDate</c> ≤ today) is surfaced as its own derived <b>Ended</b> bucket — it supersedes
    /// Paused in the counts and, being no longer billing, contributes nothing to the run-rate or
    /// renewals — while still counting toward <see cref="SubscriptionSummary.Total"/> and the interval
    /// breakdown until it is archived.
    /// </para>
    /// </summary>
    public async Task<SubscriptionSummary> GetSummary(string? baseCurrency, CancellationToken cancellationToken = default)
    {
        var today = Today;

        // Read once per call, not once per use: the value a summary was computed with must be the
        // value the whole summary saw, so BuildRenewals takes them as parameters rather than reading
        // again.
        var settings = await systemSettingsLookup.GetSubscriptionSettingsAsync(cancellationToken);

        // Bounded fetch (issue #437 key 3). This read used to be UNBOUNDED, unlike its two siblings.
        //
        // The ordering is fully deterministic — archived last, newest first, primary key breaking ties
        // — because a Take() over a non-deterministic order silently returns a different thousand rows
        // per request. The PK tiebreak is a deliberate improvement on InsuranceService's otherwise
        // identical ordering, not a divergence to be "fixed" back: CreatedAtUtc alone ties on
        // bulk-seeded or imported rows.
        //
        // Truncation is SILENT, and its consequence is worse here than on the two siblings: they
        // truncate a set feeding aggregates only, whereas this one also builds UpcomingRenewals — a
        // list of NAMED subscriptions — from the same truncated set, so above the cap a renewal due
        // next week can be omitted. Accepted for v1: the seeded 1000 is far above any realistic
        // personal portfolio, archived rows are discarded first, and the setting's own description says
        // the renewals list is affected too. A completeness indicator is a follow-up.
        var subs = await context.Subscriptions
            .AsNoTracking()
            .OrderBy(s => s.Archived != null)
            .ThenByDescending(s => s.CreatedAtUtc)
            .ThenBy(s => s.SubscriptionId)
            .Select(s => new SummaryRow(
                s.SubscriptionId, s.Name, s.Amount, s.CurrencyCode, s.Interval, s.IntervalCount,
                s.EndDate, s.FirstBillingDate, s.Paused, s.Archived))
            .Take(settings.MaxSummarySubscriptions)
            .ToListAsync(cancellationToken);

        var (counts, byInterval) = CountSubscriptions(subs, today);

        // Live for run-rate purposes: non-archived, non-paused, not yet ended. Ended (EndDate ≤ today)
        // stops billing, matching the derived status and the DS run-rate.
        var billing = subs
            .Where(s => s.Archived is null && s.Paused is null && (s.EndDate is null || s.EndDate > today))
            .ToList();

        var runRate = await BuildRunRateAsync(billing, baseCurrency, cancellationToken);

        return new SubscriptionSummary
        {
            Total = counts.Active + counts.Paused + counts.Ended,
            CountsByStatus = counts,
            CountsByInterval = byInterval
                .OrderBy(kv => kv.Key)
                .Select(kv => new SubscriptionIntervalCount { Interval = kv.Key.Adapt<DtoBillingInterval>(), Count = kv.Value })
                .ToList(),
            RunRate = runRate,
            UpcomingRenewals = BuildRenewals(subs, today, settings.RenewalWindowDays, settings.MaxSummaryRenewals),
        };
    }

    /// <summary>
    /// Status and interval tallies in one pass. Ended is derived (EndDate ≤ today) and supersedes Paused;
    /// an archived subscription counts only as Archived and drops out of the interval breakdown.
    /// </summary>
    private static (SubscriptionStatusCounts Counts, Dictionary<ContextBillingInterval, int> ByInterval)
        CountSubscriptions(List<SummaryRow> subs, DateOnly today)
    {
        var counts = new SubscriptionStatusCounts();
        var byInterval = new Dictionary<ContextBillingInterval, int>();

        foreach (var s in subs)
        {
            if (s.Archived is not null)
            {
                counts.Archived++;
                continue;
            }

            if (s.EndDate is { } end && end <= today)
            {
                counts.Ended++;
            }
            else if (s.Paused is not null)
            {
                counts.Paused++;
            }
            else
            {
                counts.Active++;
            }

            byInterval[s.Interval] = byInterval.GetValueOrDefault(s.Interval) + 1;
        }

        return (counts, byInterval);
    }

    /// <summary>
    /// The cadence-normalized run-rate: per-currency rows, blended into the base currency where a rate
    /// exists, plus the top cost driver. Currencies with no rate to base are reported in
    /// <see cref="SubscriptionRunRate.ExcludedCurrencies"/> rather than silently folded in at 1:1.
    /// </summary>
    private async Task<SubscriptionRunRate> BuildRunRateAsync(
        List<SummaryRow> billing, string? baseCurrency, CancellationToken cancellationToken)
    {
        var rows = BuildCurrencyRows(billing);
        var baseCode = ResolveBaseCurrency(baseCurrency, rows);

        var runRate = new SubscriptionRunRate
        {
            BaseCurrency = baseCode,
            Rows = rows.Values.OrderByDescending(r => r.Yearly).ToList(),
        };

        // Batch the latest rate for each present currency → base; same currency is 1:1.
        var rates = await conversion.GetLatestRatesToAsync(baseCode, rows.Keys, cancellationToken);
        decimal? convertedMonthly = null;
        decimal? convertedYearly = null;
        foreach (var row in rows.Values)
        {
            if (!TryRateToBase(row.CurrencyCode, baseCode, rates, out var rate))
            {
                runRate.ExcludedCurrencies.Add(row.CurrencyCode);
                continue;
            }

            convertedMonthly = (convertedMonthly ?? 0m) + row.Monthly * rate;
            convertedYearly = (convertedYearly ?? 0m) + row.Yearly * rate;
        }

        // Run-rate figures are display-only estimates (cadence factors like 1/12 aren't exact in
        // decimal), so round to 2 places for a clean money figure.
        runRate.ConvertedMonthly = convertedMonthly is { } cm ? Round2(cm) : null;
        runRate.ConvertedYearly = convertedYearly is { } cy ? Round2(cy) : null;
        foreach (var row in runRate.Rows)
        {
            row.Monthly = Round2(row.Monthly);
            row.Yearly = Round2(row.Yearly);
        }

        runRate.TopDriver = FindTopDriver(billing, baseCode, rates);
        return runRate;
    }

    /// <summary>Cadence-normalized monthly/yearly totals bucketed per billing currency.</summary>
    private static Dictionary<string, SubscriptionRunRateRow> BuildCurrencyRows(List<SummaryRow> billing)
    {
        var rows = new Dictionary<string, SubscriptionRunRateRow>(StringComparer.Ordinal);
        foreach (var s in billing)
        {
            var (moFactor, yrFactor) = CadenceFactors(s.Interval);
            var count = Math.Max(1, s.IntervalCount);
            if (!rows.TryGetValue(s.CurrencyCode, out var row))
            {
                row = new SubscriptionRunRateRow { CurrencyCode = s.CurrencyCode };
                rows[s.CurrencyCode] = row;
            }

            row.Monthly += s.Amount * moFactor / count;
            row.Yearly += s.Amount * yrFactor / count;
            row.Count++;
        }

        return rows;
    }

    // Blank base → the most common billing currency; the currency-code tie-break keeps the pick
    // deterministic when two currencies have the same subscription count.
    private static string ResolveBaseCurrency(
        string? requested, Dictionary<string, SubscriptionRunRateRow> rows) =>
        string.IsNullOrWhiteSpace(requested)
            ? rows.Values.OrderByDescending(r => r.Count).ThenBy(r => r.CurrencyCode, StringComparer.Ordinal)
                .FirstOrDefault()?.CurrencyCode ?? "USD"
            : CurrencyValidationService.Normalize(requested);

    /// <summary>
    /// The biggest monthly-equivalent, compared in the base currency. Only base/convertible subscriptions
    /// are ranked, so the driver is commensurate with the blended run-rate total (which likewise excludes
    /// unconvertible currencies); ranking a raw foreign amount against a converted one could crown a cheap
    /// sub in a high-nominal currency. Iterates in a stable id order so ties resolve deterministically
    /// (first wins).
    /// </summary>
    private static SubscriptionRunRateDriver? FindTopDriver(
        List<SummaryRow> billing, string baseCode, IReadOnlyDictionary<string, decimal> rates)
    {
        SubscriptionRunRateDriver? top = null;
        var topRank = decimal.MinValue;

        foreach (var s in billing.OrderBy(s => s.SubscriptionId))
        {
            if (!TryRateToBase(s.CurrencyCode, baseCode, rates, out var rate))
            {
                continue;
            }

            var (moFactor, _) = CadenceFactors(s.Interval);
            var rank = s.Amount * moFactor / Math.Max(1, s.IntervalCount) * rate;
            if (top is null || rank > topRank)
            {
                topRank = rank;
                top = new SubscriptionRunRateDriver
                {
                    SubscriptionId = s.SubscriptionId,
                    Name = s.Name,
                    Amount = s.Amount,
                    CurrencyCode = s.CurrencyCode,
                    Interval = s.Interval.Adapt<DtoBillingInterval>(),
                };
            }
        }

        return top;
    }

    /// <summary>
    /// Derived next-billing dates falling inside the window; archived/paused/ended are skipped.
    ///
    /// <para>
    /// The window and the cap arrive as parameters rather than being read here (issue #437): the value
    /// a summary was computed with must be the value the whole summary saw, so <c>GetSummary</c> reads
    /// them once and passes them down.
    /// </para>
    /// </summary>
    private static List<SubscriptionRenewal> BuildRenewals(
        List<SummaryRow> subs, DateOnly today, int renewalWindowDays, int maxSummaryRenewals)
    {
        var renewals = new List<SubscriptionRenewal>();
        foreach (var s in subs)
        {
            if (s.Archived is not null || s.Paused is not null || (s.EndDate is { } end && end <= today))
            {
                continue;
            }

            if (NextBilling(s.FirstBillingDate, s.Interval, s.IntervalCount, s.EndDate, today) is not { } next)
            {
                continue;
            }

            var days = next.DayNumber - today.DayNumber;
            if (days < 0 || days > renewalWindowDays)
            {
                continue;
            }

            renewals.Add(new SubscriptionRenewal
            {
                SubscriptionId = s.SubscriptionId,
                Name = s.Name,
                Amount = s.Amount,
                CurrencyCode = s.CurrencyCode,
                Interval = s.Interval.Adapt<DtoBillingInterval>(),
                NextBillingDate = next,
                DaysUntil = days,
            });
        }

        return renewals
            .OrderBy(r => r.NextBillingDate)
            .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .Take(maxSummaryRenewals)
            .ToList();
    }

    /// <summary>Slim projection row for the summary computation (all subscriptions, one batch query).</summary>
    private sealed record SummaryRow(
        Guid SubscriptionId, string Name, decimal Amount, string CurrencyCode, ContextBillingInterval Interval,
        int IntervalCount, DateOnly? EndDate, DateOnly FirstBillingDate, DateTime? Paused, DateTime? Archived);

    private static decimal Round2(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);

    private static bool TryRateToBase(string currency, string baseCode, IReadOnlyDictionary<string, decimal> rates, out decimal rate)
    {
        if (string.Equals(currency, baseCode, StringComparison.Ordinal))
        {
            rate = 1m;
            return true;
        }

        return rates.TryGetValue(currency, out rate);
    }

    /// <summary>Cadence → (monthly, yearly) multiplier for a single billing (matches the design's factors).</summary>
    private static (decimal Monthly, decimal Yearly) CadenceFactors(ContextBillingInterval interval) => interval switch
    {
        ContextBillingInterval.Daily => (365.25m / 12m, 365.25m),
        ContextBillingInterval.Weekly => (52.1775m / 12m, 52.1775m),
        ContextBillingInterval.Yearly => (1m / 12m, 1m),
        _ => (1m, 12m), // Monthly
    };

    /// <summary>
    /// The next billing date on or after <paramref name="today"/>, derived from the anchor + interval ×
    /// <paramref name="intervalCount"/> (never stored). Returns null when it would fall past
    /// <paramref name="endDate"/>. Month/year steps are always measured from the original anchor
    /// (<see cref="DateOnly.AddMonths"/> from <paramref name="first"/>, never from a prior clamped
    /// result) so an end-of-month anchor (e.g. the 31st) recovers its day-of-month in longer months
    /// instead of permanently drifting to the 28th after a short month.
    /// </summary>
    private static DateOnly? NextBilling(DateOnly first, ContextBillingInterval interval, int intervalCount, DateOnly? endDate, DateOnly today)
    {
        var count = Math.Max(1, intervalCount);
        var cur = first;
        if (cur < today)
        {
            switch (interval)
            {
                case ContextBillingInterval.Daily:
                case ContextBillingInterval.Weekly:
                {
                    var stepDays = (interval == ContextBillingInterval.Weekly ? 7 : 1) * count;
                    var diff = today.DayNumber - cur.DayNumber;
                    var steps = (diff + stepDays - 1) / stepDays; // ceil to the first occurrence ≥ today
                    cur = cur.AddDays(steps * stepDays);
                    break;
                }
                case ContextBillingInterval.Yearly:
                {
                    var k = 0;
                    while (cur < today) cur = first.AddMonths(12 * count * ++k);
                    break;
                }
                default: // Monthly
                {
                    var k = 0;
                    while (cur < today) cur = first.AddMonths(count * ++k);
                    break;
                }
            }
        }

        return endDate is { } end && cur > end ? null : cur;
    }

    // ── Validation helpers ────────────────────────────────────────────────────────

    private async Task EnsureContactValid(Guid? contactId, CancellationToken cancellationToken = default)
    {
        if (contactId is not { } id)
        {
            return;
        }

        var refs = await contactLookup.ResolveRefsAsync([id], cancellationToken);
        if (!refs.TryGetValue(id, out var contact) || contact.Archived is not null)
        {
            throw new DomainValidationException(
                $"ContactId {id} does not reference an existing, non-archived contact.");
        }
    }

    private async Task<string> NormalizeAndValidateCurrency(string currencyCode, CancellationToken cancellationToken = default)
    {
        await CurrencyValidationService.EnsureSupportedAndActive(
            context, currencyCode, nameof(NewSubscription.CurrencyCode), cancellationToken);
        return CurrencyValidationService.Normalize(currencyCode);
    }

    private static void ValidateDates(DateOnly startDate, DateOnly? endDate)
    {
        if (endDate is { } end && end < startDate)
        {
            throw new DomainValidationException("EndDate must be on or after StartDate.");
        }
    }

    /// <summary>
    /// Archiving requires an ended term (<c>EndDate</c> on or before today) — the lifecycle is ordered,
    /// so Archived implies Ended and the status is a single state rather than a stack of flags.
    ///
    /// <para>
    /// Only the <b>transition</b> into archived is checked. A row archived before this rule existed
    /// stays editable and restorable: re-validating it on every save would strand it, since the only
    /// way out is a PUT that carries <c>Archived = true</c> right up until the one that clears it.
    /// Restoring is always allowed.
    /// </para>
    /// </summary>
    private void EnsureArchivable(Subscription subscription, UpdateSubscription request)
    {
        if (!request.Archived || subscription.Archived is not null)
        {
            return;
        }

        if (request.EndDate is not { } end || end > Today)
        {
            throw new DomainValidationException(
                "A subscription can only be archived once it has ended. Set an EndDate on or before today first.");
        }
    }

    private static void ValidateIntervalCount(int intervalCount)
    {
        if (intervalCount < 1)
        {
            throw new DomainValidationException("IntervalCount must be at least 1.");
        }
    }

    private static string? NormalizeExternalId(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    // ── Mapping ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The detail projection. <paramref name="today"/> is passed in rather than read here so the whole
    /// response is derived against one instant — the same reason <c>GetSummary</c> passes its own down.
    /// </summary>
    private static ExistingSubscription ToDto(Subscription s, ContactRef? contact, DateOnly today) => new()
    {
        SubscriptionId = s.SubscriptionId,
        Name = s.Name,
        ExternalId = s.ExternalId,
        Contact = ToContactReference(contact),
        StartDate = s.StartDate,
        EndDate = s.EndDate,
        Amount = s.Amount,
        CurrencyCode = s.CurrencyCode,
        Interval = s.Interval.Adapt<DtoBillingInterval>(),
        IntervalCount = s.IntervalCount,
        FirstBillingDate = s.FirstBillingDate,
        Notes = s.Notes,
        Paused = s.Paused,
        Archived = s.Archived,
        // Same suppression BuildRenewals applies: a paused, ended or archived subscription has no next
        // charge. Deriving it here — rather than leaving each client to do it — is what keeps the tile
        // on the record card and the header's upcoming-renewals rollup from ever disagreeing.
        NextBillingDate = HasNextBilling(s, today)
            ? NextBilling(s.FirstBillingDate, s.Interval, s.IntervalCount, s.EndDate, today)
            : null,
        CreatedAtUtc = s.CreatedAtUtc,
    };

    /// <summary>Whether a subscription is still billing at all — the precondition for a next charge.</summary>
    private static bool HasNextBilling(Subscription s, DateOnly today) =>
        s.Archived is null && s.Paused is null && !(s.EndDate is { } end && end <= today);

    private static SubscriptionListItem ToListItem(Subscription s, ContactRef? contact) => new()
    {
        SubscriptionId = s.SubscriptionId,
        Name = s.Name,
        ExternalId = s.ExternalId,
        Contact = ToContactReference(contact),
        StartDate = s.StartDate,
        EndDate = s.EndDate,
        Amount = s.Amount,
        CurrencyCode = s.CurrencyCode,
        Interval = s.Interval.Adapt<DtoBillingInterval>(),
        IntervalCount = s.IntervalCount,
        FirstBillingDate = s.FirstBillingDate,
        Paused = s.Paused,
        Archived = s.Archived,
    };

    private static SubscriptionContactReference? ToContactReference(ContactRef? contact) =>
        contact is null
            ? null
            : new SubscriptionContactReference
            {
                ContactId = contact.ContactId,
                Name = contact.Name,
                Type = contact.Type,
            };

    // Batch-resolve the distinct, non-null contact ids for a set of subscription rows in one call.
    private async Task<IReadOnlyDictionary<Guid, ContactRef>> ResolveContactRefs(
        IEnumerable<Guid?> contactIds, CancellationToken cancellationToken)
    {
        var ids = contactIds
            .Where(id => id is not null)
            .Select(id => id!.Value)
            .Distinct()
            .ToList();

        return ids.Count == 0
            ? new Dictionary<Guid, ContactRef>()
            : await contactLookup.ResolveRefsAsync(ids, cancellationToken);
    }

    private static ContactRef? LookupRef(IReadOnlyDictionary<Guid, ContactRef> refs, Guid? contactId) =>
        contactId is { } id && refs.TryGetValue(id, out var contact) ? contact : null;
}
