using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using Odyssey.Client.Components;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// Issue #252: OdsFilesTable and OdsRecordTable used to close the editor and flash "Saved" whatever
/// the host's save did, so a failed write showed an error toast and a green "Saved" chip at the same
/// moment and discarded the user's edits with the dialog. The host now reports the outcome through
/// <see cref="OdsRecordSaveEventArgs.Fail"/>; these tests pin all three outcomes on both tables —
/// success closes and flashes, a reported failure keeps the editor open and does not flash, and a
/// throwing handler is surfaced rather than swallowed and does not flash either.
/// </summary>
public class TableSaveOutcomeTests
{
    static TableSaveOutcomeTests() => BunitContext.DefaultWaitTimeout = TimeSpan.FromSeconds(10);

    private static BunitContext NewContext(out IRenderedComponent<MudDialogProvider> dialogs)
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        dialogs = ctx.Render<MudDialogProvider>();
        return ctx;
    }

    // ── OdsFilesTable ────────────────────────────────────────────────────────

    private static readonly OdsFilesRow[] Files =
    [
        new()
        {
            Id = "f-1",
            Name = "statement-2026-04.pdf",
            Kind = "Statement",
            SizeBytes = 25_800,
            UploadedAtUtc = new DateTime(2026, 8, 31, 12, 0, 0, DateTimeKind.Utc),
        },
    ];

    private static readonly IReadOnlyList<OdsOption> Kinds = [new("Statement", "Statement")];

    private static readonly IReadOnlyList<OdsMenuItem> HostActions =
        [new() { Icon = "download", Label = "Download" }];

    private static IRenderedComponent<OdsFilesTable> RenderFilesTable(
        BunitContext ctx, Action<OdsRecordSaveEventArgs> onSave) =>
        ctx.Render<OdsFilesTable>(p => p
            .Add(t => t.Files, Files)
            .Add(t => t.Kinds, Kinds)
            .Add(t => t.AriaLabel, "Account files")
            .Add(t => t.Actions, _ => HostActions)
            .Add(t => t.OnSave, EventCallback.Factory.Create(new object(), onSave)));

    /// <summary>Opens the row's Edit dialog, types a new name, and returns the dialog's Save button.</summary>
    private static AngleSharp.Dom.IElement OpenEditAndRename(
        BunitContext ctx,
        IRenderedComponent<OdsFilesTable> cut,
        IRenderedComponent<MudDialogProvider> dialogs,
        string newName)
    {
        var popover = ctx.Render<MudPopoverProvider>();
        cut.Find("button[aria-label='Row actions']").Click();
        popover.WaitForElement("div.mud-menu-item");
        var labels = popover.FindAll(".odc-menu-item-body > span:first-child").Select(e => e.TextContent.Trim()).ToList();
        popover.FindAll("div.mud-menu-item")[labels.IndexOf("Edit")].Click();

        cut.WaitForAssertion(() => Assert.True(cut.FindComponent<OdsFilesEditDialog>().Instance.Open));
        var input = dialogs.WaitForElement("input");
        input.Input(newName);
        dialogs.Find("input").Blur();
        return SaveButton(dialogs);
    }

    private static AngleSharp.Dom.IElement SaveButton(IRenderedComponent<MudDialogProvider> dialogs) =>
        dialogs.FindAll("button").Single(b => b.TextContent.Contains("Save changes", StringComparison.Ordinal));

    private static bool FilesRowShowsSaved(IRenderedComponent<OdsFilesTable> cut) =>
        cut.Find("tbody").TextContent.Contains("Saved", StringComparison.Ordinal);

    [Fact]
    public async Task FilesTable_Success_ClosesTheDialog_AndFlashesSaved()
    {
        await using var ctx = NewContext(out var dialogs);
        OdsRecordSaveEventArgs? raised = null;
        var cut = RenderFilesTable(ctx, args => raised = args);

        OpenEditAndRename(ctx, cut, dialogs, "renamed.pdf").Click();

        cut.WaitForAssertion(() => Assert.False(cut.FindComponent<OdsFilesEditDialog>().Instance.Open));
        cut.WaitForAssertion(() => Assert.True(FilesRowShowsSaved(cut)));
        Assert.Equal("f-1", raised!.Key);
        Assert.Equal("renamed.pdf", Assert.IsType<OdsFileEdit>(raised.Patch).Name);
        Assert.False(raised.Failed);
    }

    [Fact]
    public async Task FilesTable_ReportedFailure_KeepsTheDialogOpenWithTheEdits_AndDoesNotFlash()
    {
        await using var ctx = NewContext(out var dialogs);
        var calls = 0;
        var cut = RenderFilesTable(ctx, args =>
        {
            calls++;
            args.Fail();
        });

        OpenEditAndRename(ctx, cut, dialogs, "renamed.pdf").Click();

        cut.WaitForAssertion(() => Assert.Equal(1, calls));
        var dialog = cut.FindComponent<OdsFilesEditDialog>();
        Assert.True(dialog.Instance.Open);
        Assert.False(FilesRowShowsSaved(cut));

        // The user's edit is still in the field, so a retry sends it again rather than the old name.
        Assert.Equal("renamed.pdf", dialogs.Find("input").GetAttribute("value"));
        SaveButton(dialogs).Click();
        cut.WaitForAssertion(() => Assert.Equal(2, calls));
        Assert.True(cut.FindComponent<OdsFilesEditDialog>().Instance.Open);
    }

    [Fact]
    public async Task FilesTable_ThrowingHandler_IsNotSwallowed_AndDoesNotFlashOrClose()
    {
        await using var ctx = NewContext(out var dialogs);
        var cut = RenderFilesTable(ctx, _ => throw new InvalidOperationException("boom"));

        var save = OpenEditAndRename(ctx, cut, dialogs, "renamed.pdf");

        var thrown = Assert.ThrowsAny<Exception>(() => save.Click());
        Assert.Contains("boom", thrown.ToString(), StringComparison.Ordinal);
        Assert.True(cut.FindComponent<OdsFilesEditDialog>().Instance.Open);
        Assert.False(FilesRowShowsSaved(cut));
    }

    // ── OdsRecordTable ───────────────────────────────────────────────────────

    private sealed record Row(string Id, string Name);

    private static readonly Row[] Rows = [new("a", "Alpha")];

    private static readonly List<OdsRecordColumn<Row>> Columns =
    [
        new()
        {
            Key = "name",
            HeaderText = "Name",
            Cell = (r, c) => b => b.AddContent(0, c.JustSaved ? $"{r.Name} [saved]" : r.Name),
        },
    ];

    private sealed class Captured
    {
        public OdsRecordActionContext? Actions { get; set; }

        public OdsRecordEditContext? Edit { get; set; }
    }

    private static IRenderedComponent<OdsRecordTable<Row>> RenderRecordTable(
        BunitContext ctx, Captured captured, Action<OdsRecordSaveEventArgs> onSave) =>
        ctx.Render<OdsRecordTable<Row>>(p => p
            .Add(t => t.Rows, Rows)
            .Add(t => t.Columns, Columns)
            .Add(t => t.RowKey, r => (object)r.Id)
            .Add(t => t.AriaLabel, "Records")
            .Add(t => t.SavedFlashMs, 60_000)
            .Add(t => t.Actions, (_, a) =>
            {
                captured.Actions = a;
                return HostActions;
            })
            .Add(t => t.RenderDetail, (RenderFragment<Row>)(row => b => b.AddContent(0, $"detail:{row.Id}")))
            .Add(t => t.RenderEdit, (row, e) =>
            {
                captured.Edit = e;
                return b => b.AddContent(0, $"editing:{row.Id}");
            })
            .Add(t => t.OnSave, EventCallback.Factory.Create(new object(), onSave)));

    private static async Task StartEditAsync(IRenderedComponent<OdsRecordTable<Row>> cut, Captured captured)
    {
        await cut.InvokeAsync(() => captured.Actions!.StartEdit());
        cut.Render(_ => { });
        cut.WaitForAssertion(() => Assert.Contains("editing:a", cut.Markup, StringComparison.Ordinal));
    }

    [Fact]
    public async Task RecordTable_Success_LeavesEditMode_AndFlashesSaved()
    {
        await using var ctx = NewContext(out _);
        var captured = new Captured();
        OdsRecordSaveEventArgs? raised = null;
        var cut = RenderRecordTable(ctx, captured, args => raised = args);
        await StartEditAsync(cut, captured);

        await cut.InvokeAsync(() => captured.Edit!.Save("patch"));

        cut.WaitForAssertion(() =>
        {
            Assert.DoesNotContain("editing:a", cut.Markup, StringComparison.Ordinal);
            Assert.Contains("Alpha [saved]", cut.Markup, StringComparison.Ordinal);
        });
        Assert.Equal("patch", raised!.Patch);
    }

    [Fact]
    public async Task RecordTable_ReportedFailure_StaysInEditMode_AndDoesNotFlash()
    {
        await using var ctx = NewContext(out _);
        var captured = new Captured();
        var cut = RenderRecordTable(ctx, captured, args => args.Fail());
        await StartEditAsync(cut, captured);

        await cut.InvokeAsync(() => captured.Edit!.Save("patch"));

        Assert.Contains("editing:a", cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("[saved]", cut.Markup, StringComparison.Ordinal);
    }

    /// <summary>
    /// The save used to run as a discarded task, so a throwing OnSave vanished and the flash was
    /// never reached only by accident. It now reaches the renderer's error handling.
    /// </summary>
    [Fact]
    public async Task RecordTable_ThrowingHandler_ReachesTheRenderer_AndDoesNotFlash()
    {
        await using var ctx = NewContext(out _);
        var captured = new Captured();
        var cut = RenderRecordTable(ctx, captured, _ => throw new InvalidOperationException("boom"));
        await StartEditAsync(cut, captured);

        // Discard the task the way an Action-style template would: the exception must still surface.
        await cut.InvokeAsync(() => { _ = captured.Edit!.Save("patch"); });

        var unhandled = await ctx.Renderer.UnhandledException.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Contains("boom", unhandled.ToString(), StringComparison.Ordinal);
        Assert.Contains("editing:a", cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("[saved]", cut.Markup, StringComparison.Ordinal);
    }
}
