using System.ComponentModel.DataAnnotations;

namespace Odyssey.Dtos.Journal;

public sealed record NewCalendar
{
    [StringLength(150)]
    [Required]
    public required string Name { get; set; }

    [StringLength(1000)]
    public string? Description { get; set; }

    /// <summary>
    /// Any <c>#RRGGBB</c> colour, validated exactly as a tag colour (issue #392) and stored upper-case. Unlike
    /// a tag's, it is required: a calendar always paints its events in a colour.
    /// </summary>
    [StringLength(HexColor.Length, ErrorMessage = HexColor.InvalidMessage)]
    [HexColor]
    [Required]
    public string Color { get; set; } = ColorPalette.CalendarDefault;

    /// <summary>
    /// A <see cref="TagIcons"/> key, or <c>null</c> for the calendar default (<see cref="CalendarIcons.Default"/>).
    /// The default keys are refused, so each state has one representation. A <c>PUT</c> omitting it
    /// resets the calendar to the default, as every other optional field on this replace body does.
    /// </summary>
    [StringLength(TagIcons.MaxKeyLength, ErrorMessage = TagIcons.InvalidIconMessage)]
    [TagIcon]
    public string? Icon { get; set; }
}
