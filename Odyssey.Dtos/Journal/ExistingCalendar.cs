namespace Odyssey.Dtos.Journal;

public sealed record ExistingCalendar
{
    public required Guid CalendarId { get; set; }

    public required string Name { get; set; }

    public string? Description { get; set; }

    public required string Color { get; set; }

    /// <summary>
    /// The calendar's <see cref="TagIcons"/> key, or <c>null</c> for the calendar default. A stored key the
    /// catalogue no longer has is projected as <c>null</c>, never echoed. Draw it with
    /// <see cref="CalendarIcons.Glyph(string?)"/>.
    /// </summary>
    public string? Icon { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }
}
