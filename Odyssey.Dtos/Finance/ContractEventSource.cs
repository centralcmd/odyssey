namespace Odyssey.Dtos.Finance;

/// <summary>
/// How an event row came into existence (issue #154 §4) — what lets a reader tell a hand-written line
/// from one the server recorded. Ordinals are a <b>wire contract</b>: they are persisted in
/// <c>ContractEvents</c> and appear in response bodies and the <c>?source=</c> filter.
/// </summary>
/// <remarks>
/// Deliberately absent from <see cref="NewContractEvent"/> and <see cref="UpdateContractEvent"/>: a
/// caller has no field in which to forge a <see cref="System"/> row, which makes it a compile-time
/// impossibility rather than a validation rule.
/// </remarks>
public enum ContractEventSource
{
    /// <summary>Entered by a person through <c>POST …/events</c>.</summary>
    User = 0,

    /// <summary>Recorded by the server alongside a change it made.</summary>
    System = 1,
}
