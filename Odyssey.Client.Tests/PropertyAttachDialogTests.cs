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
using Odyssey.Client.Pages.Finance;
using Odyssey.Client.Services;
using Odyssey.Dtos.Finance;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// The property attach dialog (issue #210 §5.1; design system · AddPropertyFileModal.jsx): "Upload new"
/// and "From Files" tabs over the one Files store, a per-file <see cref="PropertyFileType"/> guessed from
/// the name, and one <c>AttachPropertyFileRequest</c> per picked file.
/// </summary>
/// <remarks>
/// The library's disabled states are the client half of two server refusals — the <c>409</c> for a file
/// already linked and the <c>400</c> for a content type off <see cref="DocumentContentTypes"/> — and each
/// has to say why in text, not colour alone. The allow-list is the server's own symbol, never a copy.
/// </remarks>
public class PropertyAttachDialogTests
{
    static PropertyAttachDialogTests() => BunitContext.DefaultWaitTimeout = TimeSpan.FromSeconds(10);

    private static readonly Guid PropertyId = Guid.Parse("21021021-0000-0000-0000-000000000002");

    private static readonly DateTime Base = new(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);

    private static readonly FileListItem Deed = new(Guid.NewGuid(), "skjøte-storgata.pdf", "application/pdf", 40_000, Base, null);
    private static readonly FileListItem Vognkort = new(Guid.NewGuid(), "vognkort.jpg", "image/jpeg", 90_000, Base.AddDays(-1), null);
    private static readonly FileListItem Linked = new(Guid.NewGuid(), "linked-already.pdf", "application/pdf", 10_000, Base.AddDays(-2), null);
    private static readonly FileListItem Html = new(Guid.NewGuid(), "listing.html", "text/html", 5_000, Base.AddDays(-3), null);
    private static readonly FileListItem Scan = new(Guid.NewGuid(), "scan001.pdf", "application/pdf", 12_000, Base.AddDays(-4), null);

    private static readonly List<FileListItem> Library = [Scan, Html, Linked, Vognkort, Deed];

    private static ExistingProperty House(PropertyType type = PropertyType.RealEstate) => new()
    {
        PropertyId = PropertyId,
        Name = "Storgata 14",
        Description = "Primary residence",
        Type = type,
        CurrencyCode = "NOK",
    };

    private static ExistingPropertyFile AttachedCopyOf(FileListItem file) => new()
    {
        PropertyFileId = Guid.NewGuid(),
        PropertyId = PropertyId,
        FileType = PropertyFileType.Deed,
        AttachedAtUtc = Base,
        FileMetadata = new ExistingFileMetadata
        {
            Id = file.Id,
            FileName = file.FileName,
            ContentType = file.ContentType,
            SizeBytes = file.SizeBytes,
            FileBlobId = Guid.NewGuid(),
            UploadedAtUtc = file.UploadedAtUtc,
        },
    };

    private sealed record Harness(
        IRenderedComponent<DialogHost> Host,
        Mock<IPropertiesApiClient> Properties,
        Mock<IFilesApiClient> Files,
        List<AttachPropertyFileRequest> Posted,
        List<bool> OpenChanges,
        Func<int> AttachedRaised)
    {
        public IRenderedComponent<PropertyAttachDialog> Dialog => Host.FindComponent<PropertyAttachDialog>();

        public string Markup => Host.Markup;

        public AngleSharp.Dom.IElement Row(FileListItem file) =>
            Host.FindAll(".prop-lib-row").Single(r => r.TextContent.Contains(file.FileName, StringComparison.Ordinal));

        public void Pick(FileListItem file) => Row(file).QuerySelector("button.prop-lib-main")!.Click();

        public AngleSharp.Dom.IElement SubmitButton =>
            Host.FindAll(".mud-dialog-actions button, button")
                .Last(b => b.TextContent.Contains("Attach", StringComparison.Ordinal)
                           && !b.TextContent.Contains("Attach documents", StringComparison.Ordinal));
    }

    private static Harness Render(
        bool canUpload = false,
        IReadOnlyList<ExistingPropertyFile>? attached = null,
        bool libraryFails = false,
        bool attachFails = false,
        PropertyType type = PropertyType.RealEstate,
        bool loadLibrary = true)
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

        var posted = new List<AttachPropertyFileRequest>();
        var properties = new Mock<IPropertiesApiClient>();
        properties.Setup(p => p.AttachFileAsync(PropertyId, It.IsAny<AttachPropertyFileRequest>(), It.IsAny<CancellationToken>()))
            .Callback((Guid _, AttachPropertyFileRequest r, CancellationToken _) => posted.Add(r))
            .ReturnsAsync(() => attachFails
                ? ApiResult.Failure(HttpStatusCode.Conflict, new ApiProblem { Detail = "Already attached." })
                : ApiResult.Success(HttpStatusCode.Created));
        ctx.Services.AddSingleton(properties.Object);

        var creator = new Mock<IContactQuickCreate>();
        creator.Setup(c => c.Resolve(It.IsAny<string?>())).Returns((string? id) => id);
        creator.Setup(c => c.WhenSettledAsync()).Returns(Task.CompletedTask);
        ctx.Services.AddSingleton(creator.Object);
        ctx.Services.AddSingleton(Mock.Of<IReferenceDataCache>());
        ctx.Services.AddSingleton(Mock.Of<IUploadLimitsCache>());
        ctx.Services.AddSingleton<AuthenticationStateProvider>(new SignedOut());

        var openChanges = new List<bool>();
        var attachedRaised = 0;
        var cut = ctx.Render<DialogHost>(p => p
            .Add(h => h.Property, House(type))
            .Add(h => h.Attached, attached ?? [])
            .Add(h => h.CanUpload, canUpload)
            .Add(h => h.OnOpenChanged, (bool open) => openChanges.Add(open))
            .Add(h => h.OnAttached, () => attachedRaised++));

        var harness = new Harness(cut, properties, files, posted, openChanges, () => attachedRaised);

        // OnInitializedAsync returns before the library read outside the browser; the seam is the way in.
        if (loadLibrary && !canUpload)
            cut.InvokeAsync(() => harness.Dialog.Instance.LoadLibraryAsync()).GetAwaiter().GetResult();

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
        Assert.NotEmpty(h.Host.FindAll(".prop-lib-row"));
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

        await h.Host.InvokeAsync(() => tabs.Instance.ValueChanged.InvokeAsync(PropertyAttachDialog.LibraryTab));

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

    [Theory]
    [InlineData(PropertyType.RealEstate, "the deed, the purchase agreement")]
    [InlineData(PropertyType.Vehicle, "the registration, an inspection")]
    public void The_subtitle_names_the_documents_a_property_of_that_type_keeps(PropertyType type, string expected)
    {
        var h = Render(type: type);

        Assert.Contains(expected, h.Markup, StringComparison.Ordinal);
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
        var h = Render(attached: [AttachedCopyOf(Linked)]);

        var row = h.Row(Linked);
        Assert.True(row.QuerySelector("button.prop-lib-main")!.HasAttribute("disabled"));
        Assert.Equal("Already attached", row.QuerySelector(".prop-lib-reason")!.TextContent);
        Assert.Equal(PropertyAttachDialog.LibraryState.AlreadyAttached, h.Dialog.Instance.StateOf(Linked));
    }

    /// <summary>A server-recorded content type off the shared allow-list is disabled with the reason in text.</summary>
    [Fact]
    public void A_file_off_the_allow_list_is_disabled_with_its_type_named()
    {
        var h = Render();

        var row = h.Row(Html);
        Assert.True(row.QuerySelector("button.prop-lib-main")!.HasAttribute("disabled"));
        Assert.Equal("HTML not accepted", row.QuerySelector(".prop-lib-reason")!.TextContent);
        Assert.Equal(PropertyAttachDialog.LibraryState.TypeNotAllowed, h.Dialog.Instance.StateOf(Html));
    }

    /// <summary>Every allow-listed type is attachable — the state reads the server's symbol, not a copy.</summary>
    [Fact]
    public void Every_allow_listed_content_type_is_available()
    {
        var h = Render();

        foreach (var type in DocumentContentTypes.Allowed)
        {
            var file = new FileListItem(Guid.NewGuid(), "x", type, 1, Base, null);
            Assert.Equal(PropertyAttachDialog.LibraryState.Available, h.Dialog.Instance.StateOf(file));
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
        Assert.Equal(expected, PropertyAttachDialog.ContentTypeShort(contentType));

    [Fact]
    public void A_disabled_row_cannot_be_picked()
    {
        var h = Render();

        h.Pick(Html);

        Assert.Empty(h.Host.FindComponents<OdsPropertyFileTypeSelect>());
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

    /// <summary>Picking a row reveals its type picker, seeded from the name — Other when nothing matches.</summary>
    [Fact]
    public void Picking_a_row_shows_its_type_picker_seeded_from_the_name()
    {
        var h = Render();

        h.Pick(Deed);
        h.Pick(Scan);

        var pickers = h.Host.FindComponents<OdsPropertyFileTypeSelect>();
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

        Assert.Empty(h.Host.FindComponents<OdsPropertyFileTypeSelect>());
        Assert.Equal("false", h.Row(Deed).QuerySelector("button.prop-lib-main")!.GetAttribute("aria-pressed"));
    }

    /// <summary>One request per picked file, carrying the file's id and the type the reader chose.</summary>
    [Fact]
    public async Task Submit_posts_one_attach_per_picked_file_then_closes()
    {
        var h = Render();

        h.Pick(Deed);
        h.Pick(Vognkort);
        var vognkortPicker = h.Host.FindComponents<OdsPropertyFileTypeSelect>()
            .Single(p => p.Instance.Value == nameof(PropertyFileType.Registration));
        await h.Host.InvokeAsync(() => vognkortPicker.Instance.ValueChanged.InvokeAsync(nameof(PropertyFileType.Insurance)));

        h.SubmitButton.Click();

        h.Host.WaitForAssertion(() => Assert.Equal(2, h.Posted.Count));
        Assert.Contains(h.Posted, r => r.FileMetadataId == Deed.Id && r.FileType == PropertyFileType.Deed);
        Assert.Contains(h.Posted, r => r.FileMetadataId == Vognkort.Id && r.FileType == PropertyFileType.Insurance);
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

    private static async Task SetDate(Harness h, string label, DateTime value)
    {
        var field = h.Host.FindComponents<OdsDateField>().Single(f => f.Instance.Label == label);
        await h.Host.InvokeAsync(() => field.Instance.ValueChanged.InvokeAsync(value));
    }

    private sealed class SignedOut : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity())));
    }

    /// <summary>The dialog beside its providers, in one render tree so the teleported body is the host's markup.</summary>
    public sealed class DialogHost : ComponentBase
    {
        [Parameter] public ExistingProperty Property { get; set; } = default!;
        [Parameter] public IReadOnlyList<ExistingPropertyFile> Attached { get; set; } = [];
        [Parameter] public bool CanUpload { get; set; }
        [Parameter] public Action<bool>? OnOpenChanged { get; set; }
        [Parameter] public Action? OnAttached { get; set; }

        private bool _open = true;

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<MudDialogProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<MudPopoverProvider>(1);
            builder.CloseComponent();
            builder.OpenComponent<PropertyAttachDialog>(2);
            builder.AddComponentParameter(3, nameof(PropertyAttachDialog.Property), Property);
            builder.AddComponentParameter(4, nameof(PropertyAttachDialog.Attached), Attached);
            builder.AddComponentParameter(5, nameof(PropertyAttachDialog.CanUpload), CanUpload);
            builder.AddComponentParameter(6, nameof(PropertyAttachDialog.Open), _open);
            builder.AddComponentParameter(7, nameof(PropertyAttachDialog.OpenChanged),
                EventCallback.Factory.Create<bool>(this, open =>
                {
                    _open = open;
                    OnOpenChanged?.Invoke(open);
                }));
            builder.AddComponentParameter(8, nameof(PropertyAttachDialog.OnAttached),
                EventCallback.Factory.Create(this, () => OnAttached?.Invoke()));
            builder.CloseComponent();
        }
    }
}
