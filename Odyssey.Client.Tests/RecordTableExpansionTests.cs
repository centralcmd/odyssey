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

    /// <summary>
    /// The chevron names the panel it opens (Odyssey Design System · RecordTable): <c>aria-controls</c>
    /// points at the detail row's <c>id</c> while expanded and is absent while collapsed, when there is no
    /// such row. Two tables on one page must never share an id.
    /// </summary>
    [Fact]
    public void ExpandChevron_ControlsTheDetailRowWhileExpanded()
    {
        using var ctx = NewContext();
        var cut = RenderExpandable(ctx);

        var chevron = cut.Find("button.odc-rec-expand");
        Assert.Equal("false", chevron.GetAttribute("aria-expanded"));
        Assert.Null(chevron.GetAttribute("aria-controls"));

        chevron.Click();

        chevron = cut.Find("button.odc-rec-expand");
        Assert.Equal("true", chevron.GetAttribute("aria-expanded"));
        var controls = chevron.GetAttribute("aria-controls");
        Assert.False(string.IsNullOrEmpty(controls));
        var detail = cut.Find($"[id='{controls}']");
        Assert.Contains("odc-rec-detail-row", detail.ClassList);
        Assert.Contains("detail:a", detail.TextContent);

        var other = RenderExpandable(ctx);
        other.Find("button.odc-rec-expand").Click();
        Assert.NotEqual(controls, other.Find("button.odc-rec-expand").GetAttribute("aria-controls"));
    }

    /// <summary>The row itself is never the control — no role, tabindex or aria-expanded on the &lt;tr&gt;.</summary>
    [Fact]
    public void BodyRow_CarriesNoControlSemantics()
    {
        using var ctx = NewContext();
        var row = RenderExpandable(ctx).FindAll("tbody tr")[0];

        Assert.Null(row.GetAttribute("role"));
        Assert.Null(row.GetAttribute("tabindex"));
        Assert.Null(row.GetAttribute("aria-expanded"));
    }
}
