using Odyssey.Dtos;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// Issue #406 AC 14, measured by computation rather than by screenshot: an album glyph's ink is
/// <c>oklch(from &lt;hex&gt; var(--user-glyph-l) c h)</c>, with <c>--user-glyph-l</c> = <c>max(l, 0.62)</c>
/// on dark and <c>min(l, 0.58)</c> on light (<c>app.css</c>). Every album surface draws its glyph on the
/// page ground or the card surface, so each must reach 3:1 against both, in both themes, for every
/// palette swatch and for a sweep of the whole sRGB cube.
/// </summary>
/// <remarks>
/// Out-of-gamut results are clipped per channel, a slightly pessimistic stand-in for the browser's
/// chroma-reducing gamut mapping. The grounds are the token values in <c>app.css</c>:
/// <c>--mud-palette-background</c> and <c>--mud-palette-surface</c>.
/// </remarks>
public class AlbumGlyphContrastTests
{
    private const double Minimum = 3.0;

    private static readonly (string Theme, Func<double, double> Lightness, string[] Grounds)[] Themes =
    [
        ("dark", l => Math.Max(l, 0.62), ["#0E1525", "#141A2C"]),
        ("light", l => Math.Min(l, 0.58), ["#FAFBFD", "#FFFFFF"]),
    ];

    public static TheoryData<string> Palette()
    {
        var data = new TheoryData<string> { "#4A5670", "#7DD3FC" };
        foreach (var swatch in ColorPalette.All)
        {
            data.Add(swatch.Hex);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Palette))]
    public void Every_palette_colour_reaches_3_to_1_on_every_album_ground_in_both_themes(string hex)
    {
        foreach (var (theme, lightness, grounds) in Themes)
        {
            foreach (var ground in grounds)
            {
                var ratio = Contrast(GlyphInk(hex, lightness), Luminance(Parse(ground)));
                Assert.True(ratio >= Minimum, $"{hex} on {ground} ({theme}): {ratio:F2}:1");
            }
        }
    }

    [Fact]
    public void Any_colour_in_the_srgb_cube_reaches_3_to_1_on_every_album_ground_in_both_themes()
    {
        var worst = double.MaxValue;
        var worstCase = string.Empty;
        for (var r = 0; r <= 255; r += 17)
        {
            for (var g = 0; g <= 255; g += 17)
            {
                for (var b = 0; b <= 255; b += 17)
                {
                    var hex = $"#{r:X2}{g:X2}{b:X2}";
                    foreach (var (theme, lightness, grounds) in Themes)
                    {
                        foreach (var ground in grounds)
                        {
                            var ratio = Contrast(GlyphInk(hex, lightness), Luminance(Parse(ground)));
                            if (ratio < worst)
                            {
                                worst = ratio;
                                worstCase = $"{hex} on {ground} ({theme})";
                            }
                        }
                    }
                }
            }
        }

        Assert.True(worst >= Minimum, $"Worst case {worstCase}: {worst:F2}:1");
    }

    /// <summary>The relative luminance of the glyph ink: the colour at the theme's lightness, clipped to sRGB.</summary>
    private static double GlyphInk(string hex, Func<double, double> lightness)
    {
        var (l, a, b) = ToOklab(Parse(hex));
        var (r, g, bl) = FromOklab(lightness(l), a, b);
        return RelativeLuminance(Math.Clamp(r, 0, 1), Math.Clamp(g, 0, 1), Math.Clamp(bl, 0, 1));
    }

    private static (double R, double G, double B) Parse(string hex) =>
        (Convert.ToInt32(hex[1..3], 16) / 255.0, Convert.ToInt32(hex[3..5], 16) / 255.0, Convert.ToInt32(hex[5..7], 16) / 255.0);

    private static double ToLinear(double c) => c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);

    /// <summary>The relative luminance of a gamma-encoded sRGB colour.</summary>
    private static double Luminance((double R, double G, double B) srgb) =>
        RelativeLuminance(ToLinear(srgb.R), ToLinear(srgb.G), ToLinear(srgb.B));

    /// <summary>WCAG relative luminance from LINEAR sRGB channels.</summary>
    private static double RelativeLuminance(double r, double g, double b) => (0.2126 * r) + (0.7152 * g) + (0.0722 * b);

    private static double Contrast(double a, double b) => (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);

    private static (double L, double A, double B) ToOklab((double R, double G, double B) srgb)
    {
        double r = ToLinear(srgb.R), g = ToLinear(srgb.G), b = ToLinear(srgb.B);
        var l = Math.Cbrt((0.4122214708 * r) + (0.5363325363 * g) + (0.0514459929 * b));
        var m = Math.Cbrt((0.2119034982 * r) + (0.6806995451 * g) + (0.1073969566 * b));
        var s = Math.Cbrt((0.0883024619 * r) + (0.2817188376 * g) + (0.6299787005 * b));
        return ((0.2104542553 * l) + (0.7936177850 * m) - (0.0040720468 * s),
                (1.9779984951 * l) - (2.4285922050 * m) + (0.4505937099 * s),
                (0.0259040371 * l) + (0.7827717662 * m) - (0.8086757660 * s));
    }

    // Returns LINEAR sRGB channels.
    private static (double R, double G, double B) FromOklab(double L, double a, double b)
    {
        var l = Math.Pow(L + (0.3963377774 * a) + (0.2158037573 * b), 3);
        var m = Math.Pow(L - (0.1055613458 * a) - (0.0638541728 * b), 3);
        var s = Math.Pow(L - (0.0894841775 * a) - (1.2914855480 * b), 3);
        return ((4.0767416621 * l) - (3.3077115913 * m) + (0.2309699292 * s),
                (-1.2684380046 * l) + (2.6097574011 * m) - (0.3413193965 * s),
                (-0.0041960863 * l) - (0.7034186147 * m) + (1.7076147010 * s));
    }
}
