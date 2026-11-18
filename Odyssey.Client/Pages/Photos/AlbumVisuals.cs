using Odyssey.Client.Components;
using Odyssey.Client.Theme;
using Odyssey.Dtos;
using Odyssey.Dtos.Journal;

namespace Odyssey.Client.Pages.Photos;

/// <summary>
/// The ONE rule for how an album's icon and colour are drawn (issue #406 §3.2, design system
/// <c>Photos.jsx</c> albumGlyph/albumInk). Every album surface — cards, the open-album header, the
/// Overview rows, the album filter and the add-to-album rows — calls this, so the glyph and ink rule is
/// written once.
/// </summary>
/// <remarks>
/// <see cref="Ink"/> is the only path from an album's <c>Color</c> to a style: the colour goes through
/// <see cref="UserColor.Glyph"/>, which re-validates it and routes it through <c>--user-glyph-l</c>
/// (<c>UserColorSinkLintTests</c> pins this). Every album glyph is decorative (<c>aria-hidden</c>): the
/// album's name is always beside it.
/// </remarks>
public static class AlbumVisuals
{
    /// <summary>The glyph for an album's stored icon: a known catalogue key, else the album default.</summary>
    public static string Glyph(string? icon) => PhotoAlbumIcons.Glyph(icon);

    /// <summary>
    /// The glyph ink for an album's colour, or <paramref name="fallback"/> (a <c>var(--…)</c> token) when it
    /// has none or it is invalid. Non-null whenever a fallback is passed, which every surface does.
    /// </summary>
    public static string? Ink(string? color, string? fallback = null) => UserColor.Glyph(color) ?? fallback;

    /// <summary>The <c>style</c> value for an album glyph: <c>color:</c> its <see cref="Ink"/>.</summary>
    public static string GlyphStyle(string? color, string fallback) => $"color:{Ink(color, fallback)}";

    /// <summary>
    /// An album as a chip (<see cref="OdsTagChip"/> with <see cref="PhotoAlbumIcons.Default"/>): its name, its
    /// stored icon and its colour, exactly as a tag chip reads. <c>null</c> for an album the host does not hold.
    /// </summary>
    public static OdsTagChips.Tag? Chip(PhotoAlbumSummary? album) =>
        album is null ? null : new OdsTagChips.Tag(album.Name, album.Icon, album.Color);

    /// <summary>An album's Overview "By album" row (surface B).</summary>
    public static OdsBreakdownRow OverviewRow(PhotoAlbumSummary album) => new()
    {
        Key = album.PhotoAlbumId,
        Icon = Glyph(album.Icon),
        IconColor = Ink(album.Color, OverviewFallback),
        Label = album.Name,
        Count = album.PhotoCount,
    };

    /// <summary>An album's filter option (surface D1): the same glyph and tone as its Overview row.</summary>
    public static OdsOption FilterOption(PhotoAlbumSummary album) => new(album.PhotoAlbumId.ToString(), album.Name)
    {
        Icon = Glyph(album.Icon),
        IconColor = Ink(album.Color, OverviewFallback),
    };

    /// <summary>The fallback ink on the album cards, the open-album header and the add-to-album rows.</summary>
    public const string SurfaceFallback = "var(--mud-palette-text-secondary)";

    /// <summary>The fallback ink on the Overview rows and the album filter options (a filter row carries its Overview row's tone).</summary>
    public const string OverviewFallback = "var(--tide-400)";
}
