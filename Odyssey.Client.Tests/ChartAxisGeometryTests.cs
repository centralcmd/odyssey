using System.Globalization;
using System.Text.RegularExpressions;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// The rendering half of the informational chart axis (issue #102). <see cref="ChartAxisContrastTests"/>
/// asserts that <c>--chart-axis-strong</c> clears 3:1 <b>as a colour</b>, which is the right invariant
/// for it to hold and remains true — but a colour that clears 3:1 is not a stroke that renders at 3:1.
///
/// <para>
/// All three informational strokes are drawn into a scaled <c>viewBox</c> at <c>width: 100%</c>, so a
/// user-unit <c>stroke-width</c> is divided by the container's scale factor before it reaches the
/// device. At a 600px card that is 0.6, so the shipped 1.5 landed at 0.9 device px: a sub-pixel stroke
/// covers no whole pixel, anti-aliasing splits the token's alpha across the two rows it straddles, and
/// the darkest rendered pixel measured 1.56:1 — roughly where the decorative token was before #97.
/// At a 320px reflow width it was 1.28:1. At DPR ≥ 2 the same declaration passes, which is why the
/// defect reads as absent on a retina display.
/// </para>
///
/// <para>
/// No user-unit width fixes this, because the scale factor is whatever the container happens to be.
/// <c>vector-effect: non-scaling-stroke</c> pins the width to device space so it is independent of
/// container width, and 2px is what guarantees one fully covered device row at DPR 1.
/// </para>
///
/// <para>
/// These are source-lints over the CSS, not a measurement: measuring the rendered pixels needs a
/// rasteriser and belongs to the browser tier. What they can do is pin the geometry that measurement
/// established, in the client <b>and</b> against the design system that owns these widths — the same
/// drift guard <see cref="ChartAxisContrastTests"/> puts on the token values.
/// </para>
/// </summary>
public class ChartAxisGeometryTests
{
    /// <summary>
    /// The width, in device pixels, that covers one whole device row at DPR 1. Below it the stroke is
    /// anti-aliased across two rows and neither reaches the token's ratio.
    /// </summary>
    private const double MinimumStrokeWidth = 2.0;

    private static readonly string ComponentsCss =
        Path.Combine(ClientSource.Root, "wwwroot", "css", "odyssey-components.css");

    /// <summary>
    /// Each informational selector with the design-system file that owns its width. The zero baseline
    /// is a design-system component; the two "now" markers belong to their pages' web kits.
    /// </summary>
    public static TheoryData<string, string> InformationalRules() =>
        new()
        {
            { ".odc-lc-zero", Path.Combine("Odyssey Design System", "components.css") },
            { ".trm-chart .nowline", Path.Combine("Odyssey Design System", "ui_kits", "web", "account-terms.css") },
            { ".est-chart .nowline", Path.Combine("Odyssey Design System", "ui_kits", "web", "account-estimates.css") },
        };

    [Theory]
    [MemberData(nameof(InformationalRules))]
    public void An_informational_stroke_is_pinned_to_device_space(string selector, string _)
    {
        var rule = RuleFor(File.ReadAllText(ComponentsCss), selector);

        Assert.True(
            Regex.IsMatch(rule, @"vector-effect\s*:\s*non-scaling-stroke"),
            $"{selector} has no vector-effect: non-scaling-stroke, so its stroke-width is in user "
            + "units and is divided by the viewBox scale factor. At ordinary card widths that lands "
            + "sub-pixel and no rendered pixel reaches the 3:1 --chart-axis-strong specifies (#102).");
    }

    [Theory]
    [MemberData(nameof(InformationalRules))]
    public void An_informational_stroke_covers_a_whole_device_row(string selector, string _)
    {
        var rule = RuleFor(File.ReadAllText(ComponentsCss), selector);
        var width = StrokeWidthOf(rule, selector);

        Assert.True(
            width >= MinimumStrokeWidth,
            $"{selector} strokes at {width.ToString("0.##", CultureInfo.InvariantCulture)}px, below the "
            + $"{MinimumStrokeWidth:0.#}px that covers one whole device row at DPR 1. Anything thinner "
            + "is anti-aliased across two rows, each receiving a fraction of the token's alpha (#102).");
    }

    /// <summary>
    /// The widths are design-system-owned — changing them only in the client is the drift the second
    /// half of issue #97 exists to remove.
    /// </summary>
    [Theory]
    [MemberData(nameof(InformationalRules))]
    public void The_client_stroke_geometry_equals_the_design_systems(string selector, string designSystemCss)
    {
        var client = RuleFor(File.ReadAllText(ComponentsCss), selector);
        var design = RuleFor(File.ReadAllText(ClientSource.Sibling(designSystemCss)), selector);

        Assert.Equal(StrokeWidthOf(design, selector), StrokeWidthOf(client, selector));
        Assert.Equal(
            Regex.IsMatch(design, @"vector-effect\s*:\s*non-scaling-stroke"),
            Regex.IsMatch(client, @"vector-effect\s*:\s*non-scaling-stroke"));
    }

    // ── Parsing ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The single rule block declaring <paramref name="selector"/>. Anchored at the start of a line so
    /// a selector that is the tail of a longer one (<c>.odc-lc-axis .odc-lc-axis-zero</c>) cannot match.
    /// </summary>
    private static string RuleFor(string css, string selector) =>
        Assert.Single(
            Regex.Matches(css, @"^" + Regex.Escape(selector) + @"\s*\{[^}]*\}", RegexOptions.Multiline)
                .Select(m => m.Value));

    private static double StrokeWidthOf(string rule, string selector)
    {
        var width = Regex.Match(rule, @"stroke-width\s*:\s*(?<value>[\d.]+)");

        Assert.True(width.Success, $"{selector} declares no stroke-width.");
        return double.Parse(width.Groups["value"].Value, CultureInfo.InvariantCulture);
    }
}
