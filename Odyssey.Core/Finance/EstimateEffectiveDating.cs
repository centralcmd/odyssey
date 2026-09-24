using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Odyssey.Context;

namespace Odyssey.Core.Finance;

/// <summary>
/// The single home of the two <b>query-shaped</b> rules every estimate table obeys (issue #167 §4): the
/// duplicate-<c>EffectiveFrom</c> conflict, and the "value in force as of a date" resolution (greatest
/// <c>EffectiveFrom</c> on or before the cutoff, ties broken by the most recently created row).
///
/// <para>
/// <b>Why these two and not the scalar checks.</b> <c>AccountEstimateService</c> and
/// <c>PropertyEstimateService</c> are sibling services over sibling tables, and — like
/// <c>AccountSmartTagService</c>/<c>ContractSmartTagService</c> — they duplicate their scalar checks
/// (non-negative value, owner-currency match): a divergence there fails loudly on the very request that
/// hits it. A divergence in either rule here fails <em>silently</em> — a duplicate one surface forbids is
/// permitted on the other, or the same data returns a different "current" number — so both live once.
/// That is the <c>Odyssey.Core/Imaging</c> reasoning, applied for its actual reason.
/// </para>
///
/// <para>
/// Every method takes an <b>already owner-scoped</b> query (and, for the conflict check, one already
/// excluding the row being updated), so no marker interface beyond <see cref="IEffectiveDated"/> is
/// forced onto the entities. <c>EffectiveDatedExtensions</c> is the in-memory counterpart of the same
/// ordering for callers that materialize candidate rows first; the two must agree, and
/// <c>EstimateEffectiveDatingTests</c> pins that they do. A source-lint fails if a second copy of either
/// rule appears in an estimate service.
/// </para>
/// </summary>
public static class EstimateEffectiveDating
{
    /// <summary>The supersession order: newest effective first, tie-broken by newest created.</summary>
    public static IOrderedQueryable<T> InSupersessionOrder<T>(this IQueryable<T> ownerScoped)
        where T : class, IEffectiveDated =>
        ownerScoped.OrderByDescending(e => e.EffectiveFrom).ThenByDescending(e => e.CreatedAtUtc);

    /// <summary>
    /// Whether an entry effective from exactly <paramref name="effectiveFrom"/> already exists in
    /// <paramref name="ownerScopedOthers"/> — the owner's rows, minus the one being updated.
    /// </summary>
    public static Task<bool> HasConflictAsync<T>(
        IQueryable<T> ownerScopedOthers, DateTime effectiveFrom, CancellationToken cancellationToken = default)
        where T : class, IEffectiveDated =>
        ownerScopedOthers.AnyAsync(e => e.EffectiveFrom == effectiveFrom, cancellationToken);

    /// <summary>The entry in force on <paramref name="cutoff"/>, or <c>null</c> when none is.</summary>
    public static Task<T?> ResolveCurrentAsync<T>(
        IQueryable<T> ownerScoped, DateTime cutoff, CancellationToken cancellationToken = default)
        where T : class, IEffectiveDated =>
        ownerScoped
            .Where(e => e.EffectiveFrom <= cutoff)
            .InSupersessionOrder()
            .FirstOrDefaultAsync(cancellationToken);

    /// <summary>
    /// A translatable selector for the value in force on <paramref name="cutoff"/> — a correlated
    /// subquery over the owner's estimate navigation, <c>null</c> when none is in force. This is what
    /// lets a list <em>sort</em> by the current value in SQL, before the page slice, while resolving
    /// "current" by the same rule <see cref="ResolveCurrentAsync{T}"/> does.
    /// </summary>
    /// <param name="estimates">The owner's estimate navigation, e.g. <c>p =&gt; p.Estimates</c>.</param>
    /// <param name="value">The estimate's value member, e.g. <c>e =&gt; e.Value</c>.</param>
    public static Expression<Func<TOwner, decimal?>> CurrentValue<TOwner, T>(
        Expression<Func<TOwner, IEnumerable<T>>> estimates,
        Expression<Func<T, decimal>> value,
        DateTime cutoff)
        where T : class, IEffectiveDated
    {
        // Built as a template over a stand-in parameter and then spliced. The ordering is restated
        // here rather than calling InSupersessionOrder because EF cannot translate a call to a custom
        // method inside a projection; it is the same order, in the same file, and a test pins it.
        Expression<Func<IEnumerable<T>, decimal?>> template = rows => rows
            .Where(e => e.EffectiveFrom <= cutoff)
            .OrderByDescending(e => e.EffectiveFrom)
            .ThenByDescending(e => e.CreatedAtUtc)
            .Select(e => (decimal?)Placeholder(e))
            .FirstOrDefault();

        var withValue = new PlaceholderInliner<T>(value).Visit(template.Body);
        var body = new ParameterReplacer(template.Parameters[0], estimates.Body).Visit(withValue);
        return Expression.Lambda<Func<TOwner, decimal?>>(body, estimates.Parameters);
    }

    // Never invoked — PlaceholderInliner replaces every call with the caller's value selector.
    private static decimal Placeholder<T>(T estimate) =>
        throw new InvalidOperationException("EstimateEffectiveDating's value placeholder must be inlined.");

    private sealed class ParameterReplacer(ParameterExpression from, Expression to) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) =>
            node == from ? to : base.VisitParameter(node);
    }

    private sealed class PlaceholderInliner<T>(Expression<Func<T, decimal>> value) : ExpressionVisitor
    {
        protected override Expression VisitMethodCall(MethodCallExpression node)
        {
            if (node.Method.IsGenericMethod
                && node.Method.GetGenericMethodDefinition().Name == nameof(Placeholder)
                && node.Method.DeclaringType == typeof(EstimateEffectiveDating))
            {
                var argument = Visit(node.Arguments[0]);
                return new ParameterReplacer(value.Parameters[0], argument).Visit(value.Body);
            }

            return base.VisitMethodCall(node);
        }
    }
}
