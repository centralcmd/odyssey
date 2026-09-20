using System.Security.Claims;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MudBlazor;
using MudBlazor.Services;
using Odyssey.ApiClient.Resources;
using Odyssey.Client.Components;
using Odyssey.Client.Pages.Finance;
using Odyssey.Client.Services;
using Odyssey.Dtos.Finance;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// The contract configuration of <c>OdsFilesTable</c>'s Edit dialog (Odyssey Design System ·
/// FilesTable, <c>preview/59b-contract-document-validity.html</c>; issue #146 frontend).
/// </summary>
/// <remarks>
/// Three properties are spec rules rather than styling, and each one is a defect if it silently
/// flips: the dialog offers <b>no rename</b>, because <c>PUT …/files/{fileId}</c> accepts no name and
/// a field whose value is discarded is worse than no field; the document type is <b>required</b>,
/// because the verb is a full replacement and <c>ContractFileType.Signed</c> is the enum's zero
/// member, so an unsent type would be defaulted into a claim that the document is the signed
/// agreement; and each date is checked against the range its column can store, which is the client
/// half of the per-field <c>400</c> the service returns.
/// </remarks>
public class ContractDocumentValidityTests
{
    private static readonly Guid IssuerId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private static readonly OdsFilesRow Document = new()
    {
        Id = "f-1",
        Name = "maple-st-lease-signed.pdf",
        Kind = "Correspondence",
        SizeBytes = 25_800,
        UploadedAtUtc = new DateTime(2026, 8, 31, 12, 0, 0, DateTimeKind.Utc),
        ValidFrom = new DateTime(2026, 9, 1),
        ValidTo = new DateTime(2027, 8, 31),
        IssuedAt = new DateTime(2026, 8, 18),
        IssuedBy = IssuerId,
    };

    private static readonly IReadOnlyList<OdsOption> Kinds =
    [
        new("Signed", "Signed"), new("Amendment", "Amendment"),
        new("Correspondence", "Correspondence"), new("Other", "Other"),
    ];

    private static readonly IReadOnlyList<OdsOption> Issuers = [new(IssuerId.ToString(), "The Landlord")];

    static ContractDocumentValidityTests() => BunitContext.DefaultWaitTimeout = TimeSpan.FromSeconds(10);

    private static BunitContext NewContext()
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        return ctx;
    }

    /// <summary>
    /// The rendered host plus the box its committed patch lands in. The dialog body teleports into
    /// the MudDialogProvider, so both the provider and the dialog have to sit in ONE render tree —
    /// a separately-rendered provider is a second root and stays empty.
    /// </summary>
    private sealed record Harness(IRenderedComponent<DialogHost> Host, List<object?> Saved)
    {
        public string Markup => Host.Markup;

        /// <summary>
        /// Clicks the footer's submit button — the real path, so a refusal re-renders the body and
        /// its per-field message is actually in the markup. Invoking the submit delegate directly
        /// would return the same verdict and render nothing.
        /// </summary>
        public object? Submit()
        {
            Host.FindAll(".mud-dialog-actions button").Last().Click();
            return Saved.SingleOrDefault();
        }
    }

    /// <summary>An open edit dialog beside the provider its MudDialog teleports into.</summary>
    public sealed class DialogHost : ComponentBase
    {
        [Parameter] public OdsFilesRow File { get; set; } = default!;
        [Parameter] public bool Renameable { get; set; }
        [Parameter] public bool RequireType { get; set; }
        [Parameter] public EventCallback<object?> OnSave { get; set; }

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<MudDialogProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<MudPopoverProvider>(1);
            builder.CloseComponent();
            builder.OpenComponent<OdsFilesEditDialog>(2);
            builder.AddComponentParameter(3, nameof(OdsFilesEditDialog.File), File);
            builder.AddComponentParameter(4, nameof(OdsFilesEditDialog.Open), true);
            builder.AddComponentParameter(5, nameof(OdsFilesEditDialog.Kinds), Kinds);
            builder.AddComponentParameter(6, nameof(OdsFilesEditDialog.Issuers), Issuers);
            builder.AddComponentParameter(7, nameof(OdsFilesEditDialog.Renameable), Renameable);
            builder.AddComponentParameter(8, nameof(OdsFilesEditDialog.RequireType), RequireType);
            builder.AddComponentParameter(9, nameof(OdsFilesEditDialog.OnSave), OnSave);
            builder.CloseComponent();
        }
    }

    private static Harness RenderDialog(
        BunitContext ctx,
        bool renameable = false,
        bool requireType = true,
        OdsFilesRow? file = null)
    {
        var saved = new List<object?>();
        var host = ctx.Render<DialogHost>(p => p
            .Add(h => h.File, file ?? Document)
            .Add(h => h.Renameable, renameable)
            .Add(h => h.RequireType, requireType)
            .Add(h => h.OnSave, EventCallback.Factory.Create<object?>(new object(), saved.Add)));

        return new Harness(host, saved);
    }

    // ── No rename (§5.2 — the verb accepts no name) ───────────────────────────

    /// <summary>
    /// A contract document's name lives on the <c>FileMetadata</c> the link references, so the
    /// dialog offers no File-name field — and says "Edit document", not "Edit file".
    /// </summary>
    [Fact]
    public async Task ContractConfiguration_OffersNoRename()
    {
        await using var ctx = NewContext();
        var markup = RenderDialog(ctx).Markup;

        Assert.DoesNotContain("File name", markup, StringComparison.Ordinal);
        Assert.Contains("Edit document", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("Edit file", markup, StringComparison.Ordinal);
    }

    /// <summary>The account configuration is unchanged — the rename field is the default.</summary>
    [Fact]
    public async Task AccountConfiguration_KeepsTheRenameFieldAndTitle()
    {
        await using var ctx = NewContext();
        var markup = RenderDialog(ctx, renameable: true, requireType: false).Markup;

        Assert.Contains("File name", markup, StringComparison.Ordinal);
        Assert.Contains("Edit file", markup, StringComparison.Ordinal);
    }

    /// <summary>
    /// The patch carries the row's existing name back unchanged, so a host comparing it never reads
    /// the no-rename dialog as a rename.
    /// </summary>
    [Fact]
    public async Task ContractConfiguration_PatchCarriesTheNameBackUnchanged()
    {
        await using var ctx = NewContext();
        var harness = RenderDialog(ctx);

        var patch = Assert.IsType<OdsFileEdit>(harness.Submit());

        Assert.Equal(Document.Name, patch.Name);
        Assert.Equal(Document.Kind, patch.Kind);
    }

    // ── Required type (§8.1.1 — Signed is the enum's zero member) ─────────────

    /// <summary>
    /// A document whose type never resolved submits nothing. The alternative — defaulting — would
    /// mark an arbitrary attachment as the signed copy of the contract, because <c>Signed</c> is
    /// ordinal 0.
    /// </summary>
    [Fact]
    public async Task RequireType_RefusesASubmitWithNoTypeSelected()
    {
        await using var ctx = NewContext();
        var harness = RenderDialog(ctx, file: Document with { Kind = string.Empty });

        Assert.Null(harness.Submit());
        Assert.Contains("Pick the document type.", harness.Markup, StringComparison.Ordinal);
    }

    /// <summary>Without the flag an empty type still submits — the account surface's behaviour.</summary>
    [Fact]
    public async Task WithoutRequireType_AnEmptyTypeStillSubmits()
    {
        await using var ctx = NewContext();
        var harness = RenderDialog(ctx, renameable: true, requireType: false,
            file: Document with { Kind = string.Empty });

        Assert.NotNull(harness.Submit());
    }

    // ── The storable date range (§8.2.1) ─────────────────────────────────────

    /// <summary>
    /// A year the column cannot store is refused on the field that holds it, rather than sent and
    /// answered with a per-field <c>400</c>. The bound is the column's, not a judgement about
    /// plausible document dates — see the far-future case below.
    /// </summary>
    [Theory]
    [InlineData(202)]
    [InlineData(1)]
    public async Task ADateOutsideTheStorableRange_IsRefusedBeforeItIsSent(int year)
    {
        await using var ctx = NewContext();
        var harness = RenderDialog(ctx, file: Document with
        {
            ValidFrom = new DateTime(year, 1, 1),
            ValidTo = null,
        });

        Assert.Null(harness.Submit());
        Assert.Contains("Enter a year between 1000 and 9999.", harness.Markup, StringComparison.Ordinal);
    }

    /// <summary>
    /// A warranty running to 2147 is a legitimate record. The check is the column's limit, so no
    /// narrower "sensible year" window is imposed on top of it.
    /// </summary>
    [Fact]
    public async Task AFarFutureButStorableDate_IsAccepted()
    {
        await using var ctx = NewContext();
        var harness = RenderDialog(ctx, file: Document with
        {
            ValidFrom = new DateTime(2026, 9, 1),
            ValidTo = new DateTime(2147, 1, 1),
        });

        var patch = Assert.IsType<OdsFileEdit>(harness.Submit());

        Assert.Equal(new DateTime(2147, 1, 1), patch.ValidTo);
    }

    /// <summary>An inverted period is refused here too, on the control that most likely holds it.</summary>
    [Fact]
    public async Task AnInvertedPeriod_IsRefusedBeforeItIsSent()
    {
        await using var ctx = NewContext();
        var harness = RenderDialog(ctx, file: Document with
        {
            ValidFrom = new DateTime(2027, 1, 1),
            ValidTo = new DateTime(2026, 1, 1),
        });

        Assert.Null(harness.Submit());
        Assert.Contains("can’t be before", harness.Markup, StringComparison.Ordinal);
    }

    // ── The archive state (§9.3 — and what it does NOT cover) ────────────────

    /// <summary>
    /// An archived contract refuses <c>POST …/files</c> and <c>PUT …/files/{fileId}</c> with a
    /// <c>400</c>, so <b>Edit</b> is withheld rather than offered and then failed.
    /// </summary>
    [Fact]
    public async Task ArchivedContract_WithholdsEdit()
    {
        await using var ctx = NewTableContext();
        var labels = OpenRowMenu(ctx, RenderTable(ctx, archived: true));

        Assert.DoesNotContain("Edit", labels);
    }

    /// <summary>
    /// <b>Detach stays live on an archived contract.</b> <c>ContractService.DetachFile</c> carries no
    /// archive guard — the same asymmetry <c>DeleteParty</c> has, on the same reasoning: detaching a
    /// link needs only the link. Withholding it would refuse something the API allows, and it is the
    /// easiest thing to get wrong here, because the design system's own contract table bundles Edit
    /// and Delete behind one read-only flag.
    /// </summary>
    [Fact]
    public async Task ArchivedContract_KeepsDetach()
    {
        await using var ctx = NewTableContext();
        var labels = OpenRowMenu(ctx, RenderTable(ctx, archived: true));

        Assert.Contains("Delete", labels);
    }

    /// <summary>Both are offered on a live contract — so the assertions above fail for the right reason.</summary>
    [Fact]
    public async Task LiveContract_OffersBothEditAndDetach()
    {
        await using var ctx = NewTableContext();
        var labels = OpenRowMenu(ctx, RenderTable(ctx, archived: false));

        Assert.Contains("Edit", labels);
        Assert.Contains("Delete", labels);
    }

    private static readonly ContractFileItem Attachment = new(
        Guid.Parse("22222222-2222-2222-2222-222222222222"),
        "maple-st-lease-signed.pdf",
        "application/pdf",
        25_800,
        new DateTime(2026, 8, 31, 12, 0, 0, DateTimeKind.Utc),
        ContractFileType.Signed);

    private static BunitContext NewTableContext()
    {
        var ctx = NewContext();
        ctx.Services.AddSingleton(Mock.Of<IContractsApiClient>());
        ctx.Services.AddSingleton(Mock.Of<IClipboardService>());
        ctx.Services.AddSingleton(Mock.Of<IReferenceDataCache>());
        ctx.Services.AddSingleton(Mock.Of<IContactQuickCreate>());
        ctx.Services.AddSingleton<AuthenticationStateProvider>(new SignedOut());
        return ctx;
    }

    private sealed class SignedOut : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity())));
    }

    private static IRenderedComponent<ContractFilesTable> RenderTable(BunitContext ctx, bool archived) =>
        ctx.Render<ContractFilesTable>(p => p
            .Add(t => t.ContractId, Guid.Parse("33333333-3333-3333-3333-333333333333"))
            .Add(t => t.Files, new[] { Attachment })
            .Add(t => t.CanDownload, true)
            .Add(t => t.CanUpdate, true)
            .Add(t => t.CanDelete, true)
            .Add(t => t.Archived, archived));

    /// <summary>
    /// The labels on the row's open overflow menu. An item's TextContent also carries its leading
    /// icon ligature, so the label is read off the body span — matching the whole item's text would
    /// make an absence assertion pass for the wrong reason.
    /// </summary>
    private static IReadOnlyList<string> OpenRowMenu(BunitContext ctx, IRenderedComponent<ContractFilesTable> cut)
    {
        var popover = ctx.Render<MudPopoverProvider>();
        cut.Find("button[aria-label='Row actions']").Click();
        popover.WaitForElement("div.mud-menu-item");
        return [.. popover.FindAll(".odc-menu-item-body > span:first-child").Select(e => e.TextContent.Trim())];
    }

    /// <summary>
    /// The four fields round-trip: what the dialog was seeded with is what it commits, so opening and
    /// saving a document unchanged does not quietly clear a date.
    /// </summary>
    [Fact]
    public async Task TheFourFields_RoundTripThroughThePatch()
    {
        await using var ctx = NewContext();
        var harness = RenderDialog(ctx);

        var patch = Assert.IsType<OdsFileEdit>(harness.Submit());

        Assert.Equal(Document.ValidFrom, patch.ValidFrom);
        Assert.Equal(Document.ValidTo, patch.ValidTo);
        Assert.Equal(Document.IssuedAt, patch.IssuedAt);
        Assert.Equal(IssuerId, patch.IssuedBy);
    }
}
