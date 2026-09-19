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
/// Two conditions fall back, and they are NOT the same condition.
/// <list type="bullet">
/// <item><b>Unspecified</b> — "nobody has said". Rendered as a stated absence, never as the
/// deliberate <c>Other</c> ("somebody looked and none of these fit").</item>
/// <item><b>Unknown ordinal</b> — a real version-skew state, since issue #121 lets members be
/// appended and fixes only the initial seven. Named as unrecognised rather than collapsed into
/// <c>Unspecified</c>, which would read as "no role stated" and is a different, wrong claim.</item>
/// </list>
/// </para>
/// </remarks>
public static class PartyRoleLabel
{
    /// <summary>What the tile's overline reads for a role with no name of its own.</summary>
    public const string UnspecifiedText = "No role set";

    /// <summary>What an ordinal outside this build's registry reads as.</summary>
    public const string UnknownText = "Unrecognised role";

    /// <summary>
    /// The role as text. <see cref="UnspecifiedText"/> for <see cref="ContractPartyRole.Unspecified"/>,
    /// <see cref="UnknownText"/> for an ordinal this build does not know, and the registry label
    /// otherwise.
    /// </summary>
    public static string For(ContractPartyRole role) => role switch
    {
        ContractPartyRole.Unspecified => UnspecifiedText,
        _ => OdsTypeRegistries.ContractPartyRoleOf(role)?.Label ?? UnknownText,
    };

    /// <summary>
    /// True when the role is stated AND this build can name it — the only case that reads as a
    /// category rather than as an absence, and the only case whose tile offers <b>Edit party</b>: a
    /// client that cannot name a role cannot round-trip it through a full-replacement <c>PUT</c>
    /// without silently rewriting it (#122 §3 state 6).
    /// </summary>
    public static bool IsNamed(ContractPartyRole role) =>
        role != ContractPartyRole.Unspecified && OdsTypeRegistries.ContractPartyRoleOf(role) is not null;

    /// <summary>
    /// True when this build's registry has no entry for the ordinal — the version-skew case alone,
    /// which is what withholds the edit affordance. <see cref="ContractPartyRole.Unspecified"/> is
    /// perfectly editable and is deliberately not included.
    /// </summary>
    public static bool IsUnknown(ContractPartyRole role) =>
        OdsTypeRegistries.ContractPartyRoleOf(role) is null;
}
