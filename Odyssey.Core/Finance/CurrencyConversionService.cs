using Odyssey.Context;
using Odyssey.Core.Finance.ExchangeRateSync;
using Odyssey.Dtos.Finance;
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
/// same day is not beaten by the hop. A missing rate yields <c>null</c> so callers can flag the amount as
/// unconvertible.
/// </para>
/// <para>
/// <b>Fetched rows are inverted; manual rows never are</b> (issue #472). A pair's candidates come from two
/// branches: <b>S</b>, the stored rows of the pair, and <b>R</b>, the FETCHED rows of the swapped pair, used as
/// <c>1 / Rate</c>, unrounded. Norges Bank publishes mid-rates, whose reciprocal is exact; a hand-entered rate
/// may be a buy or sell quote, and turning it around would change what it means. Branch R filters on
/// <c>Provider</c> BEFORE its grouped MAX, so a newer manual row of the same stored pair can never hide an
/// older fetched one. The two branches are separate queries, never one <c>OR</c> across pairs, so each keeps
/// the <c>(From, To, AsOf)</c> range scan. They meet in memory under <see cref="ExchangeRateOrdering"/>, which
/// gives a stored row the win on a full tie.
/// </para>
/// <para>
/// "Latest" is <see cref="ExchangeRateOrdering"/>, and it is bounded: the unbounded lookups take
/// <c>AsOf ≤ now</c>, so a future-dated rate is ignored until its date arrives; the bounded ones keep
/// their exclusive <c>AsOf &lt; bound</c> (issue #90 AC2). The hop is never stored, and its composite is
/// the unrounded product of its legs. A row with <c>Rate ≤ 0</c> is never divided by: the CHECK forbids
/// one, and a row that slipped past it is dropped where it wins. The latest lookups then read "no rate" for that
/// pair; the timeline, whose step series cannot express a gap, keeps the previous rate in force.
/// </para>
/// </remarks>
public class CurrencyConversionService(OdysseyContext context, TimeProvider? injectedTimeProvider = null)
{
    private const string Hub = ExchangeRateSyncConstants.BaseCurrencyCode;

    /// <summary>
    /// Every provider, as an equality list rather than <c>Provider IS NOT NULL</c>: an equality on
    /// <c>Provider</c> lets the <c>(From, To, Provider, AsOf)</c> index answer branch R's grouped MAX with a loose
    /// index scan, which a range on it does not.
    /// </summary>
    internal static readonly List<ExchangeRateProvider?> FetchedProviders =
        [.. Enum.GetValues<ExchangeRateProvider>().Select(provider => (ExchangeRateProvider?)provider)];

    private readonly TimeProvider timeProvider = injectedTimeProvider ?? TimeProvider.System;

    /// <summary>
    /// Converts <paramref name="amount"/> from one currency to another using the latest rate in force.
    /// Returns <c>null</c> when neither a direct, an inverted nor a NOK-hop rate exists (and the currencies differ).
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
    /// <paramref name="toCurrencyCode"/>, direct, inverted or through the hub. The map only contains
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
        var resolutions = await ResolveLatestAsync(toCurrencyCode, fromCurrencyCodes, asOfExclusive, cancellationToken);
        return resolutions.ToDictionary(pair => pair.Key, pair => pair.Value.Rate, StringComparer.Ordinal);
    }

    /// <summary>
    /// The rate a conversion of <paramref name="fromCurrencyCode"/> into <paramref name="toCurrencyCode"/> would
    /// use right now, with its method and legs (issue #472 §5.3) — the same resolution
    /// <see cref="GetLatestRatesToAsync"/> runs, so the two cannot disagree. <c>null</c> when nothing resolves.
    /// </summary>
    public async Task<RateResolution?> ResolveAsync(
        string fromCurrencyCode, string toCurrencyCode, CancellationToken cancellationToken = default)
    {
        var from = CurrencyValidationService.Normalize(fromCurrencyCode);
        var to = CurrencyValidationService.Normalize(toCurrencyCode);

        if (string.Equals(from, to, StringComparison.Ordinal))
        {
            return new RateResolution(1m, null, ExchangeRateResolutionMethod.SameCurrency, []);
        }

        var resolutions = await ResolveLatestAsync(to, [from], null, cancellationToken);
        return resolutions.GetValueOrDefault(from);
    }

    /// <summary>
    /// One rate per (from-currency, instant) in ascending order, for resolving "the rate in force at
    /// this point" across a whole time series without a query per point.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It returns the rows <b>inside</b> the window plus, per pair, the single latest row before
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
    /// <c>AsOf &lt; fromBound</c> — so the concatenation cannot double-count a row. Branches S and R each
    /// run that shape on their own (issue #472), with R's <c>Provider</c> filter inside the grouped MAX.
    /// </para>
    /// <para>
    /// No <c>EF.Functions.*</c> and no raw SQL, so it still runs on the EF InMemory provider the fast
    /// test tiers use as well as on MariaDB.
    /// </para>
    /// <para>
    /// The same resolution as everything else here (issue #349 §3 "Timeline merge"): three step series
    /// are built — direct <c>A → B</c>, <c>A → NOK</c> and <c>NOK → B</c>, each from its stored rows and
    /// its inverted fetched rows together — and merged into one, with a point at every instant where any of
    /// them changes carrying rule 4's winner. So a series can switch between direct and hop over time while
    /// the caller still walks an ordinary step series. The same-currency case is omitted because callers
    /// treat it as 1:1.
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

        var (stored, reversed) = Candidates(fromCodes, to);

        var (storedRows, reversedRows) = await LoadBranchesAsync(
            WindowWithCarryIn(stored, fromBound, toBound), WindowWithCarryIn(reversed, fromBound, toBound), cancellationToken);

        var series = storedRows
            .Concat(reversedRows.Select(row => row.Invert()))
            .GroupBy(row => (row.From, row.To))
            .Select(group => (group.Key, Series: StepSeries(group)))
            .Where(pair => pair.Series.Count > 0)
            .ToDictionary(pair => pair.Key, pair => pair.Series);

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

    /// <summary>The shared resolution behind every latest-rate method: per from-currency, the chosen candidate.</summary>
    private async Task<Dictionary<string, RateResolution>> ResolveLatestAsync(
        string toCurrencyCode,
        IEnumerable<string> fromCurrencyCodes,
        DateTime? asOfExclusive,
        CancellationToken cancellationToken)
    {
        var to = CurrencyValidationService.Normalize(toCurrencyCode);
        var fromCodes = NormalizeFrom(fromCurrencyCodes, to);
        var result = new Dictionary<string, RateResolution>(StringComparer.Ordinal);

        if (fromCodes.Count == 0)
        {
            return result;
        }

        var now = Now;
        var (stored, reversed) = Candidates(fromCodes, to);
        if (asOfExclusive is { } exclusive)
        {
            stored = stored.Where(rate => rate.AsOf < exclusive);
            reversed = reversed.Where(rate => rate.AsOf < exclusive);
        }
        else
        {
            stored = stored.Where(rate => rate.AsOf <= now);
            reversed = reversed.Where(rate => rate.AsOf <= now);
        }

        var (storedLatest, reversedLatest) = await LoadBranchesAsync(
            AtLatestInstant(stored), AtLatestInstant(reversed), cancellationToken);

        var latest = storedLatest
            .Concat(reversedLatest.Select(row => row.Invert()))
            .GroupBy(row => (row.From, row.To))
            .Select(group => ExchangeRateOrdering.Newest(group)!)
            .Where(row => row.Usable)
            .ToDictionary(row => (row.From, row.To));

        foreach (var from in fromCodes)
        {
            var direct = latest.TryGetValue((from, to), out var found) ? Single(found) : null;
            RateResolution? hop = null;
            if (!IsHub(from) && !IsHub(to)
                && latest.TryGetValue((from, Hub), out var first)
                && latest.TryGetValue((Hub, to), out var second))
            {
                hop = new RateResolution(
                    first.Rate * second.Rate,
                    first.AsOf < second.AsOf ? first.AsOf : second.AsOf,
                    ExchangeRateResolutionMethod.ViaNok,
                    [first.ToLeg(), second.ToLeg()]);
            }

            if (Choose(direct, hop) is { } chosen)
            {
                result[from] = chosen;
            }
        }

        return result;
    }

    private static RateResolution Single(Row row) =>
        new(row.Rate, row.AsOf,
            row.Inverted ? ExchangeRateResolutionMethod.Inverted : ExchangeRateResolutionMethod.Direct,
            [row.ToLeg()]);

    /// <summary>
    /// Every row any of the requested resolutions can use, as two branches (issue #472). <b>S</b>: the stored
    /// rows of the direct pairs and both hop legs. <b>R</b>: the FETCHED rows of the same pairs swapped, to be
    /// inverted. Both are supersets of what is needed (a <c>NOK → NOK</c> row cannot exist), which the per-pair
    /// dictionary ignores.
    /// </summary>
    private (IQueryable<ExchangeRate> Stored, IQueryable<ExchangeRate> Reversed) Candidates(
        IReadOnlyCollection<string> fromCodes, string to)
    {
        var froms = fromCodes.Append(Hub).Distinct(StringComparer.Ordinal).ToList();
        var tos = new[] { to, Hub }.Distinct(StringComparer.Ordinal).ToList();
        var rates = context.ExchangeRates.AsNoTracking();

        // Every filter here is on an index column — (From, To, AsOf, Provider) — so each branch's grouped MAX
        // stays a loose index scan. Rate is NOT in the index: filtering it here would make the MAX read every
        // row. A non-positive rate is dropped in memory instead (Row.Usable), so the pair reads as "no rate";
        // CK_ExchangeRates_RatePositive forbids one in the first place.
        var stored = rates.Where(rate =>
            froms.Contains(rate.FromCurrencyCode) && tos.Contains(rate.ToCurrencyCode));
        var providers = FetchedProviders;
        var reversed = rates.Where(rate =>
            providers.Contains(rate.Provider)
            && tos.Contains(rate.FromCurrencyCode) && froms.Contains(rate.ToCurrencyCode));

        return (stored, reversed);
    }

    /// <summary>The rows inside <c>[fromBound, toBound)</c> plus each pair's latest row before it.</summary>
    private static IQueryable<ExchangeRate> WindowWithCarryIn(
        IQueryable<ExchangeRate> candidates, DateTime fromBound, DateTime toBound)
    {
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
        // the ordering resolves exactly as it does anywhere else.
        var carryIn =
            from rate in candidates
            join instant in carryInInstants
                on new { rate.FromCurrencyCode, rate.ToCurrencyCode, rate.AsOf }
                equals new { instant.FromCurrencyCode, instant.ToCurrencyCode, instant.AsOf }
            where rate.AsOf < fromBound
            select rate;

        return inWindow.Concat(carryIn);
    }

    /// <summary>
    /// Both branches in ONE round trip: a <c>UNION ALL</c> of two queries, each keeping its own index range
    /// scan, never one <c>OR</c> across pairs. A constant column says which branch a row came from — the same
    /// fetched row can be in both (as a stored candidate of its own pair and a reversed one of the swapped pair).
    /// </summary>
    private static async Task<(List<Row> Stored, List<Row> Reversed)> LoadBranchesAsync(
        IQueryable<ExchangeRate> stored, IQueryable<ExchangeRate> reversed, CancellationToken cancellationToken)
    {
        var rows = await stored.Select(rate => new BranchRow
            {
                From = rate.FromCurrencyCode,
                To = rate.ToCurrencyCode,
                AsOf = rate.AsOf,
                Tiebreak = rate.UpdatedAt ?? rate.CreatedAt,
                ExchangeRateId = rate.ExchangeRateId,
                Rate = rate.Rate,
                Fetched = rate.Provider != null,
                Reversed = false,
            })
            .Concat(reversed.Select(rate => new BranchRow
            {
                From = rate.FromCurrencyCode,
                To = rate.ToCurrencyCode,
                AsOf = rate.AsOf,
                Tiebreak = rate.UpdatedAt ?? rate.CreatedAt,
                ExchangeRateId = rate.ExchangeRateId,
                Rate = rate.Rate,
                Fetched = rate.Provider != null,
                Reversed = true,
            }))
            .ToListAsync(cancellationToken);

        static Row ToRow(BranchRow row) =>
            new(row.From, row.To, row.AsOf, row.Tiebreak, row.ExchangeRateId, row.Rate, row.Fetched, false);

        return (
            rows.Where(row => !row.Reversed).Select(ToRow).ToList(),
            rows.Where(row => row.Reversed).Select(ToRow).ToList());
    }

    /// <summary>
    /// The rows at each pair's latest instant inside <paramref name="bounded"/>, resolved in SQL as a grouped
    /// <c>MAX(AsOf)</c> joined back to its rows — never by materialising a pair's history (issue #349
    /// §10). Only the few rows sharing the maximum instant reach memory, where the tiebreak applies.
    /// </summary>
    private static IQueryable<ExchangeRate> AtLatestInstant(IQueryable<ExchangeRate> bounded)
    {
        var instants = bounded
            .GroupBy(rate => new { rate.FromCurrencyCode, rate.ToCurrencyCode })
            .Select(group => new
            {
                group.Key.FromCurrencyCode,
                group.Key.ToCurrencyCode,
                AsOf = group.Max(rate => rate.AsOf),
            });

        return
            from rate in bounded
            join instant in instants
                on new { rate.FromCurrencyCode, rate.ToCurrencyCode, rate.AsOf }
                equals new { instant.FromCurrencyCode, instant.ToCurrencyCode, instant.AsOf }
            select rate;
    }

    /// <summary>
    /// One logical pair's rows — stored and inverted together — as a step series: one point per instant, the
    /// newest row at it winning by the full ordering, so a stored row beats an inverted one on a full tie.
    /// </summary>
    private static List<Point> StepSeries(IEnumerable<Row> rows) =>
        rows.GroupBy(row => row.AsOf)
            .Select(group => ExchangeRateOrdering.Newest(group)!)
            .Where(row => row.Usable)
            .Select(row => row.ToPoint())
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

    /// <summary>Rule 4 over resolutions, the same comparison as <see cref="Choose(Point?, Point?)"/>.</summary>
    private static RateResolution? Choose(RateResolution? direct, RateResolution? hop) =>
        (direct, hop) switch
        {
            ({ } d, { } h) => h.AsOf!.Value.Date > d.AsOf!.Value.Date ? h : d,
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

    /// <summary>The flat shape both branches project to, so EF can <c>UNION ALL</c> them.</summary>
    private sealed class BranchRow
    {
        public required string From { get; init; }

        public required string To { get; init; }

        public DateTime AsOf { get; init; }

        public DateTime Tiebreak { get; init; }

        public Guid ExchangeRateId { get; init; }

        public decimal Rate { get; init; }

        public bool Fetched { get; init; }

        public bool Reversed { get; init; }
    }

    /// <summary>
    /// A candidate row. A branch-R row is <see cref="Invert"/>ed into the logical pair before it competes:
    /// codes swapped, <c>1 / Rate</c> unrounded, and its <c>AsOf</c>, tiebreak and id kept as the stored row's.
    /// </summary>
    private sealed record Row(
        string From, string To, DateTime AsOf, DateTime Tiebreak, Guid ExchangeRateId, decimal Rate,
        bool Fetched, bool Inverted)
        : IRateCandidate
    {
        public Point ToPoint() => new(AsOf, Rate);

        /// <summary>
        /// False for a non-positive rate, which only a hand edit around <c>CK_ExchangeRates_RatePositive</c> can
        /// leave. It is never divided by: the latest lookups read "no rate" where it wins, the timeline keeps the
        /// previous rate (see the class remarks).
        /// </summary>
        public bool Usable => Rate > 0;

        public Row Invert() => this with { From = To, To = From, Rate = Usable ? 1m / Rate : 0m, Inverted = true };

        public RateLeg ToLeg() => new(ExchangeRateId, From, To, Rate, AsOf, Fetched, Inverted);
    }

    /// <summary>
    /// One rate and the instant it took effect. A <c>readonly record struct</c> nested here because it
    /// is an implementation detail of the timeline — it is never serialized and never leaves the
    /// domain.
    /// </summary>
    public readonly record struct RatePoint(DateTime AsOf, decimal Rate);
}

/// <summary>
/// How a latest-rate resolution was reached (issue #472 §5.3): the rate multiplied in, its effective date (the
/// older leg's for a hop; null for the same currency), the method and the legs in the logical direction.
/// </summary>
public sealed record RateResolution(
    decimal Rate, DateTime? AsOf, ExchangeRateResolutionMethod Method, IReadOnlyList<RateLeg> Legs);

/// <summary>One stored row a resolution used, in the logical direction; the id names the stored row.</summary>
public sealed record RateLeg(
    Guid ExchangeRateId, string FromCurrencyCode, string ToCurrencyCode, decimal Rate, DateTime AsOf,
    bool Fetched, bool Inverted);
