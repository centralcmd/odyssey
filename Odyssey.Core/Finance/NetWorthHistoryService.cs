using Microsoft.EntityFrameworkCore;
using Odyssey.Context;
using Odyssey.Dtos.Finance;
using AccountType = Odyssey.Context.AccountType;

namespace Odyssey.Core.Finance;

/// <summary>
/// Reconstructs a net-worth-over-time series from the data Odyssey already stores — transactions,
/// account estimates, property estimates (issue #214) and exchange rates — with every point derived as
/// of that point's own instant (issue #90).
/// </summary>
/// <remarks>
/// <para>
/// <b>Reconstruction, not snapshots.</b> There is no <c>NetWorthSnapshots</c> table and no scheduled
/// job, deliberately: reconstruction is correct <i>retroactively</i>, so a back-dated transaction, a
/// corrected estimate or a fixed exchange rate repairs the history it should have produced. A snapshot
/// would preserve the mistake, and a bad benchmark would mean memoisation rather than a change of
/// source of truth.
/// </para>
/// <para>
/// <b>Seven queries, constant in point count:</b> the currency check, the accounts, the properties,
/// the property estimates, one bucketed transaction aggregate, the account estimates, and the rate
/// timeline over the account and property currencies together. Everything after that is a fold in
/// memory. With <c>includeProperties</c> false it is <b>five</b>, and no query touches
/// <c>Properties</c> or <c>PropertyEstimates</c> at all (issue #214 §7.3).
/// </para>
/// <para>
/// <b>A property is a member only while held</b> (<see cref="PropertyMembership"/>), valued by the
/// estimate in force at each point's own bound — never backfilled from a later estimate. A held
/// property with no estimate yet contributes 0 and is counted as <i>unvalued</i>, which is disclosure
/// and does not make the point partial (issue #214 D1).
/// </para>
/// <para>
/// <b>Read skew is accepted and stated.</b> The reads are untransacted and <c>EnableRetryOnFailure</c>
/// is configured, so a concurrent write can yield a point set that never existed as a whole. A
/// snapshot read would need <c>CreateExecutionStrategy().ExecuteAsync</c>; it is not done here,
/// because the window is milliseconds, the artifact clears on one refresh, and <c>/totals</c> has the
/// same property today.
/// </para>
/// <para>
/// <b>The <see cref="TimeProvider"/> is not optional in practice.</b> The final point's bound is
/// <c>now</c> and it has to be the <i>same</i> <c>now</c> <see cref="AccountTotalsService"/> resolves,
/// or the two endpoints disagree at exactly the point issue #90 AC2 requires to be equal — and a test
/// comparing them would be flaky rather than a contract.
/// </para>
/// </remarks>
public class NetWorthHistoryService(
    OdysseyContext context,
    CurrencyConversionService conversionService,
    TimeProvider? injectedTimeProvider = null)
{
    private readonly TimeProvider timeProvider = injectedTimeProvider ?? TimeProvider.System;

    /// <param name="includeProperties">
    /// Whether property value is part of the series. Deliberately has no default (issue #214 §7.3): a
    /// call site must state its choice, so none obtains property data by omission.
    /// </param>
    public async Task<NetWorthHistory> ComputeAsync(
        NetWorthHistoryQuery query,
        bool includeProperties,
        CancellationToken cancellationToken = default)
    {
        var main = CurrencyValidationService.Normalize(
            string.IsNullOrWhiteSpace(query.MainCurrency) ? DefaultMainCurrency : query.MainCurrency);

        // 1 — validate the currency. Same rule as /totals, so the two endpoints agree on what a
        // main currency is, and ?mainCurrency=ZZZ cannot be used as a full-roster oracle on either.
        await CurrencyValidationService.EnsureSupportedAndActive(context, main, "mainCurrency", cancellationToken);

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var today = DateOnly.FromDateTime(now);
        var interval = query.EffectiveInterval;
        var requested = query.ResolveWindow(today);

        // 2 — the accounts, and (3) the properties with their estimates, all BEFORE the grid. The grid
        // needs Account.Opened and each property's first contribution to clamp its leading empty
        // periods away, so it can be neither pure nor first.
        //
        // MEMBERSHIP IS THE OPEN/CLOSED TERM, AND IT IS EVALUATED PER POINT (issue #99). Both ends of
        // the term are carried into the fold rather than applied here, because a series-wide filter is
        // retroactive: it would drop an account from months it was demonstrably open and funded. Only
        // `Opened < now` is applied here, and purely as a prefilter — it can never exclude an account
        // any slot would have kept, since every bound is at or before `now`.
        //
        // Archived is NOT read at all. It is the app's generic declutter verb, shared with photos,
        // journal entries, tags and budgets, and it is a reversible toggle with no transition history
        // (AccountService.ApplyArchiveTransition clears it on unarchive), so it could not be evaluated
        // per point even if it belonged in a valuation — which it does not.
        var accounts = await context.Accounts
            .Where(account => account.Opened < now)
            .Select(account => new AccountRow(
                account.AccountId,
                account.Name,
                account.CurrencyCode,
                account.AccountType,
                account.Opened,
                account.Closed))
            .ToListAsync(cancellationToken);

        // 3 — the properties (issue #214), skipped entirely — no query issued — when the caller may
        // not see property estimates. Only `AcquiredDate < now` is applied here, as a prefilter the
        // same way `Opened < now` is for accounts; disposal is evaluated per point by
        // PropertyMembership. Their estimates are bounded by `now` rather than by the final bound,
        // which does not exist until the grid is built: every bound is at or before `now` and the
        // cursor only advances past rows before the bound, so the extra rows never take effect.
        var properties = includeProperties
            ? await context.Properties
                .AsNoTracking()
                .Where(property => property.AcquiredDate == null || property.AcquiredDate < now)
                .Select(property => new PropertyRow(
                    property.PropertyId,
                    property.Name,
                    property.CurrencyCode,
                    property.AcquiredDate,
                    property.DisposedDate))
                .ToListAsync(cancellationToken)
            : [];
        var propertyEstimates = await LoadPropertyEstimatesAsync(
            properties.Select(property => property.PropertyId).ToList(), now, cancellationToken);

        // A property moves the grid start only from its first contribution — acquired AND valued —
        // and only if that falls before now and before its own disposal (issue #214 V11). A held but
        // never-estimated property is still folded (it is counted as unvalued) but never makes a
        // series non-empty on its own.
        var firstContributions = properties
            .Select(property => PropertyMembership.FirstContribution(
                property.AcquiredDate,
                property.DisposedDate,
                propertyEstimates.TryGetValue(property.PropertyId, out var timeline) ? timeline[0].EffectiveFrom : null,
                now))
            .OfType<DateTime>()
            .ToList();

        if (accounts.Count == 0 && firstContributions.Count == 0)
        {
            return Empty(main, interval, requested, NetWorthEmptyReason.NoAccounts, includeProperties);
        }

        // Leading empties are dropped rather than plotted as zero: a flat run at 0 before the first
        // member existed is a claim about net worth, and a false one.
        var earliestMember = accounts.Select(account => account.Opened).Concat(firstContributions).Min();
        var earliestDate = DateOnly.FromDateTime(earliestMember);
        var effectiveFrom = NetWorthPeriods.StartOfPeriod(
            earliestDate > requested.From ? earliestDate : requested.From, interval);

        if (effectiveFrom > requested.To)
        {
            // The window ends before any member existed. The caller's input was valid, so this is a
            // 200 with no points — never an inverted window, and never a 400.
            return Empty(main, interval, requested, NetWorthEmptyReason.WindowBeforeFirstAccount, includeProperties);
        }

        // 4 — the grid.
        var grid = BuildGrid(effectiveFrom, requested.To, interval, now);
        if (grid.Count == 0)
        {
            return Empty(main, interval, requested, NetWorthEmptyReason.NotBuilt, includeProperties);
        }

        var finalBound = grid[^1].Bound;
        var accountIds = accounts.Select(account => account.AccountId).ToList();

        // 5, 6, 7 — the account timelines and the rates, each bounded by the window rather than
        // trimmed after the fact. One rate timeline covers the account and property currencies.
        var buckets = await LoadTransactionBucketsAsync(accountIds, interval, finalBound, cancellationToken);
        var estimates = await LoadEstimatesAsync(accountIds, finalBound, cancellationToken);
        var rates = await conversionService.GetRateTimelineToAsync(
            main,
            accounts.Select(account => account.CurrencyCode)
                .Concat(properties.Select(property => property.CurrencyCode)),
            grid[0].Bound,
            finalBound,
            cancellationToken);

        // 8 — the fold.
        var points = new List<NetWorthHistoryPoint>(grid.Count);
        var unconverted = new Dictionary<Guid, UnconvertedAccount>();
        var unconvertedProperties = new Dictionary<Guid, UnconvertedProperty>();
        var cursors = accountIds.ToDictionary(id => id, _ => new AccountCursor());
        var propertyCursors = properties.ToDictionary(property => property.PropertyId, _ => new PropertyCursor());

        // Whether ANY member was live at ANY bound — an account inside its term, or a property held
        // with an estimate in force. It separates the two ways a series can come out wholly empty:
        // nothing could be converted, or nothing was live to convert. Closing accounts made the second
        // reachable, and reporting it as the first would tell a reader their rates are missing when
        // their accounts are simply all closed. A held, never-estimated property is NOT live: it had no
        // value to convert, so it must not turn "nothing here" into "a rate is missing".
        var liveAtSomePoint = false;

        // One rate cursor per CURRENCY, not per account: many accounts share a currency, and the rate
        // in force depends only on the currency and the bound. Advancing them once per slot makes the
        // whole conversion O(currencies) per point instead of re-walking a timeline for every
        // (slot, account) pair — the same monotonic-cursor trick AccountCursor uses, and safe for the
        // same reason: the grid's bounds only ever increase.
        var rateCursors = rates.ToDictionary(pair => pair.Key, pair => new RateCursor(pair.Value), StringComparer.Ordinal);

        foreach (var slot in grid)
        {
            foreach (var cursor in rateCursors.Values)
            {
                cursor.AdvanceTo(slot.Bound);
            }

            var totalAssets = 0m;
            var totalLiabilities = 0m;
            var unconvertedCount = 0;
            var revaluedCount = 0;
            var contributingCount = 0;

            foreach (var account in accounts)
            {
                var cursor = cursors[account.AccountId];

                // V6 — an account outside its term at this bound contributes 0, and is neither partial
                // nor contributing. It is not a defect that it has no figure yet, or no longer has one.
                if (account.Opened >= slot.Bound)
                {
                    continue;
                }

                // The mirror of the Opened rule, with the bounds exclusive throughout (issue #90 on
                // datetime(6) truncation): an account closed at exactly the measuring instant is
                // already gone by it, exactly as one opened at it has not yet arrived.
                if (account.Closed is { } closed && closed <= slot.Bound)
                {
                    continue;
                }

                liveAtSomePoint = true;

                var balance = cursor.AdvanceBalance(buckets.GetValueOrDefault(account.AccountId), slot.Bound);
                var estimate = cursor.AdvanceEstimate(estimates.GetValueOrDefault(account.AccountId), slot.Bound);

                if (cursor.TakeRevaluation())
                {
                    revaluedCount++;
                }

                // Replace policy (issue #182 §9): the in-force estimate wins over the transaction
                // balance. Both are in the account currency, so they convert identically.
                var value = estimate ?? balance;

                var converted = Convert(value, account.CurrencyCode, main, rateCursors);
                if (converted is null)
                {
                    unconvertedCount++;
                    unconverted.TryAdd(account.AccountId, new UnconvertedAccount
                    {
                        AccountId = account.AccountId,
                        Name = account.Name,
                        CurrencyCode = account.CurrencyCode,
                    });
                    continue;
                }

                contributingCount++;

                if (AccountClassification.IsAsset(account.AccountType))
                {
                    totalAssets += converted.Value;
                }
                else if (AccountClassification.IsLiability(account.AccountType))
                {
                    // Signed, like AccountTotalsService: negating keeps a normal debt a positive
                    // liability while letting a credit balance reduce total liabilities.
                    totalLiabilities += -converted.Value;
                }
                // AccountType.Unknown (0) is Unclassified, so it is excluded — as it is from the totals.
            }

            var propertyValue = 0m;
            var propertyContributing = 0;
            var propertyUnconverted = 0;
            var propertyRevalued = 0;
            var propertyUnvalued = 0;

            foreach (var property in properties)
            {
                // V6 — before acquisition or from disposal on, a property is not a member: counted
                // nowhere, and its cursor is not touched, so neither event reads as a revaluation.
                if (!PropertyMembership.IsHeldAt(property.AcquiredDate, property.DisposedDate, slot.Bound))
                {
                    continue;
                }

                var cursor = propertyCursors[property.PropertyId];
                var estimate = cursor.AdvanceEstimate(propertyEstimates.GetValueOrDefault(property.PropertyId), slot.Bound);

                if (cursor.TakeRevaluation())
                {
                    propertyRevalued++;
                }

                // V4 — held but not yet valued: contributes 0, disclosed, not partial, not live.
                if (estimate is not { } value)
                {
                    propertyUnvalued++;
                    continue;
                }

                liveAtSomePoint = true;

                var converted = Convert(value, property.CurrencyCode, main, rateCursors);
                if (converted is null)
                {
                    propertyUnconverted++;
                    unconvertedProperties.TryAdd(property.PropertyId, new UnconvertedProperty
                    {
                        PropertyId = property.PropertyId,
                        Name = property.Name,
                        CurrencyCode = property.CurrencyCode,
                    });
                    continue;
                }

                propertyContributing++;
                propertyValue += converted.Value;
            }

            // An asset line of its own (issue #214 D2, D6): never a liability.
            totalAssets += propertyValue;

            points.Add(new NetWorthHistoryPoint
            {
                Date = slot.Date,
                TotalAssets = totalAssets,
                TotalLiabilities = totalLiabilities,
                NetWorth = totalAssets - totalLiabilities,
                UnconvertedAccountCount = unconvertedCount,
                RevaluedAccountCount = revaluedCount,
                ContributingAccountCount = contributingCount,
                PropertyValue = includeProperties ? propertyValue : null,
                ContributingPropertyCount = propertyContributing,
                UnconvertedPropertyCount = propertyUnconverted,
                RevaluedPropertyCount = propertyRevalued,
                UnvaluedPropertyCount = propertyUnvalued,
            });
        }

        if (points.TrueForAll(point => point.ContributingAccountCount == 0 && point.ContributingPropertyCount == 0))
        {
            // Every period was wholly unconvertible, or had no live member. Either
            // way a line of zeroes would read as "you were worth nothing", which is the opposite of
            // "we could not tell" — and of "there was nothing here to tell you about".
            var reason = liveAtSomePoint
                ? NetWorthEmptyReason.NothingConvertible
                : NetWorthEmptyReason.WindowAfterAllAccountsClosed;

            return Empty(main, interval, (effectiveFrom, requested.To), reason, includeProperties)
                with
                {
                    UnconvertedAccounts = [.. unconverted.Values],
                    UnconvertedProperties = [.. unconvertedProperties.Values],
                };
        }

        return new NetWorthHistory
        {
            MainCurrencyCode = main,
            Interval = interval,
            From = effectiveFrom,
            To = requested.To,
            EmptyReason = null,
            Points = points,
            UnconvertedAccounts = [.. unconverted.Values],
            PropertiesIncluded = includeProperties,
            UnconvertedProperties = [.. unconvertedProperties.Values],
        };
    }

    // ── The grid ──────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The points of the window, oldest first. A point <b>covers</b>
    /// <c>[periodStart, nextPeriodStart)</c> and is dated at its exclusive upper bound.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The bound is exclusive on purpose. The columns are <c>datetime(6)</c>, so MySqlConnector
    /// truncates a 7-digit tick parameter to microseconds while EF InMemory compares full ticks — an
    /// inclusive <c>23:59:59.9999999</c> lets a Core-tier test pass while MariaDB quietly differs.
    /// </para>
    /// <para>
    /// The last bound is additionally clamped to the end of the requested <c>to</c> day and to
    /// <c>now</c>, so a series ending today ends at the same instant <c>/totals</c> measures.
    /// </para>
    /// </remarks>
    private static List<GridSlot> BuildGrid(DateOnly from, DateOnly to, NetWorthInterval interval, DateTime now)
    {
        var slots = new List<GridSlot>();
        var periodStart = NetWorthPeriods.StartOfPeriod(from, interval);
        var lastPeriodStart = NetWorthPeriods.StartOfPeriod(to, interval);

        // The day after `to`, which is where a window ending mid-period stops.
        var windowEnd = to == DateOnly.MaxValue
            ? DateTime.MaxValue
            : to.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        if (windowEnd > now)
        {
            windowEnd = now;
        }

        while (periodStart <= lastPeriodStart)
        {
            var next = NetWorthPeriods.AddSaturating(periodStart, interval, 1);
            var bound = next == periodStart
                ? DateTime.MaxValue
                : next.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);

            if (bound > windowEnd)
            {
                bound = windowEnd;
            }

            slots.Add(new GridSlot(DateOnly.FromDateTime(bound), bound));

            if (next == periodStart)
            {
                break; // saturated at DateOnly.MaxValue; there is no next period.
            }

            periodStart = next;
        }

        return slots;
    }

    // ── The three timelines ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// Per-account transaction sums, bucketed, in one aggregate query.
    /// </summary>
    /// <remarks>
    /// <para>
    /// No <c>EF.Functions.*</c> in the bucket key — <c>DateDiffDay</c>, <c>DATE_FORMAT</c> and
    /// <c>YEARWEEK</c> are relational-only and would throw at the Core tier, the same class of hazard
    /// as CLAUDE.md's <c>ExecuteDeleteAsync</c> rule. Hence day-resolution buckets for <c>Weekly</c>,
    /// rolled up in memory.
    /// </para>
    /// <para>
    /// There is no lower bound, by design: everything before the first point collapses into an
    /// opening seed, so the scan cost is invariant in <c>from</c>. Every bucket boundary is at least
    /// as fine as every point boundary, so a bucket never straddles a point's bound — except the last
    /// one, whose rows are already cut by the <c>TimeStamp &lt; toBound</c> predicate.
    /// </para>
    /// </remarks>
    private async Task<Dictionary<Guid, List<Bucket>>> LoadTransactionBucketsAsync(
        IReadOnlyCollection<Guid> accountIds,
        NetWorthInterval interval,
        DateTime toBound,
        CancellationToken cancellationToken)
    {
        if (accountIds.Count == 0)
        {
            return [];
        }

        var query = context.Transactions
            .AsNoTracking()
            .Where(transaction => accountIds.Contains(transaction.AccountId) && transaction.TimeStamp < toBound);

        List<RawBucket> rows;
        switch (interval)
        {
            case NetWorthInterval.Daily:
            case NetWorthInterval.Weekly:
                rows = await query
                    .GroupBy(transaction => new
                    {
                        transaction.AccountId,
                        transaction.TimeStamp.Year,
                        transaction.TimeStamp.Month,
                        transaction.TimeStamp.Day,
                    })
                    .Select(group => new RawBucket(
                        group.Key.AccountId,
                        group.Key.Year,
                        group.Key.Month,
                        group.Key.Day,
                        group.Sum(transaction => transaction.Amount)))
                    .ToListAsync(cancellationToken);
                break;

            case NetWorthInterval.Monthly:
            case NetWorthInterval.Quarterly:
                rows = await query
                    .GroupBy(transaction => new
                    {
                        transaction.AccountId,
                        transaction.TimeStamp.Year,
                        transaction.TimeStamp.Month,
                    })
                    .Select(group => new RawBucket(
                        group.Key.AccountId,
                        group.Key.Year,
                        group.Key.Month,
                        1,
                        group.Sum(transaction => transaction.Amount)))
                    .ToListAsync(cancellationToken);
                break;

            case NetWorthInterval.Yearly:
                rows = await query
                    .GroupBy(transaction => new { transaction.AccountId, transaction.TimeStamp.Year })
                    .Select(group => new RawBucket(
                        group.Key.AccountId,
                        group.Key.Year,
                        1,
                        1,
                        group.Sum(transaction => transaction.Amount)))
                    .ToListAsync(cancellationToken);
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(interval), interval, "Unhandled net-worth interval.");
        }

        return rows
            .GroupBy(row => row.AccountId)
            .ToDictionary(
                group => group.Key,
                group => group
                    .Select(row => new Bucket(
                        new DateTime(row.Year, row.Month, row.Day, 0, 0, 0, DateTimeKind.Utc),
                        row.Sum))
                    .OrderBy(bucket => bucket.Start)
                    .ToList());
    }

    /// <summary>
    /// Every estimate that could be in force anywhere in the window, ordered so the fold can walk it
    /// once per account.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An estimate entered today does not rewrite the past: it applies from its own
    /// <c>EffectiveFrom</c> forward, which is what makes the step at that instant a real revaluation
    /// rather than a correction to be smoothed away.
    /// </para>
    /// <para>
    /// The bound here is the window's, not the account's term: an estimate effective after an account
    /// closed is loaded and then never reached, because the fold skips the account at every bound at
    /// or after <c>Closed</c>. Filtering it out per account would be a second place for the term rule
    /// to live, and the one that is silent when the two disagree.
    /// </para>
    /// </remarks>
    private async Task<Dictionary<Guid, List<EstimatePoint>>> LoadEstimatesAsync(
        IReadOnlyCollection<Guid> accountIds,
        DateTime toBound,
        CancellationToken cancellationToken)
    {
        if (accountIds.Count == 0)
        {
            return [];
        }

        var rows = await context.AccountEstimates
            .AsNoTracking()
            .Where(estimate => accountIds.Contains(estimate.AccountId) && estimate.EffectiveFrom < toBound)
            .Select(estimate => new EstimateRow(
                estimate.AccountId,
                estimate.AccountEstimateId,
                estimate.EffectiveFrom,
                estimate.CreatedAtUtc,
                estimate.Value))
            .ToListAsync(cancellationToken);

        return rows
            .GroupBy(row => row.AccountId)
            .ToDictionary(
                group => group.Key,
                group => group
                    // The supersession order MostEffective() encodes, ascending: the last entry
                    // before a bound is the one in force at it.
                    .OrderByEffectiveAscending()
                    .Select(row => new EstimatePoint(row.EffectiveFrom, row.Value, row.AccountEstimateId))
                    .ToList());
    }

    /// <summary>
    /// Every property estimate dated before <paramref name="now"/>, per property, in the ascending
    /// supersession order — one query, skipped when there are no properties (issue #214).
    /// </summary>
    /// <remarks>
    /// Loaded before the grid exists, so bounded by <c>now</c> rather than the final bound. An estimate
    /// dated before acquisition is kept (it takes effect at the first held bound, V5); one dated on or
    /// after disposal is loaded and never reached, because the fold skips the property from disposal on.
    /// </remarks>
    private async Task<Dictionary<Guid, List<EstimatePoint>>> LoadPropertyEstimatesAsync(
        IReadOnlyCollection<Guid> propertyIds,
        DateTime now,
        CancellationToken cancellationToken)
    {
        if (propertyIds.Count == 0)
        {
            return [];
        }

        var rows = await context.PropertyEstimates
            .AsNoTracking()
            .Where(estimate => propertyIds.Contains(estimate.PropertyId) && estimate.EffectiveFrom < now)
            .Select(estimate => new PropertyEstimateRow(
                estimate.PropertyId,
                estimate.PropertyEstimateId,
                estimate.EffectiveFrom,
                estimate.CreatedAtUtc,
                estimate.Value))
            .ToListAsync(cancellationToken);

        return rows
            .GroupBy(row => row.PropertyId)
            .ToDictionary(
                group => group.Key,
                group => group
                    .OrderByEffectiveAscending()
                    .Select(row => new EstimatePoint(row.EffectiveFrom, row.Value, row.PropertyEstimateId))
                    .ToList());
    }

    // ── Conversion ────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// <paramref name="value"/> in the main currency at the rate each cursor has been advanced to, or
    /// <c>null</c> when no direct rate was in force then.
    /// </summary>
    /// <remarks>
    /// Never a substituted or a later rate: converting a 2019 balance at today's rate is exactly the
    /// class of fabrication this feature exists to remove, and understating the point while saying so
    /// is the only honest alternative. The caller advances every cursor to the slot's bound before the
    /// account loop, so this is a dictionary lookup rather than a walk.
    /// </remarks>
    private static decimal? Convert(
        decimal value,
        string currencyCode,
        string main,
        IReadOnlyDictionary<string, RateCursor> rateCursors)
    {
        var code = CurrencyValidationService.Normalize(currencyCode);
        if (string.Equals(code, main, StringComparison.Ordinal))
        {
            return value; // same currency → 1:1, no rate row required.
        }

        if (!rateCursors.TryGetValue(code, out var cursor) || cursor.Rate is not { } rate)
        {
            return null;
        }

        return value * rate;
    }

    /// <remarks>
    /// Takes <paramref name="includeProperties"/> rather than leaving the flag at its default: the
    /// client picks its empty copy by <c>(EmptyReason, PropertiesIncluded)</c>, so an early return that
    /// silently reported <c>false</c> would describe an entitled caller's empty chart as accounts-only.
    /// </remarks>
    private static NetWorthHistory Empty(
        string main,
        NetWorthInterval interval,
        (DateOnly From, DateOnly To) window,
        NetWorthEmptyReason reason,
        bool includeProperties) =>
        new()
        {
            MainCurrencyCode = main,
            Interval = interval,
            From = window.From,
            To = window.To,
            EmptyReason = reason,
            Points = [],
            UnconvertedAccounts = [],
            PropertiesIncluded = includeProperties,
            UnconvertedProperties = [],
        };


    private const string DefaultMainCurrency = "NOK";

    // ── Fold state ────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// One member's position in its estimate timeline. The grid's bounds ascend, so each timeline is
    /// walked once across the whole series rather than re-scanned per point — which is what keeps the
    /// fold linear in (members × points) instead of quadratic in rows. Shared by accounts and
    /// properties, so "in-force estimate changed" means one thing for both (issue #214 V10).
    /// </summary>
    private abstract class EstimateCursor
    {
        private int estimateIndex;
        private decimal? currentEstimate;
        private Guid? currentEstimateId;
        private bool revaluedSinceLastPoint;
        private bool seenAnyPoint;

        public decimal? AdvanceEstimate(List<EstimatePoint>? estimates, DateTime bound)
        {
            if (estimates is not null)
            {
                while (estimateIndex < estimates.Count && estimates[estimateIndex].EffectiveFrom < bound)
                {
                    var next = estimates[estimateIndex];
                    if (currentEstimateId != next.Id)
                    {
                        // A DIFFERENT estimate is now in force. Marked only from the second point
                        // onwards: a step needs two points to be visible, and marking the first would
                        // flag every account that has ever had an estimate.
                        revaluedSinceLastPoint = seenAnyPoint;
                        currentEstimateId = next.Id;
                    }

                    currentEstimate = next.Value;
                    estimateIndex++;
                }
            }

            seenAnyPoint = true;
            return currentEstimate;
        }

        public bool TakeRevaluation()
        {
            var revalued = revaluedSinceLastPoint;
            revaluedSinceLastPoint = false;
            return revalued;
        }
    }

    /// <summary>One account's position in its transaction and estimate timelines.</summary>
    private sealed class AccountCursor : EstimateCursor
    {
        private int bucketIndex;
        private decimal runningBalance;

        public decimal AdvanceBalance(List<Bucket>? buckets, DateTime bound)
        {
            if (buckets is not null)
            {
                while (bucketIndex < buckets.Count && buckets[bucketIndex].Start < bound)
                {
                    runningBalance += buckets[bucketIndex].Sum;
                    bucketIndex++;
                }
            }

            return runningBalance;
        }
    }

    /// <summary>
    /// One property's position in its estimate timeline. Advanced only at bounds where the property is
    /// held, so its first held point is never a revaluation — even when estimates dated before
    /// acquisition take effect there — and acquisition or disposal never is.
    /// </summary>
    private sealed class PropertyCursor : EstimateCursor;

    /// <summary>
    /// One currency's position in its rate timeline. Advanced once per grid slot, in the same
    /// ascending-bound order <see cref="AccountCursor"/> relies on, so the whole series costs one walk
    /// of the timeline rather than one per (slot, account) pair.
    /// </summary>
    private sealed class RateCursor(IReadOnlyList<CurrencyConversionService.RatePoint> timeline)
    {
        private int index;

        /// <summary>The rate in force at the last bound advanced to, or null if none was yet.</summary>
        public decimal? Rate { get; private set; }

        public void AdvanceTo(DateTime bound)
        {
            while (index < timeline.Count && timeline[index].AsOf < bound)
            {
                Rate = timeline[index].Rate;
                index++;
            }
        }
    }

    private readonly record struct GridSlot(DateOnly Date, DateTime Bound);

    private readonly record struct Bucket(DateTime Start, decimal Sum);

    private readonly record struct EstimatePoint(DateTime EffectiveFrom, decimal Value, Guid Id);

    private sealed record AccountRow(Guid AccountId, string Name, string CurrencyCode, AccountType AccountType, DateTime Opened, DateTime? Closed);

    private sealed record RawBucket(Guid AccountId, int Year, int Month, int Day, decimal Sum);

    private sealed record EstimateRow(Guid AccountId, Guid AccountEstimateId, DateTime EffectiveFrom, DateTime CreatedAtUtc, decimal Value)
        : IEffectiveDated;

    private sealed record PropertyRow(Guid PropertyId, string Name, string CurrencyCode, DateTime? AcquiredDate, DateTime? DisposedDate);

    private sealed record PropertyEstimateRow(Guid PropertyId, Guid PropertyEstimateId, DateTime EffectiveFrom, DateTime CreatedAtUtc, decimal Value)
        : IEffectiveDated;
}
