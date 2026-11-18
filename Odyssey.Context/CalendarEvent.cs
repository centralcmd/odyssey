using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Odyssey.Context;

[Index(nameof(CalendarId), nameof(StartDateTime))]
[Index(nameof(RecurrencePatternId))]
[Index(nameof(CalendarId), nameof(ExternalUid))]
[Index(nameof(CalendarId), nameof(ImportSourceUid))]
public class CalendarEvent
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid CalendarEventId { get; set; }

    [Required]
    public required Guid CalendarId { get; set; }

    [ForeignKey(nameof(CalendarId))]
    public Calendar? Calendar { get; set; }

    [StringLength(200)]
    [Required]
    public required string Title { get; set; }

    [StringLength(2000)]
    public string? Description { get; set; }

    [StringLength(300)]
    public string? Location { get; set; }

    /// <summary>
    /// The place this event happened at (issue #380). <c>RESTRICT</c>, indexed; the text column beside it is
    /// now an optional detail. Declared in <c>OdysseyContext.ConfigureLocationReferences</c>.
    /// </summary>
    public Guid? LocationId { get; set; }

    // The ICS UID of an imported event (issue #330). Null for app-native events. Non-unique — dedup
    // within an import file is handled in-memory; a DB unique index would 500 on legitimate duplicate
    // UIDs (e.g. RECURRENCE-ID exception rows) rather than letting the import skip them.
    [StringLength(255)]
    public string? ExternalUid { get; set; }

    // The UID this row was imported from, set ONLY when that UID was one of Odyssey's own synthetic
    // "{guid}@odyssey.local" forms and did not resolve in the target calendar (issue #516 §4). It makes a
    // second import of the same file into the same calendar an update rather than a duplicate. It is never
    // exported and never matched as a primary key — only as itself, within the row's own calendar — which
    // is what keeps the issue #345 F1 cross-calendar overwrite closed. Non-unique, like ExternalUid.
    [StringLength(255)]
    public string? ImportSourceUid { get; set; }

    public DateTime StartDateTime { get; set; } // UTC

    public DateTime EndDateTime { get; set; } // UTC; must be > StartDateTime

    public bool IsAllDay { get; set; } // exclusive-end semantics when true

    // Set only by RecurrencePatternService when generating occurrences. Never client-settable —
    // absent from NewCalendarEvent/UpdateCalendarEvent, only present on the response-only ExistingCalendarEvent.
    public Guid? RecurrencePatternId { get; set; }

    [ForeignKey(nameof(RecurrencePatternId))]
    public RecurrencePattern? RecurrencePattern { get; set; }

    public string? CreatedByUserId { get; set; }

    public string? UpdatedByUserId { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }
}
