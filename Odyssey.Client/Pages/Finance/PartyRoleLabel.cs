using Odyssey.Client.Components;
using Odyssey.Dtos.Finance;

namespace Odyssey.Client.Pages.Finance;

/// <summary>
/// The ONE place a <see cref="ContractPartyRole"/> becomes words (issue #122 §4, §6.2). Every channel
/// that names a role — the tile overline, the <c>OdsLiveAnnouncer</c> lines and the tile menu's
/// <c>aria-label</c> — goes through here, so the fallbacks apply identically in all of them.
/// </summary>
/// <remarks>
/// <para>
/// Without a single owner, a screen-reader user would be read the literal sentinel a sighted one is
/// deliberately never shown (WCAG 4.1.3 / 1.3.1): the fallbacks are a correctness property of the
/// accessible name, not a presentation detail of the tile.
/// </para>
/// <para>
/// <b>One condition falls back now, not two.</b> Issue #157 retired <c>Unspecified</c> — a role is
/// required on every write, so "nobody has said" is no longer a value the client can be handed. What
/// remains is the <b>unknown ordinal</b>: a real version-skew state, and a wider one than before,
/// since that change added nine members a deployed-but-stale client cannot name. It is named as
/// unrecognised rather than collapsed into <c>Other</c>, which would assert a deliberate role the
/// party may never have held.
/// </para>
/// </remarks>
public static class PartyRoleLabel
{
    /// <summary>What an ordinal outside this build's registry reads as.</summary>
    public const string UnknownText = "Unrecognised role";

    /// <summary>
    /// The role as text: <see cref="UnknownText"/> for an ordinal this build does not know, and the
    /// registry label otherwise.
    /// </summary>
    public static string For(ContractPartyRole role) =>
        OdsTypeRegistries.ContractPartyRoleOf(role)?.Label ?? UnknownText;

    /// <summary>
    /// True when this build can name the role — the only case that reads as a category rather than as
    /// an absence, and the only case whose tile offers <b>Edit party</b>: a client that cannot name a
    /// role cannot round-trip it through a full-replacement <c>PUT</c> without silently rewriting it
    /// (#122 §3 state 6).
    /// </summary>
    public static bool IsNamed(ContractPartyRole role) =>
        OdsTypeRegistries.ContractPartyRoleOf(role) is not null;

    /// <summary>
    /// True when this build's registry has no entry for the ordinal — the version-skew case, which is
    /// what withholds the edit affordance. Since issue #157 this is the exact complement of
    /// <see cref="IsNamed"/>: with <c>Unspecified</c> retired there is no third state.
    /// </summary>
    public static bool IsUnknown(ContractPartyRole role) =>
        OdsTypeRegistries.ContractPartyRoleOf(role) is null;
}
