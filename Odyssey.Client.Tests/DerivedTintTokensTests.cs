using System.Text.RegularExpressions;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// The derived tints in <c>app.css</c> (Odyssey Design System · colors_and_type.css §2c). Every
/// <c>*-soft</c> / <c>*-border</c> semantic tint is its base colour mixed toward transparent at the theme's
/// <c>--tint-soft</c> / <c>--tint-border</c> strength, so a tint cannot drift from its hue. These pin the
/// whole chain rather than one sample: the strengths exist in both themes, every derived tint has the one
/// permitted shape over a base that is declared, the design system's set is fully present, and no tint
/// has been reintroduced as a hand-written literal.
/// </summary>
public sealed class DerivedTintTokensTests
{
    private const string DarkSelector = @"\[data-theme='dark'\]\s*\{[^}]*\}";

    private static readonly Regex DerivedShape = new(
        @"^color-mix\(\s*in\s+srgb\s*,\s*var\(\s*(?<base>--[a-z0-9-]+)\s*\)\s+var\(\s*(?<strength>--tint-(?:soft|border))\s*\)\s*,\s*transparent\s*\)$");

    [Theory]
    [InlineData("--tint-soft", false)]
    [InlineData("--tint-soft", true)]
    [InlineData("--tint-border", false)]
    [InlineData("--tint-border", true)]
    public void Each_theme_declares_both_tint_strengths(string token, bool dark)
    {
        var value = Declaration(ThemeBlock(dark), token);
        Assert.NotNull(value);
        Assert.Matches(@"^\d{1,2}%$", value);
    }

    [Fact]
    public void Every_derived_tint_mixes_a_declared_base_at_a_tint_strength()
    {
        var derived = DerivedBlock();
        Assert.NotEmpty(derived);

        var css = AppCss();
        foreach (var (token, value) in derived)
        {
            var match = DerivedShape.Match(value);
            Assert.True(match.Success, $"{token} is not a derived tint: '{value}'.");

            var expectedStrength = token.EndsWith("-border", StringComparison.Ordinal) ? "--tint-border" : "--tint-soft";
            Assert.Equal(expectedStrength, match.Groups["strength"].Value);

            // --mud-palette-* bases are written at runtime by MudThemeProvider (OdysseyTheme.cs); every
            // other base must be declared in app.css or the tint resolves to nothing.
            var baseToken = match.Groups["base"].Value;
            if (!baseToken.StartsWith("--mud-palette-", StringComparison.Ordinal))
                Assert.NotNull(Declaration(css, baseToken));
        }
    }

    [Fact]
    public void The_design_systems_derived_tints_are_all_present()
    {
        var ds = File.ReadAllText(ClientSource.Sibling(Path.Combine("Odyssey Design System", "colors_and_type.css")));
        var dsBlock = Regex.Match(ds, @":root,\s*\[data-theme\]\s*\{(?<body>[^}]*)\}");
        Assert.True(dsBlock.Success, "The design system no longer declares a derived-tint block.");

        var declared = Regex.Matches(dsBlock.Groups["body"].Value, @"(?<token>--[a-z0-9-]+)\s*:")
            .Select(m => m.Groups["token"].Value)
            .ToList();
        Assert.NotEmpty(declared);

        var ours = DerivedBlock().Select(d => d.Token).ToHashSet();
        Assert.All(declared, token => Assert.Contains(token, ours));
    }

    [Fact]
    public void No_derived_tint_is_redeclared_as_a_literal()
    {
        // A per-theme rgba() for one of these would win over the derived value on that theme alone and
        // silently detach the tint from its base again.
        var derived = DerivedBlock().Select(d => d.Token).ToList();
        var outside = Regex.Replace(AppCss(), @":root,\s*\[data-theme\]\s*\{[^}]*\}", string.Empty);

        Assert.All(derived, token => Assert.Null(Declaration(outside, token)));
    }

    /// <summary>
    /// Amber-700 (<c>--pending-text</c>) on the derived pending tint measures ~4.4:1 on white — under
    /// WCAG 1.4.3's 4.5:1 — once the light <c>--finance-pending</c> is amber-600. Text on that tint takes
    /// <c>--warning-text</c> (amber-800, ~6.2:1). Glyphs and dots may keep <c>--finance-pending</c>.
    /// </summary>
    [Fact]
    public void Text_on_the_pending_tint_never_uses_pending_text()
    {
        var files = Directory.EnumerateFiles(ClientSource.Root, "*.css", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToList();
        Assert.NotEmpty(files);

        var offenders = new List<string>();
        foreach (var file in files)
        {
            var css = Regex.Replace(File.ReadAllText(file), @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
            foreach (Match rule in Regex.Matches(css, @"(?<selector>[^{}]+)\{(?<body>[^}]*)\}"))
            {
                var body = rule.Groups["body"].Value;
                if (Regex.IsMatch(body, @"background(?:-color)?\s*:\s*var\(--finance-pending-soft\)")
                    && Regex.IsMatch(body, @"(?<![-\w])color\s*:\s*var\(--pending-text\)"))
                    offenders.Add($"{ClientSource.Relative(file)}: {rule.Groups["selector"].Value.Trim()}");
            }
        }

        Assert.Empty(offenders);
    }

    private static List<(string Token, string Value)> DerivedBlock()
    {
        var block = Regex.Match(AppCss(), @":root,\s*\[data-theme\]\s*\{(?<body>[^}]*)\}");
        Assert.True(block.Success, "app.css has no ':root, [data-theme]' derived-tint block.");
        return Regex.Matches(block.Groups["body"].Value, @"(?<token>--[a-z0-9-]+)\s*:\s*(?<value>[^;]+);")
            .Select(m => (m.Groups["token"].Value, m.Groups["value"].Value.Trim()))
            .ToList();
    }

    private static string AppCss() => Regex.Replace(
        File.ReadAllText(Path.Combine(ClientSource.Root, "wwwroot", "css", "app.css")),
        @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);

    /// <summary>Light = everything but the dark blocks; dark = the dark blocks concatenated.</summary>
    private static string ThemeBlock(bool dark)
    {
        var css = AppCss();
        return dark
            ? string.Join("\n", Regex.Matches(css, DarkSelector, RegexOptions.Singleline).Select(m => m.Value))
            : Regex.Replace(css, DarkSelector, string.Empty, RegexOptions.Singleline);
    }

    private static string? Declaration(string css, string token)
    {
        var matches = Regex.Matches(css, Regex.Escape(token) + @"\s*:\s*(?<value>[^;]+);");
        return matches.Count == 0 ? null : matches[^1].Groups["value"].Value.Trim();
    }
}
