namespace Odyssey.Context;

/// <summary>
/// How a <see cref="ContractEvent"/> row came into existence (issue #154 §4). Persisted as its
/// <c>int</c> ordinal, so the numbering is a <b>wire and persistence contract</b> like every other
/// enum in the module.
/// </summary>
/// <remarks>
/// <para>
/// The column is <b>server-owned and never accepted from a request body</b>: neither
/// <c>NewContractEvent</c> nor <c>UpdateContractEvent</c> carries it, so every event written over HTTP
/// is <see cref="User"/> <i>by construction</i> rather than by a check — there is no request shape
/// that forges a <see cref="System"/> row.
/// </para>
/// <para>
/// It is also <b>immutable across an update</b>. <c>Source</c> records how the row came to exist,
/// which an edit does not change; flipping an edited <see cref="System"/> row to <see cref="User"/>
/// would make the log's one provenance signal depend on whether anyone had since fixed a typo.
/// </para>
/// </remarks>
public enum ContractEventSource
{
    /// <summary>Entered by a person through <c>POST …/events</c>. The entity default.</summary>
    User = 0,

    /// <summary>Recorded by the server alongside a change it made.</summary>
    System = 1,
}
