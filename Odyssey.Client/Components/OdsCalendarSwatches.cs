namespace Odyssey.Client.Components;

/// <summary>
/// A curated, contrast-vetted calendar colour swatch (Odyssey Design System · ColorSwatchSelect).
/// Each swatch ships a pre-computed foreground (<see cref="Fg"/>) that clears WCAG 1.4.3 against
/// <see cref="Hex"/>, so a chip painted with the pair never has to compute contrast.
/// </summary>
public sealed record OdsCalendarSwatch(string Key, string Name, string Hex, string Fg);

/// <summary>
/// The calendar palette + lookup helpers. Mapped onto the Odyssey ramps (sea / tide / mint / coral /
/// violet / ink / amber) rather than generic Material hues; brand tide only in its deep stop so a
/// calendar colour never reads as app chrome.
/// </summary>
public static class OdsCalendarSwatches
{
    public static readonly IReadOnlyList<OdsCalendarSwatch> All =
    [
        new("blue", "Blue", "#0369A1", "#FFFFFF"),     // sea-700
        new("teal", "Teal", "#006B5A", "#FFFFFF"),     // tide-deep
        new("green", "Green", "#15803D", "#FFFFFF"),   // mint-700
        new("coral", "Coral", "#B23B3B", "#FFFFFF"),   // coral-700
        new("violet", "Violet", "#6D28D9", "#FFFFFF"), // violet-700
        new("slate", "Slate", "#4A5670", "#FFFFFF"),   // ink-500
        new("amber", "Amber", "#F59E0B", "#0E1525"),   // amber-500 · dark text
        new("sky", "Sky", "#7DD3FC", "#0E1525"),       // sea-300 · dark text
    ];

    /// <summary>The default swatch hex (Blue).</summary>
    public const string DefaultColor = "#0369A1";

    /// <summary>Look a swatch up by its stored hex; falls back to the default so a legacy/unknown
    /// value still renders a legible chip.</summary>
    public static OdsCalendarSwatch SwatchFor(string? hex)
    {
        var key = (hex ?? string.Empty).ToUpperInvariant();
        return All.FirstOrDefault(s => s.Hex.ToUpperInvariant() == key)
               ?? All.First(s => s.Hex.ToUpperInvariant() == DefaultColor.ToUpperInvariant());
    }
}
