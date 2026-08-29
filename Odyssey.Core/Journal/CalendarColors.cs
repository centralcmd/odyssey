namespace Odyssey.Core.Journal;

/// <summary>
/// The curated, contrast-vetted swatch palette <c>Calendar.Color</c> is restricted to (spec §6/§9) —
/// a free-form hex value is rejected. Each swatch ships a pre-computed foreground so chip text always
/// meets WCAG 1.4.3 contrast regardless of which swatch a calendar uses.
///
/// Mirrors the Odyssey Design System's ColorSwatchSelect palette (<c>Odyssey.Client.Components.
/// OdsCalendarSwatches</c>) — mapped onto the Odyssey brand ramps rather than the generic Material
/// hues an earlier draft of this spec used, so a calendar colour never reads as app chrome.
/// </summary>
public static class CalendarColors
{
    public const string DefaultColor = "#0369A1";

    public static readonly IReadOnlyDictionary<string, string> Palette = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["#0369A1"] = "white", // Blue (default) — sea-700
        ["#006B5A"] = "white", // Teal — tide-deep
        ["#15803D"] = "white", // Green — mint-700
        ["#B23B3B"] = "white", // Coral — coral-700
        ["#6D28D9"] = "white", // Violet — violet-700
        ["#4A5670"] = "white", // Slate — ink-500
        ["#F59E0B"] = "black", // Amber — amber-500
        ["#7DD3FC"] = "black", // Sky — sea-300
    };

    public static bool IsValid(string color) => Palette.ContainsKey(color);
}
