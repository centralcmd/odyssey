namespace Odyssey.Dtos.Finance;

/// <summary>
/// Why a net-worth history came back with no points (issue #90 §3.2).
///
/// <para>
/// The four causes are carried explicitly because they are <b>not</b> inferable from the rest of the
/// payload: an earlier draft made the four responses byte-identical apart from
/// <c>UnconvertedAccounts</c>, which cannot separate "no accounts" from "the window ends before the
/// first account was opened" at all. A client cannot be asked to infer a cause the payload does not
/// carry, and "no data yet" is the wrong thing to say when the cause is known.
/// </para>
/// </summary>
/// <remarks>
/// The enum is named <c>NetWorthEmptyReason</c> while the response property is <c>EmptyReason</c>: a
/// property whose name equals its type's name is legal but makes the bare name resolve to the property
/// inside the declaring type, which is a trap for exactly the comparison this enum exists for.
/// Ordinals cross the wire; append, do not renumber.
/// </remarks>
public enum NetWorthEmptyReason
{
    /// <summary>No series could be built. The catch-all, and the only one that is not a statement about the data.</summary>
    NotBuilt = 0,

    /// <summary>There are no active accounts to chart.</summary>
    NoAccounts = 1,

    /// <summary>Accounts exist, but none could be converted into the main currency for any period.</summary>
    NothingConvertible = 2,

    /// <summary>
    /// The requested window ends before the earliest account was opened. The caller's input was
    /// valid, so this is a <c>200</c> with no points — never a <c>400</c>.
    /// </summary>
    WindowBeforeFirstAccount = 3,
}
