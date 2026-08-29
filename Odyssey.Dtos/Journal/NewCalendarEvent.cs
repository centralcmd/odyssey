using System.ComponentModel.DataAnnotations;

namespace Odyssey.Dtos.Journal;

public sealed record NewCalendarEvent
{
    [Required]
    public required Guid CalendarId { get; set; }

    [StringLength(200)]
    [Required]
    public required string Title { get; set; }

    [StringLength(2000)]
    public string? Description { get; set; }

    [StringLength(300)]
    public string? Location { get; set; }

    [Required]
    public required DateTime StartDateTime { get; set; } // UTC

    [Required]
    public required DateTime EndDateTime { get; set; } // UTC

    public bool IsAllDay { get; set; }
}
