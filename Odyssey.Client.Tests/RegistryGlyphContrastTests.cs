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

    /// <summary>
    /// The number of registries, pinned EXACTLY. A registry added (or removed) must change this number
    /// deliberately, which is the moment to check it renders through <see cref="OdsGlyphInk"/>.
    /// </summary>
    private const int RegistryCount = 15;

    /// <summary>
    /// The authored band, as the design system states it (handoff/README.md → "Registry glyph
    /// lightness": "tuned for dark (L 0.66–0.80)"; docs/glyph-contrast-decision.md measures the caps as
    /// the worst case "over every hue at C ≤ 0.16"). The caps' measured ratios are only a guarantee
    /// inside this band, so a member authored outside it is outside the design's warranty.
    /// </summary>
    private const double BandMinLightness = 0.66, BandMaxLightness = 0.80, BandMaxChroma = 0.16;

    /// <summary>Every registry: each public static <c>IReadOnlyList&lt;OdsTypeOption&gt;</c> field or property.</summary>
    private static readonly IReadOnlyList<(string Name, IReadOnlyList<OdsTypeOption> Items)> Registries = FindRegistries();

    private static IReadOnlyList<(string Name, IReadOnlyList<OdsTypeOption> Items)> FindRegistries()
    {
        var members = new List<(string Name, Type Type, Func<object?> Get)>();
        foreach (var f in typeof(OdsTypeRegistries).GetFields(BindingFlags.Public | BindingFlags.Static))
            members.Add((f.Name, f.FieldType, () => f.GetValue(null)));
        foreach (var p in typeof(OdsTypeRegistries).GetProperties(BindingFlags.Public | BindingFlags.Static))
            if (p.GetIndexParameters().Length == 0)
                members.Add((p.Name, p.PropertyType, () => p.GetValue(null)));

        var registryShaped = members.Where(m => MentionsOption(m.Type)).ToList();

        // Registry-shaped but not the one shape the sweep reads: fail loudly rather than skip it.
        var unrecognised = registryShaped
            .Where(m => m.Type != typeof(IReadOnlyList<OdsTypeOption>))
            .Select(m => $"{m.Name} : {m.Type}")
            .ToList();
        Assert.True(unrecognised.Count == 0,
            "OdsTypeRegistries exposes OdsTypeOption collections this contrast sweep does not read — expose them as "
            + "IReadOnlyList<OdsTypeOption> or extend the sweep:\n  " + string.Join("\n  ", unrecognised));

        return [.. registryShaped.Select(m => (m.Name, (IReadOnlyList<OdsTypeOption>)m.Get()!))];

        static bool MentionsOption(Type t) =>
            t == typeof(OdsTypeOption)
            || (t.IsArray && MentionsOption(t.GetElementType()!))
            || (t.IsGenericType && t.GetGenericArguments().Any(MentionsOption));
    }

    public static TheoryData<string, string> EveryMember()
    {
        var data = new TheoryData<string, string>();
        foreach (var (name, items) in Registries)
            foreach (var item in items)
                data.Add(name, item.Key);
        return data;
    }

    /// <summary>
    /// The sweep is by reflection over every public static field and property of
    /// <see cref="OdsTypeRegistries"/>, and fails loudly on an OdsTypeOption collection of any other
    /// shape. The count is pinned EXACTLY (<see cref="RegistryCount"/>), so this also proves the sweep
    /// found the family rather than passing over nothing.
    /// </summary>
    [Fact]
    public void The_sweep_covers_exactly_the_fifteen_registries()
    {
        Assert.Equal(RegistryCount, Registries.Count);
        Assert.All(Registries, r => Assert.NotEmpty(r.Items));
    }

    [Theory]
    [MemberData(nameof(EveryMember))]
    public void Every_colour_is_authored_in_the_design_systems_band(string registry, string key)
    {
        var (l, c, _) = ColourOf(registry, key);

        Assert.InRange(l, BandMinLightness, BandMaxLightness);
        Assert.True(c <= BandMaxChroma,
            $"{registry}[{key}] has chroma {c.ToString("0.000", CultureInfo.InvariantCulture)}, outside the C ≤ {BandMaxChroma} band the caps were measured over.");
    }

    /// <summary>
    /// Lowering L at constant chroma takes many capped colours OUT of sRGB, so how the out-of-gamut
    /// colour is brought back decides what is measured. Every foreground ratio here is therefore
    /// asserted under BOTH renderings (<see cref="Renderings"/>): per-channel clipping, and CSS Color 4
    /// gamut mapping (chroma reduced at constant L and h). This pins the mapper those assertions lean on.
    /// </summary>
    [Fact]
    public void The_gamut_mapper_leaves_in_gamut_colours_alone_and_brings_the_rest_inside()
    {
        var inGamut = Srgb(0.5, 0.05, 250);
        Assert.Equal(inGamut, GamutMapped(0.5, 0.05, 250));

        Assert.False(InGamut(0.58, 0.16, 150));
        var (r, g, b) = GamutMapped(0.58, 0.16, 150);
        Assert.All(new[] { r, g, b }, v => Assert.InRange(v, 0.0, 1.0));
    }

    public static TheoryData<string, bool, string?, bool> CssForegrounds() => new()
    {
        // selector · painted as text? · the rule whose background it sits on (null: none) · that ground over the page background (else the surface)?
        { ".trm-scheduled", true, null, false },
        { ".est-scheduled", true, null, false },
        { ".trm-inforce", true, ".trm-inforce", false },
        { ".trm-delta.up", true, ".trm-delta.up", false },
        { ".trm-delta.down", true, ".trm-delta.down", false },
        { ".odc-recordsection-notice.warning .material-icons", false, ".odc-recordsection-notice.warning", true },
    };

    /// <summary>
    /// The authored-colour foregrounds CSS paints outside the registries — the "Scheduled" / "In force"
    /// labels, the delta pills and the warning notice glyph — measured on light after the cap they
    /// declare, against the ground they actually sit on.
    /// </summary>
    [Theory]
    [MemberData(nameof(CssForegrounds))]
    public void Every_capped_css_foreground_clears_its_minimum_on_light(string selector, bool isText, string? groundRule, bool overPageBackground)
    {
        var css = ComponentsCss();
        var value = Declaration(RuleBody(css, selector), "color");
        var wrap = Regex.Match(value,
            @"^oklch\(from\s+(?<src>oklch\([^)]*\)|var\(--[a-z-]+\))\s+var\((?<token>--glyph(?:-text)?-l), l\)\s+c\s+h\)$");
        Assert.True(wrap.Success, $"'{selector}' color '{value}' is not a capped relative colour.");
        Assert.Equal(isText ? "--glyph-text-l" : "--glyph-l", wrap.Groups["token"].Value);

        var source = wrap.Groups["src"].Value;
        if (source.StartsWith("var(", StringComparison.Ordinal))
            source = Declaration(css, source[4..^1]);
        var (l, c, h, _) = Parse(source);

        var ground = overPageBackground ? Background(dark: false) : Surface(dark: false);
        if (groundRule is not null)
        {
            var (tl, tc, th, ta) = Parse(Declaration(RuleBody(css, groundRule), "background"));
            ground = Composite(Srgb(tl, tc, th), ta ?? 1.0, ground);
        }

        foreach (var (how, fg) in Renderings(Math.Min(l, LightCap(wrap.Groups["token"].Value)), c, h))
            AssertRatio("css", selector, $"{(isText ? "text" : "glyph")} (light, capped, {how})",
                fg, ground, isText ? TextContrastMinimum : NonTextContrastMinimum);
    }

    [Theory]
    [MemberData(nameof(EveryMember))]
    public void Every_glyph_clears_three_to_one_on_the_dark_surface(string registry, string key)
    {
        var (l, c, h) = ColourOf(registry, key);

        foreach (var (how, glyph) in Renderings(l, c, h))
            AssertRatio(registry, key, $"glyph (dark, authored, {how})", glyph, Surface(dark: true), NonTextContrastMinimum);
    }

    [Theory]
    [MemberData(nameof(EveryMember))]
    public void Every_glyph_clears_three_to_one_on_light_through_the_glyph_cap(string registry, string key)
    {
        var (l, c, h) = ColourOf(registry, key);
        var surface = Surface(dark: false);
        var tint = TintOver(registry, key, surface);

        foreach (var (how, glyph) in Renderings(Math.Min(l, LightCap("--glyph-l")), c, h))
        {
            AssertRatio(registry, key, $"glyph (light, --glyph-l, {how})", glyph, surface, NonTextContrastMinimum);
            AssertRatio(registry, key, $"glyph (light, --glyph-l, {how}) on its soft tint", glyph, tint, NonTextContrastMinimum);
        }
    }

    [Theory]
    [MemberData(nameof(EveryMember))]
    public void Every_colour_used_as_text_clears_four_and_a_half_to_one_on_light_through_the_text_cap(string registry, string key)
    {
        var (l, c, h) = ColourOf(registry, key);
        var surface = Surface(dark: false);
        var tint = TintOver(registry, key, surface);

        foreach (var (how, text) in Renderings(Math.Min(l, LightCap("--glyph-text-l")), c, h))
        {
            AssertRatio(registry, key, $"text (light, --glyph-text-l, {how})", text, surface, TextContrastMinimum);
            AssertRatio(registry, key, $"text (light, --glyph-text-l, {how}) on its soft tint", text, tint, TextContrastMinimum);
        }
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

            foreach (var (how, dark) in Renderings(l, c, h))
                AssertRatio("AccountTypeVisuals", name, $"glyph (dark, authored, {how})", dark, Surface(dark: true), NonTextContrastMinimum);
            foreach (var (how, light) in Renderings(Math.Min(l, LightCap("--glyph-l")), c, h))
                AssertRatio("AccountTypeVisuals", name, $"glyph (light, --glyph-l, {how})", light, Surface(dark: false), NonTextContrastMinimum);
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

    /// <summary>The member's soft tint composited over <paramref name="surface"/>.</summary>
    private static (double R, double G, double B) TintOver(string registry, string key, (double R, double G, double B) surface)
    {
        var (l, c, h, alpha) = Parse(Option(registry, key).Soft);
        return Composite(Srgb(l, c, h), alpha ?? 1.0, surface);
    }

    /// <summary>Source-over in gamma-encoded sRGB, which is how a browser blends.</summary>
    private static (double R, double G, double B) Composite(
        (double R, double G, double B) top, double a, (double R, double G, double B) under) =>
        (top.R * a + under.R * (1 - a), top.G * a + under.G * (1 - a), top.B * a + under.B * (1 - a));

    private static (double R, double G, double B) Background(bool dark)
    {
        var bg = dark ? OdysseyTheme.Theme.PaletteDark.Background : OdysseyTheme.Theme.PaletteLight.Background;
        return (bg.R / 255.0, bg.G / 255.0, bg.B / 255.0);
    }

    private static string ComponentsCss() =>
        File.ReadAllText(Path.Combine(ClientSource.Root, "wwwroot", "css", "odyssey-components.css"));

    private static string RuleBody(string css, string selector)
    {
        var rule = Regex.Match(css, @"(?m)^" + Regex.Escape(selector) + @"\s*\{(?<body>[^}]*)\}");
        Assert.True(rule.Success, $"No '{selector}' rule in odyssey-components.css.");
        return rule.Groups["body"].Value;
    }

    private static string Declaration(string body, string property)
    {
        var d = Regex.Match(body, @"(?<![-\w])" + Regex.Escape(property) + @":\s*(?<v>[^;]+);");
        Assert.True(d.Success, $"No '{property}' declaration found.");
        return d.Groups["v"].Value.Trim();
    }

    private static bool InGamut(double l, double c, double hDegrees)
    {
        const double Tolerance = 1e-4;
        var (r, g, b) = LinearSrgb(l, c, hDegrees);
        return new[] { r, g, b }.All(v => v >= -Tolerance && v <= 1 + Tolerance);
    }

    /// <summary>
    /// The two ways an out-of-gamut <c>oklch</c> colour can reach an sRGB screen, both measured, since
    /// neither is reliably the conservative one: per-channel clipping (what engines commonly do when
    /// drawing to an sRGB display) and CSS Color 4 gamut mapping (chroma reduced at constant L and h).
    /// For an in-gamut colour the two are identical.
    /// </summary>
    private static IEnumerable<(string How, (double R, double G, double B) Rgb)> Renderings(double l, double c, double h)
    {
        yield return ("clipped", Srgb(l, c, h));
        yield return ("gamut-mapped", GamutMapped(l, c, h));
    }

    /// <summary>
    /// <c>oklch(L C H)</c> → gamma-encoded sRGB in [0, 1], an out-of-gamut channel clipped per channel.
    /// This is one rendering, not a conservative bound — see <see cref="Renderings"/>.
    /// </summary>
    private static (double R, double G, double B) Srgb(double l, double c, double hDegrees)
    {
        var (r, g, b) = LinearSrgb(l, c, hDegrees);
        return (Encode(r), Encode(g), Encode(b));
    }

    private static double Encode(double linear)
    {
        var clamped = Math.Clamp(linear, 0.0, 1.0);
        return clamped <= 0.0031308 ? 12.92 * clamped : (1.055 * Math.Pow(clamped, 1.0 / 2.4)) - 0.055;
    }

    /// <summary>
    /// CSS Color 4 §13.2 "binary search gamut mapping with local MINDE": reduce chroma at constant L
    /// and h until the clipped candidate is within the just-noticeable ΔE_OK of 0.02 of it.
    /// </summary>
    private static (double R, double G, double B) GamutMapped(double l, double c, double h)
    {
        const double Jnd = 0.02, Epsilon = 0.0001;
        if (l >= 1.0) return (1.0, 1.0, 1.0);
        if (l <= 0.0) return (0.0, 0.0, 0.0);
        if (InGamut(l, c, h)) return Srgb(l, c, h);

        var clipped = Clip(l, c, h);
        if (DeltaEOk(clipped, l, c, h) < Jnd) return Encoded(clipped);

        double lo = 0.0, hi = c;
        var minInGamut = true;
        while (hi - lo > Epsilon)
        {
            var mid = (lo + hi) / 2;
            if (minInGamut && InGamut(l, mid, h))
            {
                lo = mid;
                continue;
            }

            clipped = Clip(l, mid, h);
            var e = DeltaEOk(clipped, l, mid, h);
            if (e < Jnd)
            {
                if (Jnd - e < Epsilon) return Encoded(clipped);
                minInGamut = false;
                lo = mid;
            }
            else
            {
                hi = mid;
            }
        }

        return Encoded(Clip(l, lo, h));

        static (double R, double G, double B) Clip(double l, double c, double h)
        {
            var (r, g, b) = LinearSrgb(l, c, h);
            return (Math.Clamp(r, 0, 1), Math.Clamp(g, 0, 1), Math.Clamp(b, 0, 1));
        }

        static (double R, double G, double B) Encoded((double R, double G, double B) lin) =>
            (Encode(lin.R), Encode(lin.G), Encode(lin.B));

        static double DeltaEOk((double R, double G, double B) lin, double l, double c, double h)
        {
            var (l2, a2, b2) = OkLab(lin);
            var hr = h * Math.PI / 180.0;
            var (a1, b1) = (c * Math.Cos(hr), c * Math.Sin(hr));
            return Math.Sqrt(Math.Pow(l - l2, 2) + Math.Pow(a1 - a2, 2) + Math.Pow(b1 - b2, 2));
        }
    }

    /// <summary>Linear sRGB → OKLab.</summary>
    private static (double L, double A, double B) OkLab((double R, double G, double B) c)
    {
        var l = Math.Cbrt(0.4122214708 * c.R + 0.5363325363 * c.G + 0.0514459929 * c.B);
        var m = Math.Cbrt(0.2119034982 * c.R + 0.6806995451 * c.G + 0.1073969566 * c.B);
        var s = Math.Cbrt(0.0883024619 * c.R + 0.2817188376 * c.G + 0.6299787005 * c.B);
        return (
            0.2104542553 * l + 0.7936177850 * m - 0.0040720468 * s,
            1.9779984951 * l - 2.4285922050 * m + 0.4505937099 * s,
            0.0259040371 * l + 0.7827717662 * m - 0.8086757660 * s);
    }

    /// <summary><c>oklch(L C H)</c> → linear sRGB through OKLab, unclamped.</summary>
    private static (double R, double G, double B) LinearSrgb(double l, double c, double hDegrees)
    {
        var h = hDegrees * Math.PI / 180.0;
        var a = c * Math.Cos(h);
        var b = c * Math.Sin(h);

        var lCube = Math.Pow(l + 0.3963377774 * a + 0.2158037573 * b, 3);
        var mCube = Math.Pow(l - 0.1055613458 * a - 0.0638541728 * b, 3);
        var sCube = Math.Pow(l - 0.0894841775 * a - 1.2914855480 * b, 3);

        return (
            4.0767416621 * lCube - 3.3077115913 * mCube + 0.2309699292 * sCube,
            -1.2684380046 * lCube + 2.6097574011 * mCube - 0.3413193965 * sCube,
            -0.0041960863 * lCube - 0.7034186147 * mCube + 1.7076147010 * sCube);
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
