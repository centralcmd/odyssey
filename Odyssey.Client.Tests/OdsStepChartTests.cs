using System.Globalization;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using Odyssey.Client.Components;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// OdsStepChart and OdsTermHistoryChart (Odyssey Design System · components/StepChart,
/// components/TermHistoryChart): the in-force figure, the previous-entry delta, the indexed
/// comparison and the picker's comparison rules. The present is pinned through <c>Now</c>, so a
/// scheduled entry stays scheduled however long after today these run.
/// </summary>
public class OdsStepChartTests
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

    private static OdsStepPoint P(int year, int month, decimal value) => new(new DateOnly(year, month, 1), value);

    // ── The value in force ───────────────────────────────────────────────────────────────────

    [Fact]
    public void The_figure_is_the_value_in_force_and_never_a_scheduled_one()
    {
        using var ctx = NewContext();
        var cut = ctx.Render<OdsStepChart>(p => p
            .Add(c => c.Series, [P(2025, 9, 2150), P(2026, 3, 2250), P(2026, 10, 2350)])
            .Add(c => c.Format, Whole)
            .Add(c => c.ShowDelta, true)
            .Add(c => c.DeltaTone, OdsDeltaTone.Neutral)
            .Add(c => c.Now, Now));

        Assert.Equal("2,250", cut.Find(".odc-lc-num").TextContent.Trim());
        // In force vs the entry BEFORE it — not vs the first, and not toward the scheduled one.
        var delta = cut.Find(".odc-lc-delta");
        Assert.Equal("+100", delta.TextContent.Trim());
        Assert.Contains("neutral", delta.ClassList);
    }

    [Fact]
    public void A_scheduled_entry_is_a_hollow_dot_past_a_dashed_segment()
    {
        using var ctx = NewContext();
        var cut = ctx.Render<OdsStepChart>(p => p
            .Add(c => c.Series, [P(2025, 9, 2150), P(2026, 10, 2350)])
            .Add(c => c.Format, Whole)
            .Add(c => c.Now, Now));

        Assert.Single(cut.FindAll("path.odc-sc-line.future"));
        Assert.Single(cut.FindAll("circle[fill='var(--mud-palette-surface)']"));
        Assert.Single(cut.FindAll("line.odc-sc-now"));
    }

    [Fact]
    public void A_single_entry_series_is_a_flat_hold_with_no_delta()
    {
        using var ctx = NewContext();
        var cut = ctx.Render<OdsStepChart>(p => p
            .Add(c => c.Series, [P(2025, 9, 95)])
            .Add(c => c.Format, Whole)
            .Add(c => c.ShowDelta, true)
            .Add(c => c.Now, Now));

        Assert.Empty(cut.FindAll(".odc-lc-delta"));
        Assert.Equal("no changes yet", cut.Find(".odc-sc-leg-idx").TextContent.Trim());
    }

    [Fact]
    public void An_empty_series_renders_the_empty_line_and_no_plot()
    {
        using var ctx = NewContext();
        var cut = ctx.Render<OdsStepChart>(p => p
            .Add(c => c.Series, [])
            .Add(c => c.EmptyLabel, "Nothing recorded yet.")
            .Add(c => c.Now, Now));

        Assert.Empty(cut.FindAll("svg"));
        Assert.Contains("Nothing recorded yet.", cut.Markup);
    }

    // ── Several lines ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Several_lines_plot_indexed_change_and_the_legend_keeps_the_money()
    {
        using var ctx = NewContext();
        var cut = ctx.Render<OdsStepChart>(p => p
            .Add(c => c.Lines,
            [
                new OdsStepLine { Id = "rent", Label = "Monthly rent", Color = "var(--chart-1)", Points = [P(2025, 9, 2000), P(2026, 3, 2200)] },
                new OdsStepLine { Id = "park", Label = "Parking space", Color = "var(--chart-2)", Points = [P(2025, 9, 100), P(2026, 3, 120)] },
            ])
            .Add(c => c.Format, Whole)
            .Add(c => c.TextEquivalent, true)
            .Add(c => c.Now, Now));

        // No single figure headlines a comparison.
        Assert.Empty(cut.FindAll(".odc-lc-num"));
        Assert.All(cut.FindAll(".odc-lc-axis text").Take(4), t => Assert.EndsWith("%", t.TextContent));

        var legend = cut.FindAll(".odc-sc-leg");
        Assert.Equal(2, legend.Count);
        Assert.Equal("2,200", legend[0].QuerySelector(".odc-sc-leg-val")!.TextContent.Trim());
        Assert.Equal("+10%", legend[0].QuerySelector(".odc-sc-leg-idx")!.TextContent.Trim());
        Assert.Equal("+20%", legend[1].QuerySelector(".odc-sc-leg-idx")!.TextContent.Trim());
        // The text equivalent names the series once there is more than one.
        Assert.Contains("Series", cut.Find("table.odc-sr-only thead").TextContent);
    }

    [Fact]
    public void An_absolute_comparison_states_the_move_in_money()
    {
        using var ctx = NewContext();
        var cut = ctx.Render<OdsStepChart>(p => p
            .Add(c => c.Lines,
            [
                new OdsStepLine { Id = "a", Label = "A", Points = [P(2025, 9, 2000), P(2026, 3, 2200)] },
                new OdsStepLine { Id = "b", Label = "B", Points = [P(2025, 9, 100), P(2026, 3, 90)] },
            ])
            .Add(c => c.Scale, OdsStepScale.Absolute)
            .Add(c => c.Format, Whole)
            .Add(c => c.Now, Now));

        var moves = cut.FindAll(".odc-sc-leg-idx").Select(e => e.TextContent.Trim()).ToList();
        Assert.Equal(["+200", "−10"], moves);
    }

    [Theory]
    [InlineData(0.001, "0%")]
    [InlineData(-0.004, "0%")]
    [InlineData(0.05, "+5%")]
    [InlineData(-0.1, "−10%")]
    public void An_indexed_tick_takes_its_sign_from_the_rounded_magnitude(double value, string expected) =>
        Assert.Equal(expected, OdsStepChart.FormatIndexed(value));

    [Fact]
    public void Clipping_at_the_now_marker_interpolates_the_crossing()
    {
        (double X, double Y)[] walk = [(0, 10), (100, 10), (100, 20), (200, 20)];

        var solid = OdsStepChart.Clip(walk, 150, keepBelow: true);
        var dashed = OdsStepChart.Clip(walk, 150, keepBelow: false);

        Assert.Equal((150d, 20d), solid[^1]);
        Assert.Equal((150d, 20d), dashed[0]);
        Assert.Equal((200d, 20d), dashed[^1]);
    }

    // ── The term history chart's comparison rules ────────────────────────────────────────────

    private static OdsTermHistorySeries S(string key, string group) => new()
    {
        Key = key,
        Label = key,
        Group = group,
        Points = [P(2025, 9, 1)],
    };

    private static readonly IReadOnlyList<OdsTermHistorySeries> List =
        [S("rent", "amt:USD"), S("parking", "amt:USD"), S("cleaning", "amt:USD"), S("water", "amt:USD"), S("arrears", "pct"), S("fee-eur", "amt:EUR")];

    [Fact]
    public void A_series_measured_differently_replaces_the_selection()
    {
        var next = OdsTermHistoryChart.NextSelection(["rent", "parking"], ["rent", "parking", "arrears"], List, cap: 4);
        Assert.Equal(["arrears"], next);

        // Another currency is another unit too.
        Assert.Equal(["fee-eur"], OdsTermHistoryChart.NextSelection(["rent"], ["rent", "fee-eur"], List, cap: 4));
    }

    [Fact]
    public void A_series_sharing_the_unit_joins_the_selection_up_to_the_cap()
    {
        Assert.Equal(["rent", "parking"], OdsTermHistoryChart.NextSelection(["rent"], ["rent", "parking"], List, cap: 4));
        Assert.Equal(["rent", "parking"], OdsTermHistoryChart.NextSelection(["rent", "parking"], ["rent", "parking", "water"], List, cap: 2));
    }

    [Fact]
    public void Removing_the_last_line_keeps_it()
    {
        Assert.Equal(["rent"], OdsTermHistoryChart.NextSelection(["rent"], [], List, cap: 4));
        Assert.Equal(["parking"], OdsTermHistoryChart.NextSelection(["rent", "parking"], ["parking"], List, cap: 4));
    }

    [Fact]
    public void A_lone_series_gets_no_picker_but_keeps_the_axis_toggle()
    {
        using var ctx = NewContext();
        var cut = ctx.Render<OdsTermHistoryChart>(p => p
            .Add(c => c.Series, [S("rent", "amt:USD")])
            .Add(c => c.Now, Now));

        Assert.Empty(cut.FindAll(".odc-ms-trigger"));
        Assert.Contains("Change", cut.Markup);
        Assert.Contains("Value", cut.Markup);
    }

    [Fact]
    public async Task Several_series_render_a_quiet_glyph_picker_opening_on_the_first()
    {
        // The picker is a MudMenu, whose services dispose asynchronously only.
        await using var ctx = NewContext();
        var cut = ctx.Render<OdsTermHistoryChart>(p => p
            .Add(c => c.Series, [S("rent", "amt:USD"), S("parking", "amt:USD")])
            .Add(c => c.Now, Now));

        var trigger = cut.Find(".odc-ms-trigger");
        Assert.Contains("quiet", trigger.ClassList);
        Assert.Equal("§", trigger.QuerySelector(".odc-ms-glyph")!.TextContent);
        Assert.Equal("1", trigger.QuerySelector(".odc-ms-count")!.TextContent);
        Assert.Single(cut.FindAll(".odc-sc-leg"));
    }

    /// <summary>
    /// An option row reads name · value · direction, the value in its direction's colour — the
    /// OptionContent the chart hands its multi-select, rendered by opening the picker.
    /// </summary>
    [Fact]
    public async Task A_picker_row_reads_name_value_and_direction()
    {
        await using var ctx = NewContext();
        var cut = ctx.Render<PickerHost>();

        cut.Find(".odc-ms-trigger").Click();

        var rows = cut.FindAll(".odc-ms-opt");
        Assert.Equal(2, rows.Count);
        Assert.Contains("Monthly rent", rows[0].TextContent, StringComparison.Ordinal);
        var value = rows[0].QuerySelector(".odc-thc-val")!;
        Assert.Equal("2,250.00 USD", value.TextContent);
        Assert.Contains("color:var(--finance-expense)", value.GetAttribute("style"), StringComparison.Ordinal);
        Assert.Contains("Outgoing", rows[0].QuerySelector(".odc-thc-dir")!.TextContent, StringComparison.Ordinal);
        // Only the plotted row's dash carries its line colour.
        Assert.NotNull(rows[0].QuerySelector(".odc-opt-icon")!.GetAttribute("style"));
        Assert.Null(rows[1].QuerySelector(".odc-opt-icon")!.GetAttribute("style"));
    }

    private sealed class PickerHost : Microsoft.AspNetCore.Components.ComponentBase
    {
        protected override void BuildRenderTree(Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder builder)
        {
            builder.OpenComponent<MudBlazor.MudPopoverProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<OdsTermHistoryChart>(1);
            builder.AddComponentParameter(2, nameof(OdsTermHistoryChart.Series), (IReadOnlyList<OdsTermHistorySeries>)
            [
                S("rent", "amt:USD") with { Label = "Monthly rent", Value = "2,250.00 USD", ToneLabel = "Outgoing", ToneColor = "var(--finance-expense)", Color = "var(--finance-expense)" },
                S("parking", "amt:USD") with { Label = "Parking space", Value = "95.00 USD" },
            ]);
            builder.AddComponentParameter(3, nameof(OdsTermHistoryChart.Now), (DateTime?)Now);
            builder.CloseComponent();
        }
    }

    [Theory]
    [InlineData(2150, 107.5)]
    [InlineData(1, 0.05)]
    [InlineData(0.08, 0.005)]
    [InlineData(-0.5, 0.005)]
    public void A_flat_series_gets_a_band_proportional_to_its_value(double value, double expected) =>
        Assert.Equal(expected, OdsStepChart.FlatBand(value), 6);

    [Fact]
    public void Nothing_renders_when_no_series_has_a_point()
    {
        using var ctx = NewContext();
        var cut = ctx.Render<OdsTermHistoryChart>(p => p
            .Add(c => c.Series, [new OdsTermHistorySeries { Key = "x", Label = "x", Points = [] }]));

        Assert.Equal("", cut.Markup.Trim());
    }
}
