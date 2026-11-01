using System.Globalization;
using System.Text.RegularExpressions;
using MudBlazor.Utilities;
using Odyssey.Client.Theme;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// The location map's token pairs (issue #371 AC 17; §6.7). It runs in CI with no design-system copy: the
/// ratios are computed from the MudTheme palette as configured and from <c>app.css</c> as authored, so
/// re-theming a surface without re-checking what sits on it fails here.
/// </summary>
/// <remarks>
/// The basemap is not a token — it is whatever the configured style draws. So the pin and the focus ring
/// are checked against the two extremes a tile can be, white and black: one of their two tones must reach
/// 3:1 against each, which is what lets them hold on any tile in either theme.
/// </remarks>
public class LocationMapContrastTests
{
    private const double Text = 4.5;
    private const double NonText = 3.0;
    private const string White = "#FFFFFF";
    private const string Black = "#000000";

    private static readonly string AppCss = File.ReadAllText(Path.Combine(ClientSource.Root, "wwwroot", "css", "app.css"));

    public static TheoryData<bool> Themes => [false, true];

    [Theory]
    [MemberData(nameof(Themes))]
    public void Caption_controls_and_popup_text_read_on_the_surface(bool dark)
    {
        var palette = Palette(dark);
        var surface = Hex(palette.Surface);

        AssertRatio(Hex(palette.TextSecondary), surface, Text, "caption text");
        AssertRatio(Token("--brand-text", dark), surface, Text, "caption link");
        AssertRatio(Hex(palette.TextPrimary), surface, Text, "control glyphs and popup heading");
    }

    [Theory]
    [MemberData(nameof(Themes))]
    public void One_of_the_pins_two_rings_reaches_3_to_1_on_any_tile(bool dark)
    {
        var palette = Palette(dark);
        string[] rings = [Hex(palette.Surface), Hex(palette.TextPrimary)];

        foreach (var tile in new[] { White, Black })
        {
            Assert.True(
                rings.Any(ring => Ratio(ring, tile) >= NonText),
                $"Neither pin ring ({string.Join(", ", rings)}) reaches 3:1 on {tile} in the {(dark ? "dark" : "light")} theme.");
        }
    }

    [Theory]
    [MemberData(nameof(Themes))]
    public void One_tone_of_the_focus_ring_reaches_3_to_1_on_any_tile(bool dark)
    {
        string[] tones = [Token("--focus-ring", dark), Hex(Palette(dark).Surface)];

        foreach (var tile in new[] { White, Black })
        {
            Assert.True(
                tones.Any(tone => Ratio(tone, tile) >= NonText),
                $"Neither focus-ring tone ({string.Join(", ", tones)}) reaches 3:1 on {tile} in the {(dark ? "dark" : "light")} theme.");
        }
    }

    // ── Token resolution ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The token's value for the theme (<c>:root</c> holds light, <c>[data-theme='dark']</c> overrides),
    /// followed through one <c>var(…)</c> to a hex ramp step or a MudTheme palette colour.
    /// </summary>
    private static string Token(string name, bool dark)
    {
        var css = Regex.Replace(AppCss, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
        var declarations = Regex.Matches(css, @"(?<![-\w])" + Regex.Escape(name) + @"\s*:\s*(?<value>[^;]+?)\s*;")
            .Select(m => (Dark: InDarkBlock(css, m.Index), Value: m.Groups["value"].Value))
            .ToList();

        var value = declarations.FirstOrDefault(d => d.Dark == dark).Value
                    ?? declarations.First(d => !d.Dark).Value;
        return Resolve(css, value, dark);
    }

    private static bool InDarkBlock(string css, int index)
    {
        var open = css.LastIndexOf('{', index);
        var previous = css.LastIndexOfAny(['}', ';'], open);
        return css[(previous + 1)..open].Contains("data-theme='dark'", StringComparison.Ordinal);
    }

    private static string Resolve(string css, string value, bool dark)
    {
        if (Regex.IsMatch(value, "^#[0-9A-Fa-f]{6}$"))
            return value.ToUpperInvariant();

        var reference = Regex.Match(value, @"^var\((?<name>--[\w-]+)\)$");
        Assert.True(reference.Success, $"Cannot resolve '{value}'.");

        if (reference.Groups["name"].Value == "--mud-palette-primary")
            return Hex(Palette(dark).Primary);

        var ramp = Regex.Match(css, @"(?<![-\w])" + Regex.Escape(reference.Groups["name"].Value) + @"\s*:\s*(?<hex>#[0-9A-Fa-f]{6})\s*;");
        Assert.True(ramp.Success, $"{reference.Groups["name"].Value} has no hex declaration in app.css.");
        return ramp.Groups["hex"].Value.ToUpperInvariant();
    }

    private static MudBlazor.Palette Palette(bool dark) =>
        dark ? OdysseyTheme.Theme.PaletteDark : OdysseyTheme.Theme.PaletteLight;

    private static string Hex(MudColor color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    // ── WCAG maths ────────────────────────────────────────────────────────────────────────────────

    private static void AssertRatio(string foreground, string background, double minimum, string what)
    {
        var ratio = Ratio(foreground, background);
        Assert.True(
            ratio >= minimum,
            $"{what}: {foreground} on {background} is {ratio.ToString("0.00", CultureInfo.InvariantCulture)}:1, "
            + $"below {minimum.ToString("0.0", CultureInfo.InvariantCulture)}:1.");
    }

    private static double Ratio(string a, string b)
    {
        var (la, lb) = (Luminance(a), Luminance(b));
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    private static double Luminance(string hex)
    {
        double Channel(int offset)
        {
            var s = Convert.ToByte(hex.Substring(offset, 2), 16) / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Channel(1) + 0.7152 * Channel(3) + 0.0722 * Channel(5);
    }
}
