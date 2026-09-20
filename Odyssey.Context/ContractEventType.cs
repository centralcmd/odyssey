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
    PriceChanged = 6,

    /// <summary>
    /// Correspondence the user sent about the agreement. Kept even though an event has no contact link
    /// (issue #138 Non-Goal 5): the event records <i>that</i> an email was sent and what it said, and
    /// the recipient is named in the title or description meanwhile.
    /// </summary>
    EmailSent = 7,
    Other = 8,
}
