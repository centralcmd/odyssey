using Bunit;
using MudBlazor.Services;
using Odyssey.Client.Components;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// <c>OdsRecordTable.RowLabel</c> (issue #410 F1, X7; Odyssey Design System · RecordTable <c>rowLabel</c>): a named
/// row's toggle says which record it opens and controls a labelled group; an unnamed one is exactly as before.
/// </summary>
public class OdsRecordTableRowLabelTests
{
    private sealed record Row(int Id, string Name);

    private static IRenderedComponent<OdsRecordTable<Row>> Render(BunitContext ctx, Func<Row, string>? label) =>
        ctx.Render<OdsRecordTable<Row>>(p =>
        {
            p.Add(t => t.Rows, [new Row(1, "Alpha")])
                .Add(t => t.RowKey, r => r.Id)
                .Add(t => t.Columns, [new OdsRecordColumn<Row> { Key = "name", HeaderText = "Name", Cell = (r, _) => b => b.AddContent(0, r.Name) }])
                .Add(t => t.RenderDetail, r => b => b.AddContent(0, $"Detail of {r.Name}"));
            if (label is not null)
                p.Add(t => t.RowLabel, label);
        });

    [Fact]
    public async Task A_named_row_names_its_toggle_and_controls_a_labelled_group()
    {
        await using var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        var cut = Render(ctx, r => $"{r.Name} run");

        var toggle = cut.Find("button.odc-rec-expand");
        Assert.Equal("Show details for Alpha run", toggle.GetAttribute("aria-label"));
        toggle.Click();

        toggle = cut.Find("button.odc-rec-expand");
        Assert.Equal("Hide details for Alpha run", toggle.GetAttribute("aria-label"));
        Assert.Equal("true", toggle.GetAttribute("aria-expanded"));
        var group = cut.Find($"#{toggle.GetAttribute("aria-controls")}");
        Assert.Equal("group", group.GetAttribute("role"));
        Assert.Equal("Details for Alpha run", group.GetAttribute("aria-label"));
        Assert.Contains("Detail of Alpha", group.TextContent);
        Assert.Null(cut.Find("tr.odc-rec-detail-row").GetAttribute("id"));
    }

    [Fact]
    public async Task An_unnamed_row_keeps_its_toggle_and_detail_row_as_before()
    {
        await using var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        var cut = Render(ctx, null);

        cut.Find("button.odc-rec-expand").Click();

        var toggle = cut.Find("button.odc-rec-expand");
        Assert.Equal("Collapse row", toggle.GetAttribute("aria-label"));
        Assert.Equal(toggle.GetAttribute("aria-controls"), cut.Find("tr.odc-rec-detail-row").GetAttribute("id"));
        Assert.Empty(cut.FindAll("[role=group]"));
    }
}
