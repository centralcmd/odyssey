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
/// A photo already on the entry or album is disabled and says so in text. Since issue #428 both hosts name a
/// photo by its photo id, and an upload is one multipart call that creates the library photo.
/// </remarks>
public class AttachPhotosDialogTests
{
    static AttachPhotosDialogTests() => BunitContext.DefaultWaitTimeout = TimeSpan.FromSeconds(10);

    private static PhotoSummary Photo(string title) => new()
    {
        PhotoId = Guid.NewGuid(),
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
        Func<string?, Task<ApiResult<PagedResult<PhotoSummary>>>>? list = null)
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();

        var files = new Mock<IFilesApiClient>();
        ctx.Services.AddSingleton(files.Object);

        var photos = new Mock<IPhotosApiClient>();
        photos.Setup(p => p.ContentUrl(It.IsAny<Guid>())).Returns((Guid id) => $"http://localhost/api/photos/{id}/content");
        photos.Setup(p => p.ThumbnailUrl(It.IsAny<Guid>(), It.IsAny<ThumbnailSize>()))
            .Returns((Guid id, ThumbnailSize size) => $"http://localhost/api/photos/{id}/thumbnail?size={size}");
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
        if (canUpload) claims.Add(PermissionClaims.PhotosCreate);
        if (canBrowse) claims.Add(PermissionClaims.PhotosRead);
        ctx.Services.AddSingleton<AuthenticationStateProvider>(new SignedIn(claims));

        var submitted = new List<AttachPhotoItem>();
        var cut = ctx.Render<DialogHost>(p => p
            .Add(h => h.AttachedIds, attached ?? [])
            .Add(h => h.UploadOnly, uploadOnly)
            .Add(h => h.OnSubmit, items => submitted.AddRange(items)));

        if (!canUpload && canBrowse)
            cut.WaitForState(() => cut.FindAll("button.apd-tile").Count == 3);

        return new Harness(cut, photos, files, submitted, ctx.Services.GetRequiredService<ISnackbar>());
    }

    // ── Thumbnails (issue #365 §3.2 surface 6) ─────────────────────────────────

    private static PhotoSummary Ready(PhotoSummary p) => p with { ThumbnailsReady = true };

    [Fact]
    public void A_ready_photo_shows_its_small_thumbnail_and_a_not_ready_one_its_original()
    {
        var h = Render(list: _ => Task.FromResult(Page(Ready(Fjord), Cabin, Market)));

        Assert.Equal($"http://localhost/api/photos/{Fjord.PhotoId}/thumbnail?size=Small", h.Tile(Fjord).QuerySelector("img")!.GetAttribute("src"));
        Assert.Equal($"http://localhost/api/photos/{Cabin.PhotoId}/content", h.Tile(Cabin).QuerySelector("img")!.GetAttribute("src"));
    }

    [Fact]
    public void A_failed_thumbnail_falls_back_to_the_original_silently_and_the_tile_keeps_its_name_and_state()
    {
        var h = Render(list: _ => Task.FromResult(Page(Ready(Fjord), Cabin, Market)));
        var label = h.Tile(Fjord).GetAttribute("aria-label");

        h.Tile(Fjord).QuerySelector("img")!.TriggerEvent("onerror", EventArgs.Empty);

        Assert.Equal($"http://localhost/api/photos/{Fjord.PhotoId}/content", h.Tile(Fjord).QuerySelector("img")!.GetAttribute("src"));
        Assert.Equal(label, h.Tile(Fjord).GetAttribute("aria-label"));
        Assert.Equal("false", h.Tile(Fjord).GetAttribute("aria-pressed"));
        Assert.Empty(h.Toasts);
    }

    /// <summary>Issue #428 §5.2: the original is photo data, so a tile never asks the files endpoint for it.</summary>
    [Fact]
    public void No_tile_requests_an_image_from_the_files_endpoint()
    {
        var h = Render(list: _ => Task.FromResult(Page(Ready(Fjord), Cabin, Market)));

        Assert.NotNull(h.Tile(Cabin).QuerySelector("img"));
        Assert.DoesNotContain("/api/files/", h.Host.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void A_pick_carries_its_thumbnail_readiness_to_the_host()
    {
        var h = Render(list: _ => Task.FromResult(Page(Ready(Fjord), Cabin, Market)));

        h.Tile(Fjord).Click();
        h.SubmitButton.Click();

        Assert.True(Assert.Single(h.Submitted).ThumbnailsReady);
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

    [Fact]
    public void A_photo_already_added_is_disabled()
    {
        var h = Render(attached: [Fjord.PhotoId]);

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

    /// <summary>Submit hands back each pick in pick order and uploads nothing.</summary>
    [Fact]
    public void Submit_hands_back_the_picks_in_pick_order()
    {
        var h = Render();

        h.Tile(Market).Click();
        h.Tile(Fjord).Click();
        h.SubmitButton.Click();

        h.Host.WaitForAssertion(() => Assert.Equal(2, h.Submitted.Count));
        Assert.Equal([Market.PhotoId, Fjord.PhotoId], h.Submitted.Select(i => i.PhotoId));
        Assert.All(h.Submitted, i => Assert.Equal(AttachDocumentSource.Library, i.Source));
        h.Photos.Verify(p => p.UploadAsync(It.IsAny<ApiUpload>(), It.IsAny<NewPhotoUpload?>(), It.IsAny<CancellationToken>()), Times.Never);
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

    private static ApiResult<ExistingPhoto> Created(string name) =>
        ApiResult<ExistingPhoto>.Success(new ExistingPhoto
        {
            PhotoId = Guid.NewGuid(), FileName = name, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        }, HttpStatusCode.Created);

    /// <summary>
    /// A 409 from the upload (the same image is already a photo, issue #428 §4.3) is named with its way out and the
    /// dialog stays open — never a silent no-op that reads as a dead button.
    /// </summary>
    [Fact]
    public async Task An_upload_already_in_the_library_is_named_and_the_dialog_stays_open()
    {
        var h = Render(canUpload: true);
        h.Photos.Setup(p => p.UploadAsync(It.IsAny<ApiUpload>(), It.IsAny<NewPhotoUpload?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResult<ExistingPhoto>.Failure(HttpStatusCode.Conflict, new ApiProblem { Detail = "exists" }));

        await h.Pick(Image("fjord.jpg"));
        h.Footer.Click();

        h.Host.WaitForAssertion(() => Assert.Contains(h.Toasts, t => t.Contains("already in your library", StringComparison.Ordinal)));
        Assert.Empty(h.Submitted);
        Assert.Contains("wasn’t added", h.Host.Markup, StringComparison.Ordinal);
    }

    /// <summary>An upload is one call that creates the library photo, and the item carries the new photo's id.</summary>
    [Fact]
    public async Task An_upload_creates_the_photo_in_one_call_and_hands_back_its_id()
    {
        var h = Render(canUpload: true);
        var created = Created("hike.jpg");
        h.Photos.Setup(p => p.UploadAsync(It.IsAny<ApiUpload>(), It.IsAny<NewPhotoUpload?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(created);

        await h.Pick(Image("hike.jpg"));
        h.Footer.Click();

        h.Host.WaitForAssertion(() => Assert.Single(h.Submitted));
        Assert.Equal(created.Value!.PhotoId, h.Submitted[0].PhotoId);
        Assert.Equal(AttachDocumentSource.Upload, h.Submitted[0].Source);
        h.Files.Verify(f => f.UploadAsync(It.IsAny<ApiUpload>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>A retry after a partly failed batch neither re-adds what made it nor uploads any photo twice.</summary>
    [Fact]
    public async Task A_retry_does_not_upload_the_photos_that_already_made_it()
    {
        var h = Render(canUpload: true);
        var failCabin = true;
        h.Photos.Setup(p => p.UploadAsync(It.IsAny<ApiUpload>(), It.IsAny<NewPhotoUpload?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ApiUpload u, NewPhotoUpload? _, CancellationToken _) =>
            {
                if (u.FileName == "cabin.jpg" && failCabin)
                {
                    failCabin = false;
                    return ApiResult<ExistingPhoto>.Failure(HttpStatusCode.InternalServerError, new ApiProblem { Detail = "boom" });
                }
                return Created(u.FileName);
            });

        await h.Pick(Image("fjord.jpg"), Image("cabin.jpg"));
        h.Footer.Click();
        h.Host.WaitForAssertion(() => Assert.Single(h.Submitted));

        h.Footer.Click();
        h.Host.WaitForAssertion(() => Assert.Equal(2, h.Submitted.Count));
        h.Photos.Verify(p => p.UploadAsync(It.IsAny<ApiUpload>(), It.IsAny<NewPhotoUpload?>(), It.IsAny<CancellationToken>()), Times.Exactly(3));
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
            builder.CloseComponent();
        }
    }
}
