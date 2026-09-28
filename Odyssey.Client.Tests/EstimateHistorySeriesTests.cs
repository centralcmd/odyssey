using Odyssey.Client.Components;
using Odyssey.Client.Pages.Finance;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// The value-history series both estimate sections draw (<see cref="EstimateVisuals.HistorySeries"/>)
/// and its axis tick. The sections themselves cannot render in a test — both load through the API
/// behind an <c>OperatingSystem.IsBrowser()</c> guard — so the shared builder is where the chart's
/// contract is pinned.
/// </summary>
public class EstimateHistorySeriesTests
{
    private static EstimateVisuals.HistoryEntry Entry(int year, int month, decimal value, string? note = null, int createdMinute = 0) =>
        new(Guid.NewGuid(), new DateTime(year, month, 1), new DateTime(year, month, 1, 0, createdMinute, 0), value, note);

    [Fact]
    public void An_empty_history_has_no_series()
    {
        Assert.Empty(EstimateVisuals.HistorySeries([], "—", "var(--chart-1)", "USD", v => v.ToString()));
    }

    [Fact]
    public void Entries_plot_oldest_first_with_ties_to_the_earliest_created_and_keep_their_notes()
    {
        var later = Entry(2025, 9, 34_000, "Latest insured valuation.");
        var first = Entry(2020, 11, 22_000, "Initial appraisal.", createdMinute: 5);
        var tie = Entry(2020, 11, 21_000, createdMinute: 1);

        var series = Assert.Single(EstimateVisuals.HistorySeries([later, first, tie], "34,000.00 USD", "var(--chart-1)", "USD", v => v.ToString()));

        Assert.Equal([21_000m, 22_000m, 34_000m], series.Points.Select(p => p.Value!.Value));
        Assert.Equal([null, "Initial appraisal.", "Latest insured valuation."], series.Points.Select(p => p.Note));
        Assert.Equal(later.Id.ToString(), series.Points[^1].Id);
        Assert.Equal(("value", "Estimated value", "34,000.00 USD", "amt:USD"), (series.Key, series.Label, series.Value, series.Group));
    }

    [Theory]
    [InlineData(950, "950")]
    [InlineData(9_999, "9,999")]
    [InlineData(34_000, "34K")]
    [InlineData(5_140_000, "5.14M")]
    [InlineData(-13_400, "−13K")]
    public void The_axis_tick_is_compact_and_carries_no_code(decimal value, string expected) =>
        Assert.Equal(expected, EstimateVisuals.CompactTick(value));
}
