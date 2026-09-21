using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Odyssey.Context;

/// <summary>
/// One entry in a contract's user-maintained, chronological log (issue #138): what happened, when, and
/// the user's own account of it. An owned child of the contract — it dies with it (<c>CASCADE</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Still not an audit log</b>, even though issue #154 made the server write some of these rows
/// itself: every event is editable and deletable by any <c>contracts.update</c> holder, system-recorded
/// ones included, so a row's presence is evidence that something happened and its absence is evidence
/// of nothing. The non-editable record lives in the application log
/// (<c>ContractService.LogStampWrite</c>, <c>LogPartyWrite</c>, <c>TermService.LogTermWrite</c>), which
/// no endpoint can reach. <see cref="Source"/> is what tells the two kinds of row apart, and an event of
/// type
/// <see cref="ContractEventType.Terminated"/> does <b>not</b> move the contract's derived
/// <c>ContractStatus</c> (issue #138 §4.2) — making a free-form log entry authoritative over derived
/// state would let a typo change what the contract is.
/// </para>
/// <para>
/// <b>One index, deliberately.</b> <c>(ContractId, OccurredAt)</c> serves the only read path there is:
/// always contract-scoped, always chronological. There is no unique index — the same type may
/// legitimately occur many times on one contract and there is no natural key — and no by-target
/// lookup, because an event links to no other entity (Non-Goal 5).
/// </para>
/// <para>
/// <see cref="CreatedByUserId"/> is <c>SET NULL</c>, per the project's user-attribution rule: an event
/// is shared data that must survive its author's departure, so <c>RESTRICT</c> (which would make any
/// author undeletable) and <c>CASCADE</c> (which would destroy the shared record) are both wrong.
/// </para>
/// </remarks>
[Index(nameof(ContractId), nameof(OccurredAt))]
public class ContractEvent
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid ContractEventId { get; set; }

    [Required]
    public required Guid ContractId { get; set; }

    [ForeignKey(nameof(ContractId))]
    [DeleteBehavior(DeleteBehavior.Cascade)]
    public Contract? Contract { get; set; }

    [Required]
    public ContractEventType Type { get; set; } = ContractEventType.Other;

    /// <summary>
    /// How the row came into existence (issue #154): hand-written by a person, or recorded by the
    /// server alongside a change it made. Defaults to <see cref="ContractEventSource.User"/>, which is
    /// what every pre-#154 row is — nothing had ever written one automatically.
    /// </summary>
    /// <remarks>
    /// Server-owned: no request DTO carries it, and <c>PUT …/events/{eventId}</c> leaves it alone,
    /// exactly as it leaves <see cref="CreatedByUserId"/> and <see cref="CreatedAtUtc"/>. There is no
    /// index — the <c>source</c> filter is applied inside the contract-scoped window the
    /// <c>(ContractId, OccurredAt)</c> index already serves.
    /// </remarks>
    [Required]
    public ContractEventSource Source { get; set; } = ContractEventSource.User;

    /// <summary>
    /// The short label — "Emailed landlord about the rent increase". Required for <b>every</b> type: a
    /// named type does not make it optional or derived, which keeps validation unconditional and keeps
    /// the user's own wording that a type-derived label would discard.
    /// </summary>
    [StringLength(256)]
    [Required]
    public required string Title { get; set; }

    /// <summary>A longer account of what happened. Shown on the timeline beside the title.</summary>
    [StringLength(1024)]
    public string? Description { get; set; }

    /// <summary>
    /// The user's own working notes. The timeline does not render this — a <b>presentation</b> rule,
    /// never an access one. It sits behind the same <c>contracts.read</c> claim as the other two
    /// fields, is returned in the same projection to every caller, is searched by the same term and is
    /// exported alongside them (issue #138 §4.1).
    /// </summary>
    [StringLength(1024)]
    public string? Notes { get; set; }

    /// <summary>When it happened, UTC, date and time. Must not be in the future (issue #138 §8.3).</summary>
    [Required]
    public required DateTime OccurredAt { get; set; }

    /// <summary>
    /// Who recorded the event. Attribution, <b>not</b> an ownership boundary: any
    /// <c>contracts.update</c> holder may edit or delete any event. Never rewritten by an update.
    /// </summary>
    public string? CreatedByUserId { get; set; }

    [Required]
    public required DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
