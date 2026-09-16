using System.Globalization;
using System.Text.RegularExpressions;
using MudBlazor;
using Odyssey.Client.Theme;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// The informational chart axis (issue #97). <c>OdsLineChart</c>'s zero baseline is drawn only when
/// the value domain straddles zero — it is the line separating positive net worth from negative, so
/// it is a graphical object under WCAG 2.2 SC 1.4.11 and owes 3:1 against the surface it sits on.
/// The two <c>.nowline</c> rules carry the same meaning.
///
/// <para>
/// These assert the <b>ratio</b>, computed from the token as authored and the MudTheme surface as
/// configured, rather than pinning the alpha to a literal — the defect is a stroke that cannot be
/// seen, and an alpha is only passing relative to whatever is behind it. Re-theming a surface
/// without re-checking the axis is exactly the regression a literal would wave through.
/// </para>
///
/// <para>
/// They also pin the client's chart tokens to the design system's, because the light-theme
/// <c>--chart-axis</c> had silently drifted (0.14 in the client vs 0.24 in the design system) and
/// that drift is half of what issue #97 reports.
/// </para>
/// </summary>
public class ChartAxisContrastTests
{
    /// <summary>WCAG 2.2 SC 1.4.11 (Non-text Contrast, Level AA) for graphical objects.</summary>
    private const double NonTextContrastMinimum = 3.0;

    private static readonly string AppCss =
        Path.Combine(ClientSource.Root, "wwwroot", "css", "app.css");

    private static readonly string ComponentsCss =
        Path.Combine(ClientSource.Root, "wwwroot", "css", "odyssey-components.css");

    private static readonly string DesignSystemCss =
        ClientSource.Sibling(Path.Combine("Odyssey Design System", "colors_and_type.css"));

    /// <summary>The selectors whose stroke is read rather than decorative.</summary>
    public static TheoryData<string> InformationalRules() =>
    [
        ".odc-lc-zero",      // OdsLineChart's zero baseline
        ".trm-chart .nowline",
        ".est-chart .nowline",
    ];

    // ── The ratio ───────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(Theme.Light)]
    [InlineData(Theme.Dark)]
    public void The_informational_axis_token_clears_three_to_one_against_its_surface(Theme theme)
    {
        var stroke = TokenValues(AppCss, "--chart-axis-strong")[theme];
        var surface = SurfaceOf(theme);

        var ratio = ContrastRatio(Composite(stroke, surface), surface);

        Assert.True(
            ratio >= NonTextContrastMinimum,
            $"--chart-axis-strong ({stroke}) on the {theme} surface ({surface}) is "
            + $"{ratio.ToString("0.00", CultureInfo.InvariantCulture)}:1, below the "
            + $"{NonTextContrastMinimum:0.0}:1 WCAG 1.4.11 requires for a graphical object. "
            + "Raise the alpha until it clears, and update the design system's token first.");
    }

    /// <summary>
    /// The decorative token is deliberately NOT held to 3:1 — it strokes gridlines and hover
    /// furniture, which nothing has to see to read the chart. This asserts the two are genuinely
    /// different, so a future edit cannot quietly collapse the split back into one token and leave
    /// the zero baseline at a decorative alpha.
    /// </summary>
    [Theory]
    [InlineData(Theme.Light)]
    [InlineData(Theme.Dark)]
    public void The_decorative_axis_token_is_a_distinct_value(Theme theme)
    {
        var decorative = TokenValues(AppCss, "--chart-axis")[theme];
        var informational = TokenValues(AppCss, "--chart-axis-strong")[theme];

        Assert.NotEqual(informational, decorative);
    }

    // ── The rules that must use it ──────────────────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(InformationalRules))]
    public void An_informational_stroke_uses_the_strong_token(string selector)
    {
        var css = File.ReadAllText(ComponentsCss);
        var rule = Assert.Single(
            Regex.Matches(css, @"^" + Regex.Escape(selector) + @"\s*\{[^}]*\}",
                RegexOptions.Multiline).Select(m => m.Value));

        Assert.Contains("var(--chart-axis-strong)", rule);
    }

    // ── Drift against the design system ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData("--chart-axis")]
    [InlineData("--chart-axis-strong")]
    [InlineData("--chart-grid")]
    public void The_client_chart_tokens_equal_the_design_systems(string token)
    {
        var client = TokenValues(AppCss, token);
        var design = TokenValues(DesignSystemCss, token);

        Assert.Equal(design[Theme.Light], client[Theme.Light]);
        Assert.Equal(design[Theme.Dark], client[Theme.Dark]);
    }

    // ── Parsing ─────────────────────────────────────────────────────────────────────────────────

    public enum Theme { Light, Dark }

    private readonly record struct Rgba(byte R, byte G, byte B, double A)
    {
        public override string ToString() =>
            $"rgba({R},{G},{B},{A.ToString("0.##", CultureInfo.InvariantCulture)})";
    }

    /// <summary>
    /// Both declarations of <paramref name="token"/>, keyed by the theme whose block they sit in.
    /// The client is light-default with a <c>[data-theme='dark']</c> override and the design system
    /// is dark-default with a <c>[data-theme='light']</c> one, so the block carrying no explicit
    /// theme is resolved as whichever theme the other block is not.
    /// </summary>
    private static IReadOnlyDictionary<Theme, Rgba> TokenValues(string cssPath, string token)
    {
        var css = File.ReadAllText(cssPath);
        var matches = Regex.Matches(css, Regex.Escape(token) + @"\s*:\s*(?<value>rgba\([^)]*\))\s*;");

        Assert.True(
            matches.Count == 2,
            $"Expected {token} to be declared exactly twice (one block per theme) in "
            + $"{Path.GetFileName(cssPath)}, found {matches.Count}.");

        var declarations = matches
            .Select(m => (Theme: ThemeOfBlockAt(css, m.Index), Value: ParseRgba(m.Groups["value"].Value)))
            .ToList();

        var explicitly = declarations.Where(d => d.Theme is not null).ToList();
        Assert.True(
            explicitly.Count >= 1 && explicitly.Select(d => d.Theme).Distinct().Count() == explicitly.Count,
            $"Could not tell the two {token} blocks apart in {Path.GetFileName(cssPath)}.");

        return declarations
            .Select(d => (
                Theme: d.Theme ?? (explicitly[0].Theme == Theme.Dark ? Theme.Light : Theme.Dark),
                d.Value))
            .ToDictionary(d => d.Theme, d => d.Value);
    }

    /// <summary>
    /// The theme of the block containing <paramref name="index"/>, or <c>null</c> when its selector
    /// names no theme (the file's default block).
    /// </summary>
    private static Theme? ThemeOfBlockAt(string css, int index)
    {
        var open = css.LastIndexOf('{', index);
        Assert.True(open >= 0, "Declaration found outside any block.");

        var previous = css.LastIndexOfAny(['}', ';'], open);
        var selector = css[(previous + 1)..open];

        if (selector.Contains("data-theme='dark'", StringComparison.Ordinal)) return Theme.Dark;
        if (selector.Contains("data-theme='light'", StringComparison.Ordinal)) return Theme.Light;
        return null;
    }

    private static Rgba ParseRgba(string value)
    {
        var parts = value["rgba(".Length..^1]
            .Split(',')
            .Select(p => double.Parse(p.Trim(), CultureInfo.InvariantCulture))
            .ToArray();

        Assert.Equal(4, parts.Length);
        return new Rgba((byte)parts[0], (byte)parts[1], (byte)parts[2], parts[3]);
    }

    private static Rgba SurfaceOf(Theme theme)
    {
        var surface = theme == Theme.Dark
            ? OdysseyTheme.Theme.PaletteDark.Surface
            : OdysseyTheme.Theme.PaletteLight.Surface;

        return new Rgba(surface.R, surface.G, surface.B, 1.0);
    }

    // ── WCAG maths ──────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// <paramref name="over"/> composited onto the opaque <paramref name="onto"/>. Done on the 8-bit
    /// channels rather than in linear light, which is what a browser does for a translucent stroke.
    /// </summary>
    private static Rgba Composite(Rgba over, Rgba onto) => new(
        (byte)Math.Round(over.A * over.R + (1 - over.A) * onto.R),
        (byte)Math.Round(over.A * over.G + (1 - over.A) * onto.G),
        (byte)Math.Round(over.A * over.B + (1 - over.A) * onto.B),
        1.0);

    private static double ContrastRatio(Rgba a, Rgba b)
    {
        var (lighter, darker) = (Math.Max(Luminance(a), Luminance(b)), Math.Min(Luminance(a), Luminance(b)));
        return (lighter + 0.05) / (darker + 0.05);
    }

    /// <summary>WCAG 2.2 relative luminance.</summary>
    private static double Luminance(Rgba c) =>
        0.2126 * Linear(c.R) + 0.7152 * Linear(c.G) + 0.0722 * Linear(c.B);

    private static double Linear(byte channel)
    {
        var s = channel / 255.0;
        return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
    }
}
