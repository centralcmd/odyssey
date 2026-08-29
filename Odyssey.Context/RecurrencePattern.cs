using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Odyssey.Context;

[Index(nameof(CalendarId))]
[Index(nameof(CalendarId), nameof(ExternalUid))]
public class RecurrencePattern
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid RecurrencePatternId { get; set; }

    [Required]
    public required Guid CalendarId { get; set; }

    [ForeignKey(nameof(CalendarId))]
    public Calendar? Calendar { get; set; }

    // Template fields, copied onto every generated CalendarEvent occurrence.
    [StringLength(200)]
    [Required]
    public required string Title { get; set; }

    [StringLength(2000)]
    public string? Description { get; set; }

    [StringLength(300)]
    public string? Location { get; set; }

    // The ICS UID of an imported recurring series (issue #330) — so a re-imported series updates the
    // existing pattern rather than duplicating it. Null for app-native patterns.
    [StringLength(255)]
    public string? ExternalUid { get; set; }

    public DateTime StartDateTime { get; set; } // UTC; first occurrence's start; duration = EndDateTime - StartDateTime

    public DateTime EndDateTime { get; set; } // UTC

    public bool IsAllDay { get; set; } // copied onto every generated CalendarEvent

    [EnumDataType(typeof(RecurrenceFrequency))]
    public RecurrenceFrequency Frequency { get; set; }

    [Range(1, 365)]
    public int Interval { get; set; } = 1;

    public DaysOfWeekFlags? DaysOfWeek { get; set; } // Weekly only

    [Range(1, 31)]
    public int? DayOfMonth { get; set; } // Monthly/Yearly — clamped to the month's last day when out of range

    [Range(1, 12)]
    public int? MonthOfYear { get; set; } // Yearly only

    // Exactly one of these two must be set — never-ending recurrence is not supported.
    public DateTime? RecurrenceEndDate { get; set; } // UTC

    [Range(1, 730)]
    public int? OccurrenceCount { get; set; }

    public string? CreatedByUserId { get; set; }

    public string? UpdatedByUserId { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public ICollection<CalendarEvent> GeneratedEvents { get; set; } = new List<CalendarEvent>();
}

public enum RecurrenceFrequency
{
    Daily,
    Weekly,
    Monthly,
    Yearly,
}

[Flags]
public enum DaysOfWeekFlags
{
    None = 0,
    Monday = 1 << 0,
    Tuesday = 1 << 1,
    Wednesday = 1 << 2,
    Thursday = 1 << 3,
    Friday = 1 << 4,
    Saturday = 1 << 5,
    Sunday = 1 << 6,
}
