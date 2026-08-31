namespace Odyssey.Dtos.Finance;

/// <summary>
/// The one implementation of "this contract is over".
///
/// <para>
/// It lives in <c>Odyssey.Dtos</c> — which has zero project references and is reachable from both
/// halves of the stack, the WASM client included — because <b>both</b> sides need it and must agree:
/// the service refuses to archive a contract that has not ended, and the client disables the Archive
/// action for the same reason. A client-side re-implementation of a server rule is the defect class
/// CLAUDE.md already forbids for caps; sharing the predicate is what keeps this clear of it.
/// </para>
/// </summary>
public static class ContractLifecycle
{
    /// <summary>
    /// Whether a contract's term is over: its end date has passed, or its one-off completion date has
    /// arrived.
    ///
    /// <para>
    /// The two boundaries are deliberately different and both match <c>ContractService.DeriveStatus</c>
    /// exactly — <c>end &lt; today</c> is what makes a term <c>Expired</c>, while
    /// <c>completion &lt;= today</c> is what settles a one-off. Note a settled one-off is NOT
    /// <c>Expired</c>: it derives as <c>Active</c>, because it is a completed record rather than a
    /// lapsed term. That is why this tests the dates rather than comparing against a status.
    /// </para>
    /// </summary>
    /// <param name="endDate">The contract's end date, if it has a term.</param>
    /// <param name="completionDate">The contract's completion date, if it is a one-off.</param>
    /// <param name="today">"Today" in the caller's clock — UTC on the server.</param>
    public static bool HasEnded(DateTime? endDate, DateTime? completionDate, DateTime today) =>
        (endDate is { } end && end.Date < today.Date)
        || (completionDate is { } completion && completion.Date <= today.Date);
}
