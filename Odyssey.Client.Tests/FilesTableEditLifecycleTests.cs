using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using Odyssey.Client.Components;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// OdsFilesTable's Edit-file dialog and its opt-in validity columns (Odyssey Design System ·
/// FilesTable, commit f7816ca — the release that moved editing out of an expandable row and into a
/// modal).
///
/// <para>
/// The dialog's MOUNT LIFECYCLE is the reason this file exists. An inline MudDialog dismisses its
/// teleported instance by re-rendering with Visible=false, so a host that unmounts it in the same
/// pass skips that render: OdsModal's closed-edge focus restore never runs and focus drops to the
/// document body instead of the row's action menu (WCAG 2.2 SC 2.4.3). That is invisible in a
/// screenshot and survives every assertion about the dialog's CONTENT, so it is pinned directly.
/// </para>
/// </summary>
public class FilesTableEditLifecycleTests
{
    private static readonly OdsFilesRow[] Files =
    [
        new()
        {
            Id = "f-1",
            Name = "statement-2026-04.pdf",
            Kind = "Statement",
            SizeBytes = 25_800,
            UploadedAtUtc = new DateTime(2026, 8, 31, 12, 0, 0, DateTimeKind.Utc),
            ValidFrom = new DateTime(2026, 8, 25),
            ValidTo = new DateTime(2026, 10, 31),
            IssuedAt = new DateTime(2026, 8, 18),
            IssuedBy = Guid.Parse("11111111-1111-1111-1111-111111111111"),
        },
    ];

    private static readonly IReadOnlyList<OdsOption> Kinds = [new("Statement", "Statement")];

    private static readonly IReadOnlyList<OdsOption> Issuers =
        [new("11111111-1111-1111-1111-111111111111", "First National Bank")];

    private static BunitContext NewContext()
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();

        // The row menu and the dialog are both popover-hosted; MudBlazor refuses to initialise
        // either without a provider in the tree, the same way MainLayout supplies one in the app.
        // The provider is a SEPARATE render root, so opened menu items appear in its markup rather
        // than the table's — which is why ClickRowAction searches it and not the table.
        ctx.Render<MudDialogProvider>();
        return ctx;
    }

    /// <summary>One host-supplied item, so an absence assertion has a populated menu to prove
    /// itself against — on a read-only surface the menu would otherwise be empty, and "Edit is
    /// missing" would hold for the wrong reason.</summary>
    private static readonly IReadOnlyList<OdsMenuItem> HostActions =
        [new() { Icon = "download", Label = "Download" }];

    private static IRenderedComponent<OdsFilesTable> RenderTable(
        BunitContext ctx, bool validityColumns, EventCallback<OdsRecordSaveEventArgs> onSave = default) =>
        ctx.Render<OdsFilesTable>(p => p
            .Add(t => t.Files, Files)
            .Add(t => t.Kinds, Kinds)
            .Add(t => t.Issuers, Issuers)
            .Add(t => t.ValidityColumns, validityColumns)
            .Add(t => t.IssuerFor, _ => "First National Bank")
            .Add(t => t.AriaLabel, "Account files")
            .Add(t => t.Actions, _ => HostActions)
            .Add(t => t.OnSave, onSave));

    // An item's TextContent also carries its leading icon LIGATURE ("edit" before "Edit"), so the
    // label has to be read off the body span. Matching the whole item's text instead would make an
    // absence assertion pass for the wrong reason — it can never equal the bare label.
    private static IReadOnlyList<string> MenuLabels(IRenderedComponent<MudPopoverProvider> popover) =>
        [.. popover.FindAll(".odc-menu-item-body > span:first-child").Select(e => e.TextContent.Trim())];

    /// <summary>The labels on a row's open overflow menu, in order.</summary>
    private static IReadOnlyList<string> OpenRowMenu(
        BunitContext ctx, IRenderedComponent<OdsFilesTable> cut, out IRenderedComponent<MudPopoverProvider> popover)
    {
        popover = ctx.Render<MudPopoverProvider>();
        cut.Find("button[aria-label='Row actions']").Click();

        // MudMenu's toggle is async, so the items are not in the provider's markup on the next line.
        // Every menu here carries at least the host's Download item, so this always resolves.
        popover.WaitForElement("div.mud-menu-item");
        return MenuLabels(popover);
    }

    /// <summary>Open the row's overflow menu and click one of its items by visible label.</summary>
    private static void ClickRowAction(BunitContext ctx, IRenderedComponent<OdsFilesTable> cut, string label)
    {
        var labels = OpenRowMenu(ctx, cut, out var popover);
        var index = labels.ToList().IndexOf(label);
        Assert.True(index >= 0, $"No '{label}' item. Menu offered: {string.Join(", ", labels)}");

        // The click handler is on MudMenuItem's own div.mud-menu-item — .odc-menu-item is a span
        // INSIDE it, and clicking that does not reach the handler. Dividers and headers render
        // neither, so the two lists stay index-aligned.
        popover.FindAll("div.mud-menu-item")[index].Click();
    }

    // ── The mount lifecycle (the blocking finding) ───────────────────────────

    /// <summary>
    /// Closing must toggle the dialog's Open, never unmount it. If the host clears the variable that
    /// gates the mount, the dialog vanishes from the tree in the same render pass and OdsModal never
    /// gets the closed render its focus restore hangs off.
    /// </summary>
    [Fact]
    public async Task EditDialog_StaysMountedAfterClose()
    {
        await using var ctx = NewContext();
        var cut = RenderTable(ctx, validityColumns: true,
            onSave: EventCallback.Factory.Create<OdsRecordSaveEventArgs>(new object(), _ => { }));

        ClickRowAction(ctx, cut, "Edit");
        cut.WaitForAssertion(() => Assert.Single(cut.FindComponents<OdsFilesEditDialog>()));

        var dialog = cut.FindComponent<OdsFilesEditDialog>();
        Assert.True(dialog.Instance.Open);

        // The dialog reports itself closed (Esc, scrim, Cancel — all land here).
        await cut.InvokeAsync(() => dialog.Instance.OpenChanged.InvokeAsync(false));

        // Still in the tree, simply not open. Unmounting here is the regression.
        cut.WaitForAssertion(() =>
        {
            var afterClose = cut.FindComponents<OdsFilesEditDialog>();
            Assert.Single(afterClose);
            Assert.False(afterClose[0].Instance.Open);
        });
    }

    /// <summary>Nothing is mounted before the first Edit — the dialog is not a permanent fixture.</summary>
    [Fact]
    public async Task EditDialog_IsNotMountedBeforeFirstEdit()
    {
        await using var ctx = NewContext();
        var cut = RenderTable(ctx, validityColumns: true,
            onSave: EventCallback.Factory.Create<OdsRecordSaveEventArgs>(new object(), _ => { }));

        Assert.Empty(cut.FindComponents<OdsFilesEditDialog>());
    }

    /// <summary>Edit is only offered when the result can actually be persisted.</summary>
    [Fact]
    public async Task EditAction_IsAbsentOnAReadOnlySurface()
    {
        await using var ctx = NewContext();
        var cut = RenderTable(ctx, validityColumns: false);

        var labels = OpenRowMenu(ctx, cut, out _);

        Assert.Contains("Download", labels);
        Assert.DoesNotContain("Edit", labels);
    }

    /// <summary>
    /// "View details" left every files menu when rows stopped expanding. Its return would mean the
    /// row is trying to open something that no longer exists.
    /// </summary>
    [Fact]
    public async Task RowMenu_NoLongerOffersViewDetails()
    {
        await using var ctx = NewContext();
        var cut = RenderTable(ctx, validityColumns: true,
            onSave: EventCallback.Factory.Create<OdsRecordSaveEventArgs>(new object(), _ => { }));

        var labels = OpenRowMenu(ctx, cut, out _);

        Assert.Contains("Edit", labels);
        Assert.DoesNotContain(labels, l => l.Contains("View details") || l.Contains("Collapse"));
    }

    // ── The opt-in validity columns ──────────────────────────────────────────

    /// <summary>Column labels, with the sortable headers' trailing sort-arrow ligature stripped.</summary>
    private static IReadOnlyList<string> HeaderLabels(IRenderedComponent<OdsFilesTable> cut) =>
        [.. cut.FindAll("thead th").Select(th => th.TextContent.Replace("arrow_upward", string.Empty).Trim())];

    [Fact]
    public async Task ValidityColumns_AddTheFourReadOnlyHeaders()
    {
        await using var ctx = NewContext();
        var headers = HeaderLabels(RenderTable(ctx, validityColumns: true));

        Assert.Contains("Valid from", headers);
        Assert.Contains("Valid to", headers);
        Assert.Contains("Issued", headers);
        Assert.Contains("Issued by", headers);
    }

    [Fact]
    public async Task ValidityColumns_AreAbsentByDefault()
    {
        await using var ctx = NewContext();
        var headers = HeaderLabels(RenderTable(ctx, validityColumns: false));

        Assert.DoesNotContain("Valid from", headers);
        Assert.DoesNotContain("Issued by", headers);
    }

    /// <summary>The issuing contact is resolved through IssuerFor, not printed as a raw id.</summary>
    [Fact]
    public async Task IssuedBy_RendersTheResolvedContactName()
    {
        await using var ctx = NewContext();
        var cut = RenderTable(ctx, validityColumns: true);

        Assert.Contains("First National Bank", cut.Markup);
        Assert.DoesNotContain("11111111-1111-1111-1111-111111111111", cut.Markup);
    }

    // ── The horizontal-scroll region (WCAG 2.2 SC 2.1.1) ─────────────────────

    /// <summary>
    /// The four validity columns push the table past its card, so the wrapper scrolls — and a
    /// scrollable region with no focusable content inside it must itself be reachable by keyboard,
    /// or those cells can only be revealed with a pointer.
    /// </summary>
    [Fact]
    public async Task WideVariant_ExposesAFocusableLabelledScrollRegion()
    {
        await using var ctx = NewContext();
        var region = RenderTable(ctx, validityColumns: true).Find("div.odc-ft-scroll");

        Assert.Equal("0", region.GetAttribute("tabindex"));
        Assert.Equal("region", region.GetAttribute("role"));
        Assert.False(string.IsNullOrWhiteSpace(region.GetAttribute("aria-label")));
    }

    /// <summary>
    /// A table that cannot overflow must not become a tab stop — an unreachable-by-scroll region
    /// that still takes focus is just a dead stop on the way to the rows.
    /// </summary>
    [Fact]
    public async Task NarrowVariant_AddsNoTabStop()
    {
        await using var ctx = NewContext();
        var cut = RenderTable(ctx, validityColumns: false);

        Assert.Empty(cut.FindAll("div.odc-ft-scroll"));
        Assert.Empty(cut.FindAll("[role='region']"));
    }
}
