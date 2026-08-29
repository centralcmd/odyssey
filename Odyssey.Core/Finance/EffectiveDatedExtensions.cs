using Odyssey.Context;

namespace Odyssey.Core.Finance;

/// <summary>
/// The single home for the temporal "value in force" rule shared by account terms and estimates: the
/// entry with the greatest <see cref="IEffectiveDated.EffectiveFrom"/> wins, ties broken by the most
/// recently created row (<see cref="IEffectiveDated.CreatedAtUtc"/>). Callers materialize the
/// candidate rows (already narrowed to <c>EffectiveFrom &lt;= asOf</c>), group them by whatever
/// dimension they resolve over (account, term kind, …), and pick <see cref="MostEffective{T}"/> from
/// each group. Keeping the order/tie-break here means a future change to the tie-break semantics is
/// one edit rather than four.
/// </summary>
public static class EffectiveDatedExtensions
{
    /// <summary>Orders entries newest-effective first (the supersession order), tie-broken by newest created.</summary>
    public static IOrderedEnumerable<T> OrderByEffectiveDescending<T>(this IEnumerable<T> source) where T : IEffectiveDated =>
        source.OrderByDescending(entry => entry.EffectiveFrom).ThenByDescending(entry => entry.CreatedAtUtc);

    /// <summary>Returns the entry currently in force within the group, or <c>null</c> if the group is empty.</summary>
    public static T? MostEffective<T>(this IEnumerable<T> source) where T : class, IEffectiveDated =>
        source.OrderByEffectiveDescending().FirstOrDefault();
}
