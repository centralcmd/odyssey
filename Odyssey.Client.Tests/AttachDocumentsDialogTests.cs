using System.Net;
using System.Security.Claims;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MudBlazor;
using MudBlazor.Services;
using Odyssey.ApiClient;
using Odyssey.ApiClient.Resources;
using Odyssey.Client.Components;
using Odyssey.Client.Pages.Attachments;
using Odyssey.Client.Pages.Finance;
using Odyssey.Client.Services;
using Odyssey.Dtos.Application;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos.Finance;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// The shared "Attach documents" dialog (design system · AttachDocumentsModal.jsx): "Upload new" and
/// "From Files" tabs over the one Files store, a per-file type guessed from the name, and one
/// <see cref="AttachDocumentItem"/> handed to the host per picked file. Driven here in its strictest
/// configuration — the property one (issue #210 §5.1), with the document allow-list and validity on.
/// </summary>
/// <remarks>
/// The library's disabled states are the client half of two server refusals — the <c>409</c> for a file
/// already linked and the <c>400</c> for a content type off <see cref="DocumentContentTypes"/> — and each
/// has to say why in text, not colour alone. The allow-list is the server's own symbol, never a copy.
/// The tabs follow the caller's claims: <c>files.create</c> for Upload new, <c>files.read</c> for From Files.
/// </remarks>
public class AttachDocumentsDialogTests
{
    static AttachDocumentsDialogTests() => BunitContext.DefaultWaitTimeout = TimeSpan.FromSeconds(10);

    private static readonly DateTime Base = new(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);

    private static readonly FileListItem Deed = new(Guid.NewGuid(), "skjøte-storgata.pdf", "application/pdf", 40_000, Base, null);
    private static readonly FileListItem Vognkort = new(Guid.NewGuid(), "vognkort.jpg", "image/jpeg", 90_000, Base.AddDays(-1), null);
    private static readonly FileListItem Linked = new(Guid.NewGuid(), "linked-already.pdf", "application/pdf", 10_000, Base.AddDays(-2), null);
    private static readonly FileListItem Html = new(Guid.NewGuid(), "listing.html", "text/html", 5_000, Base.AddDays(-3), null);
    private static readonly FileListItem Scan = new(Guid.NewGuid(), "scan001.pdf", "application/pdf", 12_000, Base.AddDays(-4), null);

    private static readonly List<FileListItem> Library = [Scan, Html, Linked, Vognkort, Deed];

    private sealed record Harness(
        IRenderedComponent<DialogHost> Host,
        Mock<IFilesApiClient> Files,
        List<AttachDocumentItem> Posted,
        List<bool> OpenChanges,
        Func<int> AttachedRaised,
        ISnackbar Snackbar)
    {
        public IEnumerable<string> Toasts => Snackbar.ShownSnackbars.Select(t => t.Message ?? string.Empty);

        /// <summary>Hands the upload field a picked file, as the browser picker would.</summary>
        public Task Pick(params OdsUploadFile[] files) =>
            Host.InvokeAsync(() => Host.FindComponent<OdsFileUpload>().Instance.FilesChanged.InvokeAsync(files));

        public IReadOnlyList<OdsUploadFile> Uploads => Host.FindComponent<OdsFileUpload>().Instance.Files ?? [];

        public IRenderedComponent<AttachDocumentsDialog> Dialog => Host.FindComponent<AttachDocumentsDialog>();

        public IReadOnlyList<IRenderedComponent<OdsSelect>> TypePickers =>
            [.. Host.FindComponents<OdsSelect>().Where(p => p.Instance.Label == "Document type")];

        public string Markup => Host.Markup;

        public AngleSharp.Dom.IElement Row(FileListItem file) =>
            Host.FindAll(".prop-lib-row").Single(r => r.TextContent.Contains(file.FileName, StringComparison.Ordinal));

        public void Pick(FileListItem file) => Row(file).QuerySelector("button.prop-lib-main")!.Click();

        // The footer's primary action — found by position in the dialog's own footer, not by its label,
        // which changes with the tab and the count.
        public AngleSharp.Dom.IElement SubmitButton => Host.FindAll(".ods-modal-foot button").Last();
    }

    private static Harness Render(
        bool canUpload = false,
        bool canBrowse = true,
        IReadOnlyList<Guid>? attached = null,
        bool libraryFails = false,
        bool attachFails = false,
        bool restrict = true,
        bool withKinds = true,
        Func<Task<IReadOnlyCollection<Guid>>>? loadAttached = null,
        Func<AttachDocumentItem, bool>? attachWhen = null,
        UploadLimitsDto? limits = null,
        int? surfaceMegabytes = null,
        IReadOnlyList<string>? extensions = null)
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();

        var files = new Mock<IFilesApiClient>();
        files.Setup(f => f.ListAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => libraryFails
                ? ApiResult<List<FileListItem>>.Failure(HttpStatusCode.InternalServerError, new ApiProblem { Detail = "boom" })
                : ApiResult<List<FileListItem>>.Success([.. Library], HttpStatusCode.OK));
        ctx.Services.AddSingleton(files.Object);

        var creator = new Mock<IContactQuickCreate>();
        creator.Setup(c => c.Resolve(It.IsAny<string?>())).Returns((string? id) => id);
        creator.Setup(c => c.WhenSettledAsync()).Returns(Task.CompletedTask);
        ctx.Services.AddSingleton(creator.Object);
        ctx.Services.AddSingleton(Mock.Of<IReferenceDataCache>());
        var uploadLimits = new Mock<IUploadLimitsCache>();
        uploadLimits.Setup(l => l.GetAsync(It.IsAny<CancellationToken>())).ReturnsAsync(limits ?? UploadLimitsCache.Fallback);
        ctx.Services.AddSingleton(uploadLimits.Object);

        var claims = new List<string>();
        if (canUpload) claims.Add(PermissionClaims.FilesCreate);
        if (canBrowse) claims.Add(PermissionClaims.FilesRead);
        ctx.Services.AddSingleton<AuthenticationStateProvider>(new SignedIn(claims));

        var posted = new List<AttachDocumentItem>();
        var openChanges = new List<bool>();
        var attachedRaised = 0;
        var cut = ctx.Render<DialogHost>(p => p
            .Add(h => h.AttachedIds, attached ?? [])
            .Add(h => h.Restrict, restrict)
            .Add(h => h.WithKinds, withKinds)
            .Add(h => h.LoadAttached, loadAttached)
            .Add(h => h.Attach, item =>
            {
                posted.Add(item);
                return Task.FromResult(!attachFails && (attachWhen?.Invoke(item) ?? true));
            })
            .Add(h => h.OnOpenChanged, (bool open) => openChanges.Add(open))
            .Add(h => h.SurfaceMaxMegabytes, surfaceMegabytes)
            .Add(h => h.UploadExtensions, extensions)
            .Add(h => h.OnAttached, () => attachedRaised++));

        var harness = new Harness(cut, files, posted, openChanges, () => attachedRaised, ctx.Services.GetRequiredService<ISnackbar>());
        cut.WaitForState(() => canUpload || !canBrowse || libraryFails || cut.FindAll(".prop-lib-row").Count > 0);
        return harness;
    }

    // ── Tabs ────────────────────────────────────────────────────────────────────

    /// <summary>Without files.create there is no upload tab: the dialog opens straight onto the library.</summary>
    [Fact]
    public void Without_files_create_it_opens_on_the_library_with_no_tabs()
    {
        var h = Render(canUpload: false);

        Assert.Empty(h.Host.FindComponents<OdsSegmentedControl>());
        Assert.Empty(h.Host.FindComponents<OdsFileUpload>());
        Assert.DoesNotContain("Upload new", h.Markup, StringComparison.Ordinal);
        Assert.Contains("Choose from Files", h.Markup, StringComparison.Ordinal);
        Assert.NotEmpty(h.Host.FindAll(".prop-lib-row"));
    }

    /// <summary>Without files.read there is no library to browse: upload only, and Files is never listed.</summary>
    [Fact]
    public void Without_files_read_it_offers_upload_only()
    {
        var h = Render(canUpload: true, canBrowse: false);

        Assert.Empty(h.Host.FindComponents<OdsSegmentedControl>());
        Assert.Single(h.Host.FindComponents<OdsFileUpload>());
        Assert.Contains("Attach documents", h.Markup, StringComparison.Ordinal);
        h.Files.Verify(f => f.ListAllAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task With_files_create_it_opens_on_upload_and_the_library_loads_on_switching()
    {
        var h = Render(canUpload: true);

        var tabs = h.Host.FindComponent<OdsSegmentedControl>();
        Assert.Equal(["Upload new", "From Files"], tabs.Instance.Options.Select(o => o.Label));
        Assert.Single(h.Host.FindComponents<OdsFileUpload>());
        Assert.Contains("Upload and attach", h.Markup, StringComparison.Ordinal);
        h.Files.Verify(f => f.ListAllAsync(It.IsAny<CancellationToken>()), Times.Never);

        await h.Host.InvokeAsync(() => tabs.Instance.ValueChanged.InvokeAsync(AttachDocumentsDialog.LibraryTab));

        h.Files.Verify(f => f.ListAllAsync(It.IsAny<CancellationToken>()), Times.Once);
        Assert.Empty(h.Host.FindComponents<OdsFileUpload>());
        Assert.NotEmpty(h.Host.FindAll(".prop-lib-row"));
    }

    /// <summary>The upload tab guesses each file's type with the property rule, not a contract one.</summary>
    [Fact]
    public void The_upload_tab_offers_the_property_vocabulary_and_guess()
    {
        var upload = Render(canUpload: true).Host.FindComponent<OdsFileUpload>().Instance;

        Assert.Equal(OdsTypeRegistries.PropertyFileTypes.Select(t => t.Key), upload.Kinds!.Select(k => k.Key));
        Assert.Equal("Registration", upload.GuessKind!("vognkort.jpg"));
        Assert.Equal("Other", upload.GuessKind!("IMG_0042.jpg"));
        Assert.Contains(DocumentContentTypes.Label, upload.Hint, StringComparison.Ordinal);
    }

    /// <summary>A surface with no file-type vocabulary (journal, tasks) shows no type picker on either tab.</summary>
    [Fact]
    public void A_surface_without_kinds_shows_no_type_picker()
    {
        var h = Render(withKinds: false);

        h.Pick(Deed);

        Assert.Empty(h.TypePickers);
    }

    // ── The library ─────────────────────────────────────────────────────────────

    [Fact]
    public void The_library_reads_newest_first()
    {
        var h = Render();

        Assert.Equal(
            [Deed.FileName, Vognkort.FileName, Linked.FileName, Html.FileName, Scan.FileName],
            h.Host.FindAll(".prop-lib-name").Select(n => n.TextContent));
    }

    /// <summary>A file already linked is shown but disabled, and says why — the 409 the server would answer.</summary>
    [Fact]
    public void An_already_attached_file_is_disabled_and_says_so()
    {
        var h = Render(attached: [Linked.Id]);

        var row = h.Row(Linked);
        Assert.True(row.QuerySelector("button.prop-lib-main")!.HasAttribute("disabled"));
        Assert.Equal("Already attached", row.QuerySelector(".prop-lib-reason")!.TextContent);
        Assert.Equal(AttachDocumentsDialog.LibraryState.AlreadyAttached, h.Dialog.Instance.StateOf(Linked));
    }

    /// <summary>A server-recorded content type off the shared allow-list is disabled with the reason in text.</summary>
    [Fact]
    public void A_file_off_the_allow_list_is_disabled_with_its_type_named()
    {
        var h = Render();

        var row = h.Row(Html);
        Assert.True(row.QuerySelector("button.prop-lib-main")!.HasAttribute("disabled"));
        Assert.Equal("HTML not accepted", row.QuerySelector(".prop-lib-reason")!.TextContent);
        Assert.Equal(AttachDocumentsDialog.LibraryState.TypeNotAllowed, h.Dialog.Instance.StateOf(Html));
    }

    /// <summary>A host that does not hold its linked ids (an account row) reads them, and they disable the same way.</summary>
    [Fact]
    public void Loaded_attached_ids_disable_their_rows_too()
    {
        var h = Render(loadAttached: () => Task.FromResult<IReadOnlyCollection<Guid>>([Linked.Id]));

        Assert.Equal(AttachDocumentsDialog.LibraryState.AlreadyAttached, h.Dialog.Instance.StateOf(Linked));
        Assert.Equal("Already attached", h.Row(Linked).QuerySelector(".prop-lib-reason")!.TextContent);
    }

    /// <summary>Without the allow-list (accounts, transactions, tax, journal, tasks) any stored type is attachable.</summary>
    [Fact]
    public void Without_the_allow_list_an_html_file_is_available()
    {
        var h = Render(restrict: false);

        Assert.Equal(AttachDocumentsDialog.LibraryState.Available, h.Dialog.Instance.StateOf(Html));
        Assert.Null(h.Row(Html).QuerySelector(".prop-lib-reason"));
        Assert.DoesNotContain("files can be attached", h.Markup, StringComparison.Ordinal);
    }

    /// <summary>Every allow-listed type is attachable — the state reads the server's symbol, not a copy.</summary>
    [Fact]
    public void Every_allow_listed_content_type_is_available()
    {
        var h = Render();

        foreach (var type in DocumentContentTypes.Allowed)
        {
            var file = new FileListItem(Guid.NewGuid(), "x", type, 1, Base, null);
            Assert.Equal(AttachDocumentsDialog.LibraryState.Available, h.Dialog.Instance.StateOf(file));
        }

        Assert.Null(h.Row(Deed).QuerySelector(".prop-lib-reason"));
        Assert.False(h.Row(Deed).QuerySelector("button.prop-lib-main")!.HasAttribute("disabled"));
    }

    [Theory]
    [InlineData("application/pdf", "PDF")]
    [InlineData("image/png", "PNG")]
    [InlineData("image/jpeg", "JPEG")]
    [InlineData("image/webp", "WebP")]
    [InlineData("text/html", "HTML")]
    [InlineData("text/plain", "PLAIN")]
    [InlineData("application/vnd.ms-excel", "MS-EXCEL")]
    [InlineData(null, "Unknown type")]
    [InlineData("", "Unknown type")]
    public void Content_types_read_as_a_short_name(string? contentType, string expected) =>
        Assert.Equal(expected, AttachDocumentsDialog.ContentTypeShort(contentType));

    [Fact]
    public void A_disabled_row_cannot_be_picked()
    {
        var h = Render();

        h.Pick(Html);

        Assert.Empty(h.TypePickers);
        Assert.True(h.SubmitButton.HasAttribute("disabled"));
    }

    [Fact]
    public async Task The_search_filters_by_name()
    {
        var h = Render();

        await h.Host.InvokeAsync(() => h.Host.FindComponent<OdsSearchField>().Instance.ValueChanged.InvokeAsync("VOGN"));

        Assert.Equal([Vognkort.FileName], h.Host.FindAll(".prop-lib-name").Select(n => n.TextContent));

        await h.Host.InvokeAsync(() => h.Host.FindComponent<OdsSearchField>().Instance.ValueChanged.InvokeAsync("nothing-like-it"));
        Assert.Contains("No files match “nothing-like-it”.", h.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void A_failed_library_read_offers_a_retry()
    {
        var h = Render(libraryFails: true);

        Assert.Contains("Couldn’t load your files.", h.Markup, StringComparison.Ordinal);
        Assert.Empty(h.Host.FindAll(".prop-lib-row"));
    }

    // ── Picking and submitting ──────────────────────────────────────────────────

    [Fact]
    public void Nothing_picked_leaves_the_submit_disabled()
    {
        var h = Render();

        Assert.True(h.SubmitButton.HasAttribute("disabled"));
        Assert.Contains("Attach document", h.SubmitButton.TextContent, StringComparison.Ordinal);
    }

    /// <summary>
    /// The row button's name is the file, its meta and any reason — every icon glyph inside it is
    /// aria-hidden itself (OdsMIcon), so no ligature ("check_box_outline_blank") is read as a word.
    /// </summary>
    [Fact]
    public void Every_glyph_inside_a_library_row_button_is_hidden_from_assistive_tech()
    {
        var h = Render();

        var button = h.Row(Deed).QuerySelector("button.prop-lib-main")!;
        var glyphs = button.QuerySelectorAll(".material-icons, .material-symbols-rounded");

        Assert.NotEmpty(glyphs);
        Assert.All(glyphs, g => Assert.Equal("true", g.GetAttribute("aria-hidden")));
    }

    /// <summary>Picking a row reveals its type picker, seeded from the name — Other when nothing matches.</summary>
    [Fact]
    public void Picking_a_row_shows_its_type_picker_seeded_from_the_name()
    {
        var h = Render();

        h.Pick(Deed);
        h.Pick(Scan);

        var pickers = h.TypePickers;
        Assert.Equal(["Deed", "Other"], pickers.Select(p => p.Instance.Value));
        Assert.Equal("true", h.Row(Deed).QuerySelector("button.prop-lib-main")!.GetAttribute("aria-pressed"));
        Assert.Contains("Attach 2 documents", h.SubmitButton.TextContent, StringComparison.Ordinal);
        Assert.False(h.SubmitButton.HasAttribute("disabled"));
    }

    [Fact]
    public void Picking_a_row_twice_unpicks_it()
    {
        var h = Render();

        h.Pick(Deed);
        h.Pick(Deed);

        Assert.Empty(h.TypePickers);
        Assert.Equal("false", h.Row(Deed).QuerySelector("button.prop-lib-main")!.GetAttribute("aria-pressed"));
    }

    /// <summary>One item per picked file, carrying the file's id and the type the reader chose.</summary>
    [Fact]
    public async Task Submit_hands_one_item_per_picked_file_then_closes()
    {
        var h = Render();

        h.Pick(Deed);
        h.Pick(Vognkort);
        var vognkortPicker = h.TypePickers.Single(p => p.Instance.Value == nameof(PropertyFileType.Registration));
        await h.Host.InvokeAsync(() => vognkortPicker.Instance.ValueChanged.InvokeAsync(nameof(PropertyFileType.Insurance)));

        h.SubmitButton.Click();

        h.Host.WaitForAssertion(() => Assert.Equal(2, h.Posted.Count));
        Assert.All(h.Posted, r => Assert.Equal(AttachDocumentSource.Library, r.Source));
        Assert.Contains(h.Posted, r => r.FileId == Deed.Id && r.KindAs(PropertyFileType.Other) == PropertyFileType.Deed);
        Assert.Contains(h.Posted, r => r.FileId == Vognkort.Id && r.KindAs(PropertyFileType.Other) == PropertyFileType.Insurance);
        Assert.All(h.Posted, r =>
        {
            Assert.Null(r.ValidFrom);
            Assert.Null(r.ValidTo);
            Assert.Null(r.IssuedAt);
            Assert.Null(r.IssuedBy);
        });
        h.Host.WaitForAssertion(() => Assert.Equal(1, h.AttachedRaised()));
        Assert.Contains(false, h.OpenChanges);
    }

    [Fact]
    public async Task The_validity_editor_carries_its_dates_onto_the_request()
    {
        var h = Render();

        h.Pick(Deed);
        h.Row(Deed).QuerySelector("button.afm-meta-toggle")!.Click();
        await SetDate(h, "Valid from", new DateTime(2026, 1, 1));
        await SetDate(h, "Valid to", new DateTime(2027, 1, 1));
        await SetDate(h, "Issued", new DateTime(2025, 12, 15));

        h.SubmitButton.Click();

        h.Host.WaitForAssertion(() => Assert.Single(h.Posted));
        Assert.Equal(new DateTime(2026, 1, 1), h.Posted[0].ValidFrom);
        Assert.Equal(new DateTime(2027, 1, 1), h.Posted[0].ValidTo);
        Assert.Equal(new DateTime(2025, 12, 15), h.Posted[0].IssuedAt);
    }

    /// <summary>"Valid to" before "Valid from" is refused on the dialog, as the service refuses it.</summary>
    [Fact]
    public async Task An_inverted_validity_range_is_refused_without_posting()
    {
        var h = Render();

        h.Pick(Deed);
        h.Row(Deed).QuerySelector("button.afm-meta-toggle")!.Click();
        await SetDate(h, "Valid from", new DateTime(2027, 1, 1));
        await SetDate(h, "Valid to", new DateTime(2026, 1, 1));

        Assert.Contains("“Valid to” can’t be before “Valid from”.", h.Markup, StringComparison.Ordinal);

        h.SubmitButton.Click();

        h.Host.WaitForAssertion(() =>
            Assert.Contains("A document’s “Valid to” can’t be before its “Valid from”.", h.Markup, StringComparison.Ordinal));
        Assert.Empty(h.Posted);
        Assert.Equal(0, h.AttachedRaised());
    }

    /// <summary>The kind falls back per surface when an item carries a key its enum does not know.</summary>
    [Fact]
    public void An_unknown_kind_parses_to_the_callers_fallback()
    {
        var item = new AttachDocumentItem(AttachDocumentSource.Upload, Guid.NewGuid(), "x.pdf", "NotAType", 1, null, null, null, null);

        Assert.Equal(ContractFileType.Signed, item.KindAs(ContractFileType.Signed));
        Assert.Equal(PropertyFileType.Other, item.KindAs(PropertyFileType.Other));
    }

    /// <summary>A refused attach neither reports success upward nor closes the dialog.</summary>
    [Fact]
    public void A_refused_attach_keeps_the_dialog_open()
    {
        var h = Render(attachFails: true);

        h.Pick(Deed);
        h.SubmitButton.Click();

        h.Host.WaitForAssertion(() => Assert.Single(h.Posted));
        Assert.Equal(0, h.AttachedRaised());
        Assert.DoesNotContain(false, h.OpenChanges);
    }

    // ── Upload new ──────────────────────────────────────────────────────────────

    private static long Megabytes(int n) => n * 1024L * 1024L;

    private static OdsUploadFile Upload(string name, long size = 10_000, string? rename = null) => new()
    {
        Uid = Guid.NewGuid().ToString(),
        Name = rename ?? name,
        Kind = PropertyFileTypeGuess.GuessKey(name),
        SizeBytes = size,
        Source = new FakeBrowserFile(name, size),
    };

    private static void Stores(Harness h, params Guid[] ids)
    {
        var queue = new Queue<Guid>(ids);
        h.Files.Setup(f => f.UploadAsync(It.IsAny<ApiUpload>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ApiUpload u, string? _, CancellationToken _) =>
                new FileUploadResponse(queue.Dequeue(), u.FileName, "application/pdf", 10, "hash", Base, null));
    }

    /// <summary>An upload is stored, then handed to the host as an Upload item carrying the stored id and the guessed type.</summary>
    [Fact]
    public async Task An_upload_is_stored_then_handed_to_the_host()
    {
        var h = Render(canUpload: true);
        var stored = Guid.NewGuid();
        Stores(h, stored);

        await h.Pick(Upload("skjøte.pdf"));
        h.SubmitButton.Click();

        h.Host.WaitForAssertion(() => Assert.Single(h.Posted));
        Assert.Equal(AttachDocumentSource.Upload, h.Posted[0].Source);
        Assert.Equal(stored, h.Posted[0].FileId);
        Assert.Equal(nameof(PropertyFileType.Deed), h.Posted[0].Kind);
        h.Files.Verify(f => f.UpdateMetadataAsync(It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Contains(false, h.OpenChanges);
    }

    /// <summary>A rename in the dropzone is applied; if it fails the file is still attached, under its own name, and the reader is told.</summary>
    [Fact]
    public async Task A_failed_rename_still_attaches_and_says_so()
    {
        var h = Render(canUpload: true);
        var stored = Guid.NewGuid();
        Stores(h, stored);
        h.Files.Setup(f => f.UpdateMetadataAsync(stored, null, "Deed 2026.pdf", It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResult<FileMetadataResponse>.Failure(
                HttpStatusCode.Conflict, new ApiProblem { Detail = "A file with that name already exists." }));

        await h.Pick(Upload("scan001.pdf", rename: "Deed 2026.pdf"));
        h.SubmitButton.Click();

        h.Host.WaitForAssertion(() => Assert.Single(h.Posted));
        Assert.Equal("scan001.pdf", h.Posted[0].Name);
        Assert.Contains(h.Toasts, t => t.Contains("couldn’t be renamed", StringComparison.Ordinal)
            && t.Contains("A file with that name already exists.", StringComparison.Ordinal));
    }

    /// <summary>
    /// A partly failed batch reports what linked, keeps only the failures, and stays open — and the retry
    /// links the stored file again rather than storing a second copy.
    /// </summary>
    [Fact]
    public async Task A_retry_after_a_failed_link_does_not_store_the_file_again()
    {
        var failOnce = true;
        var deedId = Guid.NewGuid();
        var h = Render(canUpload: true, attachWhen: item =>
        {
            if (item.FileId != deedId || !failOnce)
                return true;
            failOnce = false;
            return false;
        });
        Stores(h, deedId, Guid.NewGuid());

        await h.Pick(Upload("deed.pdf"), Upload("valuation.pdf"));
        h.SubmitButton.Click();

        h.Host.WaitForAssertion(() => Assert.Equal(1, h.AttachedRaised()));
        Assert.DoesNotContain(false, h.OpenChanges);
        Assert.Equal(["deed.pdf"], h.Uploads.Select(u => u.Name));
        Assert.Contains("One document wasn’t attached.", h.Markup, StringComparison.Ordinal);

        h.SubmitButton.Click();

        h.Host.WaitForAssertion(() => Assert.Contains(false, h.OpenChanges));
        h.Files.Verify(f => f.UploadAsync(It.IsAny<ApiUpload>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
        Assert.Equal(2, h.Posted.Count(p => p.FileId == deedId));
    }

    /// <summary>A rename edited between tries is applied on the retry, not lost with the first store.</summary>
    [Fact]
    public async Task A_rename_edited_before_a_retry_is_applied()
    {
        var failOnce = true;
        var stored = Guid.NewGuid();
        var h = Render(canUpload: true, attachWhen: _ =>
        {
            if (!failOnce) return true;
            failOnce = false;
            return false;
        });
        Stores(h, stored);
        h.Files.Setup(f => f.UpdateMetadataAsync(stored, null, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid id, string? _, string name, CancellationToken _) =>
                ApiResult<FileMetadataResponse>.Success(
                    new FileMetadataResponse(id, name, "application/pdf", 10, "hash", Base, null),
                    HttpStatusCode.OK));

        await h.Pick(Upload("scan001.pdf"));
        h.SubmitButton.Click();
        h.Host.WaitForAssertion(() => Assert.Contains("One document wasn’t attached.", h.Markup, StringComparison.Ordinal));

        h.Uploads[0].Name = "Deed 2026.pdf";
        h.SubmitButton.Click();

        h.Host.WaitForAssertion(() => Assert.Contains(false, h.OpenChanges));
        h.Files.Verify(f => f.UpdateMetadataAsync(stored, null, "Deed 2026.pdf", It.IsAny<CancellationToken>()), Times.Once);
        h.Files.Verify(f => f.UploadAsync(It.IsAny<ApiUpload>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal("Deed 2026.pdf", h.Posted.Last().Name);
    }

    [Fact]
    public async Task An_upload_that_fails_to_store_is_kept_for_a_retry_and_named()
    {
        var h = Render(canUpload: true);
        h.Files.Setup(f => f.UploadAsync(It.IsAny<ApiUpload>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));

        await h.Pick(Upload("deed.pdf"));
        h.SubmitButton.Click();

        h.Host.WaitForAssertion(() => Assert.Contains(h.Toasts, t => t.Contains("Couldn’t upload “deed.pdf”", StringComparison.Ordinal)));
        Assert.Empty(h.Posted);
        Assert.Single(h.Uploads);
        Assert.DoesNotContain(false, h.OpenChanges);
    }

    /// <summary>With the allow-list on, a type the server would refuse never lands in the list.</summary>
    [Fact]
    public async Task An_upload_off_the_allow_list_is_refused_before_it_lands()
    {
        var h = Render(canUpload: true);

        await h.Pick(Upload("listing.html"), Upload("deed.pdf"));

        Assert.Equal(["deed.pdf"], h.Uploads.Select(u => u.Name));
        Assert.Contains(h.Toasts, t => t.Contains(DocumentContentTypes.Label, StringComparison.Ordinal));
    }

    /// <summary>A surface without the allow-list filters by its own extensions instead.</summary>
    [Fact]
    public async Task A_surface_filters_uploads_by_its_own_extensions()
    {
        var h = Render(canUpload: true, restrict: false, extensions: [".pdf", ".jpg", ".jpeg", ".png"]);

        await h.Pick(Upload("receipt.webp"), Upload("receipt.png"));

        Assert.Equal(["receipt.png"], h.Uploads.Select(u => u.Name));
        Assert.Contains(h.Toasts, t => t.Contains("Allowed: PDF, JPG, PNG", StringComparison.Ordinal));
    }

    /// <summary>
    /// The cap is min(global, surface): a surface may tighten the admin's cap and must never loosen it,
    /// or a lowered global cap would not reach it.
    /// </summary>
    [Theory]
    [InlineData(64, 25, 25)]
    [InlineData(10, 25, 10)]
    [InlineData(64, null, 64)]
    public async Task The_upload_cap_is_the_smaller_of_the_global_and_the_surface_cap(int global, int? surface, int effective)
    {
        var limits = new UploadLimitsDto { MaxUploadMegabytes = global, MaxUploadBytes = Megabytes(global) };
        var h = Render(canUpload: true, limits: limits, surfaceMegabytes: surface);

        await h.Pick(Upload("fits.pdf", Megabytes(effective)), Upload("too-big.pdf", Megabytes(effective) + 1));

        Assert.Equal(["fits.pdf"], h.Uploads.Select(u => u.Name));
        Assert.Contains(h.Toasts, t => t.Contains($"exceeds the {effective} MB limit", StringComparison.Ordinal));
        Assert.Contains($"up to {effective} MB each", h.Host.FindComponent<OdsFileUpload>().Instance.Hint, StringComparison.Ordinal);
    }

    /// <summary>A failed read of the linked ids leaves the rows enabled rather than breaking the dialog.</summary>
    [Fact]
    public void A_failed_linked_ids_read_leaves_the_rows_enabled()
    {
        var h = Render(loadAttached: () => throw new HttpRequestException("down"));

        Assert.Equal(AttachDocumentsDialog.LibraryState.Available, h.Dialog.Instance.StateOf(Linked));
    }

    /// <summary>The library's "Try again" reads Files again and shows the rows once it succeeds.</summary>
    [Fact]
    public async Task Try_again_rereads_a_library_that_failed_to_load()
    {
        var fail = true;
        var h = Render(libraryFails: false, canBrowse: true, attached: null);
        h.Files.Setup(f => f.ListAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => fail
                ? ApiResult<List<FileListItem>>.Failure(HttpStatusCode.InternalServerError, new ApiProblem { Detail = "boom" })
                : ApiResult<List<FileListItem>>.Success([.. Library], HttpStatusCode.OK));
        await h.Host.InvokeAsync(() => h.Dialog.Instance.LoadLibraryAsync());
        Assert.Contains("Couldn’t load your files.", h.Markup, StringComparison.Ordinal);

        fail = false;
        h.Host.FindAll("button").Single(b => b.TextContent.Contains("Try again", StringComparison.Ordinal)).Click();

        h.Host.WaitForAssertion(() => Assert.Equal(Library.Count, h.Host.FindAll(".prop-lib-row").Count));
    }

    /// <summary>A partly failed library batch keeps only the picks that did not link, and stays open.</summary>
    [Fact]
    public void A_partly_failed_library_batch_keeps_only_the_failed_picks()
    {
        var h = Render(attachWhen: item => item.FileId != Vognkort.Id);

        h.Pick(Deed);
        h.Pick(Vognkort);
        h.SubmitButton.Click();

        h.Host.WaitForAssertion(() => Assert.Equal(1, h.AttachedRaised()));
        Assert.DoesNotContain(false, h.OpenChanges);
        Assert.Equal("false", h.Row(Deed).QuerySelector("button.prop-lib-main")!.GetAttribute("aria-pressed"));
        Assert.Equal("true", h.Row(Vognkort).QuerySelector("button.prop-lib-main")!.GetAttribute("aria-pressed"));
    }

    private static async Task SetDate(Harness h, string label, DateTime value)
    {
        var field = h.Host.FindComponents<OdsDateField>().Single(f => f.Instance.Label == label);
        await h.Host.InvokeAsync(() => field.Instance.ValueChanged.InvokeAsync(value));
    }

    private sealed class SignedIn(IEnumerable<string> permissions) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity(
                permissions.Select(p => new Claim(PermissionClaims.Type, p)), "test"))));
    }

    /// <summary>The dialog beside its providers, in one render tree so the teleported body is the host's markup.</summary>
    public sealed class DialogHost : ComponentBase
    {
        [Parameter] public IReadOnlyList<Guid> AttachedIds { get; set; } = [];
        [Parameter] public bool Restrict { get; set; }
        [Parameter] public bool WithKinds { get; set; }
        [Parameter] public Func<Task<IReadOnlyCollection<Guid>>>? LoadAttached { get; set; }
        [Parameter] public int? SurfaceMaxMegabytes { get; set; }
        [Parameter] public IReadOnlyList<string>? UploadExtensions { get; set; }
        [Parameter] public Func<AttachDocumentItem, Task<bool>> Attach { get; set; } = default!;
        [Parameter] public Action<bool>? OnOpenChanged { get; set; }
        [Parameter] public Action? OnAttached { get; set; }

        private bool _open = true;

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<MudDialogProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<MudPopoverProvider>(1);
            builder.CloseComponent();
            builder.OpenComponent<AttachDocumentsDialog>(2);
            builder.AddComponentParameter(3, nameof(AttachDocumentsDialog.Subtitle), "Keep the deed with Storgata 14.");
            builder.AddComponentParameter(4, nameof(AttachDocumentsDialog.Kinds), WithKinds ? OdsTypeRegistries.PropertyFileTypes : null);
            builder.AddComponentParameter(5, nameof(AttachDocumentsDialog.GuessKind), (Func<string, string>)PropertyFileTypeGuess.GuessKey);
            builder.AddComponentParameter(6, nameof(AttachDocumentsDialog.Validity), true);
            builder.AddComponentParameter(7, nameof(AttachDocumentsDialog.RestrictToDocumentTypes), Restrict);
            builder.AddComponentParameter(8, nameof(AttachDocumentsDialog.AttachedIds), AttachedIds);
            builder.AddComponentParameter(9, nameof(AttachDocumentsDialog.LoadAttachedIds), LoadAttached);
            builder.AddComponentParameter(10, nameof(AttachDocumentsDialog.Attach), Attach);
            builder.AddComponentParameter(11, nameof(AttachDocumentsDialog.Open), _open);
            builder.AddComponentParameter(12, nameof(AttachDocumentsDialog.OpenChanged),
                EventCallback.Factory.Create<bool>(this, open =>
                {
                    _open = open;
                    OnOpenChanged?.Invoke(open);
                }));
            builder.AddComponentParameter(13, nameof(AttachDocumentsDialog.OnAttached),
                EventCallback.Factory.Create<IReadOnlyList<AttachDocumentItem>>(this, _ => OnAttached?.Invoke()));
            builder.AddComponentParameter(14, nameof(AttachDocumentsDialog.SurfaceMaxMegabytes), SurfaceMaxMegabytes);
            builder.AddComponentParameter(15, nameof(AttachDocumentsDialog.UploadExtensions), UploadExtensions);
            builder.CloseComponent();
        }
    }
}
