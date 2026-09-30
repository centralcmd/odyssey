using System.Globalization;
using System.Text.RegularExpressions;
using MudBlazor.Utilities;
using Odyssey.Client.Theme;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// Issue #254: three light-theme contrast failures on ordinary controls.
///
/// <list type="number">
/// <item>Text on a primary fill (filled buttons, the skip link, the primary badge, the multiselect
/// count, the checkbox tick, the calendar "today" markers) was white on tide-600, 2.49:1. It is now
/// ink-950 in both themes.</item>
/// <item>The resting outline of a checkbox, input or select trigger was the 0.28-alpha
/// <c>--mud-palette-lines-inputs</c> hairline, under 3:1 in both themes. It is now the
/// <c>--control-border</c> token; the hairline stays for decorative dividers.</item>
/// <item>The error call-to-action painted #2A0E0E on coral-700, 3.07:1. It is now
/// <c>--error-cta-text</c>, white on light.</item>
/// </list>
///
/// <para>
/// Like <see cref="ChartAxisContrastTests"/>, these assert the <b>ratio</b> computed from the
/// token as authored and the MudTheme surface as configured, so re-theming a surface without
/// re-checking the control that sits on it fails here.
/// </para>
/// </summary>
public class ControlContrastTests
{
    /// <summary>WCAG 2.2 SC 1.4.3 (Contrast Minimum) for normal-size text.</summary>
    private const double TextContrastMinimum = 4.5;

    /// <summary>WCAG 2.2 SC 1.4.11 (Non-text Contrast) for a user-interface component's boundary.</summary>
    private const double NonTextContrastMinimum = 3.0;

    private static readonly string AppCss =
        Path.Combine(ClientSource.Root, "wwwroot", "css", "app.css");

    private static readonly string ComponentsCss =
        Path.Combine(ClientSource.Root, "wwwroot", "css", "odyssey-components.css");

    private static readonly string DesignSystemTokens =
        ClientSource.Sibling(Path.Combine("Odyssey Design System", "colors_and_type.css"));

    public enum Theme { Light, Dark }

    // ── 1. Text on a primary fill ───────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(Theme.Light)]
    [InlineData(Theme.Dark)]
    public void Primary_contrast_text_clears_text_contrast_on_the_primary_fill(Theme theme)
    {
        var palette = Palette(theme);

        AssertRatio(Hex(palette.PrimaryContrastText), Hex(palette.Primary), TextContrastMinimum,
            $"PrimaryContrastText on Primary ({theme})");
        // A filled MudButton hovers to PrimaryDarken with the same text colour.
        AssertRatio(Hex(palette.PrimaryContrastText), Hex(palette.PrimaryDarken), TextContrastMinimum,
            $"PrimaryContrastText on PrimaryDarken ({theme})");
    }

    /// <summary>
    /// The surfaces the issue lists paint their text or tick through the palette's primary-text
    /// variable, so the MudTheme value above reaches them. A literal <c>#fff</c> here would bypass it.
    /// </summary>
    [Theory]
    [InlineData("app.css", ".ods-skip-link")]
    [InlineData("odyssey-components.css", ".odc-badge.primary")]
    [InlineData("odyssey-components.css", ".odc-ms-count")]
    [InlineData("odyssey-components.css", ".odc-check-box .material-icons")]
    [InlineData("odyssey-components.css", ".odc-cal-daynum.today")]
    [InlineData("odyssey-components.css", ".cal-tg-dnum.today")]
    public void A_primary_fill_surface_paints_its_ink_with_the_primary_text_token(string file, string selector)
    {
        var rule = Rule(file == "app.css" ? AppCss : ComponentsCss, selector);

        Assert.Matches(@"(?<![-\w])color:\s*var\(--mud-palette-primary-text\)", rule);
    }

    [Fact]
    public void The_design_system_light_primary_text_matches_the_client()
    {
        var design = Declarations(DesignSystemTokens, "--mud-palette-primary-text");

        Assert.Equal("var(--ink-950)", design[Theme.Light]);
        Assert.Equal("#080C18", Hex(Palette(Theme.Light).PrimaryContrastText));
    }

    // ── 2. Control boundaries ───────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(Theme.Light)]
    [InlineData(Theme.Dark)]
    public void The_control_border_clears_three_to_one_on_every_surface_a_control_sits_on(Theme theme)
    {
        var border = Resolve(AppCss, Declarations(AppCss, "--control-border")[theme]);
        var palette = Palette(theme);

        // Surface: the default field fill. Background: the recessed fill inside a modal.
        AssertRatio(border, Hex(palette.Surface), NonTextContrastMinimum, $"--control-border on Surface ({theme})");
        AssertRatio(border, Hex(palette.Background), NonTextContrastMinimum, $"--control-border on Background ({theme})");
    }

    /// <summary>
    /// A control's hover steps its border to <c>--mud-palette-text-secondary</c>. If the resting
    /// border were the same colour the hover affordance would silently disappear — which is why the
    /// dark theme uses ink-400 rather than ink-300.
    /// </summary>
    [Theory]
    [InlineData(Theme.Light)]
    [InlineData(Theme.Dark)]
    public void The_control_border_is_distinct_from_the_hover_border(Theme theme)
    {
        var border = Resolve(AppCss, Declarations(AppCss, "--control-border")[theme]);

        Assert.NotEqual(Hex(Palette(theme).TextSecondary), border, StringComparer.OrdinalIgnoreCase);
    }

    public static TheoryData<string, string> ControlBoundaryRules() => new()
    {
        { "odyssey-components.css", ".odc-check-box" },
        { "odyssey-components.css", ".odc-input" },
        { "odyssey-components.css", ".odc-select-trigger" },
        { "odyssey-components.css", ".odc-ms-trigger" },
        { "odyssey-components.css", ".odc-tagms-control" },
        { "odyssey-components.css", ".odc-sortsel-trigger, .odc-sortsel-dir" },
        { "odyssey-components.css", ".odc-amount" },
        { "odyssey-components.css", ".odc-money" },
        { "odyssey-components.css", ".odc-dpr" },
        { "odyssey-components.css", ".odc-rpp-trigger" },
        { "odyssey-components.css", ".odc-sfield-frame" },
        { "odyssey-components.css", ".mud-input.mud-input-outlined .mud-input-outlined-border" },
        { "app.css", ".aam-type-trigger" },
    };

    [Theory]
    [MemberData(nameof(ControlBoundaryRules))]
    public void A_control_whose_outline_is_its_only_edge_uses_the_control_border(string file, string selector)
    {
        var rule = Rule(file == "app.css" ? AppCss : ComponentsCss, selector);

        Assert.Contains("var(--control-border)", rule);
        Assert.DoesNotContain("var(--mud-palette-lines-inputs)", rule);
    }

    [Theory]
    [InlineData(Theme.Light)]
    [InlineData(Theme.Dark)]
    public void The_client_control_border_equals_the_design_systems(Theme theme)
    {
        var client = Resolve(AppCss, Declarations(AppCss, "--control-border")[theme]);
        var design = Resolve(DesignSystemTokens, Declarations(DesignSystemTokens, "--control-border")[theme]);

        Assert.Equal(design, client, StringComparer.OrdinalIgnoreCase);
    }

    // ── 3. Error call-to-action ─────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(Theme.Light)]
    [InlineData(Theme.Dark)]
    public void The_error_cta_text_clears_text_contrast_on_its_fill(Theme theme)
    {
        var text = Resolve(AppCss, Declarations(AppCss, "--error-cta-text")[theme]);
        var fill = Resolve(AppCss, Declarations(AppCss, "--finance-expense")[theme]);

        AssertRatio(text, fill, TextContrastMinimum, $"--error-cta-text on --finance-expense ({theme})");
    }

    [Fact]
    public void The_error_cta_uses_the_error_cta_text_token()
    {
        var rule = Rule(ComponentsCss, ".odc-problem.error   .odc-problem-cta");

        Assert.Contains("color: var(--error-cta-text)", rule);
    }

    // ── Parsing ─────────────────────────────────────────────────────────────────────────────────

    private static string Rule(string cssPath, string selector)
    {
        var css = WithoutComments(File.ReadAllText(cssPath));
        return Assert.Single(
            Regex.Matches(css, @"^" + Regex.Escape(selector) + @"\s*\{[^}]*\}", RegexOptions.Multiline)
                .Select(m => m.Value));
    }

    /// <summary>
    /// The value of <paramref name="token"/> per theme. A token declared once, in a block that names
    /// no theme, serves both. With two declarations, the block carrying no explicit theme is the
    /// file's default — light in the client, dark in the design system.
    /// </summary>
    private static IReadOnlyDictionary<Theme, string> Declarations(string cssPath, string token)
    {
        var css = WithoutComments(File.ReadAllText(cssPath));
        var matches = Regex.Matches(css, @"(?<![-\w])" + Regex.Escape(token) + @"\s*:\s*(?<value>[^;]+?)\s*;");

        var declarations = matches
            .Select(m => (Theme: ThemeOfBlockAt(css, m.Index), Value: m.Groups["value"].Value))
            .ToList();

        Assert.True(
            declarations.Count is 1 or 2,
            $"Expected {token} to be declared once or once per theme in {Path.GetFileName(cssPath)}, "
            + $"found {declarations.Count}.");

        if (declarations.Count == 1)
        {
            Assert.Null(declarations[0].Theme);
            return new Dictionary<Theme, string>
            {
                [Theme.Light] = declarations[0].Value,
                [Theme.Dark] = declarations[0].Value,
            };
        }

        // The design system's dark block names its theme too (`:root, [data-theme='dark']`).
        if (declarations.All(d => d.Theme is not null))
        {
            Assert.NotEqual(declarations[0].Theme, declarations[1].Theme);
            return declarations.ToDictionary(d => d.Theme!.Value, d => d.Value);
        }

        var explicitTheme = Assert.Single(declarations, d => d.Theme is not null).Theme!.Value;
        var defaultTheme = explicitTheme == Theme.Dark ? Theme.Light : Theme.Dark;

        return declarations.ToDictionary(d => d.Theme ?? defaultTheme, d => d.Value);
    }

    /// <summary>
    /// Comments are dropped before parsing: a comment above a block may itself mention
    /// <c>[data-theme='dark']</c>, which would otherwise be read as that block's selector.
    /// </summary>
    private static string WithoutComments(string css) =>
        Regex.Replace(css, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);

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

    /// <summary>
    /// Follows <c>var(--ramp-step)</c> to the ramp's hex literal (the ramps are declared once, in a
    /// mode-independent block) and returns an opaque <c>#RRGGBB</c>.
    /// </summary>
    private static string Resolve(string cssPath, string value)
    {
        var reference = Regex.Match(value, @"^var\((?<name>--[\w-]+)\)$");
        if (!reference.Success)
        {
            Assert.Matches("^#[0-9A-Fa-f]{6}$", value);
            return value.ToUpperInvariant();
        }

        var css = WithoutComments(File.ReadAllText(cssPath));
        var ramp = Regex.Match(css, @"(?<![-\w])" + Regex.Escape(reference.Groups["name"].Value)
            + @"\s*:\s*(?<hex>#[0-9A-Fa-f]{6})\s*;");
        Assert.True(ramp.Success, $"{reference.Groups["name"].Value} has no hex declaration in {Path.GetFileName(cssPath)}.");
        return ramp.Groups["hex"].Value.ToUpperInvariant();
    }

    private static MudBlazor.Palette Palette(Theme theme) =>
        theme == Theme.Dark ? OdysseyTheme.Theme.PaletteDark : OdysseyTheme.Theme.PaletteLight;

    private static string Hex(MudColor color) =>
        $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    // ── WCAG maths ──────────────────────────────────────────────────────────────────────────────

    private static void AssertRatio(string foreground, string background, double minimum, string what)
    {
        var ratio = ContrastRatio(foreground, background);
        Assert.True(
            ratio >= minimum,
            $"{what}: {foreground} on {background} is {ratio.ToString("0.00", CultureInfo.InvariantCulture)}:1, "
            + $"below the {minimum.ToString("0.0", CultureInfo.InvariantCulture)}:1 WCAG requires.");
    }

    private static double ContrastRatio(string a, string b)
    {
        var (la, lb) = (Luminance(a), Luminance(b));
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    private static double Luminance(string hex)
    {
        var channel = (int offset) => Linear(Convert.ToByte(hex.Substring(offset, 2), 16));
        return 0.2126 * channel(1) + 0.7152 * channel(3) + 0.0722 * channel(5);
    }

    private static double Linear(byte channel)
    {
        var s = channel / 255.0;
        return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
    }
}
