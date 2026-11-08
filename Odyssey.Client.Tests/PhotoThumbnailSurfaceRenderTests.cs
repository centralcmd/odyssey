using System.Net;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using Moq;
using Odyssey.ApiClient;
using Odyssey.ApiClient.Resources;
using Odyssey.Client.Components;
using Odyssey.Client.Pages.Journal;
using Odyssey.Client.Pages.Photos;
using Odyssey.Client.Services;
using Odyssey.Dtos;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos.Finance;
using Odyssey.Dtos.Journal;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// Issue #365 AC 1, 2, 5 and 8 on the rendered surfaces: a ready photo shows its thumbnail at the surface's size, a
/// not-ready one its original, and neither appears without the claim that licenses it. Complements the source-lint
/// (<see cref="PhotoThumbnailSurfaceTests"/>), which keeps a surface from building a URL another way.
/// </summary>
public class PhotoThumbnailSurfaceRenderTests
{
    private static readonly string[] BothClaims =
    [
        PermissionClaims.PhotosRead, PermissionClaims.FilesRead, PermissionClaims.PhotoAlbumsRead,
        PermissionClaims.PhotoAlbumsUpdate, PermissionClaims.PhotoAlbumsCreate,
    ];

    private static readonly PhotoSummary Ready = Summary("Ready", ready: true);
    private static readonly PhotoSummary NotReady = Summary("Not ready", ready: false);

    private static PhotoSummary Summary(string title, bool ready) => new()
    {
        PhotoId = Guid.NewGuid(), Title = title, PersonCount = 0, AlbumCount = 0, ThumbnailsReady = ready,
    };

    private static string Thumb(Guid photoId, ThumbnailSize size) => $"/thumb/{photoId}/{size}";

    private static string Original(Guid photoId) => $"/photos/{photoId}/content";

    private static (Mock<IPhotosApiClient> Photos, Mock<IFilesApiClient> Files, Mock<IAlbumsApiClient> Albums) Clients(BunitContext ctx)
    {
        var photos = new Mock<IPhotosApiClient> { DefaultValueProvider = new PageTestContext.EmptySuccess() };
        photos.Setup(p => p.ThumbnailUrl(It.IsAny<Guid>(), It.IsAny<ThumbnailSize>())).Returns((Guid id, ThumbnailSize size) => Thumb(id, size));
        photos.Setup(p => p.ContentUrl(It.IsAny<Guid>())).Returns((Guid id) => Original(id));
        photos.Setup(p => p.ListAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string?>(),
                It.IsAny<IReadOnlyCollection<string>?>(), It.IsAny<IReadOnlyCollection<string>?>(), It.IsAny<IReadOnlyCollection<string>?>(),
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<bool>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResult<PagedResult<PhotoSummary>>.Success(
                new PagedResult<PhotoSummary> { Items = [Ready, NotReady], TotalCount = 2, Offset = 0, Limit = 96 }, HttpStatusCode.OK));
        var files = new Mock<IFilesApiClient> { DefaultValueProvider = new PageTestContext.EmptySuccess() };
        var albums = new Mock<IAlbumsApiClient> { DefaultValueProvider = new PageTestContext.EmptySuccess() };
        ctx.Services.AddSingleton(photos.Object);
        ctx.Services.AddSingleton(files.Object);
        ctx.Services.AddSingleton(albums.Object);
        return (photos, files, albums);
    }

    /// <summary>A dialog renders inside the dialog provider, so the two are rendered together and queried as one.</summary>
    private static IRenderedComponent<Bunit.Rendering.ContainerFragment> InDialogHost<TDialog>(
        BunitContext ctx, Action<Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder> parameters)
        where TDialog : Microsoft.AspNetCore.Components.IComponent =>
        ctx.Render(b =>
        {
            b.OpenComponent<MudDialogProvider>(0);
            b.CloseComponent();
            b.OpenComponent<MudPopoverProvider>(10);
            b.CloseComponent();
            b.OpenComponent<TDialog>(20);
            parameters(b);
            b.CloseComponent();
        });

    // ── Library grid (Medium) ─────────────────────────────────────────────────

    [Fact]
    public async Task The_library_grid_shows_a_ready_tile_as_its_medium_thumbnail_and_the_other_as_its_original()
    {
        await using var ctx = PageTestContext.Create(BothClaims);
        Clients(ctx);
        ctx.Render<MudPopoverProvider>();

        var cut = ctx.Render<PhotosCard>();
        cut.WaitForState(() => cut.FindAll("img.pl-tile-img").Count == 2);

        var srcs = cut.FindAll("img.pl-tile-img").Select(i => i.GetAttribute("src")).ToList();
        Assert.Equal([Thumb(Ready.PhotoId, ThumbnailSize.Medium), Original(NotReady.PhotoId)], srcs);
    }

    [Fact]
    public async Task Photos_read_alone_shows_every_tile_and_never_asks_the_files_endpoint()
    {
        await using var ctx = PageTestContext.Create(PermissionClaims.PhotosRead);
        Clients(ctx);
        ctx.Render<MudPopoverProvider>();

        var cut = ctx.Render<PhotosCard>();
        cut.WaitForState(() => cut.FindAll("img.pl-tile-img").Count == 2);

        Assert.Empty(cut.FindAll(".pl-tile-ph"));
        Assert.DoesNotContain("/files/", cut.Markup, StringComparison.Ordinal);
    }

    // ── Album covers (Medium) and the add-to-album list (Small) ───────────────

    private static PhotoAlbumSummary Album(string name, PhotoSummary cover) => new()
    {
        PhotoAlbumId = Guid.NewGuid(), Name = name, PhotoCount = 1,
        CoverPhotoId = cover.PhotoId, CoverThumbnailsReady = cover.ThumbnailsReady,
    };

    [Fact]
    public async Task Album_covers_are_images_at_medium_and_without_photos_read_carry_no_thumbnail()
    {
        var albums = new List<PhotoAlbumSummary> { Album("Trip", Ready), Album("Home", NotReady) };

        await using (var ctx = PageTestContext.Create(BothClaims))
        {
            var (_, _, client) = Clients(ctx);
            client.Setup(a => a.ListAllAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(ApiResult<List<PhotoAlbumSummary>>.Success(albums, HttpStatusCode.OK));
            ctx.Render<MudPopoverProvider>();
            var cut = ctx.Render<AlbumsPage>();
            cut.WaitForState(() => cut.FindAll(".pl-albcover-cell img").Count == 2);

            Assert.Equal([Thumb(Ready.PhotoId, ThumbnailSize.Medium), Original(NotReady.PhotoId)],
                cut.FindAll(".pl-albcover-cell img").Select(i => i.GetAttribute("src")));
            Assert.Empty(cut.FindAll(".pl-albcover-cell[style*=url]"));
        }

        // files.read reaches no photo since issue #428: without photos.read a cover carries no image at all.
        await using (var ctx = PageTestContext.Create(PermissionClaims.PhotoAlbumsRead, PermissionClaims.FilesRead))
        {
            var (_, _, client) = Clients(ctx);
            client.Setup(a => a.ListAllAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(ApiResult<List<PhotoAlbumSummary>>.Success(albums, HttpStatusCode.OK));
            ctx.Render<MudPopoverProvider>();
            var cut = ctx.Render<AlbumsPage>();
            cut.WaitForState(() => cut.FindAll(".pl-albcover-cell").Count == 2);

            Assert.Empty(cut.FindAll(".pl-albcover-cell img"));
            Assert.DoesNotContain("/thumb/", cut.Markup, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task The_add_to_album_list_shows_each_cover_at_small_with_alt_empty()
    {
        await using var ctx = PageTestContext.Create(BothClaims);
        var (_, _, client) = Clients(ctx);
        client.Setup(a => a.ListAllAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResult<List<PhotoAlbumSummary>>.Success([Album("Trip", Ready), Album("Home", NotReady)], HttpStatusCode.OK));

        var cut = InDialogHost<AddToAlbumDialog>(ctx, b =>
        {
            b.AddComponentParameter(1, nameof(AddToAlbumDialog.Open), true);
            b.AddComponentParameter(2, nameof(AddToAlbumDialog.PhotoIds), (IReadOnlyCollection<Guid>)[Guid.NewGuid()]);
        });
        cut.WaitForState(() => cut.FindAll(".pl-albrow-cover img").Count == 2);

        var images = cut.FindAll(".pl-albrow-cover img");
        Assert.Equal([Thumb(Ready.PhotoId, ThumbnailSize.Small), Original(NotReady.PhotoId)], images.Select(i => i.GetAttribute("src")));
        Assert.All(images, i => Assert.Equal(string.Empty, i.GetAttribute("alt")));
        // The row's accessible name and pressed state come from the button, not the image.
        Assert.All(cut.FindAll("button.pl-albrow"), b => Assert.Equal("false", b.GetAttribute("aria-pressed")));
    }

    // ── Album form members (Small) ────────────────────────────────────────────

    [Fact]
    public async Task The_album_form_lists_its_members_at_small()
    {
        await using var ctx = PageTestContext.Create(BothClaims);
        var (_, _, client) = Clients(ctx);
        var albumId = Guid.NewGuid();
        client.Setup(a => a.GetAsync(albumId, It.IsAny<CancellationToken>())).ReturnsAsync(new ExistingPhotoAlbum
        {
            PhotoAlbumId = albumId, Name = "Trip", PhotoIds = [Ready.PhotoId, NotReady.PhotoId],
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });

        var cut = InDialogHost<AlbumFormDialog>(ctx, b =>
        {
            b.AddComponentParameter(1, nameof(AlbumFormDialog.Open), true);
            b.AddComponentParameter(2, nameof(AlbumFormDialog.AlbumId), (Guid?)albumId);
        });
        cut.WaitForState(() => cut.FindAll(".pl-memthumb img").Count == 2);

        Assert.Equal([Thumb(Ready.PhotoId, ThumbnailSize.Small), Original(NotReady.PhotoId)],
            cut.FindAll(".pl-memthumb img").Select(i => i.GetAttribute("src")));
    }

    // ── Journal form strip (Small) ────────────────────────────────────────────

    [Fact]
    public async Task The_journal_form_strip_shows_a_ready_draft_photo_at_small_and_falls_back_silently()
    {
        await using var ctx = PageTestContext.Create(PermissionClaims.PhotosRead, PermissionClaims.FilesRead);
        Clients(ctx);
        ctx.Render<MudPopoverProvider>();
        var ready = new JournalDraftPhoto(Ready.PhotoId, "ready.jpg", ThumbnailsReady: true);
        var draft = new JournalDraftPhoto(NotReady.PhotoId, "draft.jpg", ThumbnailsReady: false);

        var cut = ctx.Render<JournalEntryFields>(p => p.Add(f => f.Draft, new JournalEntryDraft { Photos = [ready, draft] }));
        cut.WaitForState(() => cut.FindAll(".jef-photo img").Count == 2);

        Assert.Equal([Thumb(Ready.PhotoId, ThumbnailSize.Small), Original(NotReady.PhotoId)],
            cut.FindAll(".jef-photo img").Select(i => i.GetAttribute("src")));
        var removeLabel = cut.Find(".jef-photo-remove").GetAttribute("aria-label");

        cut.Find(".jef-photo img").TriggerEvent("onerror", EventArgs.Empty);

        Assert.Equal(Original(Ready.PhotoId), cut.Find(".jef-photo img").GetAttribute("src"));
        Assert.Equal(removeLabel, cut.Find(".jef-photo-remove").GetAttribute("aria-label"));
        Assert.Empty(cut.FindAll("[role=alert]"));
    }

    // ── Journal gallery gating (B5) ───────────────────────────────────────────

    private static JournalEntryPhotoDto Link(PhotoSummary p, int position) => new()
    {
        JournalEntryPhotoId = Guid.NewGuid(), PhotoId = p.PhotoId, Title = p.Title, Position = position,
        CreatedAt = DateTime.UtcNow, ThumbnailsReady = p.ThumbnailsReady,
    };

    [Fact]
    public void The_journal_gallery_gives_a_photos_read_caller_both_sources_and_no_access_only_without_the_claim()
    {
        using var ctx = new BunitContext();
        var (photos, _, _) = Clients(ctx);
        var links = new[] { Link(Ready, 0), Link(NotReady, 1) };

        // photos.read: the thumbnail where ready, the original always, and the label from the link (issue #428).
        var gallery = JournalCard.GalleryPhotos(links, new PhotoImageAccess(true), photos.Object);
        Assert.Equal(new PhotoImageSource(Thumb(Ready.PhotoId, ThumbnailSize.Small), Original(Ready.PhotoId)), gallery[0].Image);
        Assert.Equal(new PhotoImageSource(null, Original(NotReady.PhotoId)), gallery[1].Image);
        Assert.Equal(Ready.Title, gallery[0].Name);
        Assert.All(gallery, g => Assert.False(g.Unavailable));

        // Without it there is no source at all, and the tile says "no access".
        var none = JournalCard.GalleryPhotos(links, new PhotoImageAccess(false), photos.Object);
        Assert.All(none, g => Assert.True(g.Image.IsEmpty && g.Unavailable));
    }
}
