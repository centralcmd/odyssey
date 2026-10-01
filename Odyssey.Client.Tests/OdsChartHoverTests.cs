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
    public void A_caller_can_withhold_the_legends_change_and_keep_its_value()
    {
        using var ctx = NewContext();
        var cut = Line(ctx, [new("Jan", 100), new("Feb", 150)], p => p
            .Add(c => c.Legend, true)
            .Add(c => c.LegendShowsChange, false));

        Assert.Equal("150", cut.Find(".odc-sc-leg-val").TextContent);
        Assert.Empty(cut.FindAll(".odc-sc-leg-idx"));
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

    // ── The y-axis gutter fits its labels ────────────────────────────────────────────────────

    [Fact]
    public void Short_axis_labels_keep_the_default_gutter() =>
        Assert.Equal(OdsLineChart.DefaultX0, OdsLineChart.AxisGutter(["36K", "31K", "25K", "20K"]));

    [Fact]
    public void A_long_axis_label_widens_the_gutter_so_it_starts_inside_the_viewbox()
    {
        var x0 = OdsLineChart.AxisGutter(["17.23M NOK", "6M NOK"]);

        var start = x0 - OdsLineChart.AxisLabelGap - "17.23M NOK".Length * OdsLineChart.AxisCharWidth;
        Assert.True(x0 > OdsLineChart.DefaultX0);
        Assert.True(start >= OdsLineChart.AxisLabelMargin, $"The widest label starts at {start}.");
    }

    [Fact]
    public void The_line_chart_lays_its_plot_out_against_the_widened_gutter()
    {
        using var ctx = NewContext();
        var cut = Line(ctx, [new("Jan", 15_000_000), new("Feb", 17_000_000)], p => p
            .Add(c => c.AxisFormat, v => (v / 1_000_000m).ToString("0.00", CultureInfo.InvariantCulture) + "M NOK"));

        var labels = cut.FindAll("g.odc-lc-axis text[text-anchor=end]");
        var widest = labels.Max(t => t.TextContent.Length);
        var labelX = double.Parse(labels[0].GetAttribute("x")!, CultureInfo.InvariantCulture);
        Assert.True(labelX - widest * OdsLineChart.AxisCharWidth >= OdsLineChart.AxisLabelMargin);

        // The first point sits on the plot's left edge, right of the labels rather than under them.
        var firstX = cut.FindAll("rect.odc-lc-hit")[0];
        var edge = double.Parse(firstX.GetAttribute("x")!, CultureInfo.InvariantCulture)
                   + double.Parse(firstX.GetAttribute("width")!, CultureInfo.InvariantCulture) / 2;
        Assert.Equal(labelX + OdsLineChart.AxisLabelGap, edge, 0.5);
    }

    [Fact]
    public void The_step_chart_widens_its_gutter_the_same_way()
    {
        using var ctx = NewContext();
        var cut = ctx.Render<OdsStepChart>(p => p
            .Add(c => c.Series,
            [
                new OdsStepPoint(new DateOnly(2025, 1, 1), 15_000_000),
                new OdsStepPoint(new DateOnly(2026, 1, 1), 17_000_000),
            ])
            .Add(c => c.Format, v => v.ToString("#,##0.00", CultureInfo.InvariantCulture) + " NOK")
            .Add(c => c.Now, Now));

        var labels = cut.FindAll("g.odc-lc-axis text[text-anchor=end]");
        var widest = labels.Max(t => t.TextContent.Length);
        var labelX = double.Parse(labels[0].GetAttribute("x")!, CultureInfo.InvariantCulture);
        Assert.True(labelX - widest * OdsLineChart.AxisCharWidth >= OdsLineChart.AxisLabelMargin);
    }

    // ── Keyboard access and the live region (WCAG 2.1.1, 1.4.13, 4.1.3) ─────────────────────

    private static string LiveText(IRenderedComponent<OdsLineChart> cut) =>
        cut.Find(".odc-lc-plot [aria-live=polite]").TextContent;

    [Fact]
    public void The_plot_is_one_tab_stop_that_says_how_to_read_it()
    {
        using var ctx = NewContext();
        var cut = Line(ctx, [new("Jan", 100), new("Feb", 150)]);

        var plot = cut.Find(".odc-lc-plot");
        Assert.Equal("0", plot.GetAttribute("tabindex"));
        Assert.Contains("arrow keys", plot.GetAttribute("aria-label"));
        Assert.Single(cut.FindAll("[tabindex]"));
    }

    [Fact]
    public void Focus_opens_the_latest_point_and_the_arrows_step_through_the_points()
    {
        using var ctx = NewContext();
        var cut = Line(ctx, [new("Jan", 100), new("Feb", 80), new("Mar", 150)]);
        var plot = cut.Find(".odc-lc-plot");

        plot.Focus();
        Assert.Equal("Mar: 150, change +70", LiveText(cut));

        cut.Find(".odc-lc-plot").KeyDown("ArrowLeft");
        Assert.Equal("Feb: 80, change −20", LiveText(cut));
        Assert.StartsWith("Feb", cut.Find(".odc-lc-tip .odc-lc-tip-k").TextContent);

        cut.Find(".odc-lc-plot").KeyDown("ArrowLeft");
        cut.Find(".odc-lc-plot").KeyDown("ArrowLeft");   // clamps at the first point
        Assert.Equal("Jan: 100", LiveText(cut));
    }

    [Theory]
    [InlineData("Escape")]
    [InlineData("blur")]
    public void Escape_or_leaving_the_plot_closes_the_readout(string how)
    {
        using var ctx = NewContext();
        var cut = Line(ctx, [new("Jan", 100), new("Feb", 150)]);
        cut.Find(".odc-lc-plot").Focus();
        Assert.NotEmpty(cut.FindAll(".odc-lc-tip"));

        if (how == "Escape") cut.Find(".odc-lc-plot").KeyDown("Escape");
        else cut.Find(".odc-lc-plot").Blur();

        Assert.Empty(cut.FindAll(".odc-lc-tip"));
        Assert.Equal("", LiveText(cut));
    }

    [Fact]
    public void A_pointer_opens_the_readout_silently_and_the_bubble_is_not_a_status_message()
    {
        using var ctx = NewContext();
        var cut = Line(ctx, [new("Jan", 100), new("Feb", 150)]);

        cut.FindAll("rect.odc-lc-hit")[1].MouseEnter();

        var tip = cut.Find(".odc-lc-tip");
        Assert.Null(tip.GetAttribute("role"));
        Assert.Equal("true", tip.GetAttribute("aria-hidden"));
        Assert.Equal("", LiveText(cut));
    }

    [Fact]
    public void The_text_equivalent_states_each_points_change()
    {
        using var ctx = NewContext();
        var cut = Line(ctx, [new("Jan", 100), new("Feb", 100), new("Mar", 150)], p => p.Add(c => c.TextEquivalent, true));
        cut.Find(".odc-lc-plot").Focus();
        cut.Find(".odc-lc-plot").KeyDown("ArrowLeft");
        Assert.Equal("Feb: 100, no change", cut.Find(".odc-lc-plot [aria-live=polite]").TextContent);

        Assert.Contains("Change", cut.FindAll(".odc-sr-only table thead th").Select(th => th.TextContent));
        var changes = cut.FindAll(".odc-sr-only table tbody tr").Select(r => r.QuerySelectorAll("td")[1].TextContent);
        Assert.Equal(["—", "No change", "+50"], changes);
    }

    [Fact]
    public void The_step_chart_takes_the_same_keyboard_contract_and_speaks_the_note()
    {
        using var ctx = NewContext();
        var cut = ctx.Render<OdsStepChart>(p => p
            .Add(c => c.Series,
            [
                new OdsStepPoint(new DateOnly(2025, 9, 1), 2150),
                new OdsStepPoint(new DateOnly(2026, 3, 1), 2250) { Note = "Indexed" },
                new OdsStepPoint(new DateOnly(2026, 10, 1), 2350),
            ])
            .Add(c => c.Format, Whole)
            .Add(c => c.TextEquivalent, true)
            .Add(c => c.Now, Now));
        string Live() => cut.Find(".odc-lc-plot [aria-live=polite]").TextContent;

        // Focus opens the value in force, not the scheduled entry.
        cut.Find(".odc-lc-plot").Focus();
        Assert.Equal("Mar 1, 2026: 2,250, change +100. Indexed", Live());

        cut.Find(".odc-lc-plot").KeyDown("ArrowRight");
        Assert.Equal("Oct 1, 2026 · Scheduled: 2,350, change +100", Live());
        Assert.Equal("true", cut.Find(".odc-lc-tip").GetAttribute("aria-hidden"));

        cut.Find(".odc-lc-plot").KeyDown("Escape");
        Assert.Empty(cut.FindAll(".odc-lc-tip"));

        var changes = cut.FindAll(".odc-sr-only table tbody tr").Select(r => r.QuerySelectorAll("td")[1].TextContent);
        Assert.Equal(["—", "+100", "+100"], changes);
    }
}
