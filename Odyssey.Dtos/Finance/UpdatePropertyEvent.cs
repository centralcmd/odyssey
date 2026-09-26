using System.ComponentModel.DataAnnotations;

namespace Odyssey.Dtos.Finance;

/// <summary>
/// Replaces one entry on a property's event log (<c>PUT …/events/{eventId}</c>, issue #209 §5.3).
/// Identical field set to <see cref="NewPropertyEvent"/>, declared separately so the two request
/// contracts can diverge without a silent breaking change on the other.
/// </summary>
/// <remarks>
/// <b>Full replacement — <c>null</c> clears, it does not mean "unchanged".</b> Omitting
/// <see cref="Description"/> or <see cref="Notes"/> clears that field, and omitting <see cref="Type"/>
/// resets it to <see cref="PropertyEventType.Other"/> — the same trap <see cref="UpdateContractEvent"/>
/// documents. A system-only type may be <em>kept</em> on a row that already carries it, never
/// introduced. <c>source</c>, <c>createdBy</c> and <c>createdAtUtc</c> are never rewritten.
/// </remarks>
public sealed record UpdatePropertyEvent
{
    /// <summary>What happened. An omitted <c>type</c> binds to <see cref="PropertyEventType.Other"/>.</summary>
    [EnumDataType(typeof(PropertyEventType))]
    public PropertyEventType Type { get; set; } = PropertyEventType.Other;

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
