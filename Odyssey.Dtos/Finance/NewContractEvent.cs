using System.ComponentModel.DataAnnotations;

namespace Odyssey.Dtos.Finance;

/// <summary>
/// Writes one entry onto a contract's event log — the body of both the add
/// (<c>POST …/events</c>) and the replace (<c>PUT …/events/{eventId}</c>), which take identical
/// fields (issue #138 §5). Carries <b>only the event's own scalars</b>: no id of, and no nested object
/// for, any other entity, so an event write has no path by which it could create or mutate anything
/// but the event itself.
/// </summary>
/// <remarks>
/// <b><c>null</c> means "clear this field", not "leave unchanged".</b> A <c>PUT</c> on an event is a
/// <b>full replacement</b>, the same rule <see cref="ContractPartyRequest"/> follows and the opposite
/// of <see cref="UpdateContract"/>. A client that omits <see cref="Description"/> or
/// <see cref="Notes"/> <b>clears</b> it; one that omits <see cref="Type"/> resets it to
/// <see cref="ContractEventType.Other"/>. This is the one trap in the contract API, and a reader
/// arriving from <see cref="UpdateContract"/> will assume the other convention.
///
/// <para>
/// <c>createdBy</c> / <c>createdAtUtc</c> are <b>not</b> accepted here. Both are server-stamped, and
/// a body carrying them is ignored rather than honoured — editing an event must never rewrite who
/// recorded it.
/// </para>
/// </remarks>
public sealed record NewContractEvent
{
    /// <summary>
    /// What kind of thing happened. Non-nullable, so an omitted <c>type</c> binds to
    /// <see cref="ContractEventType.Other"/> — the same shape <see cref="NewContract.Type"/> and
    /// <see cref="ContractPartyRequest.Role"/> use. Deliberately <b>not</b> <c>[Required]</c>.
    /// </summary>
    [EnumDataType(typeof(ContractEventType))]
    public ContractEventType Type { get; set; } = ContractEventType.Other;

    /// <summary>
    /// The short label. Required for <b>every</b> type — a named type does not make it optional or
    /// derived. Whitespace-only is rejected as empty.
    /// </summary>
    [Required]
    [StringLength(256, MinimumLength = 1)]
    public required string Title { get; set; }

    /// <summary>A longer account of what happened. Shown on the timeline beside the title.</summary>
    [StringLength(1024)]
    public string? Description { get; set; }

    /// <summary>
    /// The user's own working notes. The timeline does not render this, but that is a presentation
    /// rule and not an access one: it is readable by every caller holding <c>contracts.read</c>
    /// (issue #138 §4.1).
    /// </summary>
    [StringLength(1024)]
    public string? Notes { get; set; }

    /// <summary>
    /// When it happened, UTC. Must not be in the future. The bound is the server clock — runtime state
    /// rather than a compile-time constant — so it is checked in the service, not in a <c>[Range]</c>.
    /// </summary>
    [Required]
    public required DateTime OccurredAt { get; set; }
}
