namespace Odyssey.Dtos.Journal;

public sealed record ExistingRecurrencePattern
{
    public required Guid RecurrencePatternId { get; set; }

    public required Guid CalendarId { get; set; }

    public required string Title { get; set; }

    public string? Description { get; set; }

    public string? Location { get; set; }

    public DateTime StartDateTime { get; set; }

    public DateTime EndDateTime { get; set; }

    public bool IsAllDay { get; set; }

    public RecurrenceFrequency Frequency { get; set; }

    public int Interval { get; set; }

    public DaysOfWeekFlags? DaysOfWeek { get; set; }

    public int? DayOfMonth { get; set; }

    public int? MonthOfYear { get; set; }

    public DateTime? RecurrenceEndDate { get; set; }

    public int? OccurrenceCount { get; set; }

    // Count only — a bounded pattern can still generate up to 730 rows, too many to embed on every
    // read. Callers fetch the full list via GET /api/recurrence-patterns/{id}/events.
    public int GeneratedEventCount { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }
}
