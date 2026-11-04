namespace Odyssey.Dtos;

/// <summary>One curated swatch: a stable key, its word name, the stored hex and the glyph colour drawn on it.</summary>
public sealed record ColorSwatch(string Key, string Name, string Hex, string Foreground);

/// <summary>
/// The curated, contrast-vetted palette (issue #392): the only colours a calendar may take, and the
/// presets the tag colour picker offers. Declared once here — the calendar validator, the calendar
/// swatch chooser and the tag picker all read it, so the palette cannot drift between them.
/// </summary>
/// <remarks>
/// A hex is a stored value (<c>Calendar.Color</c>), so it is a data contract: change a swatch's hex only
/// with a migration. Each <see cref="ColorSwatch.Foreground"/> must equal what
/// <see cref="HexColor.Foreground"/> computes for its hex; a guard test pins that. The design-system
/// token each swatch maps to is a CSS concern and lives in the client, keyed by <see cref="ColorSwatch.Key"/>.
/// </remarks>
public static class ColorPalette
{
    /// <summary>A calendar's default colour (Blue).</summary>
    public const string CalendarDefault = "#0369A1";

    /// <summary>The eight swatches, in picker order.</summary>
    public static IReadOnlyList<ColorSwatch> All { get; } =
    [
        new("blue", "Blue", "#0369A1", HexColor.LightForeground),
        new("teal", "Teal", "#006B5A", HexColor.LightForeground),
        new("green", "Green", "#15803D", HexColor.LightForeground),
        new("coral", "Coral", "#B23B3B", HexColor.LightForeground),
        new("violet", "Violet", "#6D28D9", HexColor.LightForeground),
        new("slate", "Slate", "#4A5670", HexColor.LightForeground),
        new("amber", "Amber", "#F59E0B", HexColor.DarkForeground),
        new("sky", "Sky", "#7DD3FC", HexColor.DarkForeground),
    ];

    /// <summary>Palette membership, case-insensitive — stored calendar colours may predate upper-casing.</summary>
    public static bool Contains(string? hex) =>
        hex is not null && All.Any(swatch => string.Equals(swatch.Hex, hex, StringComparison.OrdinalIgnoreCase));

    /// <summary>The swatch for <paramref name="hex"/> (case-insensitive), or <c>null</c>.</summary>
    public static ColorSwatch? Find(string? hex) =>
        hex is null ? null : All.FirstOrDefault(swatch => string.Equals(swatch.Hex, hex, StringComparison.OrdinalIgnoreCase));
}
