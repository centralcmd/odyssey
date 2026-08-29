using System.ComponentModel.DataAnnotations;

namespace Odyssey.Dtos.Journal;

public sealed record NewCalendar
{
    [StringLength(150)]
    [Required]
    public required string Name { get; set; }

    [StringLength(1000)]
    public string? Description { get; set; }

    // Curated palette membership (CalendarColors.Palette) is enforced in CalendarService, not here —
    // see the repo-wide convention of cross-field/business-rule checks living in the service layer.
    [StringLength(7)]
    [Required]
    public string Color { get; set; } = "#0369A1";
}
