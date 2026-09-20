namespace Odyssey.Dtos.Finance;

/// <summary>
/// Derived, never stored (issue #174 §6). Computed per request from the contract's dates and its four
/// stamps: <c>Archived</c> wins. Then the <b>signature layer</b> (issue #145): an unsigned contract —
/// one with no <c>Signed</c> stamp — is <see cref="Ready"/> when its <c>Ready</c> stamp is present and
/// <see cref="Draft"/> when it is not, and that short-circuits the date chain entirely. Otherwise, for
/// a one-off (completion date set): <c>Upcoming</c> until the completion date, else <c>Active</c>; for
/// a term: <c>Upcoming</c> (start in the future); then <c>Expired</c> (end in the past); otherwise
/// <c>Active</c>.
///
/// <para>
/// <see cref="Paused"/> then <b>replaces <see cref="Active"/> and nothing else</b> (issue #140 §8), so
/// the full precedence reads
/// <c>Archived &gt; Draft/Ready &gt; Upcoming &gt; Expired &gt; Paused &gt; Active</c>: a terminal
/// fact outranks a temporary one, so a paused contract whose term has since run out reads
/// <see cref="Expired"/> while keeping its pause stamp.
/// </para>
///
/// <para>
/// <b>The signature layer sits ABOVE the date chain deliberately.</b> An unsigned contract with a
/// future start date reads <see cref="Draft"/>/<see cref="Ready"/> rather than <see cref="Upcoming"/>
/// — its dates describe a term nobody has agreed to, and reporting <c>Upcoming</c> would assert a
/// commitment that does not exist. An unsigned contract whose end date has passed reads
/// <see cref="Draft"/>/<see cref="Ready"/> rather than <see cref="Expired"/> — a term cannot lapse
/// before it begins, and what the reader has to act on is an abandoned negotiation, not a retired
/// agreement.
/// </para>
///
/// <para>
/// <b>The ordinals are not the reading order.</b> <see cref="Draft"/> and <see cref="Ready"/> are
/// appended, so sorting on the ordinal puts the two earliest lifecycle states last, behind
/// <see cref="Archived"/>. <see cref="ContractStatusOrder"/> holds the lifecycle rank every sort and
/// every status list reads instead — the same split <c>OdsTypeRegistries.ContractTypes</c> makes for
/// <c>ContractType</c>.
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

    /// <summary>
    /// Recorded but not yet marked ready for signature (issue #145) — no <c>Ready</c> stamp and no
    /// <c>Signed</c> stamp. The state a contract is created in when neither is supplied, which is the
    /// normal path. On file, not in force: excluded from the run rate and the upcoming charges,
    /// counted in the by-type headcount. Appended, so no existing ordinal moves.
    /// </summary>
    Draft = 5,

    /// <summary>
    /// Ready for signature and not yet signed (issue #145) — a <c>Ready</c> stamp, no <c>Signed</c>
    /// stamp. Like <see cref="Draft"/> it is on file but not in force. Appended, so no existing
    /// ordinal moves.
    /// </summary>
    Ready = 6,
}
