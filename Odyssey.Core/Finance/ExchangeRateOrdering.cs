using System.Linq.Expressions;
using Odyssey.Context;
using Odyssey.Dtos.Finance;

namespace Odyssey.Core.Finance;

/// <summary>
/// The ONE definition of "which exchange rate is newest" (issue #349 §3), shared by the three conversion
/// methods and every status rule in <see cref="ExchangeRateService"/>: <c>AsOf</c> newest first, then
/// <c>UpdatedAt ?? CreatedAt</c> newest first, then a stored row before an inverted one, then
/// <c>ExchangeRateId</c>. Source plays no part — a fetched row and a manual row compete on <c>AsOf</c> alone.
/// Those readers must agree with each other and with the NOK-hop selection, which is why none of them may
/// restate the ordering; a source-lint in <c>ExchangeRateHopAndStatusTests</c> fails on a second copy in
/// <c>Odyssey.Core</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Inverted candidates (issue #472).</b> A fetched <c>B → A</c> row competes for the logical pair
/// <c>A → B</c> as <c>1 / Rate</c>. On a FULL tie — same <c>AsOf</c>, same <c>UpdatedAt ?? CreatedAt</c> — the
/// stored row wins. That is what keeps a deployment whose sync still stores both directions (each observation's
/// two rows share one save, so one <c>CreatedAt</c>) giving exactly the results it gave before inversion
/// existed. The key sits before the id because a <see cref="Guid"/> order is arbitrary.
/// </para>
/// <para>
/// The SQL predicates stop at the <c>UpdatedAt ?? CreatedAt</c> key: a <see cref="Guid"/> has no translatable
/// ordering operator. Two rows of one pair tying on both instants to the microsecond is not reachable through
/// any write path — fetched rows are unique per <c>(pair, AsOf, provider)</c> — and every in-memory pick
/// (<see cref="Newest{T}"/>) applies the id as the final key, so a row's reported status is always unique.
/// The stored-beats-inverted key is expressed in SQL by counting a reversed competitor only when it is
/// STRICTLY newer (<see cref="StatusRank"/>).
/// </para>
/// </remarks>
public static class ExchangeRateOrdering
{
    /// <summary>Newest first, by the three keys a stored row has.</summary>
    public static IOrderedQueryable<ExchangeRate> NewestFirst(this IQueryable<ExchangeRate> rates) =>
        rates.OrderByDescending(rate => rate.AsOf)
            .ThenByDescending(rate => rate.UpdatedAt ?? rate.CreatedAt)
            .ThenByDescending(rate => rate.ExchangeRateId);

    /// <summary>The newest of already-materialised candidates, stored and inverted alike, by the four keys.</summary>
    public static T? Newest<T>(IEnumerable<T> candidates)
        where T : class, IRateCandidate =>
        candidates
            .OrderByDescending(candidate => candidate.AsOf)
            .ThenByDescending(candidate => candidate.Tiebreak)
            .ThenBy(candidate => candidate.Inverted)
            .ThenByDescending(candidate => candidate.ExchangeRateId)
            .FirstOrDefault();

    /// <summary>
    /// The status rank of a row: <c>0</c> Current, <c>1</c> Historical, <c>2</c> Upcoming — the
    /// <see cref="ExchangeRateStatus"/> ordinals. A row is Upcoming while its <c>AsOf</c> is after
    /// <paramref name="now"/>. Otherwise it is Historical when a newer row of its pair is already in force, or
    /// when a STRICTLY newer fetched row of the swapped pair is (that row wins the pair as its inverse). The two
    /// are separate <c>EXISTS</c>, each on the <c>(From, To, AsOf)</c> index, never one <c>OR</c> across pairs.
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
                // A CASE arm of its own, not an OR with the arm above: an OR across two correlated EXISTS
                // stops MariaDB answering either from the index and turned a 0.3 s sort into a timeout.
                : all.Any(reversed =>
                    reversed.FromCurrencyCode == rate.ToCurrencyCode
                    && reversed.ToCurrencyCode == rate.FromCurrencyCode
                    && reversed.AsOf >= rate.AsOf
                    && reversed.AsOf <= now
                    && CurrencyConversionService.FetchedProviders.Contains(reversed.Provider)
                    && (reversed.AsOf > rate.AsOf
                        || (reversed.UpdatedAt ?? reversed.CreatedAt) > (rate.UpdatedAt ?? rate.CreatedAt)))
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

    /// <summary>True for a fetched row standing in, as its reciprocal, for the reverse pair (issue #472).</summary>
    bool Inverted => false;
}
