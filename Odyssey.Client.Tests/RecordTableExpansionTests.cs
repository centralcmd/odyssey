using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using Odyssey.Client.Components;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// OdsRecordTable's opt-in expansion (Odyssey Design System · RecordTable, commit f7816ca).
///
/// <para>
/// A table given neither RenderDetail nor RenderEdit has nothing to open, so it must render no
/// chevron, ignore row clicks, and drop the pointer cursor. That is a behavioural change to the
/// component roughly nineteen list pages share, and its two halves can regress independently: a
/// chevron could come back while clicks stay inert, or — worse, because it is invisible — clicks
/// could resume toggling a row that shows no affordance at all. Both are asserted here, in both
/// directions, since the surfaces that still expand (Contacts, Users, Transactions) must be
/// unaffected.
/// </para>
/// </summary>
public class RecordTableExpansionTests
{
    private sealed record Row(string Id, string Name);

    private static readonly Row[] Rows = [new("a", "Alpha"), new("b", "Bravo")];

    private static readonly List<OdsRecordColumn<Row>> Columns =
    [
        new() { Key = "name", HeaderText = "Name", Cell = (r, _) => b => b.AddContent(0, r.Name) },
    ];

    private static BunitContext NewContext()
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        return ctx;
    }

    /// <summary>A flat table: no detail, no edit — so no chevron and no expandable affordance.</summary>
    private static IRenderedComponent<OdsRecordTable<Row>> RenderFlat(BunitContext ctx) =>
        ctx.Render<OdsRecordTable<Row>>(p => p
            .Add(t => t.Rows, Rows)
            .Add(t => t.Columns, Columns)
            .Add(t => t.RowKey, r => (object)r.Id)
            .Add(t => t.AriaLabel, "Flat"));

    /// <summary>The same table with a detail panel, i.e. the surfaces that still expand.</summary>
    private static IRenderedComponent<OdsRecordTable<Row>> RenderExpandable(BunitContext ctx) =>
        ctx.Render<OdsRecordTable<Row>>(p => p
            .Add(t => t.Rows, Rows)
            .Add(t => t.Columns, Columns)
            .Add(t => t.RowKey, r => (object)r.Id)
            .Add(t => t.AriaLabel, "Expandable")
            .Add(t => t.RenderDetail, (RenderFragment<Row>)(row => b => b.AddContent(0, $"detail:{row.Id}"))));

    [Fact]
    public void FlatTable_RendersNoExpandChevron()
    {
        using var ctx = NewContext();

        var cut = RenderFlat(ctx);

        Assert.Empty(cut.FindAll("button.odc-rec-expand"));
    }

    [Fact]
    public void ExpandableTable_StillRendersTheChevron()
    {
        using var ctx = NewContext();

        var cut = RenderExpandable(ctx);

        // One per row — the affordance the flat table drops.
        Assert.Equal(Rows.Length, cut.FindAll("button.odc-rec-expand").Count);
    }

    /// <summary>
    /// The class the pointer cursor hangs off. Without it a flat table still says "clickable" on
    /// hover, which is the visible half of the same regression.
    /// </summary>
    [Fact]
    public void FlatTable_CarriesTheFlatClass_AndExpandableDoesNot()
    {
        using var ctx = NewContext();

        Assert.Contains("odc-rec-flat", RenderFlat(ctx).Find("table").GetAttribute("class"));
        Assert.DoesNotContain("odc-rec-flat", RenderExpandable(ctx).Find("table").GetAttribute("class"));
    }

    /// <summary>
    /// The invisible half: clicking a row in a flat table must not open anything. A detail row would
    /// otherwise appear with no content and no way to close it, since there is no chevron.
    /// </summary>
    [Fact]
    public void FlatTable_IgnoresRowClicks()
    {
        using var ctx = NewContext();
        var cut = RenderFlat(ctx);

        cut.FindAll("tbody tr")[0].Click();

        Assert.Empty(cut.FindAll("tr.odc-rec-detail-row"));
        Assert.DoesNotContain("expanded", cut.FindAll("tbody tr")[0].GetAttribute("class") ?? string.Empty);
    }

    [Fact]
    public void ExpandableTable_StillOpensOnRowClick()
    {
        using var ctx = NewContext();
        var cut = RenderExpandable(ctx);

        cut.FindAll("tbody tr")[0].Click();

        Assert.Contains("detail:a", cut.Markup);
    }
}
