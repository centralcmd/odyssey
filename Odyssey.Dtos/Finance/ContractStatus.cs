namespace Odyssey.Dtos.Finance;

/// <summary>
/// Derived, never stored (issue #174 §6). Computed per request from the contract's dates and its two
/// stamps: <c>Archived</c> wins. For a one-off (completion date set): <c>Upcoming</c> until the
/// completion date, else <c>Active</c>. For a term: <c>Upcoming</c> (start in the future); then
/// <c>Expired</c> (end in the past); otherwise <c>Active</c>.
///
/// <para>
/// <see cref="Paused"/> then <b>replaces <see cref="Active"/> and nothing else</b> (issue #140 §8), so
/// the full precedence reads <c>Archived &gt; Upcoming &gt; Expired &gt; Paused &gt; Active</c>: a
/// terminal fact outranks a temporary one, and a paused contract whose term has since run out reads
/// <see cref="Expired"/> while keeping its pause stamp.
/// </para>
/// </summary>
public enum ContractStatus
{
    Active = 0,
    Upcoming = 1,
    Expired = 2,
    Archived = 3,

    /// <summary>
    /// Temporarily suspended (issue #140) — still listed, still fully editable, contributing nothing
    /// to the run rate or the upcoming charges. Appended: no existing ordinal moves, because an
    /// ordinal is a wire and persistence contract.
    /// </summary>
    Paused = 4,
}
