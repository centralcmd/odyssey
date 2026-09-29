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

    private sealed record Harness(IRenderedComponent<DialogHost> Host, Mock<IPhotosApiClient> Photos, List<AttachPhotoItem> Submitted)
    {
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
        IReadOnlyList<Guid>? attached = null)
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
            .ReturnsAsync(ApiResult<PagedResult<PhotoSummary>>.Success(
                new PagedResult<PhotoSummary> { Items = [Fjord, Cabin, Market], TotalCount = 3 }, HttpStatusCode.OK));
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
            .Add(h => h.OnSubmit, items => submitted.AddRange(items)));

        if (!canUpload && canBrowse)
            cut.WaitForState(() => cut.FindAll("button.apd-tile").Count == 3);

        return new Harness(cut, photos, submitted);
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
