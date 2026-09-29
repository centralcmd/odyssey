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
using Odyssey.Client.Services;
using Odyssey.Dtos;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos.Finance;
using Odyssey.Dtos.Journal;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// The photo twin of the "Attach documents" dialog (design system · AttachPhotosModal.jsx): "Upload new"
/// and "From Photos" over the one library, used by the journal entry dialog and the album form, and in
/// its upload-only form by the Photos page.
/// </summary>
/// <remarks>
/// A photo already on the entry or album is disabled and says so in text. The already-added match runs
/// against both a photo's id and its file id, because an album holds the one and a journal entry the
/// other — a match on only one would let the journal re-add a photo it already shows.
/// </remarks>
public class AttachPhotosDialogTests
{
    static AttachPhotosDialogTests() => BunitContext.DefaultWaitTimeout = TimeSpan.FromSeconds(10);

    private static PhotoSummary Photo(string title) => new()
    {
        PhotoId = Guid.NewGuid(),
        FileId = Guid.NewGuid(),
        Title = title,
        PersonCount = 0,
        AlbumCount = 0,
    };

    private static readonly PhotoSummary Fjord = Photo("Geirangerfjord");
    private static readonly PhotoSummary Cabin = Photo("Cabin at dusk");
    private static readonly PhotoSummary Market = Photo("Fish market");

    private sealed record Harness(IRenderedComponent<DialogHost> Host, Mock<IPhotosApiClient> Photos, Mock<IFilesApiClient> Files,
        List<AttachPhotoItem> Submitted, ISnackbar Snackbar)
    {
        public IEnumerable<string> Toasts => Snackbar.ShownSnackbars.Select(t => t.Message ?? string.Empty);

        public Task Search(string text) =>
            Host.InvokeAsync(() => Host.FindComponent<OdsSearchField>().Instance.ValueChanged.InvokeAsync(text));

        public Task Pick(params OdsUploadFile[] files) =>
            Host.InvokeAsync(() => Host.FindComponent<OdsFileUpload>().Instance.FilesChanged.InvokeAsync(files));

        public AngleSharp.Dom.IElement Footer => Host.FindAll(".ods-modal-foot button").Last();

        public AngleSharp.Dom.IElement Tile(PhotoSummary p) =>
            Host.FindAll("button.apd-tile").Single(t => t.GetAttribute("title") == p.Title);

        public AngleSharp.Dom.IElement SubmitButton =>
            Host.FindAll("button").Last(b => b.TextContent.Contains("Add photo", StringComparison.Ordinal)
                                            || b.TextContent.Contains("Add 2 photos", StringComparison.Ordinal));
    }

    private static Harness Render(
        bool canUpload = false,
        bool canBrowse = true,
        bool uploadOnly = false,
        IReadOnlyList<Guid>? attached = null,
        bool createLibraryPhoto = true,
        Func<string?, Task<ApiResult<PagedResult<PhotoSummary>>>>? list = null)
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();

        var files = new Mock<IFilesApiClient>();
        files.Setup(f => f.ContentUrl(It.IsAny<Guid>())).Returns((Guid id) => $"http://localhost/api/files/{id}/content");
        ctx.Services.AddSingleton(files.Object);

        var photos = new Mock<IPhotosApiClient>();
        photos.Setup(p => p.ListAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string?>(),
                It.IsAny<IReadOnlyCollection<string>?>(), It.IsAny<IReadOnlyCollection<string>?>(), It.IsAny<IReadOnlyCollection<string>?>(),
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<bool>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .Returns((int _, int _, string? search, IReadOnlyCollection<string>? _, IReadOnlyCollection<string>? _, IReadOnlyCollection<string>? _,
                    DateTime? _, DateTime? _, bool _, string? _, string? _, string? _, CancellationToken _) =>
                list?.Invoke(search) ?? Task.FromResult(Page(Fjord, Cabin, Market)));
        ctx.Services.AddSingleton(photos.Object);

        var limits = new Mock<IUploadLimitsCache>();
        limits.Setup(l => l.GetAsync(It.IsAny<CancellationToken>())).ReturnsAsync(UploadLimitsCache.Fallback);
        ctx.Services.AddSingleton(limits.Object);

        var claims = new List<string>();
        if (canUpload) claims.AddRange([PermissionClaims.FilesCreate, PermissionClaims.PhotosCreate]);
        if (canBrowse) claims.Add(PermissionClaims.PhotosRead);
        ctx.Services.AddSingleton<AuthenticationStateProvider>(new SignedIn(claims));

        var submitted = new List<AttachPhotoItem>();
        var cut = ctx.Render<DialogHost>(p => p
            .Add(h => h.AttachedIds, attached ?? [])
            .Add(h => h.UploadOnly, uploadOnly)
            .Add(h => h.CreateLibraryPhoto, createLibraryPhoto)
            .Add(h => h.OnSubmit, items => submitted.AddRange(items)));

        if (!canUpload && canBrowse)
            cut.WaitForState(() => cut.FindAll("button.apd-tile").Count == 3);

        return new Harness(cut, photos, files, submitted, ctx.Services.GetRequiredService<ISnackbar>());
    }

    [Fact]
    public void With_both_claims_it_opens_on_upload_with_the_two_tabs()
    {
        var h = Render(canUpload: true);

        var tabs = h.Host.FindComponent<OdsSegmentedControl>();
        Assert.Equal(["Upload new", "From Photos"], tabs.Instance.Options.Select(o => o.Label));
        Assert.Single(h.Host.FindComponents<OdsFileUpload>());
        Assert.Contains("Add photos", h.Host.Markup, StringComparison.Ordinal);
    }

    /// <summary>The Photos page's upload is upload only: its own title, no tabs, and the library is never read.</summary>
    [Fact]
    public void Upload_only_has_no_tabs_and_never_reads_the_library()
    {
        var h = Render(canUpload: true, uploadOnly: true);

        Assert.Empty(h.Host.FindComponents<OdsSegmentedControl>());
        Assert.Contains("Upload photos", h.Host.Markup, StringComparison.Ordinal);
        h.Photos.Verify(p => p.ListAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string?>(),
            It.IsAny<IReadOnlyCollection<string>?>(), It.IsAny<IReadOnlyCollection<string>?>(), It.IsAny<IReadOnlyCollection<string>?>(),
            It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<bool>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public void Without_upload_claims_it_opens_on_the_library()
    {
        var h = Render();

        Assert.Empty(h.Host.FindComponents<OdsSegmentedControl>());
        Assert.Empty(h.Host.FindComponents<OdsFileUpload>());
        Assert.Equal(3, h.Host.FindAll("button.apd-tile").Count);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_photo_already_added_is_disabled_whether_named_by_photo_or_file_id(bool byPhotoId)
    {
        var h = Render(attached: [byPhotoId ? Fjord.PhotoId : Fjord.FileId]);

        var tile = h.Tile(Fjord);
        Assert.True(tile.HasAttribute("disabled"));
        Assert.Equal("Already added", tile.QuerySelector(".apd-added")!.TextContent);
        Assert.Contains("already added", tile.GetAttribute("aria-label"), StringComparison.Ordinal);
        Assert.False(h.Tile(Cabin).HasAttribute("disabled"));
    }

    [Fact]
    public void Picking_toggles_the_tile_and_the_count()
    {
        var h = Render();

        h.Tile(Cabin).Click();
        h.Tile(Market).Click();

        Assert.Equal("true", h.Tile(Cabin).GetAttribute("aria-pressed"));
        Assert.Contains("Add 2 photos", h.Host.Markup, StringComparison.Ordinal);

        h.Tile(Market).Click();
        Assert.Equal("false", h.Tile(Market).GetAttribute("aria-pressed"));
        Assert.Contains("Add photo", h.Host.Markup, StringComparison.Ordinal);
    }

    /// <summary>Submit hands back each pick with both ids, in pick order, and never touches the library.</summary>
    [Fact]
    public void Submit_hands_back_the_picks_with_both_ids_in_pick_order()
    {
        var h = Render();

        h.Tile(Market).Click();
        h.Tile(Fjord).Click();
        h.SubmitButton.Click();

        h.Host.WaitForAssertion(() => Assert.Equal(2, h.Submitted.Count));
        Assert.Equal([Market.PhotoId, Fjord.PhotoId], h.Submitted.Select(i => i.PhotoId));
        Assert.Equal([Market.FileId, Fjord.FileId], h.Submitted.Select(i => i.FileId));
        Assert.All(h.Submitted, i => Assert.Equal(AttachDocumentSource.Library, i.Source));
        h.Photos.Verify(p => p.CreateAsync(It.IsAny<NewPhoto>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public void Nothing_picked_leaves_the_submit_disabled()
    {
        var h = Render();

        Assert.True(h.SubmitButton.HasAttribute("disabled"));
    }

    private static ApiResult<PagedResult<PhotoSummary>> Page(params PhotoSummary[] items) =>
        ApiResult<PagedResult<PhotoSummary>>.Success(new PagedResult<PhotoSummary> { Items = items, TotalCount = items.Length }, HttpStatusCode.OK);

    private static OdsUploadFile Image(string name) => new()
    {
        Uid = Guid.NewGuid().ToString(),
        Name = name,
        Kind = "Image",
        SizeBytes = 10_000,
        Source = new FakeBrowserFile(name, 10_000, "image/jpeg"),
    };

    /// <summary>A pick survives a search that takes its photo off the page — it is still submitted.</summary>
    [Fact]
    public async Task A_pick_survives_a_search_that_hides_it()
    {
        var h = Render(list: search => Task.FromResult(search is null ? Page(Fjord, Cabin, Market) : Page(Market)));

        h.Tile(Fjord).Click();
        await h.Search("fish");
        h.Host.WaitForAssertion(() => Assert.Single(h.Host.FindAll("button.apd-tile")));
        h.Tile(Market).Click();
        h.Footer.Click();

        h.Host.WaitForAssertion(() => Assert.Equal([Fjord.PhotoId, Market.PhotoId], h.Submitted.Select(i => i.PhotoId)));
    }

    /// <summary>Latest wins: a slow earlier search that resolves last does not overwrite the newer result.</summary>
    [Fact]
    public async Task A_slow_earlier_search_does_not_overwrite_a_newer_one()
    {
        var slow = new TaskCompletionSource<ApiResult<PagedResult<PhotoSummary>>>();
        var h = Render(list: search => search switch
        {
            null => Task.FromResult(Page(Fjord, Cabin, Market)),
            "cab" => slow.Task,
            _ => Task.FromResult(Page(Market)),
        });

        var first = h.Search("cab");
        await h.Search("fish");
        slow.SetResult(Page(Cabin));
        await first;

        h.Host.WaitForAssertion(() => Assert.Equal([Market.Title], h.Host.FindAll("button.apd-tile").Select(t => t.GetAttribute("title"))));
    }

    /// <summary>The result count is announced, so a screen-reader user hears what the search did (WCAG 4.1.3).</summary>
    [Fact]
    public async Task Search_results_are_announced()
    {
        var h = Render(list: search => Task.FromResult(search is null ? Page(Fjord, Cabin, Market) : Page()));

        Assert.Contains("3 photos shown.", h.Host.Find("[role=status].sr-only").TextContent, StringComparison.Ordinal);
        await h.Search("zzz");
        h.Host.WaitForAssertion(() =>
            Assert.Contains("No photos match “zzz”.", h.Host.Find("[role=status].sr-only").TextContent, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_failed_library_read_says_so()
    {
        var h = Render(canUpload: true, list: _ => Task.FromResult(
            ApiResult<PagedResult<PhotoSummary>>.Failure(HttpStatusCode.InternalServerError, new ApiProblem { Detail = "boom" })));

        await h.Host.InvokeAsync(() => h.Host.FindComponent<OdsSegmentedControl>().Instance.ValueChanged.InvokeAsync(AttachPhotosDialog.LibraryTab));

        h.Host.WaitForAssertion(() => Assert.Contains("Couldn’t load your photos.", h.Host.Markup, StringComparison.Ordinal));
    }

    /// <summary>
    /// A 409 from creating the library photo (the file is already one) is named with its way out and the
    /// dialog stays open — never a silent no-op that reads as a dead button.
    /// </summary>
    [Fact]
    public async Task An_upload_already_in_the_library_is_named_and_the_dialog_stays_open()
    {
        var h = Render(canUpload: true);
        h.Files.Setup(f => f.UploadAsync(It.IsAny<ApiUpload>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FileUploadResponse(Guid.NewGuid(), "fjord.jpg", "image/jpeg", 10, "hash", DateTime.UtcNow, null));
        h.Photos.Setup(p => p.CreateAsync(It.IsAny<NewPhoto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResult<ExistingPhoto>.Failure(HttpStatusCode.Conflict, new ApiProblem { Detail = "exists" }));

        await h.Pick(Image("fjord.jpg"));
        h.Footer.Click();

        h.Host.WaitForAssertion(() => Assert.Contains(h.Toasts, t => t.Contains("already in your library", StringComparison.Ordinal)));
        Assert.Empty(h.Submitted);
        Assert.Contains("wasn’t added", h.Host.Markup, StringComparison.Ordinal);
    }

    /// <summary>
    /// The journal's photos upload without creating a library photo — the entry write finds or creates it —
    /// so the item carries the stored file id and no photo id.
    /// </summary>
    [Fact]
    public async Task Without_a_library_photo_an_upload_hands_back_only_the_file()
    {
        var stored = Guid.NewGuid();
        var h = Render(canUpload: true, createLibraryPhoto: false);
        h.Files.Setup(f => f.UploadAsync(It.IsAny<ApiUpload>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FileUploadResponse(stored, "hike.jpg", "image/jpeg", 10, "hash", DateTime.UtcNow, null));

        await h.Pick(Image("hike.jpg"));
        h.Footer.Click();

        h.Host.WaitForAssertion(() => Assert.Single(h.Submitted));
        Assert.Equal(stored, h.Submitted[0].FileId);
        Assert.Null(h.Submitted[0].PhotoId);
        h.Photos.Verify(p => p.CreateAsync(It.IsAny<NewPhoto>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>A retry after a partly failed batch neither re-adds what made it nor stores any file twice.</summary>
    [Fact]
    public async Task A_retry_does_not_upload_the_photos_that_already_made_it()
    {
        var h = Render(canUpload: true);
        h.Files.Setup(f => f.UploadAsync(It.IsAny<ApiUpload>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ApiUpload u, string? _, CancellationToken _) =>
                new FileUploadResponse(Guid.NewGuid(), u.FileName, "image/jpeg", 10, "hash", DateTime.UtcNow, null));
        var failCabin = true;
        var calls = 0;
        h.Photos.Setup(p => p.CreateAsync(It.IsAny<NewPhoto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                calls++;
                if (calls == 2 && failCabin)
                {
                    failCabin = false;
                    return ApiResult<ExistingPhoto>.Failure(HttpStatusCode.InternalServerError, new ApiProblem { Detail = "boom" });
                }
                return ApiResult<ExistingPhoto>.Success(new ExistingPhoto { PhotoId = Guid.NewGuid(), FileId = Guid.NewGuid(), CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow }, HttpStatusCode.Created);
            });

        await h.Pick(Image("fjord.jpg"), Image("cabin.jpg"));
        h.Footer.Click();
        h.Host.WaitForAssertion(() => Assert.Single(h.Submitted));

        h.Footer.Click();
        h.Host.WaitForAssertion(() => Assert.Equal(2, h.Submitted.Count));
        // Each file is stored once: the retry only re-runs cabin's refused library-photo step.
        h.Files.Verify(f => f.UploadAsync(It.IsAny<ApiUpload>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
        h.Photos.Verify(p => p.CreateAsync(It.IsAny<NewPhoto>(), It.IsAny<CancellationToken>()), Times.Exactly(3));
        Assert.Equal(["fjord.jpg", "cabin.jpg"], h.Submitted.Select(i => i.Name));
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
        [Parameter] public bool UploadOnly { get; set; }
        [Parameter] public bool CreateLibraryPhoto { get; set; } = true;
        [Parameter] public Action<IReadOnlyList<AttachPhotoItem>>? OnSubmit { get; set; }

        private bool _open = true;

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<MudDialogProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<MudPopoverProvider>(1);
            builder.CloseComponent();
            builder.OpenComponent<AttachPhotosDialog>(2);
            builder.AddComponentParameter(3, nameof(AttachPhotosDialog.Subtitle), "Add photos to this entry.");
            builder.AddComponentParameter(4, nameof(AttachPhotosDialog.AttachedIds), AttachedIds);
            builder.AddComponentParameter(5, nameof(AttachPhotosDialog.UploadOnly), UploadOnly);
            builder.AddComponentParameter(6, nameof(AttachPhotosDialog.Open), _open);
            builder.AddComponentParameter(7, nameof(AttachPhotosDialog.OpenChanged),
                EventCallback.Factory.Create<bool>(this, open => _open = open));
            builder.AddComponentParameter(8, nameof(AttachPhotosDialog.OnSubmit),
                EventCallback.Factory.Create<IReadOnlyList<AttachPhotoItem>>(this, items => OnSubmit?.Invoke(items)));
            builder.AddComponentParameter(9, nameof(AttachPhotosDialog.CreateLibraryPhoto), CreateLibraryPhoto);
            builder.CloseComponent();
        }
    }
}
