namespace Odyssey.Dtos.Journal;

public sealed record ExistingRecurrencePattern
{
    public required Guid RecurrencePatternId { get; set; }

    public required Guid CalendarId { get; set; }

    public required string Title { get; set; }

    public string? Description { get; set; }

    public string? Location { get; set; }

    /// <summary>The linked place's id (issue #380 §5.1), present whenever the record is linked.</summary>
    public Guid? LocationId { get; set; }

    /// <summary>The linked place's embed, or <see langword="null"/> without <c>locations.read</c>.</summary>
    public LocationRef? LocationRef { get; set; }

    /// <summary>The first occurrence's wall-clock start in <see cref="TimeZoneId"/>, or floating (issue #519 §5).</summary>
    public DateTime StartDateTime { get; set; }

    public DateTime EndDateTime { get; set; }

    public bool IsAllDay { get; set; }

    public string? TimeZoneId { get; set; }

    /// <summary>The first occurrence's UTC instant; null on a floating or all-day series.</summary>
    public DateTime? StartUtc { get; set; }

    public DateTime? EndUtc { get; set; }

    /// <summary>The series' concurrency token, sent back as <c>expectedVersion</c> on <c>PUT</c>.</summary>
    public Guid Version { get; set; }

    /// <summary>Deleted occurrences the series remembers (issue #519 §5). Calendar data under <c>calendar.read</c>.</summary>
    public int ExclusionCount { get; set; }

    /// <summary>The series' rows carry occurrence keys; false for a legacy series the backfill has not reached.</summary>
    public bool IsKeyed { get; set; }

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

    /// <summary>
    /// The linked files (issue #521 §5.3): filled on a detail read, <see langword="null"/> on a list. Every occurrence
    /// shows them. Names, types and sizes are null without <c>files.read</c>.
    /// </summary>
    public List<CalendarLinkFileDto>? Attachments { get; set; }

    /// <summary>The linked photos in gallery order, as <see cref="Attachments"/>; titles and names need <c>photos.read</c>.</summary>
    public List<CalendarLinkPhotoDto>? Photos { get; set; }

    /// <summary>How many files are linked, on every read. That a document is attached is calendar data.</summary>
    public int AttachmentCount { get; set; }

    /// <summary>How many photos are linked, on every read.</summary>
    public int PhotoCount { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }
}
