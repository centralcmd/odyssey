using System.Globalization;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using Odyssey.Client.Components;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// The chart additions from the design-system update that added OdsLineChart's ledger row and head
/// controls, and a hover readout on both OdsLineChart and OdsStepChart.
/// </summary>
public class OdsChartHoverTests
{
    private static readonly DateTime Now = new(2026, 6, 15, 12, 0, 0, DateTimeKind.Utc);

    private static BunitContext NewContext()
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        return ctx;
    }

    private static string Whole(decimal n) => n.ToString("#,##0", CultureInfo.InvariantCulture);

    private static IRenderedComponent<OdsLineChart> Line(
        BunitContext ctx, IReadOnlyList<OdsLinePoint> series,
        Action<ComponentParameterCollectionBuilder<OdsLineChart>>? extra = null) =>
        ctx.Render<OdsLineChart>(p =>
        {
            p.Add(c => c.Title, "Net worth");
            p.Add(c => c.Series, series);
            p.Add(c => c.Format, Whole);
            extra?.Invoke(p);
        });

    // ── OdsLineChart: figure, legend, controls ───────────────────────────────────────────────

    [Fact]
    public void The_head_figure_is_shown_by_default_and_there_is_no_legend()
    {
        using var ctx = NewContext();
        var cut = Line(ctx, [new("Jan", 100), new("Feb", 150)]);

        Assert.Equal("150", cut.Find(".odc-lc-num").TextContent.Trim());
        Assert.Empty(cut.FindAll(".odc-sc-legend"));
    }

    [Fact]
    public void The_legend_carries_the_figure_and_the_change_when_the_head_figure_is_off()
    {
        using var ctx = NewContext();
        var cut = Line(ctx, [new("Jan", 100), new("Feb", 80), new("Mar", 150)], p => p
            .Add(c => c.ShowFigure, false)
            .Add(c => c.Legend, true)
            .Add(c => c.DeltaSuffix, "since Jan"));

        Assert.Empty(cut.FindAll(".odc-lc-num"));
        Assert.Equal("Net worth", cut.Find(".odc-sc-leg-name").TextContent);
        Assert.Equal("150", cut.Find(".odc-sc-leg-val").TextContent);
        Assert.Equal("+50 since Jan", cut.Find(".odc-sc-leg-idx").TextContent);
    }

    [Fact]
    public void The_legend_withholds_the_change_when_an_endpoint_is_understated()
    {
        using var ctx = NewContext();
        var cut = Line(ctx, [new("Jan", 100, OdsLinePointKind.Partial), new("Feb", 150)], p => p
            .Add(c => c.Legend, true));

        Assert.Equal("change withheld", cut.Find(".odc-sc-leg-idx").TextContent);
        // The marker key joins the legend row rather than getting its own.
        Assert.Empty(cut.FindAll(".odc-lc-marks"));
        Assert.Single(cut.FindAll(".odc-sc-legend .odc-lc-mark"));
    }

    [Fact]
    public void Controls_end_render_in_the_head_even_on_an_empty_series()
    {
        using var ctx = NewContext();
        var cut = Line(ctx, [], p => p
            .Add(c => c.ShowFigure, false)
            .Add(c => c.ControlsEnd, (RenderFragment)(b => b.AddMarkupContent(0, "<button id=\"cfg\">Chart settings</button>"))));

        Assert.NotNull(cut.Find(".odc-lc-figure .odc-lc-controls.end #cfg"));
    }

    // ── OdsLineChart: hover readout ──────────────────────────────────────────────────────────

    [Fact]
    public void Hovering_a_column_shows_the_point_and_its_change_and_leaving_hides_it()
    {
        using var ctx = NewContext();
        var cut = Line(ctx, [new("Jan", 100), new("Feb", 80, OdsLinePointKind.Revalued), new("Mar", 150)]);

        Assert.Empty(cut.FindAll(".odc-lc-tip"));
        var hits = cut.FindAll("rect.odc-lc-hit");
        Assert.Equal(3, hits.Count);

        hits[1].MouseEnter();
        var tip = cut.Find(".odc-lc-tip");
        Assert.Equal("Feb · Revalued", tip.QuerySelector(".odc-lc-tip-k")!.TextContent);
        Assert.StartsWith("80", tip.QuerySelector(".odc-lc-tip-v")!.TextContent.Trim());
        Assert.Equal("−20", tip.QuerySelector(".odc-lc-tip-d")!.TextContent);
        Assert.Single(cut.FindAll("circle.odc-lc-hring"));

        cut.Find(".odc-lc-plot").MouseLeave();
        Assert.Empty(cut.FindAll(".odc-lc-tip"));
    }

    [Theory]
    [InlineData(5, " start")]
    [InlineData(50, "")]
    [InlineData(95, " end")]
    public void The_readout_pins_to_the_near_edge(double left, string expected) =>
        Assert.Equal(expected, OdsLineChart.TipEdge(left));

    // ── OdsStepChart: hover readout ──────────────────────────────────────────────────────────

    [Fact]
    public void Hovering_a_scheduled_entry_says_so_and_shows_its_note()
    {
        using var ctx = NewContext();
        var cut = ctx.Render<OdsStepChart>(p => p
            .Add(c => c.Series,
            [
                new OdsStepPoint(new DateOnly(2025, 9, 1), 2150),
                new OdsStepPoint(new DateOnly(2026, 10, 1), 2350) { Note = "Advisor estimate" },
            ])
            .Add(c => c.Format, Whole)
            .Add(c => c.Now, Now));

        var hits = cut.FindAll("circle.odc-lc-hit");
        Assert.Equal(2, hits.Count);

        hits[1].MouseEnter();
        var keys = cut.FindAll(".odc-lc-tip .odc-lc-tip-k").Select(k => k.TextContent).ToArray();
        Assert.Equal(["Oct 1, 2026 · Scheduled", "Advisor estimate"], keys);
        Assert.Equal("+200", cut.Find(".odc-lc-tip-d").TextContent);

        cut.Find(".odc-lc-plot").MouseLeave();
        Assert.Empty(cut.FindAll(".odc-lc-tip"));
    }
}
