using Odyssey.Dtos;

namespace Odyssey.Client.Theme;

/// <summary>
/// The ONE door from a user-entered colour — a tag colour or a calendar colour — to a <c>style</c>
/// attribute (issues #392 §7.5, #394 §8). The client CSP allows inline styles, so the browser is no
/// backstop: every value is checked with the shared <see cref="HexColor.IsValid"/> right here, at the
/// sink, whatever the server already did.
/// </summary>
/// <remarks>
/// <para>
/// Every output is <b>rebuilt</b> from the parsed bytes (<see cref="Canonical"/>), so no character of the
/// input ever reaches a style; an invalid input yields <c>null</c> and the caller emits nothing.
/// <see cref="HexColor.Foreground"/> runs only after a successful parse.
/// </para>
/// <para>
/// Code-constant colours (charts, contract and property type registries) do not come through here and
/// are out of this scope; <c>UserColorSinkLintTests</c> keeps tag and calendar colours on this path.
/// </para>
/// </remarks>
public static class UserColor
{
    /// <summary>The canonical upper-case <c>#RRGGBB</c>, rebuilt from the parsed value, or <c>null</c>.</summary>
    public static string? Canonical(string? hex)
    {
        if (!HexColor.IsValid(hex))
        {
            return null;
        }

        var value = Convert.ToInt32(hex![1..], 16);
        return $"#{(value >> 16) & 0xFF:X2}{(value >> 8) & 0xFF:X2}{value & 0xFF:X2}";
    }

    /// <summary>A fill and its glyph colour, for <c>OdsAvatar.UserColor</c>; <c>null</c> when invalid.</summary>
    public static (string Bg, string Fg)? Fill(string? hex) =>
        Canonical(hex) is { } bg ? (bg, HexColor.Foreground(bg)) : null;

    /// <summary>The chip-dot custom property, for <c>OdsChip.UserColorDot</c>; <c>null</c> when invalid.</summary>
    public static string? Dot(string? hex) => Canonical(hex) is { } color ? $"--user-color:{color}" : null;

    /// <summary>The swatch / picker-cell custom properties; <c>null</c> when invalid.</summary>
    public static string? Swatch(string? hex) =>
        Canonical(hex) is { } color ? $"--sw:{color};--sw-fg:{HexColor.Foreground(color)}" : null;

    /// <summary>
    /// A plain background, falling back to <paramref name="fallbackHex"/> when <paramref name="hex"/> is
    /// invalid (a calendar always has a colour); <c>null</c> only when the fallback is invalid too.
    /// </summary>
    public static string? Background(string? hex, string fallbackHex) =>
        (Canonical(hex) ?? Canonical(fallbackHex)) is { } color ? $"background:{color}" : null;
}
