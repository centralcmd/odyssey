using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Odyssey.Context;

[Index(nameof(CalendarId), nameof(StartDateTime))]
[Index(nameof(RecurrencePatternId))]
[Index(nameof(CalendarId), nameof(ExternalUid))]
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

    // The ICS UID of an imported event (issue #330). Null for app-native events. Non-unique — dedup
    // within an import file is handled in-memory; a DB unique index would 500 on legitimate duplicate
    // UIDs (e.g. RECURRENCE-ID exception rows) rather than letting the import skip them.
    [StringLength(255)]
    public string? ExternalUid { get; set; }

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
