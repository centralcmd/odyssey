namespace Odyssey.Dtos.Finance;

/// <summary>
/// Why a net-worth history came back with no points (issue #90 §3.2).
///
/// <para>
/// The causes are carried explicitly because they are <b>not</b> inferable from the rest of the
/// payload: an earlier draft made the responses byte-identical apart from
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
/// <para>
/// <b>"Account" in a member name now means "member"</b> (issue #214 D8): an account, plus a property
/// when the response's <c>PropertiesIncluded</c> is true. The names stay because renaming a member is
/// source-breaking for every consumer switch; with properties not included every reason is computed
/// exactly as before.
/// </para>
/// </remarks>
public enum NetWorthEmptyReason
{
    /// <summary>No series could be built. The catch-all, and the only one that is not a statement about the data.</summary>
    NotBuilt = 0,

    /// <summary>
    /// There are no members to chart — no account had been opened by now, and no included property
    /// contributes a value before now while held.
    /// </summary>
    NoAccounts = 1,

    /// <summary>
    /// Some member was live at some point in the window — an account inside its term, or a property
    /// held with an estimate in force — but none could be converted into the main currency for any
    /// period.
    /// </summary>
    NothingConvertible = 2,

    /// <summary>
    /// The requested window ends before the earliest member's first contribution — an account's
    /// opening, or an included property's first valued, held instant. The caller's input was valid, so
    /// this is a <c>200</c> with no points — never a <c>400</c>.
    /// </summary>
    WindowBeforeFirstAccount = 3,

    /// <summary>
    /// Members exist and the window is not before the first of them, but no member was live at any
    /// point in the window: every account had closed before it (issue #99), and no included property
    /// was held with an estimate in force. A held but never-estimated property is not live, so it
    /// yields this rather than <see cref="NothingConvertible"/>.
    /// </summary>
    /// <remarks>
    /// The mirror of <see cref="WindowBeforeFirstAccount"/>, and unreachable before issue #99, when a
    /// closed account contributed forever. It is separate from <see cref="NothingConvertible"/>
    /// because the two say opposite things about the deployment: one is a missing exchange rate to fix,
    /// the other is a correct and complete answer about a window with nothing in it.
    /// </remarks>
    WindowAfterAllAccountsClosed = 4,
}
