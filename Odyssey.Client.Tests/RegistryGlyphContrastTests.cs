using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using Odyssey.Client.Components;
using Odyssey.Client.Theme;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// The contrast contract of every <see cref="OdsTypeRegistries"/> colour, across every registry at once
/// (issue #164; Odyssey Design System · <c>docs/glyph-contrast-decision.md</c>). The registry constants
/// are authored ONCE, in a lightness band tuned for the dark surface, and a render site paints them
/// through <see cref="OdsGlyphInk"/>: on dark the colour passes through unchanged, on light its
/// lightness is capped — <c>--glyph-l</c> at 0.58 for a glyph, <c>--glyph-text-l</c> at 0.50 for a
/// registry colour used as text — with chroma and hue kept. The ratios are computed from the colour
/// <b>as authored</b>, the cap <b>as declared in <c>app.css</c></b> and the surface <b>as themed</b>,
/// never pinned to a literal ratio.
/// </summary>
/// <remarks>
/// <para>
/// This replaces the per-registry <c>ContractEventTypeContrastTests</c> and its characterization test
/// of the light-theme shortfall, which the cap resolves for the whole family by construction.
/// </para>
/// <para>
/// Each light assertion is made twice: against <c>PaletteLight.Surface</c>, and against the colour's
/// own soft tint (<see cref="OdsTypeOption.Soft"/>, the <c>/ 0.16</c> ground a glyph usually sits on)
/// composited over that surface. The tint keeps the AUTHORED colour, so it is the lighter ground and
/// the one the cap was measured against.
/// </para>
/// </remarks>
public sealed class RegistryGlyphContrastTests
{
    /// <summary>WCAG 2.2 SC 1.4.11 (Non-text Contrast, Level AA) for a meaningful graphic.</summary>
    private const double NonTextContrastMinimum = 3.0;

    /// <summary>WCAG 2.2 SC 1.4.3 (Contrast (Minimum), Level AA) for normal-size text.</summary>
    private const double TextContrastMinimum = 4.5;

    private static readonly Regex OklchTriple = new(
        @"^oklch\(\s*(?<l>[\d.]+)\s+(?<c>[\d.]+)\s+(?<h>[\d.]+)\s*(?:/\s*(?<a>[\d.]+)\s*)?\)$");

    /// <summary>Every registry: each public static <c>IReadOnlyList&lt;OdsTypeOption&gt;</c> field.</summary>
    private static readonly IReadOnlyList<(string Name, IReadOnlyList<OdsTypeOption> Items)> Registries =
        [.. typeof(OdsTypeRegistries)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.FieldType == typeof(IReadOnlyList<OdsTypeOption>))
            .Select(f => (f.Name, (IReadOnlyList<OdsTypeOption>)f.GetValue(null)!))];

    public static TheoryData<string, string> EveryMember()
    {
        var data = new TheoryData<string, string>();
        foreach (var (name, items) in Registries)
            foreach (var item in items)
                data.Add(name, item.Key);
        return data;
    }

    /// <summary>
    /// The sweep is by reflection, so a registry added later is covered without touching this file —
    /// and this pins that the sweep actually found the family, rather than passing over nothing.
    /// </summary>
    [Fact]
    public void The_sweep_covers_all_fifteen_registries()
    {
        Assert.Equal(15, Registries.Count);
        Assert.All(Registries, r => Assert.NotEmpty(r.Items));
    }

    [Theory]
    [MemberData(nameof(EveryMember))]
    public void Every_glyph_clears_three_to_one_on_the_dark_surface(string registry, string key)
    {
        var (l, c, h) = ColourOf(registry, key);

        AssertRatio(registry, key, "glyph (dark, authored)", Srgb(l, c, h), Surface(dark: true), NonTextContrastMinimum);
    }

    [Theory]
    [MemberData(nameof(EveryMember))]
    public void Every_glyph_clears_three_to_one_on_light_through_the_glyph_cap(string registry, string key)
    {
        var (l, c, h) = ColourOf(registry, key);
        var glyph = Srgb(Math.Min(l, LightCap("--glyph-l")), c, h);
        var surface = Surface(dark: false);

        AssertRatio(registry, key, "glyph (light, --glyph-l)", glyph, surface, NonTextContrastMinimum);
        AssertRatio(registry, key, "glyph (light, --glyph-l) on its soft tint", glyph, TintOver(registry, key, surface), NonTextContrastMinimum);
    }

    [Theory]
    [MemberData(nameof(EveryMember))]
    public void Every_colour_used_as_text_clears_four_and_a_half_to_one_on_light_through_the_text_cap(string registry, string key)
    {
        var (l, c, h) = ColourOf(registry, key);
        var text = Srgb(Math.Min(l, LightCap("--glyph-text-l")), c, h);
        var surface = Surface(dark: false);

        AssertRatio(registry, key, "text (light, --glyph-text-l)", text, surface, TextContrastMinimum);
        AssertRatio(registry, key, "text (light, --glyph-text-l) on its soft tint", text, TintOver(registry, key, surface), TextContrastMinimum);
    }

    /// <summary>
    /// The cap keeps hue and chroma, and the tint is measured as the authored colour — so a member whose
    /// soft ground names a DIFFERENT colour would be measured against a tint it is not drawn on.
    /// </summary>
    [Theory]
    [MemberData(nameof(EveryMember))]
    public void Every_soft_tint_is_the_authored_colour_at_sixteen_percent(string registry, string key)
    {
        var option = Option(registry, key);
        var colour = Parse(option.Color);
        var soft = Parse(option.Soft);

        Assert.Null(colour.Alpha);
        Assert.Equal((colour.L, colour.C, colour.H), (soft.L, soft.C, soft.H));
        Assert.Equal(0.16, soft.Alpha);
    }

    /// <summary>
    /// No meaning rides on the hue alone: every member carries its label as text beside the glyph,
    /// which is what answers WCAG 1.4.1 — and what keeps the no-relative-colour fallback (the glyph
    /// inherits the text colour) informative.
    /// </summary>
    [Theory]
    [MemberData(nameof(EveryMember))]
    public void Every_member_carries_a_text_label_beside_its_glyph(string registry, string key)
    {
        var option = Option(registry, key);

        Assert.False(string.IsNullOrWhiteSpace(option.Label));
        Assert.NotEqual(option.Icon, option.Label);
    }

    /// <summary>
    /// The account-type hues (<c>--acct-*</c> in <c>app.css</c>, read by <c>AccountTypeVisuals</c>) are
    /// drawn through the same wrap — the account-type chip, the estimate glyph, the transaction dialog's
    /// account picker — so they owe the same two ratios.
    /// </summary>
    [Fact]
    public void Every_account_type_glyph_clears_three_to_one_in_both_themes()
    {
        var tokens = Regex.Matches(AppCss(), @"(?<name>--acct-[a-z-]+?):\s*(?<value>oklch\([^)]*\));")
            .Where(m => !m.Groups["name"].Value.EndsWith("-soft", StringComparison.Ordinal))
            .ToList();
        Assert.NotEmpty(tokens);

        foreach (var token in tokens)
        {
            var name = token.Groups["name"].Value;
            var (l, c, h, _) = Parse(token.Groups["value"].Value);

            AssertRatio("AccountTypeVisuals", name, "glyph (dark, authored)", Srgb(l, c, h), Surface(dark: true), NonTextContrastMinimum);
            AssertRatio("AccountTypeVisuals", name, "glyph (light, --glyph-l)",
                Srgb(Math.Min(l, LightCap("--glyph-l")), c, h), Surface(dark: false), NonTextContrastMinimum);
        }
    }

    /// <summary>
    /// The caps the ratios above are computed from are read out of <c>app.css</c>, so this pins their
    /// shape: dark passes <c>l</c> through, light takes <c>min(l, …)</c>, anchored to <c>html</c> —
    /// MudBlazor-independent, keyed on the <c>data-theme</c> attribute <c>OdysseyThemeProvider</c> owns.
    /// </summary>
    [Fact]
    public void The_caps_pass_dark_through_and_bound_light_only()
    {
        var css = AppCss();

        Assert.Matches(@":root,\s*\[data-theme='dark'\]\s*\{\s*--glyph-l:\s*l;\s*--glyph-text-l:\s*l;\s*\}", css);
        Assert.Equal(0.58, LightCap("--glyph-l"));
        Assert.Equal(0.50, LightCap("--glyph-text-l"));
    }

    // ── Plumbing ────────────────────────────────────────────────────────────────

    private static OdsTypeOption Option(string registry, string key) =>
        Registries.Single(r => r.Name == registry).Items.Single(t => t.Key == key);

    private static (double L, double C, double H) ColourOf(string registry, string key)
    {
        var (l, c, h, _) = Parse(Option(registry, key).Color);
        return (l, c, h);
    }

    private static (double L, double C, double H, double? Alpha) Parse(string value)
    {
        var match = OklchTriple.Match(value);
        Assert.True(match.Success, $"'{value}' is not an oklch(L C H [/ A]) colour.");

        static double D(Group g) => double.Parse(g.Value, CultureInfo.InvariantCulture);
        return (D(match.Groups["l"]), D(match.Groups["c"]), D(match.Groups["h"]),
            match.Groups["a"].Success ? D(match.Groups["a"]) : null);
    }

    private static string AppCss() =>
        File.ReadAllText(Path.Combine(ClientSource.Root, "wwwroot", "css", "app.css"));

    /// <summary>The <c>min(l, N)</c> bound a token takes on the light theme, read from <c>app.css</c>.</summary>
    private static double LightCap(string token)
    {
        var block = Regex.Match(AppCss(), @"html:not\(\[data-theme='dark'\]\)\s*\{(?<body>[^}]*)\}");
        Assert.True(block.Success, "app.css declares no html:not([data-theme='dark']) glyph-cap block.");

        var cap = Regex.Match(block.Groups["body"].Value, Regex.Escape(token) + @":\s*min\(\s*l\s*,\s*(?<n>[\d.]+)\s*\)\s*;");
        Assert.True(cap.Success, $"{token} is not declared as min(l, N) on the light theme.");
        return double.Parse(cap.Groups["n"].Value, CultureInfo.InvariantCulture);
    }

    private static (double R, double G, double B) Surface(bool dark)
    {
        var surface = dark ? OdysseyTheme.Theme.PaletteDark.Surface : OdysseyTheme.Theme.PaletteLight.Surface;
        return (surface.R / 255.0, surface.G / 255.0, surface.B / 255.0);
    }

    /// <summary>The member's soft tint composited over <paramref name="surface"/>, in gamma-encoded sRGB (how a browser blends).</summary>
    private static (double R, double G, double B) TintOver(string registry, string key, (double R, double G, double B) surface)
    {
        var (l, c, h, alpha) = Parse(Option(registry, key).Soft);
        var a = alpha ?? 1.0;
        var tint = Srgb(l, c, h);

        return (tint.R * a + surface.R * (1 - a), tint.G * a + surface.G * (1 - a), tint.B * a + surface.B * (1 - a));
    }

    /// <summary>
    /// <c>oklch(L C H)</c> → gamma-encoded sRGB in [0, 1], through OKLab and linear sRGB. Out-of-gamut
    /// channels are clamped rather than gamut-mapped — the conservative reading, as in
    /// <c>ChartAxisContrastTests</c>.
    /// </summary>
    private static (double R, double G, double B) Srgb(double l, double c, double hDegrees)
    {
        var h = hDegrees * Math.PI / 180.0;
        var a = c * Math.Cos(h);
        var b = c * Math.Sin(h);

        var lCube = Math.Pow(l + 0.3963377774 * a + 0.2158037573 * b, 3);
        var mCube = Math.Pow(l - 0.1055613458 * a - 0.0638541728 * b, 3);
        var sCube = Math.Pow(l - 0.0894841775 * a - 1.2914855480 * b, 3);

        return (
            Encode(4.0767416621 * lCube - 3.3077115913 * mCube + 0.2309699292 * sCube),
            Encode(-1.2684380046 * lCube + 2.6097574011 * mCube - 0.3413193965 * sCube),
            Encode(-0.0041960863 * lCube - 0.7034186147 * mCube + 1.7076147010 * sCube));

        static double Encode(double linear)
        {
            var clamped = Math.Clamp(linear, 0.0, 1.0);
            return clamped <= 0.0031308 ? 12.92 * clamped : (1.055 * Math.Pow(clamped, 1.0 / 2.4)) - 0.055;
        }
    }

    private static void AssertRatio(
        string registry, string key, string what,
        (double R, double G, double B) fg, (double R, double G, double B) bg, double minimum)
    {
        var ratio = ContrastRatio(fg, bg);
        Assert.True(
            ratio >= minimum,
            $"{registry}[{key}] {what} = {Hex(fg)} on {Hex(bg)} is "
            + $"{ratio.ToString("0.00", CultureInfo.InvariantCulture)}:1, below {minimum:0.0}:1. "
            + "Change the colour in the design system first, then mirror it here.");
    }

    private static double ContrastRatio((double R, double G, double B) a, (double R, double G, double B) b)
    {
        var (la, lb) = (Luminance(a), Luminance(b));
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    /// <summary>WCAG 2.2 relative luminance.</summary>
    private static double Luminance((double R, double G, double B) c) =>
        (0.2126 * Linear(c.R)) + (0.7152 * Linear(c.G)) + (0.0722 * Linear(c.B));

    private static double Linear(double s) =>
        s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);

    private static string Hex((double R, double G, double B) c) =>
        $"#{B(c.R):X2}{B(c.G):X2}{B(c.B):X2}";

    private static byte B(double v) => (byte)Math.Round(Math.Clamp(v, 0.0, 1.0) * 255);
}
