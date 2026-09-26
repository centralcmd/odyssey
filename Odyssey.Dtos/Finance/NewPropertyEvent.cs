using System.ComponentModel.DataAnnotations;

namespace Odyssey.Dtos.Finance;

/// <summary>
/// Adds one entry to a property's event log (<c>POST /api/properties/{propertyId}/events</c>, issue
/// #209 §5.2). Carries <b>only the event's own scalars</b>: the owner is the route, and there is no
/// <c>source</c>, <c>createdBy</c> or <c>createdAtUtc</c> — a body carrying any of them is ignored, so a
/// <see cref="ContractEventSource.System"/> row cannot be forged by construction.
/// </summary>
public sealed record NewPropertyEvent
{
    /// <summary>
    /// What happened. Non-nullable, so an omitted <c>type</c> binds to
    /// <see cref="PropertyEventType.Other"/>; deliberately <b>not</b> <c>[Required]</c>. Must be legal
    /// for the property's type per <see cref="PropertyEventTypeMatrix"/> and not system-only, else
    /// <c>422</c> keyed <c>type</c>.
    /// </summary>
    [EnumDataType(typeof(PropertyEventType))]
    public PropertyEventType Type { get; set; } = PropertyEventType.Other;

    /// <summary>The short label. Required for every type; whitespace-only is rejected as empty.</summary>
    [Required]
    [StringLength(256, MinimumLength = 1)]
    public required string Title { get; set; }

    /// <summary>A longer account of what happened.</summary>
    [StringLength(1024)]
    public string? Description { get; set; }

    /// <summary>The user's own working notes — readable by every <c>properties.read</c> holder.</summary>
    [StringLength(1024)]
    public string? Notes { get; set; }

    /// <summary>
    /// When it happened, UTC. More than a minute ahead of the server clock is a <c>422</c> — the bound is
    /// runtime state, so it is checked in the service rather than a <c>[Range]</c>.
    /// </summary>
    [Required]
    public required DateTime OccurredAt { get; set; }
}
