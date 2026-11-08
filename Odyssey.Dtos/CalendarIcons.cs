namespace Odyssey.Dtos;

/// <summary>
/// The calendar twin of <see cref="PhotoAlbumIcons"/>: a calendar's icon is a key from the shared
/// <see cref="TagIcons"/> catalogue, and only its never-stored default glyph differs.
/// </summary>
/// <remarks>
/// <see cref="Default"/> is <b>not</b> a member of <see cref="TagIcons.All"/>: a calendar selecting the
/// default sends <c>null</c>, and <c>[TagIcon]</c> refuses the default key on the write path.
/// </remarks>
public static class CalendarIcons
{
    /// <summary>The calendar glyph, drawn for a calendar whose icon is <c>null</c> or no longer in the catalogue.</summary>
    public const string Default = "calendar_month";

    /// <summary>The glyph to draw for a calendar's stored icon: a known catalogue key as-is, anything else <see cref="Default"/>.</summary>
    public static string Glyph(string? key) => TagIcons.IsKnown(key) ? key! : Default;
}
