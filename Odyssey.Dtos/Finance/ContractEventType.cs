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
}
