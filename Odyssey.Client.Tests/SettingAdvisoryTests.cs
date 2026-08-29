using System.Globalization;
using System.Text.RegularExpressions;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// The accessibility contract of the non-blocking advisory (issue #434 §3), and the contrast of the two
/// colours that decided its treatment.
///
/// <para>
/// The treatment is <c>.odc-setting-advisory</c>, structured like <c>.alert.warning</c> so it reads as
/// the same family, but with two deliberate departures — each one a contrast failure in the thing it
/// departs from. <c>.alert.warning</c> tints the whole container and only <c>.alert-body</c> returns to
/// text-primary, so the word "Advisory" outside the body would inherit the amber accent and miss 4.5:1;
/// and <c>.ss-grpnote</c>'s icon carries <c>opacity: 0.8</c>, which lands its glyph under 3:1. Both are
/// asserted here rather than left as prose, because a later colour tweak should fail a test rather than
/// ship.
/// </para>
/// </summary>
public class SettingAdvisoryTests
{
    private static string ComponentMarkup() =>
        File.ReadAllText(Path.Combine(ClientSource.Root, "Components", "OdsSettingRow.razor"));

    /// <summary>
    /// The rendered markup only, with the <c>@code</c> block and the Razor comments stripped. The
    /// parameter documentation below <c>@code</c> legitimately DISCUSSES <c>aria-invalid</c> and
    /// <c>role="alert"</c> — a test a doc comment can fail is a test that gets its docs deleted.
    /// </summary>
    private static string RenderedMarkup()
    {
        var markup = ComponentMarkup();
        var code = markup.IndexOf("@code {", StringComparison.Ordinal);
        var body = code < 0 ? markup : markup[..code];
        return Regex.Replace(body, @"@\*.*?\*@", string.Empty, RegexOptions.Singleline);
    }

    private static string ComponentCss() =>
        File.ReadAllText(Path.Combine(ClientSource.Root, "Components", "OdsSettingRow.razor.css"));

    /// <summary>
    /// The same stylesheet with <c>/* … */</c> comments stripped — the note explaining WHY the icon is at
    /// full opacity necessarily contains the word "opacity".
    /// </summary>
    private static string DeclaredCss() =>
        Regex.Replace(ComponentCss(), @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);

    // ── Structure and semantics ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// The advisory renders in its OWN slot, after both <c>ChildContent</c> and <c>Footer</c> and
    /// independent of either. That is the point: the two existing slots are strictly either/or, and the
    /// one row the correspondence heuristic targets is a <c>Text</c> row that already occupies
    /// <c>Footer</c>.
    /// </summary>
    [Fact]
    public void The_advisory_is_a_third_independent_slot()
    {
        var markup = ComponentMarkup();

        Assert.Contains("[Parameter] public RenderFragment? Advisory", markup, StringComparison.Ordinal);
        Assert.Contains("odc-setting-advisory", markup, StringComparison.Ordinal);

        // Rendered AFTER the footer block, so an advisory and a footer control coexist on one row.
        var footerAt = markup.IndexOf("odc-setting-footer", StringComparison.Ordinal);
        var advisoryAt = markup.IndexOf("odc-setting-advisory", StringComparison.Ordinal);
        Assert.True(footerAt >= 0 && advisoryAt > footerAt,
            "The advisory block must render after the footer block, not instead of it.");
    }

    /// <summary>
    /// Meaning as TEXT, never colour or icon alone (WCAG 1.1.1 / 1.3.3 / 1.4.1): the literal word
    /// "Advisory" precedes the message and the icon is <c>aria-hidden</c>. This is also what makes it
    /// distinguishable from a field error by more than hue — a different icon and a different prefix.
    /// </summary>
    [Fact]
    public void The_advisory_states_its_meaning_in_text_with_a_hidden_icon()
    {
        var markup = RenderedMarkup();
        var block = markup[markup.IndexOf("odc-setting-advisory", StringComparison.Ordinal)..];

        Assert.Contains("<strong>Advisory</strong>", block, StringComparison.Ordinal);
        Assert.Contains("aria-hidden=\"true\"", block, StringComparison.Ordinal);

        // A different glyph from the error path's error_outline.
        Assert.Contains("info_outline", block, StringComparison.Ordinal);
    }

    /// <summary>
    /// An advisory is not a validation error. It must never set <c>aria-invalid</c>, and it must not be a
    /// live region of its own — a live region inserted into the DOM at the same time as its content is
    /// frequently not announced at all, and <c>role="alert"</c> stays reserved for the field-error path
    /// because an advisory must not interrupt.
    /// </summary>
    [Fact]
    public void The_advisory_is_neither_invalid_nor_a_live_region()
    {
        var markup = RenderedMarkup();
        var block = markup[markup.IndexOf("odc-setting-advisory", StringComparison.Ordinal)..];

        Assert.DoesNotContain("aria-invalid", block, StringComparison.Ordinal);
        Assert.DoesNotContain("role=\"status\"", block, StringComparison.Ordinal);
        Assert.DoesNotContain("role=\"alert\"", block, StringComparison.Ordinal);
        Assert.DoesNotContain("aria-live", block, StringComparison.Ordinal);
    }

    /// <summary>
    /// Programmatic association: the advisory's id is appended to the control's
    /// <c>aria-describedby</c>, so a screen-reader user reaching the field hears the advisory as part of
    /// the field's description rather than only if they navigate past it. And the page announces through
    /// the announcer it already hosts.
    /// </summary>
    [Fact]
    public void The_advisory_is_referenced_from_the_controls_describedby()
    {
        var page = File.ReadAllText(Path.Combine(ClientSource.Root, "Pages", "Settings.razor"));
        var codeBehind = File.ReadAllText(Path.Combine(ClientSource.Root, "Pages", "Settings.razor.cs"));

        Assert.Contains("AdvisoryId=\"@AdvisoryId(item.Key)\"", page, StringComparison.Ordinal);
        Assert.Contains("AriaDescribedBy=\"@DescribedBy(item)\"", page, StringComparison.Ordinal);
        Assert.DoesNotContain("AriaDescribedBy=\"@($\"ss-desc-{item.Key}\")\"", page, StringComparison.Ordinal);

        // DescribedBy appends the advisory id when one is present — asserted on the branch rather
        // than on a single interpolated literal, since the line now also carries the field's error
        // id (inside an OdsSettingField frame the control's own error node is hidden by the sheet,
        // so that reference is the only route from the control to the message).
        var describedBy = Section(codeBehind, "private string DescribedBy(SettingItem item)");
        Assert.Contains("AdvisoryText(item) is not null", describedBy, StringComparison.Ordinal);
        Assert.Contains("parts.Add(AdvisoryId(item.Key));", describedBy, StringComparison.Ordinal);
        Assert.Contains("DescId(item.Key)", describedBy, StringComparison.Ordinal);

        // Announced through the existing OdsLiveAnnouncer, not a freshly-inserted live region.
        Assert.Contains("<OdsLiveAnnouncer", page, StringComparison.Ordinal);
        Assert.Contains("AdvisorySuffixed(", codeBehind, StringComparison.Ordinal);
    }

    /// <summary>
    /// An advisory must not participate in validation state: it cannot appear in <c>ErrorFor</c>, count
    /// toward the blocking summary, or reach <c>HasErrors</c> — which gates the Save button.
    /// </summary>
    [Fact]
    public void The_advisory_does_not_block_saving()
    {
        var codeBehind = File.ReadAllText(Path.Combine(ClientSource.Root, "Pages", "Settings.razor.cs"));

        var errorFor = Section(codeBehind, "private string? ErrorFor(SettingItem item)");
        Assert.DoesNotContain("Advisory", errorFor, StringComparison.Ordinal);

        var hasErrors = Section(codeBehind, "private bool HasErrors =>");
        Assert.DoesNotContain("Advisory", hasErrors, StringComparison.Ordinal);
    }

    // ── Contrast ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Advisory text meets 4.5:1 and its icon 3:1 against the effective row background — the amber tint
    /// composited over the card surface — in BOTH the dark default and the light theme.
    ///
    /// <para>
    /// The colours are <strong>resolved from the real token chain</strong>, not restated here:
    /// <c>--warning-text</c> → <c>--amber-800</c> → a hex, read out of <c>app.css</c>, in the theme block
    /// that actually applies. So this fails if the advisory's declared token changes, if that token's
    /// definition changes, or if the amber ramp underneath it moves — whereas hardcoding the endpoint hex
    /// would only have caught the first.
    /// </para>
    /// </summary>
    [Theory]
    // Text needs 4.5:1; the icon is a non-text graphic, so 3:1 (WCAG 1.4.11).
    [InlineData("--warning-text", false, 4.5)]
    [InlineData("--pending-text", false, 3.0)]
    [InlineData("--warning-text", true, 4.5)]
    [InlineData("--pending-text", true, 3.0)]
    public void Advisory_colours_meet_their_contrast_minimum(string token, bool dark, double minimum)
    {
        var foreground = ResolveToken(token, dark);
        var background = EffectiveRowBackground(dark);
        var ratio = ContrastRatio(foreground, background);

        Assert.True(ratio >= minimum,
            $"{token} ({(dark ? "dark" : "light")}) resolves to {Describe(foreground)} on "
            + $"{Describe(background)} = {ratio:0.00}:1, below the required {minimum:0.0}:1.");
    }

    /// <summary>
    /// The tokens the theory above resolves are the ones the component stylesheet actually references.
    /// Without this the contrast maths could stay green while the CSS drifted to something else entirely.
    /// </summary>
    [Fact]
    public void The_checked_tokens_are_the_ones_the_stylesheet_references()
    {
        var css = DeclaredCss();
        var block = css[css.IndexOf(".odc-setting-advisory", StringComparison.Ordinal)..];

        Assert.Contains("color: var(--pending-text)", block, StringComparison.Ordinal);
        Assert.Contains("color: var(--warning-text)", block, StringComparison.Ordinal);
        Assert.Contains("background: var(--finance-pending-soft)", block, StringComparison.Ordinal);
    }

    /// <summary>
    /// Reusing the established tokens means the rule needs NO dark override of its own — the tokens
    /// theme themselves. That is the substantive win over hand-picked hexes, so it is asserted rather
    /// than left as a comment: a reintroduced <c>[data-theme]</c> block here is a signal that somebody
    /// has gone back to bespoke colours.
    /// </summary>
    [Fact]
    public void The_advisory_needs_no_theme_override_of_its_own()
    {
        Assert.DoesNotContain("data-theme", DeclaredCss(), StringComparison.Ordinal);
    }

    /// <summary>
    /// The two specific traps that decided the treatment: the icon must be at FULL opacity (the
    /// <c>.ss-grpnote</c> failure) and the <c>0.38</c> disabled alpha must not appear at all.
    /// </summary>
    [Fact]
    public void The_advisory_avoids_the_two_opacity_traps()
    {
        var css = DeclaredCss();
        var block = css[css.IndexOf(".odc-setting-advisory", StringComparison.Ordinal)..];

        Assert.DoesNotContain("opacity", block, StringComparison.Ordinal);
        Assert.DoesNotContain("0.38", block, StringComparison.Ordinal);
    }

    /// <summary>
    /// The advisory has its OWN spacing rather than inheriting <c>.odc-setting-footer</c>'s, which is
    /// still missing top spacing and a divider — the open #425 gap. It also grows downward only, so an
    /// advisory appearing after a save cannot move the focused control.
    /// </summary>
    [Fact]
    public void The_advisory_declares_its_own_spacing()
    {
        var css = DeclaredCss();
        var block = css[css.IndexOf(".odc-setting-advisory {", StringComparison.Ordinal)..];
        var rule = block[..block.IndexOf('}')];

        Assert.Contains("margin:", rule, StringComparison.Ordinal);
        Assert.DoesNotContain("position: absolute", rule, StringComparison.Ordinal);
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────────

    private static string Section(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Could not find '{signature}' — the test is stale, not the code.");
        var end = source.IndexOf("\n    private", start + signature.Length, StringComparison.Ordinal);
        return end < 0 ? source[start..] : source[start..end];
    }

    private static (int R, int G, int B) Hex(string value)
    {
        var hex = value.TrimStart('#');
        return (
            int.Parse(hex[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            int.Parse(hex[2..4], NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            int.Parse(hex[4..6], NumberStyles.HexNumber, CultureInfo.InvariantCulture));
    }

    private static string AppCss() =>
        File.ReadAllText(Path.Combine(ClientSource.Root, "wwwroot", "css", "app.css"));

    /// <summary>
    /// The theme block that applies for <paramref name="dark"/>. MudBlazor stamps
    /// <c>data-theme="dark"</c> in dark mode and removes the attribute in light mode — it never writes
    /// <c>data-theme="light"</c> — so the client's convention is <c>:root</c> for light defaults and
    /// <c>[data-theme='dark']</c> for the dark overrides.
    /// </summary>
    private static string ThemeBlock(bool dark)
    {
        var css = AppCss();

        // Every dark override, concatenated: app.css has several [data-theme='dark'] blocks and the
        // tokens under test are not all in the same one.
        const string darkSelector = @"\[data-theme='dark'\]\s*\{[^}]*\}";

        if (dark)
        {
            return string.Join("\n", Regex.Matches(css, darkSelector, RegexOptions.Singleline)
                .Select(match => match.Value));
        }

        // For light, the dark blocks are REMOVED rather than the light ones selected. Both matter:
        // several of these tokens are declared in more than one light block, so last-declaration-wins
        // has to be evaluated over the whole light cascade — but leaving the dark blocks in would let a
        // later dark override win the lookup, which is what the first version of this helper did.
        return Regex.Replace(css, darkSelector, string.Empty, RegexOptions.Singleline);
    }

    /// <summary>
    /// Resolves a CSS custom property to a concrete colour, following <c>var(--x)</c> indirection — so
    /// <c>--warning-text</c> → <c>--amber-800</c> → <c>#92400E</c>. Dark lookups fall back to the light
    /// definition when the dark blocks do not override the token, which mirrors the cascade.
    /// </summary>
    private static (int R, int G, int B) ResolveToken(string token, bool dark)
    {
        // Dark lookups fall back to the light definition when the dark blocks do not override a token.
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

    /// <summary>The LAST declaration of a custom property, matching how the cascade resolves duplicates.</summary>
    private static string? Declaration(string css, string token)
    {
        var matches = Regex.Matches(css, Regex.Escape(token) + @"\s*:\s*(?<value>[^;]+);")
            .Select(match => match.Groups["value"].Value.Trim())
            .ToList();

        return matches.Count == 0 ? null : matches[^1];
    }

    /// <summary>
    /// The amber tint composited over the card surface — what the advisory's text actually sits on. Both
    /// halves come from source: the surface from the MudTheme palette, the tint alpha from
    /// <c>--finance-pending-soft</c>.
    /// </summary>
    private static (int R, int G, int B) EffectiveRowBackground(bool dark)
    {
        var theme = File.ReadAllText(Path.Combine(ClientSource.Root, "Theme", "OdysseyTheme.cs"));

        // Surface is declared once per palette; the dark palette is the first in the file.
        var surfaces = Regex.Matches(theme, @"Surface\s*=\s*""(?<hex>#[0-9A-Fa-f]{6})""")
            .Select(match => match.Groups["hex"].Value)
            .ToList();
        Assert.True(surfaces.Count >= 2, "Expected a Surface for each of the two palettes.");

        var tint = Declaration(ThemeBlock(dark), "--finance-pending-soft")
            ?? Declaration(ThemeBlock(dark: false), "--finance-pending-soft");
        Assert.NotNull(tint);

        var rgba = Regex.Match(tint!, @"rgba\(\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)\s*,\s*([\d.]+)\s*\)");
        Assert.True(rgba.Success, $"--finance-pending-soft is not an rgba() value: '{tint}'.");

        var over = (
            int.Parse(rgba.Groups[1].Value, CultureInfo.InvariantCulture),
            int.Parse(rgba.Groups[2].Value, CultureInfo.InvariantCulture),
            int.Parse(rgba.Groups[3].Value, CultureInfo.InvariantCulture));
        var alpha = double.Parse(rgba.Groups[4].Value, CultureInfo.InvariantCulture);

        return Composite(over, alpha, Hex(dark ? surfaces[0] : surfaces[1]));
    }

    private static string Describe((int R, int G, int B) rgb) => $"rgb({rgb.R},{rgb.G},{rgb.B})";

    /// <summary>Alpha-composites <paramref name="over"/> onto <paramref name="under"/>.</summary>
    private static (int R, int G, int B) Composite((int R, int G, int B) over, double alpha, (int R, int G, int B) under) =>
        ((int)Math.Round(alpha * over.R + (1 - alpha) * under.R),
         (int)Math.Round(alpha * over.G + (1 - alpha) * under.G),
         (int)Math.Round(alpha * over.B + (1 - alpha) * under.B));

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

        return 0.2126 * Channel(rgb.R) + 0.7152 * Channel(rgb.G) + 0.0722 * Channel(rgb.B);
    }
}
