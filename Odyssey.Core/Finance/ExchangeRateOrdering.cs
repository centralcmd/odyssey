using System.Linq.Expressions;
using Odyssey.Context;
using Odyssey.Dtos.Finance;

namespace Odyssey.Core.Finance;

/// <summary>
/// The ONE definition of "which exchange rate is newest" (issue #349 §3), shared by the three conversion
/// methods and every status rule in <see cref="ExchangeRateService"/>: <c>AsOf</c> newest first, then
/// <c>UpdatedAt ?? CreatedAt</c> newest first, then <c>ExchangeRateId</c>. Source plays no part — a
/// fetched row and a manual row compete on <c>AsOf</c> alone. Those readers must agree with each other
/// and with the NOK-hop selection, which is why none of them may restate the ordering;
/// a source-lint in <c>ExchangeRateHopAndStatusTests</c> fails on a second copy in <c>Odyssey.Core</c>.
/// </summary>
/// <remarks>
/// The SQL predicates stop at the <c>UpdatedAt ?? CreatedAt</c> key: a <see cref="Guid"/> has no
/// translatable ordering operator. Two rows of one pair tying on both instants to the microsecond is
/// not reachable through any write path — fetched rows are unique per <c>(pair, AsOf, provider)</c> —
/// and every in-memory pick (<see cref="Newest{T}"/>) applies the id as the final key, so a row's
/// reported status is always unique.
/// </remarks>
public static class ExchangeRateOrdering
{
    /// <summary>Newest first, by the three keys.</summary>
    public static IOrderedQueryable<ExchangeRate> NewestFirst(this IQueryable<ExchangeRate> rates) =>
        rates.OrderByDescending(rate => rate.AsOf)
            .ThenByDescending(rate => rate.UpdatedAt ?? rate.CreatedAt)
            .ThenByDescending(rate => rate.ExchangeRateId);

    /// <summary>The newest of already-materialised candidates, by the same three keys.</summary>
    public static T? Newest<T>(IEnumerable<T> candidates)
        where T : class, IRateCandidate =>
        candidates
            .OrderByDescending(candidate => candidate.AsOf)
            .ThenByDescending(candidate => candidate.Tiebreak)
            .ThenByDescending(candidate => candidate.ExchangeRateId)
            .FirstOrDefault();

    /// <summary>
    /// The status rank of a row: <c>0</c> Current, <c>1</c> Historical, <c>2</c> Upcoming — the
    /// <see cref="ExchangeRateStatus"/> ordinals. A row is Upcoming while its <c>AsOf</c> is after
    /// <paramref name="now"/>; otherwise it is Historical when a newer row of its pair is already in force.
    /// </summary>
    public static Expression<Func<ExchangeRate, int>> StatusRank(IQueryable<ExchangeRate> all, DateTime now) =>
        rate => rate.AsOf > now
            ? (int)ExchangeRateStatus.Upcoming
            : all.Any(other =>
                other.FromCurrencyCode == rate.FromCurrencyCode
                && other.ToCurrencyCode == rate.ToCurrencyCode
                && other.AsOf <= now
                && (other.AsOf > rate.AsOf
                    || (other.AsOf == rate.AsOf
                        && (other.UpdatedAt ?? other.CreatedAt) > (rate.UpdatedAt ?? rate.CreatedAt))))
                ? (int)ExchangeRateStatus.Historical
                : (int)ExchangeRateStatus.Current;

    /// <summary>The rate's tiebreak instant, for projections that carry it alongside <c>AsOf</c>.</summary>
    public static DateTime Tiebreak(ExchangeRate rate) => rate.UpdatedAt ?? rate.CreatedAt;
}

/// <summary>A materialised rate row, for <see cref="ExchangeRateOrdering.Newest{T}"/>.</summary>
public interface IRateCandidate
{
    DateTime AsOf { get; }

    DateTime Tiebreak { get; }

    Guid ExchangeRateId { get; }
}
