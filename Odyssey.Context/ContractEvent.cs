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
/// Since issue #209 the row lives in the shared <c>Events</c> table as one TPH branch of
/// <see cref="OwnedEvent"/>, which holds every column but the owner key and <c>Type</c>.
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
public class ContractEvent : OwnedEvent
{
    /// <summary>
    /// Nullable in the shared <c>Events</c> table (a property row has none), required on this type:
    /// <c>CK_Events_ExactlyOneOwner</c> holds it non-null for every contract row.
    /// </summary>
    [Required]
    public required Guid ContractId { get; set; }

    [ForeignKey(nameof(ContractId))]
    [DeleteBehavior(DeleteBehavior.Cascade)]
    public Contract? Contract { get; set; }

    /// <summary>
    /// Shares the physical <c>Type</c> column with <see cref="PropertyEvent.Type"/>. Contract ordinals
    /// stay below 100 — <c>CK_Events_TypeMatchesOwner</c> enforces it in the database and a guard test
    /// enforces it at build time.
    /// </summary>
    [Required]
    public ContractEventType Type { get; set; } = ContractEventType.Other;
}
