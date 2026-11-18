using Odyssey.Client.Components;
using Odyssey.Client.Theme;
using Odyssey.Dtos;
using Odyssey.Dtos.Journal;

namespace Odyssey.Client.Pages.Calendar;

/// <summary>
/// The ONE rule for how a calendar's icon and colour are drawn — the calendar twin of
/// <c>AlbumVisuals</c>. Every calendar surface (the visibility filter, the import / export / event
/// pickers, the manage dialog's rows and the calendar chip) calls this, so the glyph and ink are written once.
/// </summary>
/// <remarks>
/// The colour goes through <see cref="UserColor.Glyph"/>, which re-validates it (<c>UserColorSinkLintTests</c>).
/// A calendar always has a colour; an unusable stored one reads as <see cref="ColorPalette.CalendarDefault"/>.
/// </remarks>
public static class CalendarVisuals
{
    /// <summary>The glyph for a calendar's stored icon: a known catalogue key, else <see cref="CalendarIcons.Default"/>.</summary>
    public static string Glyph(ExistingCalendar calendar) => CalendarIcons.Glyph(calendar.Icon);

    /// <summary>The glyph ink for a calendar's colour, falling back to the default calendar colour.</summary>
    public static string? Ink(ExistingCalendar calendar) =>
        UserColor.Glyph(calendar.Color) ?? UserColor.Glyph(ColorPalette.CalendarDefault);

    /// <summary>The <c>style</c> value for a calendar glyph on the page ground.</summary>
    public static string GlyphStyle(ExistingCalendar calendar) => $"color:{Ink(calendar)}";

    /// <summary>A calendar as a select / filter option: its glyph in its colour, beside its name.</summary>
    public static OdsOption Option(ExistingCalendar calendar) =>
        new(calendar.CalendarId.ToString(), calendar.Name)
        {
            Icon = Glyph(calendar),
            IconColor = Ink(calendar),
        };

    /// <summary>A calendar as a chip (<see cref="OdsTagChip"/> with <see cref="CalendarIcons.Default"/>).</summary>
    public static OdsTagChips.Tag Chip(ExistingCalendar calendar) =>
        new(calendar.Name, calendar.Icon, UserColor.Canonical(calendar.Color) ?? ColorPalette.CalendarDefault);
}
