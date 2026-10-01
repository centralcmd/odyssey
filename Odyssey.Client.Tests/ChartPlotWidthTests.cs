using System.Globalization;
using System.Text.RegularExpressions;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using Microsoft.JSInterop.Infrastructure;
using Moq;
using MudBlazor.Services;
using Odyssey.Client.Components;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// The line and step charts set their viewBox width to the plot's measured pixel width (issue #274,
/// Odyssey Design System · LineChart / StepChart).
///
/// <para>
/// The axis text is sized in SVG user units (<c>.odc-lc-axis</c> is <c>--fs-micro</c>, 10 units). Under
/// the old fixed <c>viewBox="0 0 1000 252"</c> a card rendered 300px wide scaled every unit by 0.3, so
/// the three-up <c>/tax-statements</c> overview drew its 10px labels at about 3px. A label's rendered
/// size is <c>10 × renderedWidth / viewBoxWidth</c>; with the viewBox tracking the measured width that
/// ratio is 1, so the label renders at a true 10px at any width.
/// </para>
///
/// <para>
/// These run the .NET half — <see cref="PlotWidthObserver"/> and the charts — against bUnit's JS
/// interop, delivering a width the way <c>plot-width.js</c> would. The module itself, a real
/// <c>ResizeObserver</c> and the rendered pixel size of the labels are exercised in a browser by
/// <c>Odyssey.E2ETests.ChartAxisLabelSizeTests</c>.
/// </para>
/// </summary>
public class ChartPlotWidthTests
{
    private const string Module = "./js/plot-width.js";

    private static readonly DateTime Now = new(2026, 6, 15, 12, 0, 0, DateTimeKind.Utc);

    private static BunitContext NewContext(out BunitJSModuleInterop module)
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        module = ctx.JSInterop.SetupModule(Module);
        ctx.Services.AddMudServices();
        return ctx;
    }

    private static IReadOnlyList<OdsLinePoint> LineSeries =>
    [
        new("2022", 1_200_000m), new("2023", 1_350_000m), new("2024", 1_481_000m),
    ];

    private static IReadOnlyList<OdsStepPoint> StepSeries =>
    [
        new(new DateOnly(2025, 9, 1), 2150m), new(new DateOnly(2026, 3, 1), 2250m),
    ];

    private static string Whole(decimal n) => n.ToString("#,##0", CultureInfo.InvariantCulture);

    /// <summary>Reports <paramref name="width"/> through the observer the chart registered, as the browser would.</summary>
    private static async Task ReportWidth<T>(IRenderedComponent<T> cut, BunitJSModuleInterop module, double width)
        where T : IComponent
    {
        cut.WaitForAssertion(() => Assert.NotEmpty(module.Invocations["observe"]));
        var observer = (DotNetObjectReference<PlotWidthObserver>)module.Invocations["observe"].Last().Arguments[1]!;
        await cut.InvokeAsync(() => observer.Value.OnPlotWidth(width));
    }

    private static double[] ViewBox<T>(IRenderedComponent<T> cut) where T : IComponent =>
        [.. cut.Find("svg.odc-line-svg").GetAttribute("viewBox")!
            .Split(' ').Select(v => double.Parse(v, CultureInfo.InvariantCulture))];

    private static IRenderedComponent<OdsLineChart> RenderLine(BunitContext ctx) =>
        ctx.Render<OdsLineChart>(p => p
            .Add(c => c.Series, LineSeries)
            .Add(c => c.Format, Whole));

    private static IRenderedComponent<OdsStepChart> RenderStep(BunitContext ctx) =>
        ctx.Render<OdsStepChart>(p => p
            .Add(c => c.Series, StepSeries)
            .Add(c => c.Format, Whole)
            .Add(c => c.Now, Now));

    public static TheoryData<string, double> Widths => new()
    {
        { nameof(OdsLineChart), 300 }, { nameof(OdsLineChart), 352 }, { nameof(OdsLineChart), 1180 },
        { nameof(OdsStepChart), 300 }, { nameof(OdsStepChart), 352 }, { nameof(OdsStepChart), 1180 },
    };

    /// <summary>Renders <paramref name="chart"/>, reports <paramref name="width"/>, and hands back its markup queries.</summary>
    private static async Task<(double[] ViewBox, Func<string, IReadOnlyList<AngleSharp.Dom.IElement>> FindAll)> Measured(
        string chart, BunitContext ctx, BunitJSModuleInterop module, double width)
    {
        if (chart == nameof(OdsLineChart))
        {
            var line = RenderLine(ctx);
            await ReportWidth(line, module, width);
            return (ViewBox(line), sel => [.. line.FindAll(sel)]);
        }

        var step = RenderStep(ctx);
        await ReportWidth(step, module, width);
        return (ViewBox(step), sel => [.. step.FindAll(sel)]);
    }

    /// <summary>
    /// The viewBox width becomes the reported width, at about 300px (one of the three-up
    /// <c>/tax-statements</c> cards) as at full width. That equality is what makes a 10-unit label a
    /// 10px one; the browser tier measures the pixels themselves.
    /// </summary>
    [Theory]
    [MemberData(nameof(Widths))]
    public async Task The_viewbox_width_is_the_measured_width(string chart, double width)
    {
        await using var ctx = NewContext(out var module);
        var (viewBox, _) = await Measured(chart, ctx, module, width);

        Assert.Equal([0, 0, width, OdsLineChart.ViewBoxHeight], viewBox);
    }

    /// <summary>
    /// The auto x stride follows the measured width, through the same observer callback the browser
    /// drives: a phone-width dashboard chart drew eight overlapping <c>Apr '16</c> labels because the
    /// stride was a count while the axis had become pixel-sized. Narrowing must thin the labels until
    /// neighbours sit a label apart, and widening again must restore the count rule's density — the
    /// re-stride happens on the width callback, not only on a parameter change.
    /// </summary>
    [Fact]
    public async Task The_auto_x_stride_rethins_on_every_reported_width()
    {
        await using var ctx = NewContext(out var module);
        IReadOnlyList<OdsLinePoint> quarters = [.. Enumerable.Range(0, 44).Select(i =>
            new OdsLinePoint($"{(i % 4) switch { 0 => "Jan", 1 => "Apr", 2 => "Jul", _ => "Oct" }} '{16 + i / 4:00}", 1000m + i))];
        var cut = ctx.Render<OdsLineChart>(p => p
            .Add(c => c.Series, quarters)
            .Add(c => c.Format, Whole)
            .Add(c => c.XTickEveryAuto, true));

        double[] XLabels() => [.. cut.FindAll("g.odc-lc-axis text")
            .Where(t => t.GetAttribute("y") == "238")
            .Select(t => double.Parse(t.GetAttribute("x")!, CultureInfo.InvariantCulture))];

        var unmeasured = XLabels().Length;

        await ReportWidth(cut, module, 280);
        var narrow = XLabels();
        var need = 7 * OdsLineChart.AxisCharWidth + OdsLineChart.XLabelGap;
        Assert.True(narrow.Length < unmeasured, $"{narrow.Length} labels at 280px, {unmeasured} unmeasured.");
        Assert.All(narrow.Zip(narrow.Skip(1)), pair => Assert.True(pair.Second - pair.First >= need,
            $"labels at {pair.First} and {pair.Second} are closer than {need}."));

        await ReportWidth(cut, module, 1180);
        Assert.Equal(unmeasured, XLabels().Length);
    }

    /// <summary>
    /// Everything drawn sits inside the measured box: the gridlines end at <c>W − 32</c> rather than the
    /// old fixed 968, which at 300px would have drawn the plot three times past the right edge. The step
    /// chart lays its paths out once per parameter set, so this also proves a new width re-runs that.
    /// </summary>
    [Theory]
    [InlineData(nameof(OdsLineChart))]
    [InlineData(nameof(OdsStepChart))]
    public async Task The_plot_ends_inside_the_measured_width(string chart)
    {
        await using var ctx = NewContext(out var module);
        var (_, findAll) = await Measured(chart, ctx, module, 300);

        var x1 = 300 - OdsLineChart.PlotRightInset;
        foreach (var line in findAll("svg.odc-line-svg > g > line"))
            Assert.Equal(x1, double.Parse(line.GetAttribute("x2")!, CultureInfo.InvariantCulture));

        // Every coordinate in every drawn path, x and y alike, stays inside the 300 × 252 box.
        var numbers = findAll("svg.odc-line-svg path")
            .SelectMany(p => Regex.Matches(p.GetAttribute("d")!, @"-?\d+(\.\d+)?").Select(m => double.Parse(m.Value, CultureInfo.InvariantCulture)));
        Assert.All(numbers, n => Assert.InRange(n, 0, 300));
    }

    /// <summary>
    /// The y gutter still fits its widest label at the narrow width — the labels are now a true 10px, so a
    /// gutter sized for the old scaled-down text would clip them. A label's estimated left end must stay
    /// inside the viewBox.
    /// </summary>
    [Fact]
    public async Task The_y_labels_fit_their_gutter_at_the_narrow_width()
    {
        await using var ctx = NewContext(out var module);
        var cut = RenderLine(ctx);

        await ReportWidth(cut, module, 300);

        foreach (var label in cut.FindAll("g.odc-lc-axis text[text-anchor=end]"))
        {
            var right = double.Parse(label.GetAttribute("x")!, CultureInfo.InvariantCulture);
            Assert.True(right - label.TextContent.Length * OdsLineChart.AxisCharWidth >= 0,
                $"'{label.TextContent}' would start left of the viewBox");
        }
    }

    /// <summary>
    /// The hover readout is positioned as a percentage of the plot width. It used to be <c>Sx / 10</c>,
    /// which assumed a 1000-unit width; at 300 the last point would have read 26.8% instead of 89.3%.
    /// </summary>
    [Fact]
    public async Task The_readout_is_placed_as_a_share_of_the_measured_width()
    {
        await using var ctx = NewContext(out var module);
        var cut = RenderLine(ctx);
        await ReportWidth(cut, module, 300);

        cut.Find(".odc-lc-plot").Focus();

        var lastX = 300 - OdsLineChart.PlotRightInset;
        var expected = (lastX / 300 * 100).ToString("0.##", CultureInfo.InvariantCulture);
        Assert.StartsWith($"left:{expected}%", cut.Find(".odc-lc-tip").GetAttribute("style"));
    }

    /// <summary>
    /// Before the plot is measured — prerender, or no JS runtime — the chart keeps the 1000-unit viewBox,
    /// so it still renders. And no width is ever clamped to a minimum: a clamp brings the scaling back.
    /// </summary>
    [Fact]
    public async Task An_unmeasured_chart_keeps_the_default_box_and_a_tiny_width_is_not_clamped()
    {
        await using var ctx = NewContext(out var module);
        var cut = RenderLine(ctx);

        Assert.Equal(OdsLineChart.DefaultViewBoxWidth, ViewBox(cut)[2]);

        await ReportWidth(cut, module, 120);
        Assert.Equal(120, ViewBox(cut)[2]);
    }

    /// <summary>
    /// An empty chart renders no plot box, so there is nothing to observe; when data arrives the new box
    /// is observed. Without this an empty → data swap would leave the chart at the 1000-unit default.
    /// </summary>
    [Fact]
    public async Task A_plot_that_appears_after_an_empty_render_is_observed()
    {
        await using var ctx = NewContext(out var module);
        var cut = ctx.Render<OdsLineChart>(p => p.Add(c => c.Series, []).Add(c => c.Format, Whole));

        Assert.Empty(module.Invocations["observe"]);

        cut.Render(p => p.Add(c => c.Series, LineSeries));

        cut.WaitForAssertion(() => Assert.Single(module.Invocations["observe"]));
    }

    /// <summary>
    /// The CSS half (design-system components.css). Without <c>contain: inline-size</c> the SVG's viewBox
    /// width feeds back into the plot's min-content width, so a shrinking grid track could not shrink
    /// the chart and a residual scale error would remain. And no chart may go back to a literal width.
    /// </summary>
    [Fact]
    public void The_plot_box_is_inline_size_contained_and_no_chart_hardcodes_its_width()
    {
        var css = File.ReadAllText(Path.Combine(ClientSource.Root, "wwwroot", "css", "odyssey-components.css"));
        var plot = Regex.Match(css, @"^\.odc-lc-plot \{([^}]*)\}", RegexOptions.Multiline);
        Assert.True(plot.Success, ".odc-lc-plot rule not found");
        Assert.Contains("contain: inline-size", plot.Groups[1].Value, StringComparison.Ordinal);
        Assert.Matches(@"\.odc-lc-axis \{ font: var\(--fw-regular\) var\(--fs-micro\)", css);

        foreach (var file in new[] { "OdsLineChart.razor", "OdsStepChart.razor", "OdsLineChart.razor.cs", "OdsStepChart.razor.cs" })
        {
            var source = File.ReadAllText(Path.Combine(ClientSource.Root, "Components", file));
            Assert.DoesNotContain("viewBox=\"0 0 1000", source, StringComparison.Ordinal);
            Assert.DoesNotContain("X1 = 968", source, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The step chart lays its paths out once per parameter set, so a new width re-runs the layout —
    /// and that re-layout must keep an open readout, re-placed at the new width, rather than closing it
    /// the way a parameter change does.
    /// </summary>
    [Fact]
    public async Task A_resize_keeps_the_step_chart_readout_open_and_moves_it()
    {
        await using var ctx = NewContext(out var module);
        var cut = RenderStep(ctx);
        await ReportWidth(cut, module, 1180);

        cut.Find(".odc-lc-plot").Focus();
        var before = cut.Find(".odc-lc-tip");
        var (beforeStyle, beforeText) = (before.GetAttribute("style"), before.TextContent);

        await ReportWidth(cut, module, 300);

        var after = cut.Find(".odc-lc-tip");
        Assert.Equal(beforeText, after.TextContent);   // the same entry…
        Assert.NotEqual(beforeStyle, after.GetAttribute("style"));   // …re-placed at the new width
    }

    [Fact]
    public async Task A_resize_keeps_the_line_chart_readout_open()
    {
        await using var ctx = NewContext(out var module);
        var cut = RenderLine(ctx);
        await ReportWidth(cut, module, 1180);
        cut.Find(".odc-lc-plot").Focus();

        await ReportWidth(cut, module, 300);

        Assert.Single(cut.FindAll(".odc-lc-tip"));
    }

    /// <summary>A zero width (the plot under <c>display:none</c>) is never applied.</summary>
    [Fact]
    public async Task A_zero_width_is_ignored()
    {
        await using var ctx = NewContext(out var module);
        var cut = RenderLine(ctx);

        await ReportWidth(cut, module, 0);

        Assert.Equal(OdsLineChart.DefaultViewBoxWidth, ViewBox(cut)[2]);
    }
}
