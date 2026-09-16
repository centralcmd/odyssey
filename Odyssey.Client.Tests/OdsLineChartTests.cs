using AngleSharp.Dom;
using System.Globalization;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using Odyssey.Client.Components;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// OdsLineChart's reconstructed-series set (issue #90 G8): negatives across a labelled zero, the three
/// point kinds, the opt-in text equivalent and the auto tick stride.
///
/// <para>
/// The component's <c>Sy</c> and <c>yMin</c> are <c>private</c> inside its <c>@code</c> block, which
/// <c>InternalsVisibleTo</c> does not reach, so these assert <b>rendered output</b> — the marker
/// <c>cy</c> values, the area <c>path</c>, the gridline labels — rather than the scale function. That
/// is the stricter test anyway: the defect these guard against is a point drawn outside the viewBox,
/// which is a property of the output.
/// </para>
/// </summary>
public class OdsLineChartTests
{
    // The plot geometry OdsLineChart declares. A marker outside [yTop, yBot] is off the plot; a
    // marker past ViewBoxHeight is off the drawing surface entirely, which is what a negative value
    // used to do (issue #90's second blocking observation).
    private const double YTop = 28, YBot = 212, ViewBoxHeight = 252;

    private static BunitContext NewContext()
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        return ctx;
    }

    private static IRenderedComponent<OdsLineChart> Render(
        BunitContext ctx,
        IReadOnlyList<OdsLinePoint> series,
        Action<ComponentParameterCollectionBuilder<OdsLineChart>>? extra = null) =>
        ctx.Render<OdsLineChart>(p =>
        {
            p.Add(c => c.Series, series);
            p.Add(c => c.Format, static n => n.ToString("0", CultureInfo.InvariantCulture));
            extra?.Invoke(p);
        });

    private static double Cy(IElement element) =>
        double.Parse(element.GetAttribute("cy")!, CultureInfo.InvariantCulture);

    // ── AC27 — a negative series renders inside the plot, and the area anchors at zero ──────────

    [Fact]
    public void A_negative_series_draws_every_marker_inside_the_plot_area()
    {
        using var ctx = NewContext();

        // The first point is the sample from issue #90 §5: under the old floor clamp
        // (yMin = Math.Max(0, …)) it mapped to y ≈ 256 — past the 252 viewBox and past the x-tick
        // baseline at 238, so it was not merely clipped, it was off the surface.
        var cut = Render(ctx,
        [
            new OdsLinePoint("Nov '24", -827_700m),
            new OdsLinePoint("Mar '25", -402_300m),
            new OdsLinePoint("Sep '26", 2_801_756m),
        ]);

        var markers = cut.FindAll(".odc-line-svg circle");
        Assert.Equal(3, markers.Count);

        foreach (var marker in markers)
        {
            var cy = Cy(marker);
            Assert.InRange(cy, YTop, YBot);
            Assert.InRange(cy, 0, ViewBoxHeight);
        }
    }

    [Fact]
    public void A_straddling_domain_anchors_the_area_at_the_zero_line_and_draws_it()
    {
        using var ctx = NewContext();

        var cut = Render(ctx,
        [
            new OdsLinePoint("a", -1000m),
            new OdsLinePoint("b", 1000m),
        ]);

        var zero = cut.Find("line.odc-lc-zero");
        var zeroY = double.Parse(zero.GetAttribute("y1")!, CultureInfo.InvariantCulture);

        // A symmetric domain puts zero exactly halfway down the plot.
        Assert.Equal((YTop + YBot) / 2, zeroY, 1);

        var path = cut.Find(".odc-line-svg path").GetAttribute("d")!;
        Assert.StartsWith(
            $"M 64 {zeroY.ToString("0.#", CultureInfo.InvariantCulture)} ",
            path,
            StringComparison.Ordinal);
        Assert.EndsWith(
            $"L 968 {zeroY.ToString("0.#", CultureInfo.InvariantCulture)} Z",
            path,
            StringComparison.Ordinal);
    }

    [Fact]
    public void A_non_negative_domain_still_anchors_the_area_at_the_plot_floor()
    {
        using var ctx = NewContext();

        // NG8: every consumer that predates G8 has a non-negative domain, so this is the branch
        // their markup goes down and it must be the old one.
        var cut = Render(ctx, [new OdsLinePoint("a", 100m), new OdsLinePoint("b", 300m)]);

        Assert.Empty(cut.FindAll("line.odc-lc-zero"));

        var path = cut.Find(".odc-line-svg path").GetAttribute("d")!;
        Assert.StartsWith("M 64 212 ", path, StringComparison.Ordinal);
        Assert.EndsWith("L 968 212 Z", path, StringComparison.Ordinal);

        // …and the gradient fill, not the flat one a straddling domain uses.
        Assert.StartsWith("url(#odc-lc-fill-", cut.Find(".odc-line-svg path").GetAttribute("fill")!, StringComparison.Ordinal);
    }

    // ── AC28 — zero is a labelled gridline when the domain straddles it ─────────────────────────

    [Fact]
    public void Zero_is_included_and_labelled_when_the_domain_straddles_it()
    {
        using var ctx = NewContext();

        var cut = Render(ctx, [new OdsLinePoint("a", -1000m), new OdsLinePoint("b", 3000m)]);

        var labels = cut.FindAll("g.odc-lc-axis text").Select(e => e.TextContent).ToList();
        Assert.Contains("0", labels);

        var zeroLabel = cut.Find("text.odc-lc-axis-zero");
        Assert.Equal("0", zeroLabel.TextContent);

        // The four evenly-spaced values a non-straddling domain uses generally do NOT include zero,
        // so the guard has to be that the straddling branch adds it rather than that it happens to
        // fall out of the arithmetic.
        var plain = Render(ctx, [new OdsLinePoint("a", 1000m), new OdsLinePoint("b", 3000m)]);
        Assert.Empty(plain.FindAll("text.odc-lc-axis-zero"));
    }

    // ── AC32 / AC18 — Partial and Revalued are visually AND textually distinct ──────────────────

    [Fact]
    public void Partial_and_revalued_points_render_as_distinct_marks()
    {
        using var ctx = NewContext();

        var cut = Render(ctx,
        [
            new OdsLinePoint("a", 100m),
            new OdsLinePoint("b", 200m, OdsLinePointKind.Partial),
            new OdsLinePoint("c", 300m, OdsLinePointKind.Revalued),
        ]);

        var markers = cut.FindAll(".odc-line-svg > circle, .odc-line-svg > g > circle");

        // Partial: hollow — a surface fill with a coloured stroke.
        var partial = markers.Single(m => m.GetAttribute("fill") == "var(--mud-palette-surface)");
        Assert.Equal("var(--chart-1)", partial.GetAttribute("stroke"));

        // Revalued: filled, inside a group that also carries the vertical tick. The tick is what
        // makes it distinguishable from an ordinary point without reading the fill colour.
        var revaluedGroup = cut.FindAll(".odc-line-svg > g").Single(g => g.QuerySelector("circle") is not null);
        var tick = revaluedGroup.QuerySelector("line")!;
        Assert.Equal(tick.GetAttribute("x1"), tick.GetAttribute("x2"));
        Assert.NotEqual(tick.GetAttribute("y1"), tick.GetAttribute("y2"));

        // Neither state is signalled by fill colour alone: nothing here differs only in `fill`.
        var dashed = cut.FindAll(".odc-line-svg > line[stroke-dasharray]");
        Assert.Equal(2, dashed.Count); // both segments adjoining the partial point

        // Two separate legend entries, with different wording.
        var marks = cut.FindAll(".odc-lc-mark").Select(e => e.TextContent.Trim()).ToList();
        Assert.Equal(2, marks.Count);
        Assert.Contains("Understated", marks);
        Assert.Contains("Revalued", marks);
    }

    [Fact]
    public void The_text_equivalent_gives_the_two_states_their_own_sentences()
    {
        using var ctx = NewContext();

        var cut = Render(ctx,
        [
            new OdsLinePoint("a", 100m),
            new OdsLinePoint("b", 200m, OdsLinePointKind.Partial),
            new OdsLinePoint("c", 300m, OdsLinePointKind.Revalued),
        ], p => p.Add(c => c.TextEquivalent, true));

        var states = cut.FindAll("table.odc-sr-only tbody td:last-child")
            .Select(e => e.TextContent.Trim())
            .ToList();

        Assert.Equal(3, states.Count);
        Assert.Equal(3, states.Distinct(StringComparer.Ordinal).Count());
        Assert.Contains(states, s => s.Contains("exchange rate", StringComparison.Ordinal));
        Assert.Contains(states, s => s.Contains("estimate took effect", StringComparison.Ordinal));
    }

    // ── AC17 / V15 — the delta ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// BOTH endpoints are varied, not just the last. The delta gate reads
    /// <c>_pts[0].Kind != Partial &amp;&amp; _pts[^1].Kind != Partial</c>, and a theory that only moved the
    /// tail would pass unchanged if the first-point half were dropped — which is the regression this
    /// exists to catch. A partial FIRST point matters just as much: the delta is measured from it.
    /// </summary>
    [Theory]
    [InlineData(OdsLinePointKind.Normal, OdsLinePointKind.Normal, true)]
    [InlineData(OdsLinePointKind.Normal, OdsLinePointKind.Partial, false)]
    [InlineData(OdsLinePointKind.Partial, OdsLinePointKind.Normal, false)]
    [InlineData(OdsLinePointKind.Partial, OdsLinePointKind.Partial, false)]
    [InlineData(OdsLinePointKind.Revalued, OdsLinePointKind.Normal, true)]
    [InlineData(OdsLinePointKind.Normal, OdsLinePointKind.Revalued, true)]
    [InlineData(OdsLinePointKind.Revalued, OdsLinePointKind.Revalued, true)]
    [InlineData(OdsLinePointKind.Partial, OdsLinePointKind.Revalued, false)]
    public void The_delta_is_withheld_when_either_endpoint_is_partial(
        OdsLinePointKind firstKind, OdsLinePointKind lastKind, bool expected)
    {
        using var ctx = NewContext();

        var cut = Render(ctx,
        [
            new OdsLinePoint("a", 100m, firstKind),
            new OdsLinePoint("mid", 200m),
            new OdsLinePoint("b", 300m, lastKind),
        ], p => p.Add(c => c.ShowDelta, true));

        Assert.Equal(expected, cut.FindAll(".odc-lc-delta").Count == 1);
    }

    /// <summary>
    /// A partial point in the MIDDLE marks its own segment but does not withhold the delta: both
    /// endpoints are measured, so the difference between them is still a real figure.
    /// </summary>
    [Fact]
    public void A_partial_point_between_the_endpoints_does_not_withhold_the_delta()
    {
        using var ctx = NewContext();

        var cut = Render(ctx,
        [
            new OdsLinePoint("a", 100m),
            new OdsLinePoint("mid", 200m, OdsLinePointKind.Partial),
            new OdsLinePoint("b", 300m),
        ], p => p.Add(c => c.ShowDelta, true));

        Assert.Single(cut.FindAll(".odc-lc-delta"));
        Assert.Equal(2, cut.FindAll(".odc-line-svg > line[stroke-dasharray]").Count);
    }

    [Fact]
    public void A_partial_point_is_plotted_and_marked_never_dropped()
    {
        using var ctx = NewContext();

        // V16: nulling it would move pts[0]/pts[^1], so the delta would silently span a shorter
        // window than its suffix claims.
        var cut = Render(ctx,
        [
            new OdsLinePoint("a", 100m, OdsLinePointKind.Partial),
            new OdsLinePoint("b", 200m),
            new OdsLinePoint("c", 300m),
        ]);

        Assert.Equal(3, cut.FindAll(".odc-line-svg circle").Count);
        Assert.Equal("a", cut.FindAll("g.odc-lc-axis text").Select(e => e.TextContent).First(t => t == "a"));
    }

    // ── AC33 — the auto tick stride never leaves two adjacent tail labels ───────────────────────

    [Fact]
    public void The_auto_tick_stride_never_leaves_two_adjacent_tail_labels()
    {
        for (var n = 2; n <= 120; n++)
        {
            var every = OdsLineChart.TickEvery(n);

            Assert.True(every >= 1, $"n={n}: stride must be at least 1.");

            // The rule is about the TAIL specifically: the tail label is always drawn, so the last
            // strided label landing one index short of it is the pair that overlaps at caption size.
            // A stride of 1 labels everything and is the intended dense mode for a short series, so
            // it is not a violation — with n <= 8 it is what `ceil(n / 8)` asks for.
            Assert.True((n - 1) % every != 1,
                $"n={n}, stride={every}: the last strided label sits directly beside the tail label.");

            if (every == 1)
            {
                Assert.True(n <= 8, $"n={n}: a stride of 1 is only right while ceil(n / 8) == 1.");
                continue;
            }

            var drawn = Enumerable.Range(0, n).Where(i => i % every == 0 || i == n - 1).ToList();
            var adjacent = drawn.Zip(drawn.Skip(1)).Where(pair => pair.Second - pair.First == 1).ToList();

            Assert.True(adjacent.Count == 0,
                $"n={n}, stride={every}: labels {string.Join(", ", adjacent.Select(p => $"{p.First}/{p.Second}"))} are adjacent.");
        }
    }

    [Fact]
    public void The_auto_stride_steps_past_seventeen_where_a_single_check_was_a_no_op()
    {
        // ceil(17/8) == 3 and ceil(17/7) == 3, so a rule that recomputed from a smaller divisor
        // changed nothing at n = 17 — reachable under the interval caps. Incrementing until the
        // condition clears is what fixes it, and it has to loop: 17-1 = 16, 16 % 3 == 1.
        Assert.NotEqual(3, OdsLineChart.TickEvery(17));
        Assert.Equal(0, (17 - 1) % OdsLineChart.TickEvery(17) == 1 ? 1 : 0);
    }

    // ── NG8 / AC34 — the additions are off by default ───────────────────────────────────────────

    [Fact]
    public void The_text_equivalent_and_the_marker_key_are_absent_by_default()
    {
        using var ctx = NewContext();

        var cut = Render(ctx, [new OdsLinePoint("a", 100m), new OdsLinePoint("b", 300m)]);

        Assert.Empty(cut.FindAll("table.odc-sr-only"));
        Assert.Empty(cut.FindAll(".odc-lc-marks"));

        // An all-Normal series keeps the single polyline rather than per-segment strokes, so the
        // consumers that predate G8 emit exactly the markup they did before.
        Assert.Single(cut.FindAll(".odc-line-svg polyline"));
        Assert.Empty(cut.FindAll(".odc-line-svg > line[stroke-dasharray]"));
    }

    [Fact]
    public void An_all_normal_series_emits_no_marker_key_even_when_it_is_enabled()
    {
        using var ctx = NewContext();

        var cut = Render(ctx,
            [new OdsLinePoint("a", 100m), new OdsLinePoint("b", 300m)],
            p => p.Add(c => c.MarkLegend, true));

        Assert.Empty(cut.FindAll(".odc-lc-marks"));
    }
}
