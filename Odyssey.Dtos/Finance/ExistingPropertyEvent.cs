namespace Odyssey.Dtos.Finance;

/// <summary>
/// One entry in a property's event log as returned on the read path (issue #209 §5.1).
/// </summary>
/// <remarks>
/// <b>No claim crossover.</b> Every field is the event's own except <see cref="CreatedBy"/>, a display
/// label resolved at the API edge by <c>IUserDisplayNameResolver</c> — the raw author id is never
/// exposed, and a deleted author reads back as "Unknown user". There is no estimate figure, property
/// detail or linked entity here: an event links to nothing (Non-Goal 3).
/// </remarks>
public sealed record ExistingPropertyEvent
{
    public required Guid PropertyEventId { get; set; }

    public required Guid PropertyId { get; set; }

    public PropertyEventType Type { get; set; }

    /// <summary>
    /// Hand-written or server-recorded. Read-only — no request DTO carries it, so a caller cannot forge
    /// a <see cref="ContractEventSource.System"/> row, and an edit does not change it.
    /// </summary>
    public ContractEventSource Source { get; set; }

    public required string Title { get; set; }

    public string? Description { get; set; }

    public string? Notes { get; set; }

    public DateTime OccurredAt { get; set; }

    /// <summary>Who recorded the event, as a display label — never a raw user id.</summary>
    public string? CreatedBy { get; set; }

    public DateTime CreatedAtUtc { get; set; }
}
