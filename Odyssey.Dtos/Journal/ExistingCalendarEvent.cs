namespace Odyssey.Dtos.Journal;

public sealed record ExistingCalendarEvent
{
    public required Guid CalendarEventId { get; set; }

    public required Guid CalendarId { get; set; }

    public required string Title { get; set; }

    public string? Description { get; set; }

    public string? Location { get; set; }

    public DateTime StartDateTime { get; set; }

    public DateTime EndDateTime { get; set; }

    public bool IsAllDay { get; set; }

    // Response-only; never accepted as a request body — see the write-binding note on NewCalendarEvent.
    public Guid? RecurrencePatternId { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }
}
