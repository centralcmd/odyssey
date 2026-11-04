using Odyssey.Dtos;

namespace Odyssey.Client.Components;

/// <summary>
/// A curated, contrast-vetted calendar colour swatch (Odyssey Design System · ColorSwatchSelect).
/// Each swatch ships a pre-computed foreground (<see cref="Fg"/>) that clears WCAG 1.4.3 against
/// <see cref="Hex"/>, so a chip painted with the pair never has to compute contrast. <see cref="Hex"/> is
/// the persisted <c>Calendar.Color</c> value — a data contract, so it stays a literal — and
/// <see cref="Token"/> names the ramp stop it must equal: change both together.
/// </summary>
public sealed record OdsCalendarSwatch(string Key, string Name, string Hex, string Fg, string Token);

/// <summary>
/// The calendar palette + lookup helpers. Mapped onto the Odyssey ramps (sea / tide / mint / coral /
/// violet / ink / amber) rather than generic Material hues; brand tide only in its deep stop so a
/// calendar colour never reads as app chrome.
/// </summary>
public static class OdsCalendarSwatches
{
    /// <summary>
    /// The design-system ramp stop each swatch must equal — a CSS concern, so it stays client-side, keyed by
    /// <see cref="ColorSwatch.Key"/>. The hexes and foregrounds come from the shared
    /// <see cref="ColorPalette"/> (issue #392), so this file declares no colour of its own.
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, string> TokenByKey = new Dictionary<string, string>
    {
        ["blue"] = "--sea-700",
        ["teal"] = "--tide-deep",
        ["green"] = "--mint-700",
        ["coral"] = "--coral-700",
        ["violet"] = "--violet-700",
        ["slate"] = "--ink-500",
        ["amber"] = "--amber-500",
        ["sky"] = "--sea-300",
    };

    public static readonly IReadOnlyList<OdsCalendarSwatch> All =
        [.. ColorPalette.All.Select(swatch => new OdsCalendarSwatch(swatch.Key, swatch.Name, swatch.Hex, swatch.Foreground, TokenByKey[swatch.Key]))];

    /// <summary>The default swatch hex (Blue).</summary>
    public const string DefaultColor = ColorPalette.CalendarDefault;

    /// <summary>Look a swatch up by its stored hex; falls back to the default so a legacy/unknown
    /// value still renders a legible chip.</summary>
    public static OdsCalendarSwatch SwatchFor(string? hex)
    {
        var key = (hex ?? string.Empty).ToUpperInvariant();
        return All.FirstOrDefault(s => s.Hex.ToUpperInvariant() == key)
               ?? All.First(s => s.Hex.ToUpperInvariant() == DefaultColor.ToUpperInvariant());
    }
}
