namespace Odyssey.Dtos;

/// <summary>
/// The one hex-colour rule (issue #392): exactly <c>#</c> plus six ASCII hex digits, stored upper-case.
/// Shared by the write attribute, the service guards, every read projection, the display resolver and
/// the WASM client's style sink, so no second implementation exists anywhere.
/// </summary>
/// <remarks>
/// <para>
/// <b>Not a regex.</b> In .NET <c>^#[0-9A-Fa-f]{6}$</c> matches <c>"#FFFFFF\n"</c>, because <c>$</c>
/// also matches before a final newline; <c>[StringLength(7)]</c> would hide that on the HTTP path only.
/// The check is a length test plus a loop over <c>char</c> ranges — no trimming, no culture, no
/// <c>char.IsXxx</c> (which admits non-ASCII digits).
/// </para>
/// <para>
/// A value reaching a client <c>style</c> attribute is the reason this is strict: the client CSP allows
/// inline styles, so a loose rule would be a CSS-injection vector.
/// </para>
/// </remarks>
public static class HexColor
{
    /// <summary>The column and DTO length.</summary>
    public const int Length = 7;

    /// <summary>The one message every colour rejection carries. It never echoes the submitted value.</summary>
    public const string InvalidMessage = "Not a valid color. Use #RRGGBB.";

    /// <summary>The foreground drawn on a light fill — the darkest ink.</summary>
    public const string DarkForeground = "#0E1525";

    /// <summary>The foreground drawn on a dark fill.</summary>
    public const string LightForeground = "#FFFFFF";

    /// <summary>Exactly <c>#</c> plus six ASCII hex digits.</summary>
    public static bool IsValid(string? value)
    {
        if (value is null || value.Length != Length || value[0] != '#')
        {
            return false;
        }

        for (var i = 1; i < Length; i++)
        {
            if (!IsAsciiHex(value[i]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// The read- and write-side projection: a valid value upper-cased, anything else <c>null</c>.
    /// Upper-casing is invariant and safe because only ASCII hex reaches it.
    /// </summary>
    public static string? Normalize(string? value) => IsValid(value) ? value!.ToUpperInvariant() : null;

    /// <summary>
    /// <see cref="LightForeground"/> or <see cref="DarkForeground"/>, whichever has the higher WCAG 2.x
    /// contrast ratio against <paramref name="hex"/>; a tie goes to <see cref="DarkForeground"/>.
    /// For an arbitrary fill the winner is always at least 4.27:1 — enough for a non-text glyph (3:1),
    /// not always for text (4.5:1), which is why chip text is never drawn on a tag colour.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="hex"/> is not <see cref="IsValid"/>.</exception>
    public static string Foreground(string hex)
    {
        if (!IsValid(hex))
        {
            throw new ArgumentException(InvalidMessage, nameof(hex));
        }

        var luminance = RelativeLuminance(hex);
        var onLight = ContrastRatio(luminance, RelativeLuminance(LightForeground));
        var onDark = ContrastRatio(luminance, RelativeLuminance(DarkForeground));
        return onLight > onDark ? LightForeground : DarkForeground;
    }

    /// <summary>WCAG 2.x contrast ratio between two valid hex colours.</summary>
    public static double ContrastRatio(string first, string second) =>
        ContrastRatio(RelativeLuminance(first), RelativeLuminance(second));

    private static double ContrastRatio(double a, double b) =>
        (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);

    private static double RelativeLuminance(string hex)
    {
        static double Channel(int value)
        {
            var c = value / 255d;
            return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }

        static int Byte(string s, int at) => (HexValue(s[at]) << 4) | HexValue(s[at + 1]);

        return 0.2126 * Channel(Byte(hex, 1)) + 0.7152 * Channel(Byte(hex, 3)) + 0.0722 * Channel(Byte(hex, 5));
    }

    private static bool IsAsciiHex(char c) => c is >= '0' and <= '9' or >= 'A' and <= 'F' or >= 'a' and <= 'f';

    private static int HexValue(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'A' and <= 'F' => c - 'A' + 10,
        _ => c - 'a' + 10,
    };
}
