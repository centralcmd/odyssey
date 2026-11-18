using System.ComponentModel.DataAnnotations;
using Odyssey.Dtos;

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

    /// <summary>
    /// The wall-clock start in <see cref="TimeZoneId"/> (issue #519 §5); the server derives the instant. With no zone the
    /// time is floating, and a value carrying <c>Z</c> or an offset is converted to UTC first, as before.
    /// </summary>
    [Required]
    public required DateTime StartDateTime { get; set; }

    /// <summary>The wall-clock end, with the same rules as <see cref="StartDateTime"/>.</summary>
    [Required]
    public required DateTime EndDateTime { get; set; }

    public bool IsAllDay { get; set; }

    /// <summary>
    /// The IANA zone the times are in (issue #519 §8.1). On <c>POST</c> null means floating; on <c>PUT</c> null keeps the
    /// stored zone, and <c>"UTC"</c> sets UTC. Ignored, and stored as null, on an all-day event.
    /// </summary>
    [StringLength(IanaTimeZones.MaxLength)]
    [IanaTimeZone]
    public string? TimeZoneId { get; set; }
}
