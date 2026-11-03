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

    /// <summary>Optional detail text beside the linked place (issue #380 §4), e.g. "room 3".</summary>
    [StringLength(300)]
    public string? Location { get; set; }

    /// <summary>
    /// The linked place, by id only (issue #380 §5.2). A changed link needs <c>locations.read</c>; re-sending
    /// the stored id needs nothing. This is a full replacement, so an omitted id clears the link.
    /// </summary>
    public Guid? LocationId { get; set; }

    [Required]
    public required DateTime StartDateTime { get; set; } // UTC

    [Required]
    public required DateTime EndDateTime { get; set; } // UTC

    public bool IsAllDay { get; set; }
}
