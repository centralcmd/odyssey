using System.ComponentModel.DataAnnotations;

namespace Odyssey.Dtos.Finance;

/// <summary>
/// Replaces one entry on a contract's event log (<c>PUT …/events/{eventId}</c>). Identical field set
/// to <see cref="NewContractEvent"/>, declared separately so the two request contracts can diverge
/// without a silent breaking change on the other.
/// </summary>
/// <remarks>
/// <b>Full replacement — <c>null</c> clears, it does not mean "unchanged".</b> Omitting
/// <see cref="Description"/> or <see cref="Notes"/> <b>clears</b> that field, and omitting
/// <see cref="Type"/> resets it to <see cref="ContractEventType.Other"/>. This is the opposite of
/// <see cref="UpdateContract"/>'s convention and the same as <see cref="ContractPartyRequest"/>'s.
///
/// <para>
/// <c>createdBy</c> and <c>createdAtUtc</c> are not rewritten by an update, and a body supplying them
/// is ignored.
/// </para>
/// </remarks>
public sealed record UpdateContractEvent
{
    /// <summary>
    /// What kind of thing happened. An omitted <c>type</c> binds to
    /// <see cref="ContractEventType.Other"/> rather than leaving the stored value alone.
    /// </summary>
    [EnumDataType(typeof(ContractEventType))]
    public ContractEventType Type { get; set; } = ContractEventType.Other;

    /// <summary>The short label. Required for every type; whitespace-only is rejected as empty.</summary>
    [Required]
    [StringLength(256, MinimumLength = 1)]
    public required string Title { get; set; }

    /// <summary>A longer account of what happened. Omitting it clears the stored value.</summary>
    [StringLength(1024)]
    public string? Description { get; set; }

    /// <summary>The user's own working notes. Omitting it clears the stored value.</summary>
    [StringLength(1024)]
    public string? Notes { get; set; }

    /// <summary>When it happened, UTC. Must not be in the future (checked in the service).</summary>
    [Required]
    public required DateTime OccurredAt { get; set; }
}
