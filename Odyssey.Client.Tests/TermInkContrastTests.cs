using System.Globalization;
using System.Text.RegularExpressions;
using Odyssey.Client.Pages.Finance;
using Odyssey.Client.Theme;
using Odyssey.Dtos.Finance;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// The term unit INK — the unit hue where it is TEXT (a tile or row figure) or a chart line. The design
/// system's unit hues sit at L 0.77–0.78, which reads ~2:1 on the light theme's ground: under both the
/// 4.5:1 text floor (WCAG 2.2 SC 1.4.3) and the 3:1 graphics floor (SC 1.4.11). So the figure and the
/// line read a per-theme token instead, while the decorative glyph keeps the design hue.
/// </summary>
/// <remarks>
/// Asserts the <b>ratio</b>, computed from the token as authored in <c>app.css</c> against the MudTheme
/// background and surface as configured, rather than pinning the literal — a re-themed ground that
/// was not re-checked is exactly the regression a literal would wave through.
/// </remarks>
public class TermInkContrastTests
{
    private const double TextContrastMinimum = 4.5;

    private static readonly string AppCss = Path.Combine(ClientSource.Root, "wwwroot", "css", "app.css");

    public static TheoryData<string, bool> Cases() => new()
    {
        { "--term-percentage-ink", false },
        { "--term-percentage-ink", true },
        { "--term-amount-ink", false },
        { "--term-amount-ink", true },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void The_ink_clears_the_text_floor_on_both_grounds(string token, bool dark)
    {
        var ink = ParseOklch(TokenValue(token, dark));
        var palette = dark ? (MudBlazor.Palette)OdysseyTheme.Theme.PaletteDark : OdysseyTheme.Theme.PaletteLight;

        foreach (var ground in new[] { palette.Background, palette.Surface })
        {
            var ratio = Contrast(Luminance(ink), Luminance(Linear(ground)));
            Assert.True(ratio >= TextContrastMinimum,
                $"{token} ({(dark ? "dark" : "light")}) on {ground} is {ratio.ToString("0.00", CultureInfo.InvariantCulture)}:1, below {TextContrastMinimum}:1.");
        }
    }

    /// <summary>Every figure and chart line reads the ink; only the glyph keeps the raw design hue.</summary>
    [Fact]
    public void Figures_and_chart_lines_read_the_ink_token()
    {
        var account = new ExistingTerm { TermId = Guid.NewGuid(), AccountId = Guid.NewGuid(), Label = "APR", ValueUnit = TermValueUnit.Percentage, Value = 0.05m };

        Assert.Equal("var(--term-percentage-ink)", TermVisuals.ValueColor(account));
        Assert.Equal("var(--term-amount-ink)", TermVisuals.UnitInfo(TermValueUnit.Amount).Ink);
        Assert.Equal("var(--term-percentage-ink)", Assert.Single(TermChartSeries.Build([account], DateTime.UtcNow, (v, _) => v.ToString(CultureInfo.InvariantCulture))).Color);
    }

    // ── Parsing ─────────────────────────────────────────────────────────────────────────────────

    private static string TokenValue(string token, bool dark)
    {
        var css = File.ReadAllText(AppCss);
        var blockStart = dark ? css.IndexOf("[data-theme='dark'] {", StringComparison.Ordinal) : css.IndexOf(":root {", StringComparison.Ordinal);
        var block = css[blockStart..css.IndexOf('}', blockStart)];
        var m = Regex.Match(block, Regex.Escape(token) + @":\s*([^;]+);");
        Assert.True(m.Success, $"{token} is not declared in the {(dark ? "dark" : "light")} block of app.css.");
        return m.Groups[1].Value.Trim();
    }

    private static (double R, double G, double B) ParseOklch(string value)
    {
        var m = Regex.Match(value, @"^oklch\(\s*([\d.]+)\s+([\d.]+)\s+([\d.]+)\s*\)$");
        Assert.True(m.Success, $"Expected an opaque oklch() literal, got '{value}'.");
        double L = D(m.Groups[1].Value), C = D(m.Groups[2].Value), h = D(m.Groups[3].Value) * Math.PI / 180;
        double a = C * Math.Cos(h), b = C * Math.Sin(h);
        double l = Math.Pow(L + 0.3963377774 * a + 0.2158037573 * b, 3);
        double mm = Math.Pow(L - 0.1055613458 * a - 0.0638541728 * b, 3);
        double s = Math.Pow(L - 0.0894841775 * a - 1.2914855480 * b, 3);
        // Linear sRGB, clamped to gamut.
        return (Clamp(4.0767416621 * l - 3.3077115913 * mm + 0.2309699292 * s),
                Clamp(-1.2684380046 * l + 2.6097574011 * mm - 0.3413193965 * s),
                Clamp(-0.0041960863 * l - 0.7034186147 * mm + 1.7076147010 * s));
    }

    private static (double R, double G, double B) Linear(MudBlazor.Utilities.MudColor color)
    {
        static double Lin(byte channel)
        {
            var c = channel / 255.0;
            return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }
        return (Lin(color.R), Lin(color.G), Lin(color.B));
    }

    private static double Luminance((double R, double G, double B) c) => 0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B;

    private static double Contrast(double a, double b) => (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);

    private static double Clamp(double x) => Math.Clamp(x, 0, 1);

    private static double D(string s) => double.Parse(s, CultureInfo.InvariantCulture);
}
