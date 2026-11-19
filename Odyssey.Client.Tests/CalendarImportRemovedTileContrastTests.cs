using System.Globalization;
using System.Text.RegularExpressions;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// The import result's removed tile (issue #517 §6.7, AC 18): its <c>delete_sweep</c> glyph is the error token on the
/// tile's own surface, and as a meaningful graphic it needs 3:1 (WCAG 2.2 1.4.11) in both themes. The colours are
/// resolved from the stylesheet's declared token and the theme's palette, not restated here.
/// </summary>
public class CalendarImportRemovedTileContrastTests
{
    [Fact]
    public void The_removed_glyph_and_tile_use_the_error_token_on_the_surface()
    {
        Assert.Contains("color: var(--mud-palette-error)", Rule(".cal-imp-stat.rm .material-icons"), StringComparison.Ordinal);
        Assert.Contains("background: var(--mud-palette-surface)", Rule(".cal-imp-stat {"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void The_removed_glyph_meets_3_to_1_on_the_tile(bool dark)
    {
        var glyph = Palette("Error", dark);
        var surface = Palette("Surface", dark);

        var ratio = Contrast(glyph, surface);

        Assert.True(ratio >= 3.0, $"Error {glyph} on surface {surface} ({(dark ? "dark" : "light")}) is {ratio:0.00}:1, below 3:1.");
    }

    private static string Rule(string selector)
    {
        var css = Regex.Replace(
            File.ReadAllText(Path.Combine(ClientSource.Root, "wwwroot", "css", "odyssey-components.css")),
            @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
        var start = css.IndexOf(selector, StringComparison.Ordinal);
        Assert.True(start >= 0, $"No rule for '{selector}'.");
        return css[start..css.IndexOf('}', start)];
    }

    /// <summary>A MudBlazor palette entry out of <c>OdysseyTheme.cs</c>; the dark palette is declared first.</summary>
    private static (int R, int G, int B) Palette(string property, bool dark)
    {
        var theme = File.ReadAllText(Path.Combine(ClientSource.Root, "Theme", "OdysseyTheme.cs"));
        var values = Regex.Matches(theme, @"\b" + Regex.Escape(property) + @"\s*=\s*""(?<hex>#[0-9A-Fa-f]{6})""")
            .Select(m => m.Groups["hex"].Value)
            .ToList();
        Assert.True(values.Count >= 2, $"Expected a {property} in each palette.");
        var hex = (dark ? values[0] : values[1]).TrimStart('#');
        return (
            int.Parse(hex[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            int.Parse(hex[2..4], NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            int.Parse(hex[4..6], NumberStyles.HexNumber, CultureInfo.InvariantCulture));
    }

    private static double Contrast((int R, int G, int B) a, (int R, int G, int B) b)
    {
        var (high, low) = (Math.Max(Luminance(a), Luminance(b)), Math.Min(Luminance(a), Luminance(b)));
        return (high + 0.05) / (low + 0.05);
    }

    private static double Luminance((int R, int G, int B) rgb)
    {
        static double Channel(int value)
        {
            var c = value / 255.0;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }

        return (0.2126 * Channel(rgb.R)) + (0.7152 * Channel(rgb.G)) + (0.0722 * Channel(rgb.B));
    }
}
