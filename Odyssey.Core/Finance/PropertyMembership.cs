using System.Linq.Expressions;
using Odyssey.Context;

namespace Odyssey.Core.Finance;

/// <summary>
/// The single definition of when a <see cref="Property"/> belongs to net worth (issue #214 V1, V11):
/// a property is <b>held</b> at instant <c>b</c> when it had been acquired before <c>b</c> and had not
/// yet been disposed of at it. Both valuation services read this rule, so <c>/accounts/totals</c> and
/// <c>/accounts/net-worth-history</c> cannot disagree about which properties count.
/// </summary>
/// <remarks>
/// <para>
/// <b>Exclusive at both ends</b>, mirroring an account's <c>Opened &lt; b</c> and
/// <c>Closed &lt;= b ⇒ gone</c>: a property disposed of at exactly <c>b</c> is already gone by it, and
/// one acquired at exactly <c>b</c> has not yet arrived. So a property whose acquisition and disposal
/// fall on the same instant is never held. At <c>now</c> this agrees with
/// <c>PropertyService.DeriveStatus</c> (<c>disposed &lt;= now ⇒ Disposed</c>).
/// </para>
/// <para>
/// <b>The list-declutter flag is never read</b> (issue #99, #214 D3). It is a reversible toggle with no
/// transition history, so it cannot be evaluated per point; disposal is the valuation event and has a
/// date. <c>NetWorthMembershipSourceTests</c> scans this file for that reason.
/// </para>
/// <para>
/// The rule has <b>two bodies</b> — <see cref="HeldAt(DateTime)"/> is translated to SQL for
/// <c>/totals</c>, <see cref="IsHeldAt"/> runs in the history fold over its projected rows — and a
/// parity test evaluates both over the same boundary cases, because two bodies of one rule is exactly
/// where drift hides.
/// </para>
/// </remarks>
public static class PropertyMembership
{
    /// <summary>The translatable form: properties held at <paramref name="bound"/>.</summary>
    public static Expression<Func<Property, bool>> HeldAt(DateTime bound) =>
        property => (property.AcquiredDate == null || property.AcquiredDate < bound)
                    && (property.DisposedDate == null || bound < property.DisposedDate);

    /// <summary>The in-memory form of <see cref="HeldAt(DateTime)"/>, over a projected row's two dates.</summary>
    public static bool IsHeldAt(DateTime? acquiredDate, DateTime? disposedDate, DateTime bound) =>
        (acquiredDate is null || acquiredDate < bound)
        && (disposedDate is null || bound < disposedDate);

    /// <summary>
    /// The instant a property first contributes a value — <c>max(AcquiredDate, first EffectiveFrom)</c>
    /// — or <c>null</c> when it never contributes before <paramref name="now"/> while held. Used for the
    /// history's grid start and its <c>NoAccounts</c> test; it never moves either for a property that
    /// has no estimate before <paramref name="now"/>, or whose first contribution would fall on or after
    /// its own disposal.
    /// </summary>
    /// <remarks>
    /// An individual estimate dated on or after disposal never takes effect, but it does not make an
    /// otherwise-valued property ineligible — only the <i>first</i> estimate decides the instant.
    /// </remarks>
    public static DateTime? FirstContribution(
        DateTime? acquiredDate,
        DateTime? disposedDate,
        DateTime? firstEffectiveFrom,
        DateTime now)
    {
        if (firstEffectiveFrom is not { } first)
        {
            return null;
        }

        var contribution = acquiredDate is { } acquired && acquired > first ? acquired : first;
        if (contribution >= now)
        {
            return null;
        }

        if (disposedDate is { } disposed && contribution >= disposed)
        {
            return null;
        }

        return contribution;
    }
}
