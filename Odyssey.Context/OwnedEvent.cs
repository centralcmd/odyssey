using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Odyssey.Context;

/// <summary>
/// Which kind of owner an <see cref="OwnedEvent"/> row belongs to — the TPH discriminator of the shared
/// <c>Events</c> table (issue #209 §4.1). Persisted as its <c>int</c> ordinal in <c>OwnerKind</c>, so the
/// numbering is a persistence contract: <c>CK_Events_ExactlyOneOwner</c> and
/// <c>CK_Events_TypeMatchesOwner</c> name these ordinals literally.
/// </summary>
public enum EventOwnerKind
{
    Contract = 0,
    Property = 1,
}

/// <summary>
/// The columns every entry in an owner's chronological event log carries, whichever owner that is
/// (issue #209). The abstract TPH base of <see cref="ContractEvent"/> and <see cref="PropertyEvent"/>,
/// which share one table, <c>Events</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>A deliberate exception to "a new owner gets its own table"</b> (issues #173, #191). The shared
/// table is contained by four controls rather than trusted: a discriminator every <c>DbSet</c> filters
/// on; <c>CK_Events_ExactlyOneOwner</c>, tying the discriminator to exactly one non-null owner key;
/// <c>CK_Events_TypeMatchesOwner</c>, which keeps each owner's <c>Type</c> inside its own disjoint
/// ordinal range (contract 0–99, property 100–199) so a stored value always identifies its enum; and
/// services that scope every read and write by owner id as well. Do not "fix" it back into two tables
/// without reading issue #209 §3.2 first.
/// </para>
/// <para>
/// <b>Still not an audit log.</b> Every row, system-recorded ones included, is editable and deletable
/// by the owner's <c>update</c> claim holder. <see cref="Source"/> tells a hand-written row from one the
/// server recorded.
/// </para>
/// <para>
/// <see cref="CreatedByUserId"/> is <c>SET NULL</c>, per the project's user-attribution rule: an event
/// is shared data that must survive its author's departure.
/// </para>
/// </remarks>
public abstract class OwnedEvent
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid EventId { get; set; }

    /// <summary>
    /// How the row came into existence (issue #154): hand-written by a person, or recorded by the
    /// server alongside a change it made. Server-owned — no request DTO carries it, and an update
    /// leaves it alone.
    /// </summary>
    [Required]
    public ContractEventSource Source { get; set; } = ContractEventSource.User;

    /// <summary>The short label. Required for every type.</summary>
    [StringLength(256)]
    [Required]
    public required string Title { get; set; }

    /// <summary>A longer account of what happened. Shown on the timeline beside the title.</summary>
    [StringLength(1024)]
    public string? Description { get; set; }

    /// <summary>
    /// The user's own working notes. The timeline does not render this — a <b>presentation</b> rule,
    /// never an access one.
    /// </summary>
    [StringLength(1024)]
    public string? Notes { get; set; }

    /// <summary>When it happened, UTC. Must not be in the future.</summary>
    [Required]
    public required DateTime OccurredAt { get; set; }

    /// <summary>
    /// Who recorded the event. Attribution, <b>not</b> an ownership boundary. Never rewritten by an
    /// update.
    /// </summary>
    public string? CreatedByUserId { get; set; }

    [Required]
    public required DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
