using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Odyssey.Context;

[Index(nameof(CalendarId), nameof(StartDateTime))]
[Index(nameof(RecurrencePatternId))]
[Index(nameof(CalendarId), nameof(ExternalUid))]
[Index(nameof(CalendarId), nameof(ImportSourceUid))]
[Index(nameof(RecurrencePatternId), nameof(OccurrenceKey), IsUnique = true)]
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

    /// <summary>
    /// Two stored meanings (issue #519 §4 invariant 2): the UTC instant for a zoned row (<see cref="TimeZoneId"/> set),
    /// the wall-clock for a floating one (null zone, every row written before #519 and every all-day row).
    /// </summary>
    public DateTime StartDateTime { get; set; }

    /// <summary>Same two meanings as <see cref="StartDateTime"/>; must be after it.</summary>
    public DateTime EndDateTime { get; set; }

    /// <summary>
    /// The IANA zone this row's times are in (issue #519 §4), stored as the resolved <c>TimeZoneInfo.Id</c>. Null means
    /// floating: the stored value is a wall-clock shown unconverted to every viewer. Always null on an all-day row
    /// (<c>CK_CalendarEvents_AllDayHasNoZone</c>).
    /// </summary>
    [StringLength(64)]
    public string? TimeZoneId { get; set; }

    /// <summary>
    /// The occurrence's local wall-clock start under its series' rule when it was generated (issue #519 §8.2), with
    /// <c>Kind = Unspecified</c>. Server-set only, never changed by an edit of the occurrence's time, and unique per
    /// series. Null on a standalone row and on a generated row of a series that is not keyed yet.
    /// </summary>
    public DateTime? OccurrenceKey { get; set; }

    /// <summary>
    /// The occurrence was edited on its own (issue #519 §3 component 4), so a series edit keeps its fields while its key
    /// survives. Server-set only.
    /// </summary>
    public bool IsOverride { get; set; }

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
