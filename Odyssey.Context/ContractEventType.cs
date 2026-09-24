namespace Odyssey.Context;

/// <summary>
/// What kind of thing happened to a contract (issue #138 §4). Persisted as its <c>int</c> ordinal, so
/// the numbering is a <b>wire and persistence contract</b>: renumbering silently re-labels every
/// stored row and every request body already in flight.
/// </summary>
/// <remarks>
/// <see cref="Other"/> is the catch-all and the entity default — a kind the eight named members do not
/// cover is expressed as <see cref="Other"/> plus whatever the user writes in the title, exactly the
/// shape <c>Contract.Name</c> + <see cref="ContractType.Other"/> already uses.
/// </remarks>
public enum ContractEventType
{
    Signed = 0,
    Amended = 1,
    Renewed = 2,
    Extended = 3,
    NoticeGiven = 4,
    Terminated = 5,
    TermChanged = 6,

    /// <summary>
    /// Correspondence the user sent about the agreement. Kept even though an event has no contact link
    /// (issue #138 Non-Goal 5): the event records <i>that</i> an email was sent and what it said, and
    /// the recipient is named in the title or description meanwhile.
    /// </summary>
    EmailSent = 7,
    Other = 8,

    // ── Appended by issue #154 at ordinals 9-17 ──────────────────────────────────
    //
    // Every existing ordinal above is untouched, Other included: a stored 8 must keep meaning Other.
    // The consequence is that Other is no longer the LAST ordinal, so the enum's storage order and the
    // UI's reading order diverge — the same split ContractType already carries. OdsTypeRegistries
    // documents its TRAILING entry as the fallback for an unknown ordinal, so Other must stay last in
    // that registry even though it no longer ends this enum.
    //
    // There is deliberately no new Signed member: ordinal 0 already is one, and the signing transition
    // reuses it.

    /// <summary>The contract was suspended.</summary>
    Paused = 9,

    /// <summary>The contract was resumed.</summary>
    Unpaused = 10,

    /// <summary>The contract was marked ready for signature.</summary>
    Ready = 11,

    /// <summary>The ready-for-signature mark was withdrawn.</summary>
    Unready = 12,

    /// <summary>The signed date was cleared.</summary>
    Unsigned = 13,

    /// <summary>The contract was archived.</summary>
    Archived = 14,

    /// <summary>The contract was restored from the archive.</summary>
    Unarchived = 15,

    /// <summary>A party was linked to the contract.</summary>
    PartyAdded = 16,

    /// <summary>A party was unlinked from the contract.</summary>
    PartyRemoved = 17,
}
