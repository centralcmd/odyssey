using System.Globalization;
using System.Text.RegularExpressions;
using MudBlazor.Utilities;
using Odyssey.Client.Theme;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// Issue #254: contrast failures on ordinary controls, mostly in the light theme.
///
/// <list type="number">
/// <item>Text on a filled palette colour. White on the light tide-600 primary was 2.49:1, and
/// MudBlazor's white default for every unset <c>*ContrastText</c> was 1.7-2.8:1 on the bright
/// dark-theme fills. Every palette colour now names a contrast text that clears 4.5:1.</item>
/// <item>The resting outline of a control whose outline is its only edge was the 0.28-alpha
/// <c>--mud-palette-lines-inputs</c> hairline, under 3:1 in both themes. It is now the
/// <c>--control-border</c> token; the hairline stays for an allow-listed set of decorative uses.</item>
/// <item>The error call-to-action painted #2A0E0E on coral-700, 3.07:1. It is now
/// <c>--error-cta-text</c>, white on light.</item>
/// </list>
///
/// <para>
/// Like <see cref="ChartAxisContrastTests"/>, these assert the <b>ratio</b> computed from the
/// token as authored and the MudTheme palette as configured, so re-theming a fill or surface
/// without re-checking what sits on it fails here.
/// </para>
/// </summary>
public class ControlContrastTests
{
    /// <summary>WCAG 2.2 SC 1.4.3 (Contrast Minimum) for normal-size text.</summary>
    private const double TextContrastMinimum = 4.5;

    /// <summary>WCAG 2.2 SC 1.4.11 (Non-text Contrast) for a user-interface component's boundary.</summary>
    private const double NonTextContrastMinimum = 3.0;

    private const string LinesInputs = "--mud-palette-lines-inputs";

    private static readonly string AppCss =
        Path.Combine(ClientSource.Root, "wwwroot", "css", "app.css");

    private static readonly string ComponentsCss =
        Path.Combine(ClientSource.Root, "wwwroot", "css", "odyssey-components.css");

    private static readonly string DesignSystemTokens =
        ClientSource.Sibling(Path.Combine("Odyssey Design System", "colors_and_type.css"));

    public enum Theme { Light, Dark }

    public enum Fill { Primary, Secondary, Tertiary, Info, Success, Warning, Error }

    // ── 1. Text on a filled palette colour ──────────────────────────────────────────────────────

    public static TheoryData<Theme, Fill> EveryFill()
    {
        var data = new TheoryData<Theme, Fill>();
        foreach (var theme in Enum.GetValues<Theme>())
            foreach (var fill in Enum.GetValues<Fill>())
                data.Add(theme, fill);
        return data;
    }

    [Theory]
    [MemberData(nameof(EveryFill))]
    public void A_palette_contrast_text_clears_text_contrast_on_its_fill(Theme theme, Fill fill)
    {
        var (background, text) = FillAndText(Palette(theme), fill);

        AssertRatio(Hex(text), Hex(background), TextContrastMinimum, $"{fill}ContrastText on {fill} ({theme})");
    }

    [Theory]
    [InlineData(Theme.Light)]
    [InlineData(Theme.Dark)]
    public void Primary_contrast_text_clears_text_contrast_on_the_hover_fill(Theme theme)
    {
        var palette = Palette(theme);

        // A filled MudButton hovers to PrimaryDarken with the same text colour.
        AssertRatio(Hex(palette.PrimaryContrastText), Hex(palette.PrimaryDarken), TextContrastMinimum,
            $"PrimaryContrastText on PrimaryDarken ({theme})");
    }

    /// <summary>
    /// The surfaces issue #254 lists paint their ink through the palette's contrast-text variable,
    /// so the MudTheme value above reaches them. A literal <c>#fff</c> here would bypass it.
    /// </summary>
    [Theory]
    [InlineData("app.css", ".ods-skip-link", "primary")]
    [InlineData("odyssey-components.css", ".odc-badge.primary", "primary")]
    [InlineData("odyssey-components.css", ".odc-ms-count", "primary")]
    [InlineData("odyssey-components.css", ".odc-check-box .material-icons", "primary")]
    [InlineData("odyssey-components.css", ".odc-cal-daynum.today", "primary")]
    [InlineData("odyssey-components.css", ".cal-tg-dnum.today", "primary")]
    [InlineData("odyssey-components.css", ".odc-badge", "error")]
    public void A_filled_surface_paints_its_ink_with_the_palette_contrast_text(string file, string selector, string fill)
    {
        var rule = Rule(file == "app.css" ? AppCss : ComponentsCss, selector);

        Assert.Matches(@"(?<![-\w])color:\s*var\(--mud-palette-" + fill + @"-text\)", rule);
    }

    /// <summary>
    /// The design system declares the same contrast text for every fill, so a design export cannot
    /// quietly reintroduce white on a fill the client has moved off it.
    /// </summary>
    [Theory]
    [MemberData(nameof(EveryFill))]
    public void The_design_system_contrast_text_matches_the_client(Theme theme, Fill fill)
    {
        var token = $"--mud-palette-{fill.ToString().ToLowerInvariant()}-text";
        var design = Resolve(DesignSystemTokens, Declarations(DesignSystemTokens, token)[theme]);
        var (_, client) = FillAndText(Palette(theme), fill);

        Assert.Equal(Hex(client), design, StringComparer.OrdinalIgnoreCase);
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

    /// <summary>
    /// The only uses of the sub-3:1 hairline the client may keep, each for a reason that makes it
    /// decorative: nothing interactive is identified by it. Keyed by client-relative file and
    /// whitespace-normalised selector.
    /// </summary>
    private static readonly IReadOnlyDictionary<(string File, string Selector), string> DecorativeLinesInputs =
        new Dictionary<(string, string), string>
        {
            [("wwwroot/css/odyssey-components.css", ".odc-tagpick-empty,.odc-tagpick-unavailable")] = "static empty-state box, not a control",
            [("wwwroot/css/odyssey-components.css", ".odc-money-sign.btn")] = "hairline separator inside the .odc-money box, which carries --control-border",
            [("wwwroot/css/odyssey-components.css", ".odc-money-cur")] = "hairline separator inside the .odc-money box",
            [("wwwroot/css/odyssey-components.css", ".odc-money-cur.btn")] = "hairline separator inside the .odc-money box",
            [("wwwroot/css/odyssey-components.css", ".odn-cmd-esc")] = "keyboard-hint chip, not a control",
            [("wwwroot/css/odyssey-components.css", ".odn-cmd-kbd")] = "keyboard-hint chip, not a control",
            [("wwwroot/css/odyssey-components.css", ".odn-cmd-foot .odn-kbd,.odn-kbd")] = "keyboard-hint chip, not a control",
            [("wwwroot/css/odyssey-components.css", ".odc-photogrid-tile:hover")] = "hover emphasis on a tile identified by its image",
            [("wwwroot/css/odyssey-components.css", ".odc-board-card:hover")] = "hover emphasis on a card identified by its content",
            [("wwwroot/css/odyssey-components.css", ".odc-capacity-nolimit")] = "static 'No limit' stand-in, not a control",
            [("wwwroot/css/legal.css", ".lg-scroll,.lg-ver-view,.lg-admin-card .odc-input-multiline")] = "scrollbar colour",
            [("wwwroot/css/legal.css", ".lg-scroll::-webkit-scrollbar-thumb,.lg-ver-view::-webkit-scrollbar-thumb,.lg-admin-card .odc-input-multiline::-webkit-scrollbar-thumb")] = "scrollbar thumb",
            [("Pages/Photos/AlbumFormDialog.razor.css", ".pl-memcover:hover,.pl-membtn:hover")] = "hover emphasis on icon buttons identified by their glyph",
        };

    /// <summary>
    /// Every use of the hairline across the client's CSS, scoped <c>.razor.css</c> included, must be
    /// on the decorative allow-list. A new control drawn with it fails here instead of shipping at
    /// ~1.9:1; a control that genuinely is decorative is added to the list with its reason.
    /// </summary>
    [Fact]
    public void Every_lines_inputs_use_is_an_allow_listed_decorative_one()
    {
        var unlisted = LinesInputsUses().Where(use => !DecorativeLinesInputs.ContainsKey(use)).ToList();

        Assert.True(
            unlisted.Count == 0,
            $"{LinesInputs} (under 3:1) is used by rules not on the decorative allow-list. Use "
            + "var(--control-border) for a control's boundary, or allow-list the rule with its reason:\n  "
            + string.Join("\n  ", unlisted.Select(u => $"{u.File}: {u.Selector}")));
    }

    /// <summary>An exemption cannot outlive the rule it excuses.</summary>
    [Fact]
    public void Every_decorative_allow_list_entry_is_still_a_live_use()
    {
        var live = LinesInputsUses().ToHashSet();
        var stale = DecorativeLinesInputs.Keys.Where(key => !live.Contains(key)).ToList();

        Assert.True(
            stale.Count == 0,
            "Allow-list entries whose rule no longer exists or no longer uses " + LinesInputs + ":\n  "
            + string.Join("\n  ", stale.Select(u => $"{u.File}: {u.Selector}")));
    }

    /// <summary>
    /// A positive sample of the boundaries issue #254 names, so a control that moves off the hairline
    /// onto some other sub-3:1 colour (the divider, say) is not waved through by the guard above.
    /// </summary>
    [Theory]
    [InlineData("odyssey-components.css", ".odc-check-box")]
    [InlineData("odyssey-components.css", ".odc-input")]
    [InlineData("odyssey-components.css", ".odc-select-trigger")]
    [InlineData("odyssey-components.css", ".odc-ms-trigger")]
    [InlineData("odyssey-components.css", ".mud-input.mud-input-outlined .mud-input-outlined-border")]
    [InlineData("app.css", ".aam-type-trigger")]
    public void A_named_control_boundary_uses_the_control_border(string file, string selector)
    {
        Assert.Contains("var(--control-border)", Rule(file == "app.css" ? AppCss : ComponentsCss, selector));
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
        Assert.Contains("color: var(--error-cta-text)", Rule(ComponentsCss, ".odc-problem.error .odc-problem-cta"));
    }

    [Theory]
    [InlineData(Theme.Light)]
    [InlineData(Theme.Dark)]
    public void The_client_error_cta_text_equals_the_design_systems(Theme theme)
    {
        var client = Resolve(AppCss, Declarations(AppCss, "--error-cta-text")[theme]);
        var design = Resolve(DesignSystemTokens, Declarations(DesignSystemTokens, "--error-cta-text")[theme]);

        Assert.Equal(design, client, StringComparer.OrdinalIgnoreCase);
    }

    // ── CSS parsing ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Every innermost <c>selector { body }</c> in <paramref name="css"/>, comments removed, with the
    /// selector whitespace-normalised: runs collapse to one space and none survive around a comma, so
    /// a selector list reads the same however it is wrapped or aligned.
    /// </summary>
    private static IEnumerable<(string Selector, string Body)> Rules(string css) =>
        Regex.Matches(WithoutComments(css), @"(?<selector>[^{}]+)\{(?<body>[^{}]*)\}")
            .Select(m => (NormaliseSelector(m.Groups["selector"].Value), m.Groups["body"].Value));

    private static string NormaliseSelector(string selector) =>
        Regex.Replace(Regex.Replace(selector.Trim(), @"\s+", " "), @"\s*,\s*", ",");

    /// <summary>The body of the one rule whose normalised selector equals <paramref name="selector"/>'s.</summary>
    private static string Rule(string cssPath, string selector)
    {
        var wanted = NormaliseSelector(selector);
        return Assert.Single(Rules(File.ReadAllText(cssPath)), r => r.Selector == wanted).Body;
    }

    /// <summary>
    /// (client-relative file, normalised selector) for each rule across the client's stylesheets and
    /// scoped <c>.razor.css</c> files whose body names <see cref="LinesInputs"/>.
    /// </summary>
    private static IEnumerable<(string File, string Selector)> LinesInputsUses() =>
        Directory.EnumerateFiles(ClientSource.Root, "*.css", SearchOption.AllDirectories)
            .Where(file => !IsBuildOutput(file))
            .SelectMany(file => Rules(File.ReadAllText(file))
                .Where(rule => Regex.IsMatch(rule.Body, @"var\(\s*" + Regex.Escape(LinesInputs) + @"\b"))
                .Select(rule => (RelativePath(file), rule.Selector)));

    private static bool IsBuildOutput(string file)
    {
        var segments = Path.GetRelativePath(ClientSource.Root, file).Split(Path.DirectorySeparatorChar);
        return segments.Contains("bin") || segments.Contains("obj");
    }

    private static string RelativePath(string file) =>
        Path.GetRelativePath(ClientSource.Root, file).Replace(Path.DirectorySeparatorChar, '/');

    /// <summary>
    /// Comments are dropped before parsing: a comment above a block may itself mention
    /// <c>[data-theme='dark']</c>, or a braced example, which would otherwise be read as code.
    /// </summary>
    private static string WithoutComments(string css) =>
        Regex.Replace(css, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);

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

    // ── Palette ─────────────────────────────────────────────────────────────────────────────────

    private static MudBlazor.Palette Palette(Theme theme) =>
        theme == Theme.Dark ? OdysseyTheme.Theme.PaletteDark : OdysseyTheme.Theme.PaletteLight;

    private static (MudColor Fill, MudColor Text) FillAndText(MudBlazor.Palette palette, Fill fill) => fill switch
    {
        Fill.Primary => (palette.Primary, palette.PrimaryContrastText),
        Fill.Secondary => (palette.Secondary, palette.SecondaryContrastText),
        Fill.Tertiary => (palette.Tertiary, palette.TertiaryContrastText),
        Fill.Info => (palette.Info, palette.InfoContrastText),
        Fill.Success => (palette.Success, palette.SuccessContrastText),
        Fill.Warning => (palette.Warning, palette.WarningContrastText),
        Fill.Error => (palette.Error, palette.ErrorContrastText),
        _ => throw new ArgumentOutOfRangeException(nameof(fill)),
    };

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
