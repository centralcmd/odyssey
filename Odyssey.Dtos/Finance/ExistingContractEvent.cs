namespace Odyssey.Dtos.Finance;

/// <summary>
/// One entry in a contract's event log as returned on the read path (issue #138 §5).
/// </summary>
/// <remarks>
/// <b>No claim crossover.</b> Every field is the event's own except <see cref="CreatedBy"/> — there is
/// no contact name, no account name, no contract name and no file metadata here, because an event
/// links to no other entity (Non-Goal 5).
///
/// <para>
/// <see cref="CreatedBy"/> is a <b>display label</b> resolved at the API edge by
/// <c>IUserDisplayNameResolver</c>; the raw <c>CreatedByUserId</c> is deliberately not exposed. A
/// deleted author reads back as "Unknown user".
/// </para>
///
/// <para>
/// All three free-text fields are returned to <b>every</b> caller holding <c>contracts.read</c>,
/// <see cref="Notes"/> included. That the timeline does not render it is a presentation rule, not an
/// access one (§4.1).
/// </para>
/// </remarks>
public sealed record ExistingContractEvent
{
    public required Guid ContractEventId { get; set; }

    public required Guid ContractId { get; set; }

    public ContractEventType Type { get; set; }

    /// <summary>
    /// How the row came into existence (issue #154): hand-written by a person, or recorded by the
    /// server alongside a change it made. Read-only — there is no request DTO that carries it, so a
    /// caller cannot forge a <see cref="ContractEventSource.System"/> row, and an edit does not change
    /// it: a <c>System</c> row stays <c>System</c> after a <c>PUT</c>.
    /// </summary>
    public ContractEventSource Source { get; set; }

    public required string Title { get; set; }

    public string? Description { get; set; }

    public string? Notes { get; set; }

    public DateTime OccurredAt { get; set; }

    /// <summary>
    /// Who recorded the event, as a display label — never a raw user id. Null is not expected: the
    /// resolver answers "Unknown user" for an absent or unresolvable author.
    /// </summary>
    public string? CreatedBy { get; set; }

    public DateTime CreatedAtUtc { get; set; }
}
