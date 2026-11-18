using System.Globalization;
using System.Text.RegularExpressions;
using MudBlazor.Utilities;
using Odyssey.Client.Pages.Journal;
using Odyssey.Client.Theme;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// The location map's colour pairs (issue #371 AC 17; §6.7). Runs in CI with no design-system copy.
/// </summary>
/// <remarks>
/// <para>
/// The pairs are <b>read out of the stylesheets</b> — <c>location-map.css</c> for the MapLibre-built parts,
/// <c>OdsLocationMap.razor.css</c> for the caption — and resolved against the MudTheme palette and
/// <c>app.css</c>. Re-skinning the pin, the ring or the caption onto other tokens therefore re-runs these
/// ratios instead of passing on the old pair.
/// </para>
/// <para>
/// The basemap is not a token — it is whatever the configured style draws. So the pin and the focus ring
/// are checked against the two extremes a tile can be, white and black: one of their two tones must reach
/// 3:1 against each, which is what lets them hold on any tile in either theme.
/// </para>
/// </remarks>
public class LocationMapContrastTests
{
    private const double Text = 4.5;
    private const double NonText = 3.0;
    private static readonly Rgb White = new(1, 1, 1);
    private static readonly Rgb Black = new(0, 0, 0);

    private static readonly string AppCss = Read("wwwroot", "css", "app.css");
    private static readonly string MapCss = Read("wwwroot", "css", "location-map.css");
    private static readonly string ComponentCss = Read("Components", "OdsLocationMap.razor.css");

    public static TheoryData<bool> Themes => [false, true];

    [Theory]
    [MemberData(nameof(Themes))]
    public void The_caption_reads_on_the_surface(bool dark)
    {
        var surface = Token("--mud-palette-surface", dark);

        AssertRatio(Colour(Declaration(ComponentCss, ".loc-map-caption", "color"), dark), surface, Text, "caption text", dark);
        AssertRatio(Colour(Declaration(ComponentCss, ".loc-map-caption a", "color"), dark), surface, Text, "caption link", dark);
    }

    [Theory]
    [MemberData(nameof(Themes))]
    public void Control_glyphs_and_the_popup_read_on_their_panels(bool dark)
    {
        var button = Token(Var(Declaration(MapCss, ".loc-map-btn", "background")), dark);
        var popup = Token(Var(Declaration(MapCss, ".loc-map-popup", "background")), dark);

        AssertRatio(Colour(Declaration(MapCss, ".loc-map-btn", "color"), dark), button, Text, "control glyph", dark);
        AssertRatio(Colour(Declaration(MapCss, ".loc-map-popup", "color"), dark), popup, Text, "popup heading", dark);
    }

    [Theory]
    [MemberData(nameof(Themes))]
    public void The_re_centre_glyph_in_the_location_accent_clears_3_to_1_on_its_button(bool dark)
    {
        var rule = Declaration(MapCss, ".loc-map-btn.c", "color");
        Assert.Matches(@"^oklch\(from var\(--loc-accent\) var\(--glyph-l, l\) c h\)$", rule);

        var (l, c, h) = ParseOklch(LocationVisuals.Accent);
        var capped = dark ? l : Math.Min(l, GlyphCap());
        var button = Token(Var(Declaration(MapCss, ".loc-map-btn", "background")), dark);

        AssertRatio(FromOklch(capped, c, h), button, NonText, "Re-centre glyph", dark);
    }

    [Theory]
    [MemberData(nameof(Themes))]
    public void One_of_the_pins_two_rings_reaches_3_to_1_on_any_tile(bool dark)
    {
        var rings = Vars(Declaration(MapCss, ".loc-map-pin-shape", "box-shadow"))
            .Where(v => v.StartsWith("--mud-palette-", StringComparison.Ordinal))
            .Select(v => (Name: v, Rgb: Token(v, dark)))
            .ToList();
        Assert.Equal(2, rings.Count);

        foreach (var tile in new[] { White, Black })
        {
            Assert.True(
                rings.Any(r => Ratio(r.Rgb, tile) >= NonText),
                $"Neither pin ring ({string.Join(", ", rings.Select(r => r.Name))}) reaches 3:1 on {tile} in the {Theme(dark)} theme.");
        }
    }

    [Theory]
    [MemberData(nameof(Themes))]
    public void One_tone_of_the_focus_ring_reaches_3_to_1_on_any_tile(bool dark)
    {
        const string selector = ".loc-map-btn:focus-visible,\n.loc-map-pin:focus-visible,\n.loc-map-popup-close:focus-visible";
        var tones = Vars(Declaration(MapCss, selector, "outline"))
            .Concat(Vars(Declaration(MapCss, selector, "box-shadow")))
            .Where(v => v is "--focus-ring" or "--mud-palette-surface")
            .Distinct()
            .Select(v => (Name: v, Rgb: Token(v, dark)))
            .ToList();
        Assert.Equal(2, tones.Count);

        foreach (var tile in new[] { White, Black })
        {
            Assert.True(
                tones.Any(t => Ratio(t.Rgb, tile) >= NonText),
                $"Neither focus-ring tone ({string.Join(", ", tones.Select(t => t.Name))}) reaches 3:1 on {tile} in the {Theme(dark)} theme.");
        }
    }

    // ── CSS reading ──────────────────────────────────────────────────────────────────────────────

    private static string Read(params string[] parts) =>
        Regex.Replace(File.ReadAllText(Path.Combine([ClientSource.Root, .. parts])), @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);

    /// <summary>A declaration from the rule whose selector list is exactly <paramref name="selector"/>, outside any @media.</summary>
    private static string Declaration(string css, string selector, string property)
    {
        var normalised = Normalise(selector);
        var outside = Regex.Replace(css, @"@media[^{]*\{(?:[^{}]*\{[^{}]*\})*[^{}]*\}", string.Empty);
        var rule = Regex.Matches(outside, @"(?<selector>[^{}]+)\{(?<body>[^{}]*)\}")
            .FirstOrDefault(m => Normalise(m.Groups["selector"].Value) == normalised);
        Assert.True(rule is not null, $"No rule for '{selector}'.");

        var declaration = Regex.Match(rule.Groups["body"].Value, @"(?<![-\w])" + Regex.Escape(property) + @"\s*:\s*(?<value>[^;]+);");
        Assert.True(declaration.Success, $"'{selector}' declares no {property}.");
        return declaration.Groups["value"].Value.Trim();
    }

    private static string Normalise(string selector) =>
        string.Join(",", selector.Split(',').Select(s => Regex.Replace(s.Trim(), @"\s+", " ")));

    private static IEnumerable<string> Vars(string value) =>
        Regex.Matches(value, @"var\((?<name>--[\w-]+)").Select(m => m.Groups["name"].Value);

    private static string Var(string value)
    {
        var match = Regex.Match(value, @"^var\((?<name>--[\w-]+)\)$");
        Assert.True(match.Success, $"Expected a bare token, got '{value}'.");
        return match.Groups["name"].Value;
    }

    private static Rgb Colour(string value, bool dark) => Token(Var(value), dark);

    // ── Token resolution ─────────────────────────────────────────────────────────────────────────

    private static readonly Dictionary<string, Func<MudBlazor.Palette, MudColor>> PaletteTokens = new(StringComparer.Ordinal)
    {
        ["--mud-palette-surface"] = p => p.Surface,
        ["--mud-palette-text-primary"] = p => p.TextPrimary,
        ["--mud-palette-text-secondary"] = p => p.TextSecondary,
        ["--mud-palette-primary"] = p => p.Primary,
    };

    /// <summary>
    /// A MudTheme palette token, or an <c>app.css</c> token for the theme (<c>:root</c> is light,
    /// <c>[data-theme='dark']</c> overrides), followed through <c>var(…)</c> to a hex literal.
    /// </summary>
    private static Rgb Token(string name, bool dark)
    {
        if (PaletteTokens.TryGetValue(name, out var pick))
        {
            var c = pick(dark ? OdysseyTheme.Theme.PaletteDark : OdysseyTheme.Theme.PaletteLight);
            return new Rgb(c.R / 255.0, c.G / 255.0, c.B / 255.0);
        }

        var declarations = Regex.Matches(AppCss, @"(?<![-\w])" + Regex.Escape(name) + @"\s*:\s*(?<value>[^;]+?)\s*;")
            .Select(m => (Dark: InDarkBlock(m.Index), Value: m.Groups["value"].Value))
            .ToList();
        Assert.True(declarations.Count > 0, $"{name} is declared nowhere in app.css.");
        var value = declarations.FirstOrDefault(d => d.Dark == dark).Value ?? declarations.First(d => !d.Dark).Value;

        if (Regex.IsMatch(value, "^#[0-9A-Fa-f]{6}$"))
            return Hex(value);

        return Token(Var(value), dark);
    }

    private static bool InDarkBlock(int index)
    {
        var open = AppCss.LastIndexOf('{', index);
        var previous = AppCss.LastIndexOfAny(['}', ';'], open);
        return AppCss[(previous + 1)..open].Contains("data-theme='dark'", StringComparison.Ordinal);
    }

    /// <summary>The light theme's glyph lightness cap (<c>--glyph-l: min(l, 0.58)</c>); dark passes <c>l</c> through.</summary>
    private static double GlyphCap()
    {
        var match = Regex.Match(AppCss, @"--glyph-l:\s*min\(l,\s*(?<cap>[\d.]+)\)");
        Assert.True(match.Success, "app.css has no light-theme --glyph-l cap.");
        return double.Parse(match.Groups["cap"].Value, CultureInfo.InvariantCulture);
    }

    // ── Colour maths ──────────────────────────────────────────────────────────────────────────────

    private readonly record struct Rgb(double R, double G, double B)
    {
        public override string ToString() =>
            $"#{(int)Math.Round(R * 255):X2}{(int)Math.Round(G * 255):X2}{(int)Math.Round(B * 255):X2}";
    }

    private static Rgb Hex(string hex) => new(
        Convert.ToByte(hex.Substring(1, 2), 16) / 255.0,
        Convert.ToByte(hex.Substring(3, 2), 16) / 255.0,
        Convert.ToByte(hex.Substring(5, 2), 16) / 255.0);

    private static (double L, double C, double H) ParseOklch(string value)
    {
        var m = Regex.Match(value, @"oklch\((?<l>[\d.]+)\s+(?<c>[\d.]+)\s+(?<h>[\d.]+)\)");
        Assert.True(m.Success, $"'{value}' is not an oklch() colour.");
        double D(string g) => double.Parse(m.Groups[g].Value, CultureInfo.InvariantCulture);
        return (D("l"), D("c"), D("h"));
    }

    /// <summary>OKLCH → sRGB (Björn Ottosson's matrices), clipped to the gamut.</summary>
    private static Rgb FromOklch(double l, double c, double hDegrees)
    {
        var h = hDegrees * Math.PI / 180;
        var (a, b) = (c * Math.Cos(h), c * Math.Sin(h));
        var l_ = Math.Pow(l + 0.3963377774 * a + 0.2158037573 * b, 3);
        var m_ = Math.Pow(l - 0.1055613458 * a - 0.0638541728 * b, 3);
        var s_ = Math.Pow(l - 0.0894841775 * a - 1.2914855480 * b, 3);
        double Encode(double x) => Math.Clamp(x <= 0.0031308 ? 12.92 * x : 1.055 * Math.Pow(x, 1 / 2.4) - 0.055, 0, 1);
        return new Rgb(
            Encode(4.0767416621 * l_ - 3.3077115913 * m_ + 0.2309699292 * s_),
            Encode(-1.2684380046 * l_ + 2.6097574011 * m_ - 0.3413193965 * s_),
            Encode(-0.0041960863 * l_ - 0.7034186147 * m_ + 1.7076147010 * s_));
    }

    private static void AssertRatio(Rgb foreground, Rgb background, double minimum, string what, bool dark)
    {
        var ratio = Ratio(foreground, background);
        Assert.True(
            ratio >= minimum,
            $"{what} ({Theme(dark)}): {foreground} on {background} is {ratio.ToString("0.00", CultureInfo.InvariantCulture)}:1, "
            + $"below {minimum.ToString("0.0", CultureInfo.InvariantCulture)}:1.");
    }

    private static double Ratio(Rgb a, Rgb b)
    {
        var (la, lb) = (Luminance(a), Luminance(b));
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    private static double Luminance(Rgb c) => 0.2126 * Linear(c.R) + 0.7152 * Linear(c.G) + 0.0722 * Linear(c.B);

    private static double Linear(double s) => s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);

    private static string Theme(bool dark) => dark ? "dark" : "light";
}
