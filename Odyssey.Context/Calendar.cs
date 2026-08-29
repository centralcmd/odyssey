using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Odyssey.Context;

[Index(nameof(Name), IsUnique = true)]
public class Calendar
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid CalendarId { get; set; }

    [StringLength(150)]
    [Required]
    public required string Name { get; set; }

    [StringLength(1000)]
    public string? Description { get; set; }

    // Must be one of the curated palette values, not an arbitrary hex — validated in the DTO.
    [StringLength(7)]
    [Required]
    public string Color { get; set; } = "#0369A1";

    public string? CreatedByUserId { get; set; }

    public string? UpdatedByUserId { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public ICollection<RecurrencePattern> RecurrencePatterns { get; set; } = new List<RecurrencePattern>();

    public ICollection<CalendarEvent> Events { get; set; } = new List<CalendarEvent>();
}
