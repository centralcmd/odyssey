using Bunit;
using MudBlazor.Services;
using Odyssey.Client.Components;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// The table extensions of issue #451 (Odyssey Design System · Table / RecordTable): <c>Caption</c> +
/// <c>CaptionHidden</c>, <c>RowHeader</c>, <c>OdsRecordTable.RowBusy</c>, and the rule that a row whose actions
/// yield no available item renders no menu trigger.
/// </summary>
public class OdsTableCaptionRowHeaderBusyTests
{
    private sealed record Row(int Id, string Symbol, string Name);

    private static readonly Row[] Rows = [new(1, "EQNR", "Equinor"), new(2, "NHY", "Norsk Hydro")];

    private static OdsRecordColumn<Row>[] RecordColumns =>
    [
        new() { Key = "symbol", HeaderText = "Symbol", Cell = (r, _) => b => b.AddContent(0, r.Symbol) },
        new() { Key = "name", HeaderText = "Name", Cell = (r, _) => b => b.AddContent(0, r.Name) },
    ];

    private static BunitContext NewContext()
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        return ctx;
    }

    private static IReadOnlyList<OdsMenuItem> EditItem(Row r, OdsRecordActionContext _) =>
        [new OdsMenuItem { Label = "Edit", Icon = "edit" }];

    [Fact]
    public async Task Record_table_renders_a_caption_visible_or_hidden()
    {
        await using var ctx = NewContext();
        var cut = ctx.Render<OdsRecordTable<Row>>(p => p
            .Add(t => t.Rows, Rows).Add(t => t.RowKey, r => r.Id).Add(t => t.Columns, RecordColumns)
            .Add(t => t.Caption, "Instrument prices").Add(t => t.CaptionHidden, true));

        var caption = cut.Find("table > caption");
        Assert.Equal("Instrument prices", caption.TextContent);
        Assert.Contains("odc-sr-only", caption.ClassList);

        cut.Render(p => p.Add(t => t.CaptionHidden, false));
        Assert.Contains("odc-table-caption", cut.Find("table > caption").ClassList);
    }

    [Fact]
    public async Task Record_table_without_a_caption_renders_none()
    {
        await using var ctx = NewContext();
        var cut = ctx.Render<OdsRecordTable<Row>>(p => p
            .Add(t => t.Rows, Rows).Add(t => t.RowKey, r => r.Id).Add(t => t.Columns, RecordColumns));

        Assert.Empty(cut.FindAll("caption"));
        Assert.Empty(cut.FindAll("tbody th"));
    }

    [Fact]
    public async Task Record_table_row_header_column_renders_th_scope_row()
    {
        await using var ctx = NewContext();
        var cut = ctx.Render<OdsRecordTable<Row>>(p => p
            .Add(t => t.Rows, Rows).Add(t => t.RowKey, r => r.Id).Add(t => t.Columns, RecordColumns)
            .Add(t => t.RowHeader, "symbol"));

        var heads = cut.FindAll("tbody th[scope=row]");
        Assert.Equal(["EQNR", "NHY"], heads.Select(h => h.TextContent.Trim()));
        // Without RowBusy the header cell is not a focus target.
        Assert.All(heads, h => Assert.Null(h.GetAttribute("tabindex")));
    }

    [Fact]
    public async Task A_row_whose_actions_yield_nothing_available_renders_no_menu_trigger()
    {
        await using var ctx = NewContext();
        var cut = ctx.Render<OdsRecordTable<Row>>(p => p
            .Add(t => t.Rows, Rows).Add(t => t.RowKey, r => r.Id).Add(t => t.Columns, RecordColumns)
            .Add(t => t.Actions, (r, c) => r.Id == 1
                ? EditItem(r, c)
                : [new OdsMenuItem { Label = "Edit", Disabled = true }, new OdsMenuItem { Divider = true }]));

        var rows = cut.FindAll("tbody tr");
        Assert.Single(rows[0].QuerySelectorAll(".odc-rec-menu"));
        Assert.Empty(rows[1].QuerySelectorAll(".odc-rec-menu"));
    }

    [Fact]
    public async Task A_busy_row_shows_a_named_spinner_instead_of_its_menu()
    {
        await using var ctx = NewContext();
        var busy = new HashSet<int> { 2 };
        var cut = ctx.Render<OdsRecordTable<Row>>(p => p
            .Add(t => t.Rows, Rows).Add(t => t.RowKey, r => r.Id).Add(t => t.Columns, RecordColumns)
            .Add(t => t.RowHeader, "symbol").Add(t => t.RowLabel, r => r.Symbol)
            .Add(t => t.Actions, EditItem)
            .Add(t => t.RowBusy, r => busy.Contains(r.Id)));

        var rows = cut.FindAll("tbody tr");
        Assert.Single(rows[0].QuerySelectorAll(".odc-rec-menu"));
        Assert.Empty(rows[0].QuerySelectorAll(".odc-rt-busy"));
        Assert.Empty(rows[1].QuerySelectorAll(".odc-rec-menu"));
        var spinner = rows[1].QuerySelector(".odc-rt-busy [role=status]");
        Assert.NotNull(spinner);
        Assert.Equal("Saving NHY", spinner!.GetAttribute("aria-label"));

        // With RowBusy given, the row header cell is the programmatic focus target.
        var head = rows[1].QuerySelector("th[scope=row]")!;
        Assert.Equal("-1", head.GetAttribute("tabindex"));
        Assert.True(head.HasAttribute("data-rt-focus"));

        busy.Clear();
        cut.Render(p => p.Add(t => t.RowBusy, r => busy.Contains(r.Id)));
        rows = cut.FindAll("tbody tr");
        Assert.Single(rows[1].QuerySelectorAll(".odc-rec-menu"));
        Assert.Empty(cut.FindAll(".odc-rt-busy"));
    }

    [Fact]
    public async Task A_row_going_busy_with_focus_on_its_menu_moves_focus_to_its_row_header_and_back()
    {
        await using var ctx = NewContext();
        ctx.JSInterop.Setup<bool>("odsRecFocusInMenu", _ => true).SetResult(true);
        var busy = new HashSet<int>();
        var cut = ctx.Render<OdsRecordTable<Row>>(p => p
            .Add(t => t.Rows, Rows).Add(t => t.RowKey, r => r.Id).Add(t => t.Columns, RecordColumns)
            .Add(t => t.RowHeader, "symbol").Add(t => t.Actions, EditItem)
            .Add(t => t.RowBusy, r => busy.Contains(r.Id)));
        var headId = cut.FindAll("tbody tr")[0].QuerySelector("th[scope=row]")!.Id;

        busy.Add(1);
        cut.Render(p => p.Add(t => t.RowBusy, r => busy.Contains(r.Id)));
        var focusHead = Assert.Single(ctx.JSInterop.Invocations, i => i.Identifier == "odsRecFocusEl");
        Assert.Equal(headId, focusHead.Arguments[0]);

        busy.Clear();
        cut.Render(p => p.Add(t => t.RowBusy, r => busy.Contains(r.Id)));
        var back = Assert.Single(ctx.JSInterop.Invocations, i => i.Identifier == "odsRecFocusMenuFromHead");
        Assert.Equal(headId, back.Arguments[0]);
    }

    [Fact]
    public async Task Table_renders_a_native_caption_and_row_header_cells()
    {
        await using var ctx = NewContext();
        OdsTableColumn<Row>[] columns =
        [
            new() { Key = "symbol", HeaderText = "Symbol", Cell = r => b => b.AddContent(0, r.Symbol) },
            new() { Key = "name", HeaderText = "Name", Cell = r => b => b.AddContent(0, r.Name) },
        ];
        var cut = ctx.Render<OdsTable<Row>>(p => p
            .Add(t => t.Rows, Rows).Add(t => t.Columns, columns)
            .Add(t => t.RowHeader, "symbol")
            .Add(t => t.Caption, "Watchlist, prices in NOK").Add(t => t.CaptionHidden, true));

        var caption = cut.Find("table > caption");
        Assert.Equal("Watchlist, prices in NOK", caption.TextContent);
        Assert.Contains("odc-sr-only", caption.ClassList);
        Assert.Equal(["EQNR", "NHY"], cut.FindAll("tbody th[scope=row]").Select(h => h.TextContent.Trim()));
        Assert.Equal(2, cut.FindAll("tbody td").Count);
        Assert.Empty(ctx.JSInterop.Invocations);

        cut.Render(p => p.Add(t => t.Caption, null));
        Assert.Empty(cut.FindAll("caption"));
    }

    [Fact]
    public async Task Table_sortable_header_reports_aria_sort_and_raises_the_key()
    {
        await using var ctx = NewContext();
        string? sorted = null;
        OdsTableColumn<Row>[] columns =
        [
            new() { Key = "symbol", HeaderText = "Symbol", Sortable = true, Cell = r => b => b.AddContent(0, r.Symbol) },
            new() { Key = "name", HeaderText = "Name", Sortable = true, Cell = r => b => b.AddContent(0, r.Name) },
        ];
        var cut = ctx.Render<OdsTable<Row>>(p => p
            .Add(t => t.Rows, Rows).Add(t => t.Columns, columns)
            .Add(t => t.Sort, new OdsTableSort("symbol", OdsSortDirection.Asc))
            .Add(t => t.OnSort, k => sorted = k));

        var heads = cut.FindAll("thead th");
        Assert.Equal("ascending", heads[0].GetAttribute("aria-sort"));
        Assert.Equal("none", heads[1].GetAttribute("aria-sort"));
        heads[1].QuerySelector("button")!.Click();
        Assert.Equal("name", sorted);
    }

    [Fact]
    public async Task Table_with_no_rows_renders_its_empty_content_across_every_column()
    {
        await using var ctx = NewContext();
        OdsTableColumn<Row>[] columns =
        [
            new() { Key = "symbol", HeaderText = "Symbol", Cell = r => b => b.AddContent(0, r.Symbol) },
            new() { Key = "name", HeaderText = "Name", Cell = r => b => b.AddContent(0, r.Name) },
        ];
        var cut = ctx.Render<OdsTable<Row>>(p => p
            .Add(t => t.Rows, Array.Empty<Row>()).Add(t => t.Columns, columns)
            .Add(t => t.Empty, b => b.AddContent(0, "Nothing yet")));

        var cell = cut.Find("tbody td.odc-table-empty");
        Assert.Equal("2", cell.GetAttribute("colspan"));
        Assert.Equal("Nothing yet", cell.TextContent.Trim());
    }
}
