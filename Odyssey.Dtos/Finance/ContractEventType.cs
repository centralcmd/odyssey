namespace Odyssey.Dtos.Finance;

/// <summary>
/// What kind of thing happened to a contract (issue #138 §4). Ordinals are a <b>wire contract</b>:
/// they are persisted in <c>ContractEvents</c> and appear in request and response bodies, so
/// renumbering silently re-labels existing rows.
/// </summary>
public enum ContractEventType
{
    Signed = 0,
    Amended = 1,
    Renewed = 2,
    Extended = 3,
    NoticeGiven = 4,
    Terminated = 5,
    PriceChanged = 6,
    EmailSent = 7,

    /// <summary>The catch-all and the default — a kind the eight named members do not cover, carried by the title.</summary>
    Other = 8,

    // Appended by issue #154 at ordinals 9-17; every existing ordinal is untouched. Other is therefore
    // no longer the last ordinal, and OdsTypeRegistries.ContractEventTypes pulls it to the end of the
    // READING order instead — its trailing entry is the documented fallback for an unknown ordinal.

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
