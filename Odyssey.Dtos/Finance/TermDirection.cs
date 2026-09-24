namespace Odyssey.Dtos.Finance;

/// <summary>
/// The wire half of the mirrored pair (issue #159) — member-for-member aligned with
/// <c>Odyssey.Context.TermDirection</c> by ordinal, the same shape <see cref="TermValueUnit"/> already
/// has. Ordinals are a wire and persistence contract and are never renumbered; later members append.
/// </summary>
public enum TermDirection
{
    /// <summary>Money leaves the household — the default, and what an omitted direction means.</summary>
    Outgoing = 0,

    /// <summary>Money arrives.</summary>
    Incoming = 1,
}
