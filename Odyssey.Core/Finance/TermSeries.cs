using Odyssey.Context;

namespace Odyssey.Core.Finance;

/// <summary>
/// The one place a materialised set of <see cref="Term"/> rows is collapsed to "the value in force
/// per series". Three read paths resolve the same thing for three different owners — the account
/// record card (<c>AccountService</c>), the contract record card (<c>ContractService</c>) and the
/// <c>…/terms/current</c> endpoints (<c>TermService</c>) — and a series is a run of supersessions, so
/// the grouping key, the winner rule and the display order have to mean the same thing in all three.
/// </summary>
/// <remarks>
/// Callers narrow to their owner and to <c>EffectiveFrom &lt;= asOf</c> in SQL first; the grouping is
/// in memory over that bounded set, which is what keeps the current-value read to one indexed query.
/// </remarks>
public static class TermSeries
{
    /// <summary>
    /// Picks the in-force entry of each <c>(TermKind, LabelKey)</c> series out of
    /// <paramref name="candidates"/>, ordered by kind then by folded label.
    /// </summary>
    public static List<Term> Current(IEnumerable<Term> candidates) =>
        candidates
            .GroupBy(term => (term.TermKind, term.LabelKey))
            .Select(group => group.MostEffective()!)
            .OrderBy(term => term.TermKind)
            .ThenBy(term => term.LabelKey, StringComparer.Ordinal)
            .ToList();
}
