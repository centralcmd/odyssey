using System.Text.RegularExpressions;

namespace Odyssey.Client.Components;

/// <summary>
/// How a registry colour is PAINTED (Odyssey Design System · handoff/README.md → "Registry glyph
/// lightness", issue #164). The registry constants are authored once, in the L 0.66–0.80 band tuned
/// for the dark surface; on light that band cannot reach contrast, so every render site routes the
/// colour through a relative-colour wrap that caps its lightness per theme (<c>--glyph-l</c> /
/// <c>--glyph-text-l</c> in <c>app.css</c>) and keeps chroma and hue. Dark passes <c>l</c> through
/// unchanged.
/// </summary>
/// <remarks>
/// <para>
/// Only the FOREGROUND goes through here. A soft tint (<see cref="OdsTypeOption.Soft"/>, the
/// <c>/ 0.16</c> background) keeps the authored colour — the cap is measured against that tint. A
/// browser without relative colour syntax drops the declaration and the glyph inherits the text
/// colour: contrast kept, hue lost.
/// </para>
/// <para>
/// <b>For trusted colours only</b> — registry constants, design tokens, stored calendar swatches. The
/// result is written into an inline <c>style</c>, so the input is shape-checked (a hex, an
/// <c>oklch()</c>/<c>rgb()</c>/<c>hsl()</c> function or a <c>var(--…)</c> reference, with no
/// declaration or markup punctuation) and anything else paints <c>inherit</c> rather than reaching the
/// style attribute.
/// </para>
/// </remarks>
public static partial class OdsGlyphInk
{
    /// <summary>The relative-colour lightness channel a glyph takes — the light cap is L 0.58 (1.4.11, 3:1).</summary>
    public const string GlyphLightness = "var(--glyph-l, l)";

    /// <summary>The relative-colour lightness channel a colour used as text takes — the light cap is L 0.50 (1.4.3, 4.5:1).</summary>
    public const string TextLightness = "var(--glyph-text-l, l)";

    /// <summary>What an unrecognised input paints instead: the surrounding text colour.</summary>
    public const string Fallback = "inherit";

    /// <summary>A registry colour drawn as a glyph, icon tile or rail node.</summary>
    public static string Glyph(string? color) => Wrap(color, GlyphLightness);

    /// <summary>A registry colour drawn as TEXT — a kind chip's label, a value figure.</summary>
    public static string Text(string? color) => Wrap(color, TextLightness);

    /// <summary>Whether <paramref name="color"/> has one of the accepted colour shapes.</summary>
    public static bool IsColour(string? color) => color is not null && ColourShape().IsMatch(color);

    private static string Wrap(string? color, string lightness) =>
        IsColour(color) ? $"oklch(from {color!.Trim()} {lightness} c h)" : Fallback;

    [GeneratedRegex(
        @"^\s*(?:#(?:[0-9A-Fa-f]{3,4}|[0-9A-Fa-f]{6}|[0-9A-Fa-f]{8})|(?:oklch|rgba?|hsla?)\([^;:{}<>""'\\]*\)|var\(\s*--[A-Za-z0-9-]+\s*(?:,[^;:{}<>""'\\]*)?\))\s*$")]
    private static partial Regex ColourShape();
}
