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

    private static string DesignSystemTokens => DesignSystemSource.File("tokens.css");

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
    [SkippableTheory]
    [MemberData(nameof(EveryFill))]
    public void The_design_system_contrast_text_matches_the_client(Theme theme, Fill fill)
    {
        var token = $"--mud-palette-{fill.ToString().ToLowerInvariant()}-text";
        var design = Resolve(DesignSystemTokens, Declarations(DesignSystemTokens, token)[theme]);
        var (_, client) = FillAndText(Palette(theme), fill);

        Assert.Equal(Hex(client), design, StringComparer.OrdinalIgnoreCase);
    }

    // ── Journal gallery placeholder label (issue #365 A2) ───────────────────────────────────────

    /// <summary>
    /// The "photo" / "no access" label sits on the placeholder's background fill. It was text-secondary at 0.75 opacity,
    /// about 3.9:1 in the light theme; with the opacity gone it is text-secondary as-is and clears 4.5:1 in both themes.
    /// </summary>
    [Theory]
    [InlineData(Theme.Light)]
    [InlineData(Theme.Dark)]
    public void The_gallery_placeholder_label_clears_text_contrast_with_no_opacity(Theme theme)
    {
        var rule = Rule(ComponentsCss, ".odc-photogrid-ph-label");
        Assert.Matches(@"(?<![-\w])color:\s*var\(--mud-palette-text-secondary\)", rule);
        Assert.DoesNotContain("opacity", rule, StringComparison.Ordinal);

        var palette = Palette(theme);
        AssertRatio(Hex(palette.TextSecondary), Hex(palette.Background), TextContrastMinimum, $"gallery placeholder label ({theme})");
    }

    [SkippableFact]
    public void The_design_system_gallery_placeholder_label_has_no_opacity_either()
    {
        var rule = Rule(DesignSystemSource.File("components/bundle.css"), ".odc-photogrid-ph-label");

        Assert.DoesNotContain("opacity", rule, StringComparison.Ordinal);
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
    /// The tag colour picker's focus ring (issue #394 AC 14): its outer tone is <c>--mud-palette-primary</c>,
    /// drawn outside the cell with a surface-coloured gap, so it always sits on the dialog surface whatever
    /// the cell's fill — and must clear 3:1 there in both themes. Runs on every build, not only at sync time.
    /// </summary>
    [Theory]
    [InlineData(Theme.Light)]
    [InlineData(Theme.Dark)]
    public void The_colour_picker_focus_ring_clears_three_to_one_on_the_dialog_surface(Theme theme)
    {
        var palette = Palette(theme);
        var declared = Declarations(AppCss, "--focus-ring")[theme];
        var ring = declared == "var(--mud-palette-primary)" ? Hex(palette.Primary) : Resolve(AppCss, declared);

        // --focus-ring, not the raw primary: light-theme primary (tide-600) is only ~2.5:1 on white.
        AssertRatio(ring, Hex(palette.Surface), NonTextContrastMinimum, $"--focus-ring on Surface ({theme})");
        Assert.Matches(@"\.odc-colorpick-cell:focus-visible \{[^}]*box-shadow:\s*0 0 0 2px var\(--mud-palette-surface\),\s*0 0 0 4px var\(--focus-ring\b",
            File.ReadAllText(ComponentsCss));
        Assert.Matches(@"\.odc-colorpick-cell \{[^}]*border: 1px solid var\(--control-border\)", File.ReadAllText(ComponentsCss));
    }

    /// <summary>
    /// The weather day card is a control (issue #415 §6.10, AC 17): its edge is <c>--control-border</c>, its selected bar
    /// <c>--brand-text</c>, its focus ring <c>--focus-ring</c> inset one band below the bar, and its chevron the secondary
    /// text colour. Each is a non-text mark on the surface, so each must clear 3:1 there in both themes. Runs on every build.
    /// </summary>
    [Theory]
    [InlineData(Theme.Light)]
    [InlineData(Theme.Dark)]
    public void The_weather_day_cards_marks_clear_three_to_one_on_the_surface(Theme theme)
    {
        var palette = Palette(theme);
        var surface = Hex(palette.Surface);
        var declaredRing = Declarations(AppCss, "--focus-ring")[theme];
        var ring = declaredRing == "var(--mud-palette-primary)" ? Hex(palette.Primary) : Resolve(AppCss, declaredRing);

        AssertRatio(Resolve(AppCss, Declarations(AppCss, "--control-border")[theme]), surface, NonTextContrastMinimum, $"day card edge ({theme})");
        AssertRatio(Resolve(AppCss, Declarations(AppCss, "--brand-text")[theme]), surface, NonTextContrastMinimum, $"selected bar ({theme})");
        AssertRatio(ring, surface, NonTextContrastMinimum, $"focus ring ({theme})");
        AssertRatio(Hex(palette.TextSecondary), surface, NonTextContrastMinimum, $"chevron ({theme})");

        var css = File.ReadAllText(Path.Combine(ClientSource.Root, "wwwroot", "css", "weather.css"));
        Assert.Matches(@"\.wx-day-btn \{[^}]*border: 1px solid var\(--control-border\);[^}]*border-top: var\(--focus-ring-width\) solid var\(--control-border\)", css);
        Assert.Matches(@"\.wx-day-btn\[aria-expanded=""true""\] \{[^}]*border-top-color: var\(--brand-text\)", css);
        Assert.Matches(@"\.wx-day-btn:focus-visible \{[^}]*outline: var\(--focus-ring-width\) solid var\(--focus-ring\);[^}]*outline-offset: calc\(-3 \* var\(--focus-ring-width\)\)", css);
        Assert.Matches(@"\.wx-day-chev \{[^}]*color: var\(--mud-palette-text-secondary\)", css);
    }

    /// <summary>
    /// The page header's Search region recesses its controls to the Background fill inside the
    /// Surface card and outlines them with the softer <c>--control-border-quiet</c>. Softer is
    /// only licensed down to 3:1, against the fill inside the edge and the card outside it.
    /// </summary>
    [Theory]
    [InlineData(Theme.Light)]
    [InlineData(Theme.Dark)]
    public void The_quiet_control_border_still_clears_three_to_one_on_both_sides_of_the_edge(Theme theme)
    {
        var border = Resolve(AppCss, Declarations(AppCss, "--control-border-quiet")[theme]);
        var palette = Palette(theme);

        AssertRatio(border, Hex(palette.Surface), NonTextContrastMinimum, $"--control-border-quiet on Surface ({theme})");
        AssertRatio(border, Hex(palette.Background), NonTextContrastMinimum, $"--control-border-quiet on Background ({theme})");
        Assert.NotEqual(Hex(palette.TextSecondary), border, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The client's outlined-input border rule (see <see cref="A_named_control_boundary_uses_the_control_border"/>).</summary>
    private const string ClientOutlinedBorder =
        ".mud-input.mud-input-outlined .mud-input-outlined-border.mud-input-outlined-border";

    /// <summary>
    /// MudBlazor.min.css loads after the client's sheets and paints the resting outlined-input border
    /// with the sub-3:1 hairline. At equal specificity the later sheet wins, so the client's
    /// <c>--control-border</c> rule must be strictly MORE specific than MudBlazor's resting rule or it
    /// never applies — which it silently didn't, until the search field was seen beside the filter
    /// triggers. Read from the MudBlazor package actually restored, so a MudBlazor upgrade that
    /// re-specifies the resting rule fails here rather than in a browser.
    /// </summary>
    [Fact]
    public void The_outlined_input_border_rule_outranks_MudBlazors_resting_rule()
    {
        var ours = Specificity(Assert.Single(Rules(File.ReadAllText(ComponentsCss)), r => r.Selector == ClientOutlinedBorder).Selector);
        var resting = Specificity(MudSelectorSettingBorderColor(
            ".mud-input.mud-input-outlined .mud-input-outlined-border", "--mud-palette-lines-inputs"));

        Assert.True(ours.CompareTo(resting) > 0,
            $"The client rule {ours} must out-rank MudBlazor's resting rule {resting}.");
    }

    /// <summary>
    /// The other half of the same bargain: MudBlazor's focus and disabled rules are (0,4,0) like the
    /// client's and win only because MudBlazor.min.css loads later. One more class on the client
    /// selector would silently replace the focus border and the disabled border on every outlined
    /// input. Hover is more specific and the error rule is <c>!important</c>, so they are pinned too.
    /// </summary>
    [Theory]
    [InlineData(".mud-input.mud-input-outlined:focus-within>.mud-input-outlined-border", "--mud-palette-primary")]
    [InlineData(".mud-input.mud-input-outlined.mud-disabled .mud-input-outlined-border", "--mud-palette-action-disabled")]
    [InlineData(".mud-input.mud-input-outlined:not(.mud-disabled):not(:focus-within):hover .mud-input-outlined-border", "--mud-palette-action-default")]
    public void The_outlined_input_border_rule_never_outranks_MudBlazors_state_rules(string mudSelector, string mudColor)
    {
        var ours = Specificity(ClientOutlinedBorder);
        var state = Specificity(MudSelectorSettingBorderColor(mudSelector, mudColor));

        Assert.True(ours.CompareTo(state) <= 0,
            $"The client rule {ours} must not out-rank MudBlazor's state rule {mudSelector} {state}.");
    }

    [Fact]
    public void MudBlazors_error_border_stays_important()
    {
        var error = Assert.Single(MudRules(), r => r.Selector == ".mud-input-error .mud-input-outlined-border");
        Assert.Matches(@"border-color:\s*var\(--mud-palette-error\)\s*!important", error.Body);
    }

    /// <summary>Ties above resolve by load order, so MudBlazor's sheet has to stay after the client's.</summary>
    [Fact]
    public void MudBlazors_stylesheet_loads_after_the_clients()
    {
        var index = File.ReadAllText(Path.Combine(ClientSource.Root, "wwwroot", "index.html"));
        var mud = index.IndexOf("_content/MudBlazor/MudBlazor.min.css", StringComparison.Ordinal);
        var ours = index.IndexOf("css/odyssey-components.css", StringComparison.Ordinal);

        Assert.True(ours >= 0 && mud > ours, "MudBlazor.min.css must be linked after css/odyssey-components.css.");
    }

    // ── Page-header Search region ───────────────────────────────────────────────────────────────

    private static readonly string PageHeaderCss =
        Path.Combine(ClientSource.Root, "Components", "PageHeader.razor.css");

    /// <summary>
    /// The Search region swaps every control's edge to the quiet border by redefining the token on the
    /// region itself, so the controls need no per-control rule. Without this declaration the region's
    /// controls fall back to the full-strength --control-border while the fill stays recessed.
    /// </summary>
    [Fact]
    public void The_search_region_redefines_the_control_border_as_the_quiet_one()
    {
        var body = Rule(PageHeaderCss, ".ph-root ::deep .ph-search-region");

        Assert.Matches(@"--control-border:\s*var\(--control-border-quiet\)\s*;", body);
    }

    /// <summary>
    /// Every control a Search region holds recesses to the page-background fill, the same fill the
    /// overview's breakdown tiles use; one missing here reads as a lighter box among darker ones, which
    /// is the inconsistency this rule exists to remove. A quiet multiselect keeps its transparent rest.
    /// </summary>
    [Theory]
    [InlineData(".mud-input.mud-input-outlined")]
    [InlineData(".odc-input")]
    [InlineData(".odc-ms-trigger:not(.quiet)")]
    [InlineData(".odc-sortsel-trigger")]
    [InlineData(".odc-sortsel-static")]
    [InlineData(".odc-sortsel-dir")]
    [InlineData(".odc-rpp-trigger")]
    [InlineData(".odc-dpr")]
    public void Every_search_region_control_recesses_to_the_background_fill(string control)
    {
        var wanted = ".ph-root ::deep .ph-search-region " + control;
        var rule = Assert.Single(Rules(File.ReadAllText(PageHeaderCss)),
            r => r.Selector.Split(',').Contains(wanted, StringComparer.Ordinal));

        Assert.Matches(@"background-color:\s*var\(--mud-palette-background\)\s*;", rule.Body);
    }

    /// <summary>
    /// The date range's inner fields are outlined MudInputs, so the recess rule above reaches them too.
    /// They are as tall as the range's box, so an opaque fill on them paints over its top and bottom
    /// border; the region must hand them back a transparent fill.
    /// </summary>
    [Fact]
    public void The_date_range_inner_fields_stay_transparent_in_the_search_region()
    {
        var body = Rule(PageHeaderCss, ".ph-root ::deep .ph-search-region .odc-dpr .mud-input.mud-input-outlined");

        Assert.Matches(@"background-color:\s*transparent\s*;", body);
    }

    // ── MudBlazor stylesheet + specificity ──────────────────────────────────────────────────────

    private static readonly Lazy<string> MudCss = new(() =>
    {
        var props = File.ReadAllText(Path.Combine(ClientSource.Root, "..", "Directory.Packages.props"));
        var version = Regex.Match(props, @"<PackageVersion\s+Include=""MudBlazor""\s+Version=""(?<v>[^""]+)""").Groups["v"].Value;
        Assert.False(string.IsNullOrEmpty(version), "MudBlazor has no PackageVersion in Directory.Packages.props.");

        var packages = Environment.GetEnvironmentVariable("NUGET_PACKAGES") is { Length: > 0 } configured
            ? configured
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
        var path = Path.Combine(packages, "mudblazor", version, "staticwebassets", "MudBlazor.min.css");
        Assert.True(File.Exists(path), $"MudBlazor.min.css not found at {path} — restore the solution first.");
        return File.ReadAllText(path);
    });

    /// <summary>MudBlazor's rules with each comma-separated selector split into its own entry.</summary>
    private static IEnumerable<(string Selector, string Body)> MudRules() =>
        Rules(MudCss.Value).SelectMany(r => r.Selector.Split(',').Select(sel => (sel, r.Body)));

    private static string MudSelectorSettingBorderColor(string selector, string colorToken) =>
        Assert.Single(MudRules(), r => r.Selector == selector
            && Regex.IsMatch(r.Body, @"border-color:\s*var\(" + Regex.Escape(colorToken) + @"\)")).Selector;

    /// <summary>
    /// CSS selector specificity as (ids, classes/attributes/pseudo-classes, types). Covers what these
    /// selectors use: <c>:not()</c> and <c>:is()</c> count their argument, <c>:where()</c> counts nothing,
    /// pseudo-elements count as types.
    /// </summary>
    private static (int A, int B, int C) Specificity(string selector)
    {
        var (a, b, c) = (0, 0, 0);
        var rest = Regex.Replace(selector, @":where\((?:[^()]|\((?<d>)|\)(?<-d>))*\)", "");
        foreach (Match m in Regex.Matches(rest, @":(?:not|is)\((?<arg>(?:[^()]|\((?<d>)|\)(?<-d>))*)\)"))
        {
            var inner = Specificity(m.Groups["arg"].Value);
            (a, b, c) = (a + inner.A, b + inner.B, c + inner.C);
        }
        rest = Regex.Replace(rest, @":(?:not|is)\((?:[^()]|\((?<d>)|\)(?<-d>))*\)", "");
        a += Regex.Matches(rest, @"#[\w-]+").Count;
        b += Regex.Matches(rest, @"\.[\w-]+|\[[^\]]*\]|(?<!:):[\w-]+").Count;
        c += Regex.Matches(rest, @"::[\w-]+").Count;
        c += Regex.Matches(Regex.Replace(rest, @"[.#:\[][^\s>+~.#:\[]*\]?", " "), @"(?<![\w-])[a-zA-Z][\w-]*").Count;
        return (a, b, c);
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
    [InlineData("odyssey-components.css", ".mud-input.mud-input-outlined .mud-input-outlined-border.mud-input-outlined-border")]
    [InlineData("app.css", ".aam-type-trigger")]
    public void A_named_control_boundary_uses_the_control_border(string file, string selector)
    {
        Assert.Contains("var(--control-border)", Rule(file == "app.css" ? AppCss : ComponentsCss, selector));
    }

    [SkippableTheory]
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

    [SkippableTheory]
    [InlineData(Theme.Light)]
    [InlineData(Theme.Dark)]
    public void The_client_error_cta_text_equals_the_design_systems(Theme theme)
    {
        var client = Resolve(AppCss, Declarations(AppCss, "--error-cta-text")[theme]);
        var design = Resolve(DesignSystemTokens, Declarations(DesignSystemTokens, "--error-cta-text")[theme]);

        Assert.Equal(design, client, StringComparer.OrdinalIgnoreCase);
    }

    // ── Outlined and text button labels (issue #419) ────────────────────────────────────────────

    private const string ClientOutlinedButton =
        ".mud-button-root.mud-button-outlined.mud-button-outlined-primary:where(:not(:disabled,.odc-btn-aria-disabled))";

    private const string ClientTextButton =
        ".mud-button-root.mud-button-text.mud-button-text-primary:where(:not(:disabled,.odc-btn-aria-disabled))";

    /// <summary>
    /// MudBlazor draws both labels in <c>--mud-palette-primary</c>, which in light is tide-600 on white: 2.49:1.
    /// The design system draws the outlined label in text-primary and the text label in <c>--brand-text</c>. Both
    /// must clear 4.5:1 on every ground a button sits on — Surface, Background, and the hover tint over either.
    /// </summary>
    [Theory]
    [InlineData(Theme.Light)]
    [InlineData(Theme.Dark)]
    public void Outlined_and_text_button_labels_clear_text_contrast_at_rest_and_on_hover(Theme theme)
    {
        var palette = Palette(theme);
        var labels = new Dictionary<string, string>
        {
            ["Outlined label (text-primary)"] = Hex(palette.TextPrimary),
            ["Text label (--brand-text)"] = Resolve(AppCss, Declarations(AppCss, "--brand-text")[theme]),
        };

        foreach (var (what, label) in labels)
        {
            foreach (var (groundName, ground) in new[] { ("Surface", Hex(palette.Surface)), ("Background", Hex(palette.Background)) })
            {
                AssertRatio(label, ground, TextContrastMinimum, $"{what} on {groundName} ({theme})");
                AssertRatio(label, Over(palette.ActionDefault, palette.HoverOpacity, ground), TextContrastMinimum,
                    $"{what} on the hover tint over {groundName} ({theme})");
            }
        }
    }

    /// <summary>
    /// A dialog's Cancel is a text button on the footer's wash, so the hover tint stacks on that wash. At the old 0.04
    /// light wash the label fell to 4.47:1; the design system's 0.02 keeps it at 4.65:1. Read from the rules, so
    /// thickening the wash fails here.
    /// </summary>
    [Theory]
    [InlineData(Theme.Light, ".mud-dialog .mud-dialog-actions.ods-modal-foot")]
    [InlineData(Theme.Dark, "[data-theme='dark'] .mud-dialog .mud-dialog-actions.ods-modal-foot")]
    public void The_text_button_label_clears_text_contrast_when_hovered_in_a_dialog_footer(Theme theme, string footer)
    {
        var palette = Palette(theme);
        var wash = Regex.Match(Rule(ComponentsCss, footer),
            @"background:\s*rgba\(\s*(?<r>\d+),\s*(?<g>\d+),\s*(?<b>\d+),\s*(?<a>[\d.]+)\s*\)\s*;");
        Assert.True(wash.Success, $"{footer} has no rgba() background.");

        var washColour = new MudColor(int.Parse(wash.Groups["r"].Value, CultureInfo.InvariantCulture),
            int.Parse(wash.Groups["g"].Value, CultureInfo.InvariantCulture),
            int.Parse(wash.Groups["b"].Value, CultureInfo.InvariantCulture), 255);
        var ground = Over(washColour, double.Parse(wash.Groups["a"].Value, CultureInfo.InvariantCulture), Hex(palette.Surface));
        var label = Resolve(AppCss, Declarations(AppCss, "--brand-text")[theme]);

        AssertRatio(label, ground, TextContrastMinimum, $"Text label on the dialog footer ({theme})");
        AssertRatio(label, Over(palette.ActionDefault, palette.HoverOpacity, ground), TextContrastMinimum,
            $"Text label hovered on the dialog footer ({theme})");
    }

    /// <summary>The rules themselves, so a later edit cannot swap the label back to the primary fill colour.</summary>
    [Theory]
    [InlineData(ClientOutlinedButton, "color", "--mud-palette-text-primary")]
    [InlineData(ClientOutlinedButton, "border-color", "--control-border")]
    [InlineData(ClientTextButton, "color", "--brand-text")]
    public void Outlined_and_text_buttons_paint_the_design_systems_tokens(string selector, string property, string token)
    {
        var body = Rule(ComponentsCss, selector);

        Assert.Matches(@"(?<![-\w])" + Regex.Escape(property) + @":\s*var\(" + Regex.Escape(token) + @"\)\s*;", body);
    }

    /// <summary>
    /// MudBlazor.min.css loads after the client's sheet, so each client rule must be strictly more specific than the
    /// MudBlazor rule it replaces — at rest and in the hover / focus / active states that re-draw the edge in primary.
    /// </summary>
    [Theory]
    [InlineData(ClientOutlinedButton, "", ".mud-button-outlined.mud-button-outlined-primary")]
    [InlineData(ClientOutlinedButton, ":hover", ".mud-button-outlined.mud-button-outlined-primary:hover")]
    [InlineData(ClientOutlinedButton, ":focus-visible", ".mud-button-outlined.mud-button-outlined-primary:focus-visible")]
    [InlineData(ClientOutlinedButton, ":active", ".mud-button-outlined.mud-button-outlined-primary:active")]
    [InlineData(ClientTextButton, "", ".mud-button-text.mud-button-text-primary")]
    [InlineData(ClientTextButton, ":hover", ".mud-button-text.mud-button-text-primary:hover")]
    [InlineData(ClientTextButton, ":focus-visible", ".mud-button-text.mud-button-text-primary:focus-visible")]
    [InlineData(ClientTextButton, ":active", ".mud-button-text.mud-button-text-primary:active")]
    public void Outlined_and_text_button_rules_outrank_MudBlazors(string clientBase, string state, string mudSelector)
    {
        var client = clientBase + state;
        Assert.Contains(Rules(File.ReadAllText(ComponentsCss)), r => SelectorList(r.Selector).Contains(client, StringComparer.Ordinal));
        Assert.Contains(MudRules(), r => r.Selector == mudSelector);

        var ours = Specificity(client);
        var theirs = Specificity(mudSelector);
        Assert.True(ours.CompareTo(theirs) > 0, $"{client} {ours} must out-rank MudBlazor's {mudSelector} {theirs}.");
    }

    /// <summary>
    /// Neither disabled state may be restyled: native disabled keeps MudBlazor's own look, and AriaDisabled keeps
    /// the client's <c>.odc-btn-aria-disabled</c> rule, which is less specific than these and would otherwise lose.
    /// </summary>
    [Theory]
    [InlineData(ClientOutlinedButton)]
    [InlineData(ClientTextButton)]
    public void Outlined_and_text_button_rules_leave_both_disabled_states_alone(string selector)
    {
        Assert.EndsWith(":where(:not(:disabled,.odc-btn-aria-disabled))", selector, StringComparison.Ordinal);
        Assert.Equal((0, 3, 0), Specificity(selector));
    }

    /// <summary>The client adopts the design system's rule rather than restating its own (issue #419).</summary>
    [SkippableTheory]
    [InlineData(".odc-btn.outlined", "color", "--mud-palette-text-primary")]
    [InlineData(".odc-btn.outlined", "border-color", "--control-border")]
    [InlineData(".odc-btn.text", "color", "--brand-text")]
    public void The_design_system_specifies_the_same_button_label_tokens(string selector, string property, string token)
    {
        var body = Rule(DesignSystemSource.File("components/bundle.css"), selector);

        Assert.Matches(@"(?<![-\w])" + Regex.Escape(property) + @":\s*var\(" + Regex.Escape(token) + @"\)\s*;", body);
    }

    /// <summary>A selector list split at its top-level commas — never at one inside <c>:where(...)</c> or <c>:not(...)</c>.</summary>
    private static IEnumerable<string> SelectorList(string selectorList)
    {
        var depth = 0;
        var start = 0;
        for (var i = 0; i < selectorList.Length; i++)
        {
            switch (selectorList[i])
            {
                case '(': depth++; break;
                case ')': depth--; break;
                case ',' when depth == 0:
                    yield return selectorList[start..i];
                    start = i + 1;
                    break;
            }
        }
        yield return selectorList[start..];
    }

    /// <summary>
    /// <paramref name="top"/>'s RGB at <paramref name="alpha"/> composited over an opaque <c>#RRGGBB</c> ground. MudBlazor
    /// generates <c>--mud-palette-action-default-hover</c> as <c>ActionDefault</c>'s RGB at <c>HoverOpacity</c>.
    /// </summary>
    private static string Over(MudColor top, double alpha, string ground)
    {
        var a = alpha;
        int Channel(byte fg, int offset) =>
            (int)Math.Round(fg * a + Convert.ToByte(ground.Substring(offset, 2), 16) * (1 - a));
        return $"#{Channel(top.R, 1):X2}{Channel(top.G, 3):X2}{Channel(top.B, 5):X2}";
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

        if (NamesTheme(selector, "dark")) return Theme.Dark;
        if (NamesTheme(selector, "light")) return Theme.Light;
        return null;
    }

    /// <summary>The design system's token file quotes the attribute value in double quotes, the client in single.</summary>
    private static bool NamesTheme(string selector, string theme) =>
        Regex.IsMatch(selector, @"data-theme=['""]" + theme + @"['""]");

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
