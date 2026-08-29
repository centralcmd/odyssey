using Odyssey.Client.Components;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// Covers <see cref="OdsBreakdown"/>'s two shapes of summary-grid row, and in particular that the
/// count-based overloads added by issue #372 — which read figures a summary endpoint already
/// computed — produce exactly what the item-counting originals did. Four page headers switched from
/// one to the other, so any divergence is a silent rendering change.
/// </summary>
public class OdsBreakdownTests
{
    private sealed record Row(string Status, string Type);

    private static readonly OdsBreakdownDef<string>[] StatusDefs =
    [
        new("open", "Open", "income", "lock_open"),
        new("closed", "Closed", "expense", "lock"),
        new("archived", "Archived", "outline", "inventory_2"),
    ];

    private static readonly string[] TypeOrder = ["cash", "savings", "loan"];

    private static (string Icon, string Color, string Label) Visual(string type) =>
        (type + "_icon", "var(--x)", type.ToUpperInvariant());

    [Fact]
    public void StatusRows_shows_every_defined_status_including_the_empty_ones()
    {
        var rows = OdsBreakdown.StatusRows([new Row("open", "cash"), new Row("open", "savings")], r => r.Status, StatusDefs);

        Assert.Equal(["Open", "Closed", "Archived"], rows.Select(r => r.Label));
        Assert.Equal([2, 0, 0], rows.Select(r => r.Count));
        Assert.Equal(OdsBreakdown.Tone("income"), rows[0].IconColor);
    }

    [Fact]
    public void TypeRows_keeps_only_the_present_types_in_registry_order()
    {
        var rows = OdsBreakdown.TypeRows(
            [new Row("open", "loan"), new Row("open", "cash"), new Row("open", "cash")],
            r => r.Type, TypeOrder, Visual);

        // "savings" has no rows, so it is omitted entirely; the rest follow TypeOrder, not input order.
        Assert.Equal(["CASH", "LOAN"], rows.Select(r => r.Label));
        Assert.Equal([2, 1], rows.Select(r => r.Count));
        Assert.Equal("cash_icon", rows[0].Icon);
    }

    [Fact]
    public void CountedStatusRows_matches_the_item_counting_original()
    {
        var items = new[] { new Row("open", "cash"), new Row("archived", "cash"), new Row("open", "loan") };
        var counted = new Dictionary<string, int> { ["open"] = 2, ["archived"] = 1 };

        var fromItems = OdsBreakdown.StatusRows(items, r => r.Status, StatusDefs);
        var fromCounts = OdsBreakdown.CountedStatusRows<string>(k => counted.GetValueOrDefault(k), StatusDefs);

        Assert.Equal(
            fromItems.Select(r => (r.Key, r.Label, r.Icon, r.IconColor, r.Count)),
            fromCounts.Select(r => (r.Key, r.Label, r.Icon, r.IconColor, r.Count)));
    }

    [Fact]
    public void CountedTypeRows_matches_the_item_counting_original()
    {
        var items = new[] { new Row("open", "loan"), new Row("open", "cash"), new Row("open", "cash") };
        var counted = new Dictionary<string, int> { ["loan"] = 1, ["cash"] = 2 };

        var fromItems = OdsBreakdown.TypeRows(items, r => r.Type, TypeOrder, Visual);
        var fromCounts = OdsBreakdown.CountedTypeRows(counted, TypeOrder, Visual);

        Assert.Equal(
            fromItems.Select(r => (r.Key, r.Label, r.Icon, r.IconColor, r.Count)),
            fromCounts.Select(r => (r.Key, r.Label, r.Icon, r.IconColor, r.Count)));
    }

    /// <summary>A summary that hasn't loaded yet renders the defined statuses at zero, not nothing —
    /// the tile keeps its shape instead of flashing its "No accounts." empty text.</summary>
    [Fact]
    public void CountedStatusRows_with_no_counts_still_renders_every_status_at_zero()
    {
        var rows = OdsBreakdown.CountedStatusRows<string>(_ => 0, StatusDefs);

        Assert.Equal(3, rows.Count);
        Assert.All(rows, r => Assert.Equal(0, r.Count));
    }

    [Fact]
    public void CountedTypeRows_with_no_counts_renders_nothing()
    {
        Assert.Empty(OdsBreakdown.CountedTypeRows(new Dictionary<string, int>(), TypeOrder, Visual));
    }
}
