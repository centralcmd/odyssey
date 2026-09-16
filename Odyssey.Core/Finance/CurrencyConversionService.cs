using Odyssey.Context;
using Microsoft.EntityFrameworkCore;

namespace Odyssey.Core.Finance;

/// <summary>
/// Converts amounts between currencies using the latest manually-entered <see cref="ExchangeRate"/>.
/// v1 rules: a same-currency conversion is 1:1; otherwise the newest direct (from, to) rate is used.
/// There is no inversion or triangulation — a (to, from) rate does not satisfy a (from, to) request.
/// A missing rate yields <c>null</c> so callers can flag the amount as unconvertible.
/// </summary>
public class CurrencyConversionService(OdysseyContext context)
{
    /// <summary>
    /// Converts <paramref name="amount"/> from one currency to another using the latest rate.
    /// Returns <c>null</c> when no direct rate exists (and the currencies differ).
    /// </summary>
    public async Task<decimal?> ConvertAsync(decimal amount, string fromCurrencyCode, string toCurrencyCode, CancellationToken cancellationToken = default)
    {
        var from = CurrencyValidationService.Normalize(fromCurrencyCode);
        var to = CurrencyValidationService.Normalize(toCurrencyCode);

        if (string.Equals(from, to, StringComparison.Ordinal))
        {
            return amount;
        }

        var rate = await context.ExchangeRates
            .Where(value => value.FromCurrencyCode == from && value.ToCurrencyCode == to)
            .OrderByDescending(value => value.AsOf)
            .ThenByDescending(value => value.UpdatedAt ?? value.CreatedAt)
            .Select(value => (decimal?)value.Rate)
            .FirstOrDefaultAsync(cancellationToken);

        return rate is null ? null : amount * rate.Value;
    }

    /// <summary>
    /// Resolves the latest rate from each of <paramref name="fromCurrencyCodes"/> into
    /// <paramref name="toCurrencyCode"/> in a single query. The map only contains pairs that
    /// have a rate; the same-currency case is intentionally omitted (callers treat it as 1:1).
    /// Used by the totals computation to avoid one query per account.
    /// </summary>
    /// <param name="asOfExclusive">
    /// When given, only rates with <c>AsOf &lt;</c> this instant are considered — "latest as of then"
    /// rather than "latest outright" (issue #90 G7). Exclusive on purpose: the net-worth history
    /// measures each point at its period's exclusive upper bound, and a rate stamped exactly on a
    /// bound that counted here but not there would make the two disagree at the one point AC2
    /// requires to be equal.
    /// </param>
    public async Task<IReadOnlyDictionary<string, decimal>> GetLatestRatesToAsync(
        string toCurrencyCode,
        IEnumerable<string> fromCurrencyCodes,
        DateTime? asOfExclusive = null,
        CancellationToken cancellationToken = default)
    {
        var to = CurrencyValidationService.Normalize(toCurrencyCode);
        var fromCodes = fromCurrencyCodes
            .Select(CurrencyValidationService.Normalize)
            .Where(code => !string.Equals(code, to, StringComparison.Ordinal))
            .Distinct()
            .ToList();

        if (fromCodes.Count == 0)
        {
            return new Dictionary<string, decimal>();
        }

        // The composite (From, To, AsOf) index serves this Where; the per-pair "latest"
        // pick is done in memory after materializing only the relevant pairs' rows.
        var rates = await context.ExchangeRates
            .Where(value => value.ToCurrencyCode == to && fromCodes.Contains(value.FromCurrencyCode))
            .Where(value => asOfExclusive == null || value.AsOf < asOfExclusive)
            .ToListAsync(cancellationToken);

        return rates
            .GroupBy(value => value.FromCurrencyCode)
            .ToDictionary(
                group => group.Key,
                group => group
                    .OrderByDescending(value => value.AsOf)
                    .ThenByDescending(value => value.UpdatedAt ?? value.CreatedAt)
                    .First().Rate);
    }

    /// <summary>
    /// One rate per (from-currency, instant) in ascending order, for resolving "the rate in force at
    /// this point" across a whole time series without a query per point.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The predicate returns the rows <b>inside</b> the window plus, per currency, the single latest
    /// row before it — the carry-in that serves the first point. Bounding it in the predicate rather
    /// than trimming after materialisation is the whole point: an in-memory trim still ships every
    /// rate row since inception across the wire and holds them all at peak, which is the cost the
    /// bound exists to avoid. Query count was never the concern; payload and peak allocation were.
    /// </para>
    /// <para>
    /// No <c>EF.Functions.*</c> and no grouping, so it runs on the EF InMemory provider the fast test
    /// tiers use as well as on MariaDB. The correlated <c>Any</c> is what picks exactly one carry-in
    /// row per currency: a row before the window qualifies only when no <i>later</i> row also sits
    /// before the window.
    /// </para>
    /// <para>
    /// Same v1 rules as everything else here: direct <c>(from, to)</c> pairs only, no inversion and no
    /// triangulation, and the same-currency case is omitted because callers treat it as 1:1.
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
        var fromCodes = fromCurrencyCodes
            .Select(CurrencyValidationService.Normalize)
            .Where(code => !string.Equals(code, to, StringComparison.Ordinal))
            .Distinct()
            .ToList();

        if (fromCodes.Count == 0)
        {
            return new Dictionary<string, IReadOnlyList<RatePoint>>();
        }

        var rows = await context.ExchangeRates
            .Where(rate => rate.ToCurrencyCode == to
                && fromCodes.Contains(rate.FromCurrencyCode)
                && rate.AsOf < toBound
                && (rate.AsOf >= fromBound
                    || !context.ExchangeRates.Any(carry => carry.ToCurrencyCode == to
                        && carry.FromCurrencyCode == rate.FromCurrencyCode
                        && carry.AsOf < fromBound
                        && carry.AsOf > rate.AsOf)))
            .Select(rate => new
            {
                rate.FromCurrencyCode,
                rate.AsOf,
                Tiebreak = rate.UpdatedAt ?? rate.CreatedAt,
                rate.Rate,
            })
            .ToListAsync(cancellationToken);

        return rows
            .GroupBy(row => row.FromCurrencyCode, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<RatePoint>)group
                    .OrderBy(row => row.AsOf)
                    .ThenBy(row => row.Tiebreak)
                    .Select(row => new RatePoint(row.AsOf, row.Rate))
                    .ToList(),
                StringComparer.Ordinal);
    }

    /// <summary>
    /// One rate and the instant it took effect. A <c>readonly record struct</c> nested here because it
    /// is an implementation detail of the timeline — it is never serialized and never leaves the
    /// domain.
    /// </summary>
    public readonly record struct RatePoint(DateTime AsOf, decimal Rate);
}
