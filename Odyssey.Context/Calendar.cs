using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;
using Odyssey.Dtos;

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

    // Any #RRGGBB, stored upper-case — the tag colour rule (issue #392), without "no colour": a calendar
    // always has one. Read through HexColor.Normalize, falling back to ColorPalette.CalendarDefault.
    [StringLength(7)]
    [Required]
    public string Color { get; set; } = ColorPalette.CalendarDefault;

    /// <summary>
    /// A <c>TagIcons</c> catalogue key, or <c>NULL</c> for the calendar default (<c>CalendarIcons.Default</c>).
    /// Never markup or a URL, and never a default key itself. A key the catalogue no longer has is
    /// projected as <c>null</c> on read, so retiring one needs no data migration.
    /// </summary>
    [StringLength(64)]
    public string? Icon { get; set; }

    public string? CreatedByUserId { get; set; }

    public string? UpdatedByUserId { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public ICollection<RecurrencePattern> RecurrencePatterns { get; set; } = new List<RecurrencePattern>();

    public ICollection<CalendarEvent> Events { get; set; } = new List<CalendarEvent>();
}
