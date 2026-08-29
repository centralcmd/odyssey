using System.Text.RegularExpressions;
using Odyssey.Client.Components;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// Covers <see cref="OdsDonutPalette"/> — the slice filter and colour assignment behind every donut
/// and its legend.
/// </summary>
/// <remarks>
/// The chart and its legend call <c>Filter</c> and <c>ColorFor</c> independently, so any disagreement
/// between them shows up as a legend swatch that does not match its arc. Two things make that easy
/// to get wrong: the index passed to <c>ColorFor</c> must be the index <b>after</b> filtering, and
/// the palette wraps by modulo, so a seventh slice reuses the first colour rather than throwing.
/// </remarks>
public class OdsDonutPaletteTests
{
    private static OdsDonutSlice Slice(string label, decimal value, string? color = null) =>
        new() { Label = label, Value = value, Color = color };

    [Fact]
    public void The_default_palette_is_the_six_chart_tokens_in_order()
    {
        Assert.Equal(
            ["var(--chart-1)", "var(--chart-2)", "var(--chart-3)", "var(--chart-4)", "var(--chart-5)", "var(--chart-6)"],
            OdsDonutPalette.Default);
    }

    /// <summary>A token no stylesheet declares renders as no colour at all — an invisible arc.</summary>
    [Fact]
    public void Every_palette_token_is_declared_in_the_stylesheets()
    {
        var cssRoot = Path.Combine(ClientSource.Root, "wwwroot", "css");
        var declared = Directory.EnumerateFiles(cssRoot, "*.css", SearchOption.AllDirectories)
            .SelectMany(file => Regex.Matches(File.ReadAllText(file), @"(--[a-z0-9-]+)\s*:").Select(m => m.Groups[1].Value))
            .ToHashSet(StringComparer.Ordinal);

        foreach (var token in OdsDonutPalette.Default)
            Assert.Contains(Regex.Match(token, @"^var\((--[a-z0-9-]+)\)$").Groups[1].Value, declared);
    }

    /// <summary>A zero slice would draw a zero-width arc but still take a legend row and consume a
    /// palette stop, shifting every colour after it.</summary>
    [Fact]
    public void Filter_drops_non_positive_slices_and_keeps_caller_order()
    {
        var filtered = OdsDonutPalette.Filter([
            Slice("a", 5),
            Slice("zero", 0),
            Slice("b", 3),
            Slice("negative", -2),
            Slice("c", 1),
        ]);

        Assert.Equal(["a", "b", "c"], filtered.Select(s => s.Label));
    }

    [Fact]
    public void Filter_of_an_all_zero_series_is_empty_rather_than_a_blank_ring()
    {
        Assert.Empty(OdsDonutPalette.Filter([Slice("a", 0), Slice("b", 0)]));
    }

    [Fact]
    public void ColorFor_walks_the_default_palette_by_index()
    {
        for (var i = 0; i < OdsDonutPalette.Default.Count; i++)
            Assert.Equal(OdsDonutPalette.Default[i], OdsDonutPalette.ColorFor(Slice("s", 1), i, null));
    }

    /// <summary>More slices than stops must wrap, not index out of range — a category breakdown can
    /// legitimately exceed six rows.</summary>
    [Fact]
    public void ColorFor_wraps_when_there_are_more_slices_than_palette_stops()
    {
        Assert.Equal(OdsDonutPalette.Default[0], OdsDonutPalette.ColorFor(Slice("s", 1), 6, null));
        Assert.Equal(OdsDonutPalette.Default[1], OdsDonutPalette.ColorFor(Slice("s", 1), 13, null));
    }

    [Fact]
    public void ColorFor_prefers_the_slices_own_colour_over_the_palette()
    {
        Assert.Equal("var(--acct-cash)", OdsDonutPalette.ColorFor(Slice("s", 1, "var(--acct-cash)"), 3, null));
    }

    [Fact]
    public void ColorFor_uses_a_caller_supplied_palette_when_one_is_given()
    {
        string[] custom = ["red", "green"];

        Assert.Equal("red", OdsDonutPalette.ColorFor(Slice("s", 1), 0, custom));
        Assert.Equal("green", OdsDonutPalette.ColorFor(Slice("s", 1), 1, custom));
        Assert.Equal("red", OdsDonutPalette.ColorFor(Slice("s", 1), 2, custom));
    }

    /// <summary>An empty override is a caller that built its palette from an empty collection; it
    /// falls back to the default rather than dividing by zero.</summary>
    [Fact]
    public void ColorFor_falls_back_to_the_default_palette_when_the_override_is_empty()
    {
        Assert.Equal(OdsDonutPalette.Default[0], OdsDonutPalette.ColorFor(Slice("s", 1), 0, []));
    }

    /// <summary>
    /// The pairing the chart and the legend both depend on: colours are assigned over the
    /// <b>filtered</b> series. Assigning over the unfiltered one would give the legend a different
    /// colour for the same slice.
    /// </summary>
    [Fact]
    public void Colours_assigned_over_the_filtered_series_stay_contiguous()
    {
        var filtered = OdsDonutPalette.Filter([Slice("a", 5), Slice("zero", 0), Slice("b", 3)]);

        Assert.Equal(
            [OdsDonutPalette.Default[0], OdsDonutPalette.Default[1]],
            filtered.Select((s, i) => OdsDonutPalette.ColorFor(s, i, null)));
    }
}
