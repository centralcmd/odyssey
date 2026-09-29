using System.Text.RegularExpressions;
using Odyssey.Client.Components;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// Source-lints for the registry glyph lightness cap (issue #164). <see cref="RegistryGlyphContrastTests"/>
/// proves the capped colour clears contrast — which is only true where the colour is actually PAINTED
/// through the cap, and through the RIGHT cap. A render site writing <c>color:@opt.Color</c> raw
/// compiles, renders, and quietly draws the authored L ≈ 0.75 colour at ~2:1 on the light surface
/// again; one wrapping a text colour in <see cref="OdsGlyphInk.Glyph"/> (L 0.58) instead of
/// <see cref="OdsGlyphInk.Text"/> (L 0.50) passes 3:1 and misses the 4.5:1 text owes. These pin every
/// site shape: an inline style in markup or code, a component parameter that paints raw, a CSS rule
/// reading a colour a component publishes as a custom property, and a pale oklch literal in CSS.
/// </summary>
/// <remarks>
/// Every expected wrap is built from <see cref="OdsGlyphInk"/>'s own constants, never a second literal,
/// so the lints and the helper cannot drift apart. Every allow-list entry must still match something,
/// so an exemption cannot outlive the site it excuses.
/// </remarks>
public sealed class RegistryGlyphInkSourceTests
{
    /// <summary>An inline <c>color:</c> declaration whose value is a Razor / interpolated expression.</summary>
    private static readonly Regex InlineColour = new(@"(?<![-\w])color:\s*(?:@\(?|\{)(?<expr>[^;""}]+)");

    /// <summary>
    /// What names a colour in such an expression: any identifier ending in <c>Color</c> or <c>Fg</c> —
    /// <c>opt.Color</c>, <c>IconColor</c>, <c>FgColor(…)</c>, <c>t.Fg</c>, and local sources such as
    /// <c>TypeFg</c> or <c>iconColor</c>.
    /// </summary>
    private static readonly Regex NamesAColour = new(@"\b\w*(?:Color|Fg)\b");

    /// <summary>
    /// Inline colour expressions that are deliberately NOT registry colours, per file, with the reason.
    /// </summary>
    private static readonly (string File, string Expr, string Why)[] InlineExempt =
    [
        (P("Pages", "Finance", "BudgetsCard.razor"), "BalanceColor(PlannedBalance)", "per-theme --finance-* tokens, text-safe on both surfaces"),
        (P("Pages", "Finance", "AccountsCard.razor"), "BalanceColor(CombinedBalance)", "per-theme --finance-* tokens, text-safe on both surfaces"),
        (P("Pages", "Finance", "TermHistoryTable.razor"), "TermVisuals.ValueColor(term)", "per-theme --finance-* / --trm-fact-ink tokens (fact ink is L 0.50 on light)"),
        (P("Pages", "Finance", "ContractTermsSection.razor"), "valueColor", "TermVisuals.ValueColor: per-theme --finance-* / --trm-fact-ink tokens"),
        (P("Components", "OdsChip.razor"), "Fg", "the atom's own parameter; its callers are linted (OdsChip Fg must be OdsGlyphInk.Text)"),
        (P("Components", "OdsAvatar.razor"), "Fg", "the atom's own parameter; its callers pass per-theme palette tokens"),
        (P("Components", "OdsCalTimeGrid.razor"), "ev.Fg", "a calendar chip's background/foreground PAIR, whose contrast is the swatch's (OdsCalendarSwatches)"),
        (P("Components", "OdsTermHistoryChart.razor"), "x.ToneColor", "chart series — out of scope per the design decision"),
        (P("Components", "OdsLineChart.razor"), "Color", "chart series — out of scope per the design decision"),
        (P("Components", "OdsStepChart.razor"), "FigureColor", "chart series — out of scope per the design decision"),
        (P("Components", "OdsStepChart.razor"), "hp.Color", "chart series — out of scope per the design decision"),
        (P("Components", "OdsStepChart.razor"), "p.Color", "chart series — out of scope per the design decision"),
    ];

    /// <summary>
    /// Sites that paint a registry colour as TEXT, and so must take <see cref="OdsGlyphInk.Text"/>. Each
    /// pattern captures the helper method the site calls; each must be found, so the list cannot
    /// silently stop describing the code.
    /// </summary>
    private static readonly (string File, string Pattern)[] TextSites =
    [
        (P("Components", "OdsFilesTable.razor"), @"class=""odc-ft-type"" style=""[^""]*OdsGlyphInk\.(?<m>\w+)\("),
        (P("Pages", "Journal", "JournalFileRows.razor"), @"<OdsChip[^>]*\bFg=""@\(OdsGlyphInk\.(?<m>\w+)\("),
        (P("Pages", "Finance", "ContactDetailPanel.razor"), @"class=""cp-tile-value"" style=""[^""]*OdsGlyphInk\.(?<m>\w+)\("),
        (P("Pages", "Finance", "CreateTransactionDialog.razor"), @"OdsGlyphInk\.(?<m>\w+)\(sv\.Color\)"),
    ];

    /// <summary>CSS rules that paint a colour as TEXT and so must take the text cap.</summary>
    private static readonly string[] CssTextRules =
        [".trm-scheduled", ".est-scheduled", ".trm-inforce", ".trm-delta.up", ".trm-delta.down"];

    /// <summary>The custom properties a registry / authored foreground colour is published through.</summary>
    private const string PublishedColourVars = "rec|odc-infotile-accent|odc-er-node-fg|odc-cardsel-accent|ods-amber|acct-[a-z-]+";

    /// <summary>
    /// Pale oklch literals a CSS <c>color:</c> may still paint raw, with the reason. Empty today: every
    /// such foreground in the client goes through the cap. Chart series would belong here.
    /// </summary>
    private static readonly (string File, string Rule, string Why)[] PaleLiteralExempt = [];

    // ── Markup / code ───────────────────────────────────────────────────────────

    [Fact]
    public void Every_inline_colour_expression_is_painted_through_OdsGlyphInk_or_is_a_listed_exemption()
    {
        var wrapped = 0;
        var offenders = new List<string>();
        var usedExemptions = new HashSet<(string, string)>();

        foreach (var (file, text) in RazorSources())
        {
            var relative = ClientSource.Relative(file);
            foreach (Match m in InlineColour.Matches(text))
            {
                var expr = m.Groups["expr"].Value.Trim();
                if (!NamesAColour.IsMatch(expr))
                    continue;

                if (expr.StartsWith(nameof(OdsGlyphInk) + ".", StringComparison.Ordinal))
                {
                    wrapped++;
                    continue;
                }

                var exempt = InlineExempt.FirstOrDefault(e => e.File == relative && Normalise(e.Expr) == Normalise(expr));
                if (exempt.File is not null)
                    usedExemptions.Add((exempt.File, exempt.Expr));
                else
                    offenders.Add($"{relative}:{ClientSource.LineAt(text, m.Index)}  color:{expr}");
            }
        }

        Assert.True(wrapped >= 25, $"Only {wrapped} wrapped colour sites found; the lint's patterns have drifted from the code.");
        Assert.True(offenders.Count == 0,
            "A colour is painted raw, bypassing the light-theme lightness cap (issue #164). Wrap it in "
            + "OdsGlyphInk.Glyph(…) for a glyph / icon tile, or OdsGlyphInk.Text(…) for a colour used as text — "
            + "or, if it is genuinely not a registry colour, add it to InlineExempt with the reason:\n  "
            + string.Join("\n  ", offenders));

        var stale = InlineExempt.Where(e => !usedExemptions.Contains((e.File, e.Expr))).Select(e => $"{e.File}: {e.Expr}").ToList();
        Assert.True(stale.Count == 0, "InlineExempt entries no longer match any site — delete them:\n  " + string.Join("\n  ", stale));
    }

    [Fact]
    public void Every_registry_colour_painted_as_text_takes_the_text_cap()
    {
        foreach (var (file, pattern) in TextSites)
        {
            var text = File.ReadAllText(Path.Combine(ClientSource.Root, file));
            var matches = Regex.Matches(text, pattern);
            Assert.True(matches.Count > 0, $"Text site pattern no longer matches in {file}: {pattern}");
            Assert.All(matches, m => Assert.True(
                m.Groups["m"].Value == nameof(OdsGlyphInk.Text),
                $"{file}:{ClientSource.LineAt(text, m.Index)} paints a registry colour as TEXT through "
                + $"OdsGlyphInk.{m.Groups["m"].Value} — text owes 4.5:1 (WCAG 1.4.3), which only OdsGlyphInk.Text (L 0.50) reaches."));
        }
    }

    /// <summary>
    /// A component parameter that is painted raw by its component must be wrapped by the caller. The
    /// pickers, info tiles and record cards wrap <c>IconColor</c> / <c>Accent</c> themselves (covered by
    /// the inline and CSS lints); <c>OdsChip.Fg</c> is painted as the chip's text colour verbatim.
    /// </summary>
    [Fact]
    public void Every_OdsChip_foreground_is_wrapped_in_the_text_cap()
    {
        var chipFg = new Regex(@"<OdsChip\b[^>]*?\sFg=""(?<v>[^""]*)""", RegexOptions.Singleline);
        var found = 0;
        foreach (var (file, text) in RazorSources())
        {
            foreach (Match m in chipFg.Matches(text))
            {
                found++;
                Assert.True(
                    m.Groups["v"].Value.StartsWith("@(" + nameof(OdsGlyphInk) + "." + nameof(OdsGlyphInk.Text) + "(", StringComparison.Ordinal),
                    $"{ClientSource.Relative(file)}:{ClientSource.LineAt(text, m.Index)} passes OdsChip Fg=\"{m.Groups["v"].Value}\" — "
                    + "the chip paints it as text verbatim, so it must be OdsGlyphInk.Text(…).");
            }
        }

        Assert.True(found >= 1, "No OdsChip Fg= sites found; the lint's pattern has drifted from the code.");
    }

    // ── CSS ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The custom properties a component publishes a registry colour through. Read as <c>color</c>
    /// directly, each would bypass the cap; the rule must wrap it.
    /// </summary>
    [Fact]
    public void Every_css_rule_painting_a_published_colour_wraps_it_in_a_cap()
    {
        var raw = new Regex(@"(?<![-\w])color:\s*var\(\s*--(?:" + PublishedColourVars + @")\s*[,)]");
        var wrappedRule = new Regex(
            @"(?<![-\w])color:\s*oklch\(from\s+var\(\s*--(?:" + PublishedColourVars + @")\b[^;]*?\s(?:"
            + Regex.Escape(OdsGlyphInk.GlyphLightness) + "|" + Regex.Escape(OdsGlyphInk.TextLightness) + @")\s+c\s+h\)");

        var offenders = new List<string>();
        var wrapped = 0;
        foreach (var (file, text) in CssSources())
        {
            wrapped += wrappedRule.Matches(text).Count;
            offenders.AddRange(raw.Matches(text).Select(m => $"{ClientSource.Relative(file)}:{ClientSource.LineAt(text, m.Index)}"));
        }

        // infotile, record mark, alias, prop-type glyph, rail node, card-select, warning glyph, 2× scheduled.
        Assert.True(wrapped >= 9, $"Only {wrapped} wrapped CSS colour rules found; the lint's patterns have drifted from the code.");
        Assert.True(offenders.Count == 0,
            "A CSS rule paints a published colour raw, bypassing the light-theme lightness cap (issue #164). "
            + $"Write color: {OdsGlyphInk.Glyph("var(--rec, var(--brand-text))")}:\n  " + string.Join("\n  ", offenders));
    }

    /// <summary>The record mark's rule is exactly what the helper writes for the same input.</summary>
    [Fact]
    public void The_css_wrap_is_the_helpers_wrap()
    {
        var css = File.ReadAllText(Path.Combine(ClientSource.Root, "wwwroot", "css", "odyssey-components.css"));

        Assert.Contains("color: " + OdsGlyphInk.Glyph("var(--rec, var(--brand-text))") + ";", css, StringComparison.Ordinal);
        Assert.Contains("color: " + OdsGlyphInk.Text("var(--ods-amber)") + ";", css, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_css_rule_painting_text_takes_the_text_cap()
    {
        var css = File.ReadAllText(Path.Combine(ClientSource.Root, "wwwroot", "css", "odyssey-components.css"));
        foreach (var selector in CssTextRules)
        {
            var rule = Regex.Match(css, @"(?m)^" + Regex.Escape(selector) + @"\s*\{(?<body>[^}]*)\}");
            Assert.True(rule.Success, $"No '{selector}' rule found in odyssey-components.css.");

            var colour = Regex.Match(rule.Groups["body"].Value, @"(?<![-\w])color:\s*(?<v>[^;]+);");
            Assert.True(colour.Success, $"'{selector}' declares no color.");
            Assert.Contains(OdsGlyphInk.TextLightness, colour.Groups["v"].Value, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// A pale oklch literal (L ≥ 0.60) painted as <c>color</c> is the dark-tuned band again: ~2:1 on the
    /// light surface. It must go through a cap (<c>oklch(from oklch(…) var(--glyph…-l, l) c h)</c>).
    /// </summary>
    [Fact]
    public void No_css_rule_paints_a_pale_oklch_literal_raw()
    {
        var raw = new Regex(@"(?<![-\w])color:\s*oklch\(\s*0?\.[6-9]");
        var capped = new Regex(@"(?<![-\w])color:\s*oklch\(from\s+(?:oklch\(|var\()");

        var offenders = new List<string>();
        var cappedCount = 0;
        var scanned = 0;
        foreach (var (file, text) in CssSources())
        {
            scanned++;
            cappedCount += capped.Matches(text).Count;
            foreach (Match m in raw.Matches(text))
            {
                var relative = ClientSource.Relative(file);
                var line = text.Split('\n')[ClientSource.LineAt(text, m.Index) - 1];
                if (!PaleLiteralExempt.Any(e => e.File == relative && line.Contains(e.Rule, StringComparison.Ordinal)))
                    offenders.Add($"{relative}:{ClientSource.LineAt(text, m.Index)}  {line.Trim()}");
            }
        }

        Assert.True(scanned >= 5, $"Only {scanned} CSS files scanned; the sweep is looking in the wrong place.");
        Assert.True(cappedCount >= 12, $"Only {cappedCount} capped CSS colours found; the lint's patterns have drifted from the code.");
        Assert.True(offenders.Count == 0,
            "A CSS rule paints a pale oklch literal raw — the dark-tuned band, ~2:1 on the light surface (issue #164). "
            + $"Wrap it: color: {OdsGlyphInk.Text("oklch(L C H)")} for text, {OdsGlyphInk.Glyph("oklch(L C H)")} for a glyph:\n  "
            + string.Join("\n  ", offenders));
    }

    // ── Plumbing ────────────────────────────────────────────────────────────────

    private static string P(params string[] parts) => Path.Combine(parts);

    private static string Normalise(string expr) => Regex.Replace(expr, @"\s+", "").TrimEnd(')');

    private static IEnumerable<(string File, string Text)> RazorSources() =>
        ClientSource.SourceFiles()
            .Where(f => f.EndsWith(".razor", StringComparison.Ordinal) || f.EndsWith(".razor.cs", StringComparison.Ordinal))
            .Select(f => (f, File.ReadAllText(f)));

    private static IEnumerable<(string File, string Text)> CssSources() =>
        Directory.EnumerateFiles(ClientSource.Root, "*.css", SearchOption.AllDirectories)
            .Where(f =>
            {
                var r = ClientSource.Relative(f);
                return !r.StartsWith("obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                    && !r.StartsWith("bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                    && !r.Contains(Path.Combine("wwwroot", "_"), StringComparison.Ordinal);
            })
            .Select(f => (f, File.ReadAllText(f)));
}
