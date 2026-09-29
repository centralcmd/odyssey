using System.Text.RegularExpressions;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// Source-lints for the registry glyph lightness cap (issue #164). <see cref="RegistryGlyphContrastTests"/>
/// proves the capped colour clears contrast — which is only true where the colour is actually PAINTED
/// through the cap. A render site writing <c>color:@opt.Color</c> raw compiles, renders, and quietly
/// draws the authored L ≈ 0.75 colour at ~2:1 on the light surface again, so these pin the wrap at
/// every site shape: an inline style in markup or code, and a scoped/global CSS rule reading a colour
/// a component publishes as a custom property.
/// </summary>
public sealed class RegistryGlyphInkSourceTests
{
    /// <summary>
    /// An inline <c>color:</c> declaration whose value is a Razor / interpolated expression, captured up
    /// to the end of the declaration.
    /// </summary>
    private static readonly Regex InlineColour = new(@"(?<![-\w])color:\s*(?:@\(?|\{)(?<expr>[^;""}]+)");

    /// <summary>What names a registry-sourced colour in such an expression.</summary>
    private static readonly Regex RegistryColour = new(@"\.Color\b|\bIconColor\b|\bFgColor\(|\.Fg\b");

    /// <summary>
    /// Files that paint a <c>.Color</c> that is not a registry glyph. Chart series follow the chart
    /// token rules (the design decision's stated out-of-scope), and a calendar event chip carries its
    /// own background/foreground PAIR, whose contrast is the swatch's (OdsCalendarSwatches), not the cap's.
    /// </summary>
    private static readonly HashSet<string> Exempt = new(StringComparer.Ordinal)
    {
        Path.Combine("Components", "OdsStepChart.razor"),
        Path.Combine("Components", "OdsLineChart.razor"),
        Path.Combine("Components", "OdsTermHistoryChart.razor"),
        Path.Combine("Components", "OdsCalTimeGrid.razor"),
    };

    [Fact]
    public void Every_inline_registry_colour_is_painted_through_OdsGlyphInk()
    {
        var scanned = 0;
        var wrapped = 0;
        var offenders = new List<string>();

        foreach (var file in ClientSource.SourceFiles().Where(f => f.EndsWith(".razor", StringComparison.Ordinal) || f.EndsWith(".razor.cs", StringComparison.Ordinal)))
        {
            var relative = ClientSource.Relative(file);
            if (Exempt.Contains(relative))
                continue;

            var text = File.ReadAllText(file);
            foreach (Match m in InlineColour.Matches(text))
            {
                var expr = m.Groups["expr"].Value.Trim();
                if (!RegistryColour.IsMatch(expr))
                    continue;

                scanned++;
                if (expr.StartsWith("OdsGlyphInk.", StringComparison.Ordinal))
                    wrapped++;
                else
                    offenders.Add($"{relative}:{ClientSource.LineAt(text, m.Index)}  color:{expr}");
            }
        }

        // Non-vacuous: the sweep must actually be looking at the render sites it guards.
        Assert.True(wrapped >= 20, $"Only {wrapped} wrapped registry-colour sites found; the lint's patterns have drifted from the code.");
        Assert.True(offenders.Count == 0,
            "A registry colour is painted raw, bypassing the light-theme lightness cap (issue #164). Wrap it in "
            + "OdsGlyphInk.Glyph(…) for a glyph / icon tile, or OdsGlyphInk.Text(…) for a colour used as text:\n  "
            + string.Join("\n  ", offenders) + $"\n({scanned} registry-colour sites scanned.)");
    }

    /// <summary>
    /// The custom properties a component publishes a registry colour through. Read as <c>color</c>
    /// directly, each would bypass the cap; the rule must wrap it in <c>oklch(from … var(--glyph-l, l) c h)</c>.
    /// </summary>
    [Fact]
    public void Every_css_rule_painting_a_published_registry_colour_wraps_it_in_the_glyph_cap()
    {
        var raw = new Regex(@"(?<![-\w])color:\s*var\(\s*--(?:rec|odc-infotile-accent|odc-er-node-fg|odc-cardsel-accent|acct-[a-z-]+)\s*[,)]");
        var wrappedRule = new Regex(@"(?<![-\w])color:\s*oklch\(from\s+var\(\s*--(?:rec|odc-infotile-accent|odc-er-node-fg|odc-cardsel-accent)\b[^;]*var\(--glyph-l, l\) c h\)");

        var files = Directory.EnumerateFiles(ClientSource.Root, "*.css", SearchOption.AllDirectories)
            .Where(f => !ClientSource.Relative(f).StartsWith("obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                && !ClientSource.Relative(f).StartsWith("bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                && !ClientSource.Relative(f).Contains(Path.Combine("wwwroot", "_"), StringComparison.Ordinal))
            .ToList();
        Assert.NotEmpty(files);

        var offenders = new List<string>();
        var wrapped = 0;
        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            wrapped += wrappedRule.Matches(text).Count;
            offenders.AddRange(raw.Matches(text).Select(m => $"{ClientSource.Relative(file)}:{ClientSource.LineAt(text, m.Index)}"));
        }

        // .odc-infotile-ic, .odc-record-mark, .odc-alias-ic, .prop-type-glyph, .odc-er-node, .odc-cardsel-opt.
        Assert.True(wrapped >= 6, $"Only {wrapped} wrapped CSS glyph rules found; the lint's patterns have drifted from the code.");
        Assert.True(offenders.Count == 0,
            "A CSS rule paints a published registry colour raw, bypassing the light-theme lightness cap (issue #164). "
            + "Write color: oklch(from var(--rec, var(--brand-text)) var(--glyph-l, l) c h):\n  "
            + string.Join("\n  ", offenders));
    }
}
