using Microsoft.EntityFrameworkCore;
using Odyssey.Context;
using Odyssey.Dtos.Finance;
using AccountType = Odyssey.Context.AccountType;

namespace Odyssey.Core.Finance;

/// <summary>
/// Reconstructs a net-worth-over-time series from the data Odyssey already stores — transactions,
/// account estimates and exchange rates — with every point derived as of that point's own instant
/// (issue #90).
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
/// <b>Five queries, constant in point count:</b> the currency check, the accounts, one bucketed
/// transaction aggregate, the estimates, and the rate timeline. Everything after that is a fold in
/// memory.
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

    public async Task<NetWorthHistory> ComputeAsync(
        NetWorthHistoryQuery query,
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

        // 2 — the accounts, BEFORE the grid. The grid needs Account.Opened to clamp its leading
        // empty periods away, so it can be neither pure nor first.
        var accounts = await context.Accounts
            .Where(account => account.Archived == null && account.Opened < now)
            .Select(account => new AccountRow(
                account.AccountId,
                account.Name,
                account.CurrencyCode,
                account.AccountType,
                account.Opened))
            .ToListAsync(cancellationToken);

        if (accounts.Count == 0)
        {
            return Empty(main, interval, requested, NetWorthEmptyReason.NoAccounts);
        }

        // Leading empties are dropped rather than plotted as zero: a flat run at 0 before the first
        // account existed is a claim about net worth, and a false one.
        var earliestOpened = DateOnly.FromDateTime(accounts.Min(account => account.Opened));
        var effectiveFrom = NetWorthPeriods.StartOfPeriod(
            earliestOpened > requested.From ? earliestOpened : requested.From, interval);

        if (effectiveFrom > requested.To)
        {
            // The window ends before any account existed. The caller's input was valid, so this is a
            // 200 with no points — never an inverted window, and never a 400.
            return Empty(main, interval, requested, NetWorthEmptyReason.WindowBeforeFirstAccount);
        }

        // 3 — the grid.
        var grid = BuildGrid(effectiveFrom, requested.To, interval, now);
        if (grid.Count == 0)
        {
            return Empty(main, interval, requested, NetWorthEmptyReason.NotBuilt);
        }

        var finalBound = grid[^1].Bound;
        var accountIds = accounts.Select(account => account.AccountId).ToList();

        // 4, 5, 6 — the three timelines, each bounded by the window rather than trimmed after the fact.
        var buckets = await LoadTransactionBucketsAsync(accountIds, interval, finalBound, cancellationToken);
        var estimates = await LoadEstimatesAsync(accountIds, finalBound, cancellationToken);
        var rates = await conversionService.GetRateTimelineToAsync(
            main,
            accounts.Select(account => account.CurrencyCode),
            grid[0].Bound,
            finalBound,
            cancellationToken);

        // 7 — the fold.
        var points = new List<NetWorthHistoryPoint>(grid.Count);
        var unconverted = new Dictionary<Guid, UnconvertedAccount>();
        var cursors = accountIds.ToDictionary(id => id, _ => new AccountCursor());

        foreach (var slot in grid)
        {
            var totalAssets = 0m;
            var totalLiabilities = 0m;
            var unconvertedCount = 0;
            var revaluedCount = 0;
            var contributingCount = 0;

            foreach (var account in accounts)
            {
                var cursor = cursors[account.AccountId];

                // V6 — an account that had not opened by this bound contributes 0, and is neither
                // partial nor contributing. It is not a defect that it has no figure yet.
                if (account.Opened >= slot.Bound)
                {
                    continue;
                }

                var balance = cursor.AdvanceBalance(buckets.GetValueOrDefault(account.AccountId), slot.Bound);
                var estimate = cursor.AdvanceEstimate(estimates.GetValueOrDefault(account.AccountId), slot.Bound);

                if (cursor.TakeRevaluation())
                {
                    revaluedCount++;
                }

                // Replace policy (issue #182 §9): the in-force estimate wins over the transaction
                // balance. Both are in the account currency, so they convert identically.
                var value = estimate ?? balance;

                var converted = Convert(value, account.CurrencyCode, main, rates, slot.Bound);
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

                if (IsAsset(account.AccountType))
                {
                    totalAssets += converted.Value;
                }
                else if (IsLiability(account.AccountType))
                {
                    // Signed, like AccountTotalsService: negating keeps a normal debt a positive
                    // liability while letting a credit balance reduce total liabilities.
                    totalLiabilities += -converted.Value;
                }
                // AccountType.Unknown (0) is excluded, as it is from the totals.
            }

            points.Add(new NetWorthHistoryPoint
            {
                Date = slot.Date,
                TotalAssets = totalAssets,
                TotalLiabilities = totalLiabilities,
                NetWorth = totalAssets - totalLiabilities,
                UnconvertedAccountCount = unconvertedCount,
                RevaluedAccountCount = revaluedCount,
                ContributingAccountCount = contributingCount,
            });
        }

        if (points.TrueForAll(point => point.ContributingAccountCount == 0))
        {
            // Every period was wholly unconvertible. A line of zeroes would read as "you were worth
            // nothing", which is the opposite of "we could not tell".
            return Empty(main, interval, (effectiveFrom, requested.To), NetWorthEmptyReason.NothingConvertible)
                with { UnconvertedAccounts = [.. unconverted.Values] };
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
    /// An estimate entered today does not rewrite the past: it applies from its own
    /// <c>EffectiveFrom</c> forward, which is what makes the step at that instant a real revaluation
    /// rather than a correction to be smoothed away.
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
                    .OrderBy(row => row.EffectiveFrom)
                    .ThenBy(row => row.CreatedAtUtc)
                    .Select(row => new EstimatePoint(row.EffectiveFrom, row.Value, row.AccountEstimateId))
                    .ToList());
    }

    // ── Conversion ────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// <paramref name="value"/> in the main currency at the rate in force at <paramref name="bound"/>,
    /// or <c>null</c> when no direct rate was in force then.
    /// </summary>
    /// <remarks>
    /// Never a substituted or a later rate: converting a 2019 balance at today's rate is exactly the
    /// class of fabrication this feature exists to remove, and understating the point while saying so
    /// is the only honest alternative.
    /// </remarks>
    private static decimal? Convert(
        decimal value,
        string accountCurrencyCode,
        string main,
        IReadOnlyDictionary<string, IReadOnlyList<CurrencyConversionService.RatePoint>> rates,
        DateTime bound)
    {
        var code = CurrencyValidationService.Normalize(accountCurrencyCode);
        if (string.Equals(code, main, StringComparison.Ordinal))
        {
            return value; // same currency → 1:1, no rate row required.
        }

        if (!rates.TryGetValue(code, out var timeline))
        {
            return null;
        }

        decimal? rate = null;
        foreach (var point in timeline)
        {
            if (point.AsOf >= bound) break;
            rate = point.Rate;
        }

        return rate is null ? null : value * rate.Value;
    }

    private static NetWorthHistory Empty(
        string main,
        NetWorthInterval interval,
        (DateOnly From, DateOnly To) window,
        NetWorthEmptyReason reason) =>
        new()
        {
            MainCurrencyCode = main,
            Interval = interval,
            From = window.From,
            To = window.To,
            EmptyReason = reason,
            Points = [],
            UnconvertedAccounts = [],
        };

    // Asset accounts: AccountType 1–8. Liability accounts: 9–15. Same split as the totals.
    private static bool IsAsset(AccountType type) => type is >= AccountType.Cash and <= AccountType.OtherAsset;

    private static bool IsLiability(AccountType type) => type is >= AccountType.CreditCard and <= AccountType.OtherLiability;

    private const string DefaultMainCurrency = "NOK";

    // ── Fold state ────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// One account's position in its own two timelines. The grid's bounds ascend, so each timeline is
    /// walked once across the whole series rather than re-scanned per point — which is what keeps the
    /// fold linear in (accounts × points) instead of quadratic in rows.
    /// </summary>
    private sealed class AccountCursor
    {
        private int bucketIndex;
        private decimal runningBalance;
        private int estimateIndex;
        private decimal? currentEstimate;
        private Guid? currentEstimateId;
        private bool revaluedSinceLastPoint;
        private bool seenAnyPoint;

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

    private readonly record struct GridSlot(DateOnly Date, DateTime Bound);

    private readonly record struct Bucket(DateTime Start, decimal Sum);

    private readonly record struct EstimatePoint(DateTime EffectiveFrom, decimal Value, Guid Id);

    private sealed record AccountRow(Guid AccountId, string Name, string CurrencyCode, AccountType AccountType, DateTime Opened);

    private sealed record RawBucket(Guid AccountId, int Year, int Month, int Day, decimal Sum);

    private sealed record EstimateRow(Guid AccountId, Guid AccountEstimateId, DateTime EffectiveFrom, DateTime CreatedAtUtc, decimal Value);
}
