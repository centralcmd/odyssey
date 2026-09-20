namespace Odyssey.Dtos.Finance;

/// <summary>
/// The lifecycle <b>reading</b> order of <see cref="ContractStatus"/> —
/// <c>Draft → Ready → Upcoming → Active → Paused → Expired → Archived</c> — decoupled from the wire
/// ordinal (issue #145 §8).
///
/// <para>
/// It exists because <see cref="ContractStatus.Draft"/> and <see cref="ContractStatus.Ready"/> are
/// <b>appended</b> members (5 and 6): an ordinal is a persistence and wire contract and is never
/// renumbered, so sorting on it would put the two <i>earliest</i> lifecycle states <i>last</i>, behind
/// <see cref="ContractStatus.Archived"/>. Only the reading order changes, in one place — exactly as
/// <c>OdsTypeRegistries.ContractTypes</c> already does for <c>ContractType</c>'s appended members.
/// </para>
///
/// <para>
/// It lives in <c>Odyssey.Dtos</c> — zero project references, reachable from both halves of the stack
/// including the WASM client — for the same reason <see cref="ContractLifecycle"/> does: the server
/// sorts the list by this rank and the client writes its status filter, its summary pills and its sort
/// options in it, and a client-side re-implementation of a server rule is the defect class CLAUDE.md
/// forbids.
/// </para>
/// </summary>
public static class ContractStatusOrder
{
    /// <summary>
    /// The lifecycle order, ascending. Every status list on the page is written in it, and
    /// <c>ContractSortBy.Status</c> sorts by it.
    /// </summary>
    private static readonly ContractStatus[] Lifecycle =
    [
        ContractStatus.Draft,
        ContractStatus.Ready,
        ContractStatus.Upcoming,
        ContractStatus.Active,
        ContractStatus.Paused,
        ContractStatus.Expired,
        ContractStatus.Archived,
    ];

    public static readonly IReadOnlyList<ContractStatus> Order = Lifecycle;

    /// <summary>
    /// The lifecycle rank of a status. An <b>unrecognised</b> member — a client or a stored value from
    /// a newer server enum — ranks <i>last</i> rather than first, so an unknown state never displaces
    /// the two the reader is looking for at the top of the list.
    /// </summary>
    public static int Rank(ContractStatus status)
    {
        var index = Array.IndexOf(Lifecycle, status);
        return index < 0 ? Lifecycle.Length : index;
    }

    /// <summary>
    /// The two states that are on file but <b>not in force</b>. The single predicate every money
    /// roll-up gates on — never a re-test of the two stamps, which is how "counts one set, prices
    /// another" gets in (issue #140 §3).
    /// </summary>
    public static bool IsUnsigned(ContractStatus status) =>
        status is ContractStatus.Draft or ContractStatus.Ready;
}
