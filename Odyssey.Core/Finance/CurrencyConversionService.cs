using Odyssey.Context;
using Odyssey.Core.Finance.ExchangeRateSync;
using Microsoft.EntityFrameworkCore;

namespace Odyssey.Core.Finance;

/// <summary>
/// Converts amounts between currencies using the latest <see cref="ExchangeRate"/> in force.
/// </summary>
/// <remarks>
/// <para>
/// Every method resolves <c>A → B</c> the same way (issue #349 §3): the same currency is 1:1; the
/// <b>direct</b> candidate is the latest <c>A → B</c> row; the <b>hop</b> candidate — when neither side is
/// the hub — is the latest <c>A → NOK</c> times the latest <c>NOK → B</c>, effective from the OLDER leg's
/// instant. When both exist the later UTC calendar date wins and the same date goes to the direct one,
/// so a stale hand-entered direct rate does not beat today's fetched data and a direct rate entered the
/// same day is not beaten by the hop. There is still no inversion: a <c>(B, A)</c> row never satisfies
/// <c>(A, B)</c> — the sync stores both directions. A missing rate yields <c>null</c> so callers can flag
/// the amount as unconvertible.
/// </para>
/// <para>
/// "Latest" is <see cref="ExchangeRateOrdering"/>, and it is bounded: the unbounded lookups take
/// <c>AsOf ≤ now</c>, so a future-dated rate is ignored until its date arrives; the bounded ones keep
/// their exclusive <c>AsOf &lt; bound</c> (issue #90 AC2). The hop is never stored, and its composite is
/// the unrounded product of its legs.
/// </para>
/// </remarks>
public class CurrencyConversionService(OdysseyContext context, TimeProvider? injectedTimeProvider = null)
{
    private const string Hub = ExchangeRateSyncConstants.BaseCurrencyCode;

    private readonly TimeProvider timeProvider = injectedTimeProvider ?? TimeProvider.System;

    /// <summary>
    /// Converts <paramref name="amount"/> from one currency to another using the latest rate in force.
    /// Returns <c>null</c> when neither a direct rate nor a NOK hop exists (and the currencies differ).
    /// </summary>
    public async Task<decimal?> ConvertAsync(decimal amount, string fromCurrencyCode, string toCurrencyCode, CancellationToken cancellationToken = default)
    {
        var from = CurrencyValidationService.Normalize(fromCurrencyCode);
        var to = CurrencyValidationService.Normalize(toCurrencyCode);

        if (string.Equals(from, to, StringComparison.Ordinal))
        {
            return amount;
        }

        var rates = await GetLatestRatesToAsync(to, [from], cancellationToken: cancellationToken);
        return rates.TryGetValue(from, out var rate) ? amount * rate : null;
    }

    /// <summary>
    /// Resolves the latest rate from each of <paramref name="fromCurrencyCodes"/> into
    /// <paramref name="toCurrencyCode"/>, direct or through the hub, in one query. The map only contains
    /// currencies that resolve; the same-currency case is intentionally omitted (callers treat it as 1:1).
    /// Used by the totals computation to avoid one query per account.
    /// </summary>
    /// <param name="asOfExclusive">
    /// When given, only rates with <c>AsOf &lt;</c> this instant are considered — "latest as of then"
    /// rather than "latest in force now" (issue #90 G7). Exclusive on purpose: the net-worth history
    /// measures each point at its period's exclusive upper bound, and a rate stamped exactly on a
    /// bound that counted here but not there would make the two disagree at the one point AC2
    /// requires to be equal. Without it the bound is <c>AsOf ≤ now</c>.
    /// </param>
    public async Task<IReadOnlyDictionary<string, decimal>> GetLatestRatesToAsync(
        string toCurrencyCode,
        IEnumerable<string> fromCurrencyCodes,
        DateTime? asOfExclusive = null,
        CancellationToken cancellationToken = default)
    {
        var to = CurrencyValidationService.Normalize(toCurrencyCode);
        var fromCodes = NormalizeFrom(fromCurrencyCodes, to);

        if (fromCodes.Count == 0)
        {
            return new Dictionary<string, decimal>();
        }

        var bounded = asOfExclusive is { } exclusive
            ? Candidates(fromCodes, to).Where(rate => rate.AsOf < exclusive)
            : Candidates(fromCodes, to).Where(rate => rate.AsOf <= Now);

        var latest = await LatestPerPairAsync(bounded, cancellationToken);

        var result = new Dictionary<string, decimal>(StringComparer.Ordinal);
        foreach (var from in fromCodes)
        {
            Point? direct = latest.TryGetValue((from, to), out var found) ? found : null;
            Point? hop = null;
            if (!IsHub(from) && !IsHub(to)
                && latest.TryGetValue((from, Hub), out var first)
                && latest.TryGetValue((Hub, to), out var second))
            {
                hop = Compose(first, second);
            }

            if (Choose(direct, hop) is { } chosen)
            {
                result[from] = chosen.Rate;
            }
        }

        return result;
    }

    /// <summary>
    /// One rate per (from-currency, instant) in ascending order, for resolving "the rate in force at
    /// this point" across a whole time series without a query per point.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It returns the rows <b>inside</b> the window plus, per currency, the single latest row before
    /// it — the carry-in that serves the first point. Bounding it in the query rather than trimming
    /// after materialisation is the whole point: an in-memory trim still ships every rate row since
    /// inception across the wire and holds them all at peak, which is the cost the bound exists to
    /// avoid. Query count was never the concern; payload and peak allocation were.
    /// </para>
    /// <para>
    /// <b>The carry-in is a join to a grouped MAX, not a correlated subquery, and the difference is
    /// not stylistic.</b> The obvious form — "keep a row before the window when no <i>later</i> row
    /// also sits before it" — reads well and is a dependent subquery: MariaDB re-executes it once per
    /// candidate row, so a currency with 10 000 pre-window rows costs 10 000 executions. Measured on
    /// the issue #90 AC36 dataset (20 000 rows) that form took <b>23.6 s</b>, and the correlated-MAX
    /// variant of it 52.8 s — past MySqlConnector's 30 s command timeout, which surfaces as the
    /// distinctly unhelpful "Query execution was interrupted". The shape below is <b>0.8 ms</b> on
    /// the same data, because the grouped MAX resolves to a loose index scan over
    /// <c>(From, To, AsOf)</c> and the join back is one index lookup per currency.
    /// </para>
    /// <para>
    /// The two halves are disjoint by construction — one takes <c>AsOf &gt;= fromBound</c>, the other
    /// <c>AsOf &lt; fromBound</c> — so the concatenation cannot double-count a row.
    /// </para>
    /// <para>
    /// No <c>EF.Functions.*</c> and no raw SQL, so it still runs on the EF InMemory provider the fast
    /// test tiers use as well as on MariaDB.
    /// </para>
    /// <para>
    /// The same resolution as everything else here (issue #349 §3 "Timeline merge"): three step series
    /// are loaded — direct <c>A → B</c>, <c>A → NOK</c> and <c>NOK → B</c> — and merged into one, with a
    /// point at every instant where any of them changes carrying rule 4's winner. So a series can switch
    /// between direct and hop over time while the caller still walks an ordinary step series. No
    /// inversion, and the same-currency case is omitted because callers treat it as 1:1.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyDictionary<string, IReadOnlyList<RatePoint>>> GetRateTimelineToAsync(
        string toCurrencyCode,
        IEnumerable<string> fromCurrencyCodes,
        DateTime fromBound,
        DateTime toBound,
        CancellationToken cancellationToken = default)
    {
        var to = CurrencyValidationService.Normalize(toCurrencyCode);
        var fromCodes = NormalizeFrom(fromCurrencyCodes, to);

        if (fromCodes.Count == 0)
        {
            return new Dictionary<string, IReadOnlyList<RatePoint>>();
        }

        var candidates = Candidates(fromCodes, to);

        // Everything that falls inside the window.
        var inWindow = candidates.Where(rate => rate.AsOf >= fromBound && rate.AsOf < toBound);

        // The instant of each pair's latest rate before the window: one grouped MAX, which the
        // (From, To, AsOf) index prefix answers with a loose index scan rather than a row-by-row search.
        var carryInInstants = candidates
            .Where(rate => rate.AsOf < fromBound)
            .GroupBy(rate => new { rate.FromCurrencyCode, rate.ToCurrencyCode })
            .Select(group => new
            {
                group.Key.FromCurrencyCode,
                group.Key.ToCurrencyCode,
                AsOf = group.Max(rate => rate.AsOf),
            });

        // …joined back to the rows sitting at those instants. A tie on AsOf yields both rows, which
        // the ordering below resolves exactly as it does anywhere else.
        var carryIn =
            from rate in candidates
            join instant in carryInInstants
                on new { rate.FromCurrencyCode, rate.ToCurrencyCode, rate.AsOf }
                equals new { instant.FromCurrencyCode, instant.ToCurrencyCode, instant.AsOf }
            where rate.AsOf < fromBound
            select rate;

        var rows = await inWindow
            .Concat(carryIn)
            .Select(rate => new Row(
                rate.FromCurrencyCode, rate.ToCurrencyCode, rate.AsOf, rate.UpdatedAt ?? rate.CreatedAt,
                rate.ExchangeRateId, rate.Rate))
            .ToListAsync(cancellationToken);

        var series = rows
            .GroupBy(row => (row.From, row.To))
            .ToDictionary(group => group.Key, group => StepSeries(group));

        var result = new Dictionary<string, IReadOnlyList<RatePoint>>(StringComparer.Ordinal);
        foreach (var from in fromCodes)
        {
            var direct = series.GetValueOrDefault((from, to));
            var first = !IsHub(from) && !IsHub(to) ? series.GetValueOrDefault((from, Hub)) : null;
            var second = !IsHub(from) && !IsHub(to) ? series.GetValueOrDefault((Hub, to)) : null;

            var merged = Merge(direct, first, second);
            if (merged.Count > 0)
            {
                result[from] = merged;
            }
        }

        return result;
    }

    private DateTime Now => timeProvider.GetUtcNow().UtcDateTime;

    private static bool IsHub(string code) => string.Equals(code, Hub, StringComparison.Ordinal);

    private static List<string> NormalizeFrom(IEnumerable<string> fromCurrencyCodes, string to) =>
        fromCurrencyCodes
            .Select(CurrencyValidationService.Normalize)
            .Where(code => !string.Equals(code, to, StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// Every row any of the requested resolutions can use: the direct pairs and both hop legs. A superset
    /// of the needed pairs (a <c>NOK → NOK</c> row cannot exist), which the per-pair dictionary ignores.
    /// </summary>
    private IQueryable<ExchangeRate> Candidates(IReadOnlyCollection<string> fromCodes, string to)
    {
        var froms = fromCodes.Append(Hub).Distinct(StringComparer.Ordinal).ToList();
        var tos = new[] { to, Hub }.Distinct(StringComparer.Ordinal).ToList();
        return context.ExchangeRates.AsNoTracking()
            .Where(rate => froms.Contains(rate.FromCurrencyCode) && tos.Contains(rate.ToCurrencyCode));
    }

    /// <summary>
    /// The latest row per pair inside <paramref name="bounded"/>, resolved in SQL as a grouped
    /// <c>MAX(AsOf)</c> joined back to its rows — never by materialising a pair's history (issue #349
    /// §10). Only the few rows sharing the maximum instant reach memory, where the tiebreak applies.
    /// </summary>
    private static async Task<Dictionary<(string From, string To), Point>> LatestPerPairAsync(
        IQueryable<ExchangeRate> bounded, CancellationToken cancellationToken)
    {
        var instants = bounded
            .GroupBy(rate => new { rate.FromCurrencyCode, rate.ToCurrencyCode })
            .Select(group => new
            {
                group.Key.FromCurrencyCode,
                group.Key.ToCurrencyCode,
                AsOf = group.Max(rate => rate.AsOf),
            });

        var rows = await (
                from rate in bounded
                join instant in instants
                    on new { rate.FromCurrencyCode, rate.ToCurrencyCode, rate.AsOf }
                    equals new { instant.FromCurrencyCode, instant.ToCurrencyCode, instant.AsOf }
                select new Row(
                    rate.FromCurrencyCode, rate.ToCurrencyCode, rate.AsOf, rate.UpdatedAt ?? rate.CreatedAt,
                    rate.ExchangeRateId, rate.Rate))
            .ToListAsync(cancellationToken);

        return rows
            .GroupBy(row => (row.From, row.To))
            .ToDictionary(
                group => group.Key,
                group => ExchangeRateOrdering.Newest(group)!.ToPoint());
    }

    /// <summary>One pair's rows as a step series: one point per instant, the newest row at it winning.</summary>
    private static List<Point> StepSeries(IEnumerable<Row> rows) =>
        rows.GroupBy(row => row.AsOf)
            .Select(group => ExchangeRateOrdering.Newest(group)!.ToPoint())
            .OrderBy(point => point.AsOf)
            .ToList();

    private static Point Compose(Point first, Point second) =>
        new(first.AsOf < second.AsOf ? first.AsOf : second.AsOf, first.Rate * second.Rate);

    /// <summary>Rule 4: the later UTC calendar date wins; the same date goes to the direct candidate.</summary>
    private static Point? Choose(Point? direct, Point? hop) =>
        (direct, hop) switch
        {
            ({ } d, { } h) => h.AsOf.Date > d.AsOf.Date ? h : d,
            ({ } d, null) => d,
            (null, { } h) => h,
            _ => null,
        };

    /// <summary>
    /// Merges the direct series and the two hop legs into one step series: a point at every instant
    /// where any of the three changes, carrying the rule-4 winner of the values in force then.
    /// Consecutive equal rates collapse, so the caller sees an ordinary step series.
    /// </summary>
    private static List<RatePoint> Merge(List<Point>? direct, List<Point>? first, List<Point>? second)
    {
        var instants = new SortedSet<DateTime>();
        foreach (var point in (direct ?? []).Concat(first ?? []).Concat(second ?? []))
        {
            instants.Add(point.AsOf);
        }

        var merged = new List<RatePoint>();
        int di = -1, fi = -1, si = -1;
        foreach (var instant in instants)
        {
            di = Advance(direct, di, instant);
            fi = Advance(first, fi, instant);
            si = Advance(second, si, instant);

            Point? d = di >= 0 ? direct![di] : null;
            Point? h = fi >= 0 && si >= 0 ? Compose(first![fi], second![si]) : null;
            if (Choose(d, h) is not { } winner)
            {
                continue;
            }

            if (merged.Count == 0 || merged[^1].Rate != winner.Rate)
            {
                merged.Add(new RatePoint(instant, winner.Rate));
            }
        }

        return merged;
    }

    private static int Advance(List<Point>? series, int index, DateTime instant)
    {
        if (series is null)
        {
            return -1;
        }

        while (index + 1 < series.Count && series[index + 1].AsOf <= instant)
        {
            index++;
        }

        return index;
    }

    private readonly record struct Point(DateTime AsOf, decimal Rate);

    private sealed record Row(string From, string To, DateTime AsOf, DateTime Tiebreak, Guid ExchangeRateId, decimal Rate)
        : IRateCandidate
    {
        public Point ToPoint() => new(AsOf, Rate);
    }

    /// <summary>
    /// One rate and the instant it took effect. A <c>readonly record struct</c> nested here because it
    /// is an implementation detail of the timeline — it is never serialized and never leaves the
    /// domain.
    /// </summary>
    public readonly record struct RatePoint(DateTime AsOf, decimal Rate);
}
