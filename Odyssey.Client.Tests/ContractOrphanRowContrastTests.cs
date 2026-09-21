using System.Globalization;
using System.Text.RegularExpressions;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// The contrast contract of the type-change refusal's orphaned-party rows (issue #157): the role label
/// in <c>.con-orphan-role</c> is <b>11px text</b>, so it needs 4.5:1 (WCAG 2.2 <b>1.4.3</b>, Level AA),
/// not the 3:1 non-text-graphics floor.
/// </summary>
/// <remarks>
/// This exists because the first cut of that rule used <c>--mud-palette-warning</c>, which is the
/// <em>graphics</em> token: on light it resolves to <c>#B57820</c>, and the theme's own comment says it
/// was darkened from the design system's amber-600 precisely because "MudBlazor also uses Warning as
/// <em>text</em> — where amber-600 (~3.4:1) fails AA". <c>app.css</c> then carries a second token,
/// <c>--warning-text</c>, for exactly this case. Every other warning-toned label in
/// <c>odyssey-components.css</c> uses it; the raw token is reserved for glyphs, borders and tints.
///
/// <para>
/// The colours are <b>resolved from the real token chain</b> rather than restated here —
/// <c>--warning-text</c> → <c>--amber-800</c> → a hex, out of <c>app.css</c>, in the theme block that
/// actually applies, and the surface out of the MudTheme palette. So this fails if the rule's declared
/// token changes, if that token's definition changes, or if the amber ramp underneath it moves.
/// Hardcoding the endpoint hex would only have caught the first.
/// </para>
/// </remarks>
public class ContractOrphanRowContrastTests
{
    /// <summary>The role label and the party id both sit on the row's own surface, and both are text.</summary>
    [Theory]
    [InlineData("--warning-text", false)]
    [InlineData("--warning-text", true)]
    [InlineData("--mud-palette-text-secondary", false)]
    [InlineData("--mud-palette-text-secondary", true)]
    public void Orphan_row_text_meets_the_AA_minimum(string token, bool dark)
    {
        var foreground = ResolveToken(token, dark);
        var background = RowSurface(dark);
        var ratio = ContrastRatio(foreground, background);

        Assert.True(ratio >= 4.5,
            $"{token} ({(dark ? "dark" : "light")}) resolves to {Describe(foreground)} on "
            + $"{Describe(background)} = {ratio:0.00}:1, below the required 4.5:1.");
    }

    /// <summary>
    /// The tokens the theory resolves are the ones the stylesheet actually references. Without this the
    /// contrast maths could stay green while the rule drifted back to the graphics token — which is the
    /// exact regression this file was added for.
    /// </summary>
    [Fact]
    public void The_checked_tokens_are_the_ones_the_stylesheet_references()
    {
        var rule = Rule(".con-orphan-role");

        Assert.Contains("color: var(--warning-text)", rule, StringComparison.Ordinal);
        Assert.DoesNotContain("var(--mud-palette-warning)", rule, StringComparison.Ordinal);

        Assert.Contains("color: var(--mud-palette-text-secondary)", Rule(".con-orphan-id"), StringComparison.Ordinal);
        Assert.Contains("background: var(--mud-palette-surface)", Rule(".con-orphan "), StringComparison.Ordinal);
    }

    /// <summary>
    /// The raw warning token stays available for the row's non-text parts, so this is a token-choice
    /// rule rather than a ban: a border or a glyph may still use it at the 3:1 graphics floor.
    /// </summary>
    [Fact]
    public void The_graphics_token_is_still_the_one_used_for_glyphs_elsewhere()
    {
        var css = ComponentCss();

        Assert.Contains(".material-icons { color: var(--mud-palette-warning); }", css, StringComparison.Ordinal);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static string ComponentCss() =>
        File.ReadAllText(Path.Combine(ClientSource.Root, "wwwroot", "css", "odyssey-components.css"));

    private static string AppCss() =>
        File.ReadAllText(Path.Combine(ClientSource.Root, "wwwroot", "css", "app.css"));

    /// <summary>One declaration block, comments stripped so a rationale note cannot satisfy an assertion.</summary>
    private static string Rule(string selector)
    {
        var css = Regex.Replace(ComponentCss(), @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
        var start = css.IndexOf(selector + "{", StringComparison.Ordinal);
        if (start < 0)
        {
            start = css.IndexOf(selector, StringComparison.Ordinal);
        }

        Assert.True(start >= 0, $"No rule for '{selector}' in odyssey-components.css.");

        var end = css.IndexOf('}', start);
        Assert.True(end > start, $"Unterminated rule for '{selector}'.");
        return css[start..end];
    }

    /// <summary>
    /// Resolves a CSS custom property to a concrete colour, following <c>var(--x)</c> indirection, and
    /// falling back to the light definition when the dark blocks do not override it — which mirrors the
    /// cascade. A MudBlazor palette token is resolved out of the theme instead, since that is where it
    /// is declared.
    /// </summary>
    private static (int R, int G, int B) ResolveToken(string token, bool dark)
    {
        if (token.StartsWith("--mud-palette-", StringComparison.Ordinal))
        {
            return PaletteToken(token["--mud-palette-".Length..], dark);
        }

        var scoped = ThemeBlock(dark);

        for (var hop = 0; hop < 5; hop++)
        {
            var value = Declaration(scoped, token) ?? Declaration(ThemeBlock(dark: false), token);
            Assert.NotNull(value);

            if (value!.StartsWith('#'))
            {
                return Hex(value);
            }

            var indirect = Regex.Match(value, @"var\(\s*(?<name>--[a-z0-9-]+)\s*\)");
            Assert.True(indirect.Success, $"Cannot resolve '{token}' — it is '{value}'.");
            token = indirect.Groups["name"].Value;
        }

        throw new InvalidOperationException($"Token indirection for '{token}' did not terminate.");
    }

    /// <summary>
    /// A MudBlazor palette entry, read out of <c>OdysseyTheme.cs</c>. The dark palette is declared first
    /// in that file, which is the same ordering <c>SettingAdvisoryTests</c> relies on.
    /// </summary>
    private static (int R, int G, int B) PaletteToken(string name, bool dark)
    {
        var theme = File.ReadAllText(Path.Combine(ClientSource.Root, "Theme", "OdysseyTheme.cs"));
        var property = string.Concat(name.Split('-').Select(part =>
            char.ToUpperInvariant(part[0]) + part[1..]));

        var values = Regex.Matches(theme, Regex.Escape(property) + @"\s*=\s*""(?<hex>#[0-9A-Fa-f]{6})""")
            .Select(match => match.Groups["hex"].Value)
            .ToList();
        Assert.True(values.Count >= 2, $"Expected a {property} for each of the two palettes.");

        return Hex(dark ? values[0] : values[1]);
    }

    /// <summary>The orphan row's own background — an opaque surface, not a tint, so nothing composites.</summary>
    private static (int R, int G, int B) RowSurface(bool dark) => PaletteToken("surface", dark);

    /// <summary>The LAST declaration of a custom property, matching how the cascade resolves duplicates.</summary>
    private static string? Declaration(string css, string token)
    {
        var matches = Regex.Matches(css, Regex.Escape(token) + @"\s*:\s*(?<value>[^;]+);")
            .Select(match => match.Groups["value"].Value.Trim())
            .ToList();

        return matches.Count == 0 ? null : matches[^1];
    }

    private static string ThemeBlock(bool dark)
    {
        var css = AppCss();
        const string DarkSelector = @"\[data-theme='dark'\]\s*\{[^}]*\}";

        if (dark)
        {
            return string.Join("\n", Regex.Matches(css, DarkSelector, RegexOptions.Singleline)
                .Select(match => match.Value));
        }

        // For light the dark blocks are REMOVED rather than the light ones selected: several tokens are
        // declared in more than one light block, so last-declaration-wins has to be evaluated over the
        // whole light cascade, and leaving the dark blocks in would let a dark override win the lookup.
        return Regex.Replace(css, DarkSelector, string.Empty, RegexOptions.Singleline);
    }

    private static (int R, int G, int B) Hex(string value)
    {
        var hex = value.TrimStart('#');
        return (
            int.Parse(hex[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            int.Parse(hex[2..4], NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            int.Parse(hex[4..6], NumberStyles.HexNumber, CultureInfo.InvariantCulture));
    }

    private static string Describe((int R, int G, int B) rgb) => $"rgb({rgb.R},{rgb.G},{rgb.B})";

    private static double ContrastRatio((int R, int G, int B) a, (int R, int G, int B) b)
    {
        var (high, low) = (Math.Max(Luminance(a), Luminance(b)), Math.Min(Luminance(a), Luminance(b)));
        return (high + 0.05) / (low + 0.05);
    }

    /// <summary>WCAG relative luminance.</summary>
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
