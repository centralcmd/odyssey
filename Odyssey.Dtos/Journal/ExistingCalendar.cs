namespace Odyssey.Dtos.Journal;

public sealed record ExistingCalendar
{
    public required Guid CalendarId { get; set; }

    public required string Name { get; set; }

    public string? Description { get; set; }

    public required string Color { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }
}
