using System.Net;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MudBlazor;
using Odyssey.ApiClient;
using Odyssey.ApiClient.Resources;
using Odyssey.Client.Components;
using Odyssey.Client.Pages.Journal;
using Odyssey.Client.Pages.Photos;
using Odyssey.Dtos;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos.Journal;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// A tag created inline in the Edit photo dialog exists only on the server until the host reloads its
/// tag names. The photo detail view resolves a tag id through those names and shows "—" for one it
/// cannot name, so a save that refreshed only the photos left the new tag unnamed until a page reload.
/// </summary>
public sealed class PhotoEditTagRefreshTests
{
    static PhotoEditTagRefreshTests() => BunitContext.DefaultWaitTimeout = TimeSpan.FromSeconds(10);

    private static readonly Guid HikingId = Guid.NewGuid();
    private static readonly Guid PhotoId = Guid.NewGuid();

    // The first load predates the inline create; every later one includes the new tag.
    private static Mock<IPhotoTagsApiClient> TagsGainingHiking()
    {
        var tags = new Mock<IPhotoTagsApiClient> { DefaultValueProvider = new PageTestContext.EmptySuccess() };
        tags.SetupSequence(t => t.ListAllAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResult<List<ExistingPhotoTag>>.Success([], HttpStatusCode.OK))
            .ReturnsAsync(ApiResult<List<ExistingPhotoTag>>.Success(
                [new ExistingPhotoTag { PhotoTagId = HikingId, Name = "Hiking", Icon = "hiking" }], HttpStatusCode.OK));
        return tags;
    }

    [Fact]
    public async Task Library_the_detail_view_names_a_tag_created_in_the_edit_dialog()
    {
        await using var ctx = PageTestContext.Create(PermissionClaims.PhotosRead, PermissionClaims.PhotosUpdate);
        ctx.Services.AddSingleton(TagsGainingHiking().Object);

        var photos = new Mock<IPhotosApiClient> { DefaultValueProvider = new PageTestContext.EmptySuccess() };
        photos.Setup(p => p.ListAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string?>(),
                It.IsAny<IReadOnlyCollection<string>?>(), It.IsAny<IReadOnlyCollection<string>?>(), It.IsAny<IReadOnlyCollection<string>?>(),
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<bool>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResult<PagedResult<PhotoSummary>>.Success(new PagedResult<PhotoSummary>
            {
                Items = [new PhotoSummary { PhotoId = PhotoId, Title = "Summit", PersonCount = 0, AlbumCount = 0 }],
                TotalCount = 1,
                Offset = 0,
                Limit = 24,
            }, HttpStatusCode.OK));
        photos.Setup(p => p.GetAsync(PhotoId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExistingPhoto
            {
                PhotoId = PhotoId,
                Title = "Summit",
                CreatedAt = new DateTime(2026, 9, 1),
                UpdatedAt = new DateTime(2026, 9, 1),
                TagIds = [HikingId],
            });
        ctx.Services.AddSingleton(photos.Object);

        // The detail view is a MudDialog, which renders into the provider rather than the page.
        var dialogs = ctx.Render<MudDialogProvider>();
        ctx.Render<MudPopoverProvider>();
        var cut = ctx.Render<PhotosCard>();
        cut.WaitForAssertion(() => Assert.Single(cut.FindComponents<OdsPhotoTile>()));

        await cut.InvokeAsync(cut.Instance.OnPhotoSaved);
        var tile = cut.FindComponent<OdsPhotoTile>().Instance;
        await cut.InvokeAsync(() => tile.OnOpen.InvokeAsync(PhotoId));

        // The chip carries the tag's own icon too: a page that forgot to pass it would fall back to the
        // generic glyph silently.
        dialogs.WaitForAssertion(() => Assert.Contains(
            dialogs.FindAll(".pl-vchips .odc-chip.tag"),
            e => e.TextContent.Trim().EndsWith("Hiking") && e.QuerySelector(".material-icons")?.TextContent.Trim() == "hiking"));
    }

    [Fact]
    public async Task Journal_the_photo_rail_names_a_tag_created_in_the_edit_dialog()
    {
        await using var ctx = PageTestContext.Create(PermissionClaims.JournalRead, PermissionClaims.PhotosUpdate);
        ctx.Services.AddSingleton(TagsGainingHiking().Object);

        var cut = ctx.Render<JournalCard>();

        // The rail loaded its names before the edit, so the new tag is not among them yet.
        await cut.InvokeAsync(cut.Instance.EnsurePhotoRefsAsync);
        Assert.Equal("—", cut.Instance.PhotoTagName(HikingId));

        await cut.InvokeAsync(cut.Instance.OnPhotoSaved);

        Assert.Equal("Hiking", cut.Instance.PhotoTagName(HikingId));
        Assert.Equal("hiking", cut.Instance.PhotoTagIcon(HikingId));
    }

    /// <summary>
    /// Every host of the photo detail dialog hands it the tag icons as well as the names. AlbumsPage has no
    /// render harness, so this source check is what covers it; the render tests above cover the other two.
    /// </summary>
    [Theory]
    [InlineData("Pages/Photos/PhotosCard.razor")]
    [InlineData("Pages/Photos/AlbumsPage.razor")]
    [InlineData("Pages/Journal/JournalCard.razor")]
    public void Every_photo_dialog_host_passes_the_tag_icons(string page)
    {
        var source = File.ReadAllText(Path.Combine(ClientSource.Root, page));
        var start = source.IndexOf("<PhotoDetailDialog", StringComparison.Ordinal);
        Assert.True(start >= 0, $"{page} no longer hosts PhotoDetailDialog; update this list.");
        var tag = source[start..source.IndexOf("/>", start, StringComparison.Ordinal)];
        Assert.Contains("TagIcon=", tag);
    }

    [Fact]
    public async Task Journal_a_failed_reload_after_a_save_keeps_the_names_already_loaded()
    {
        await using var ctx = PageTestContext.Create(PermissionClaims.JournalRead, PermissionClaims.PhotosUpdate);
        var tags = new Mock<IPhotoTagsApiClient> { DefaultValueProvider = new PageTestContext.EmptySuccess() };
        tags.SetupSequence(t => t.ListAllAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResult<List<ExistingPhotoTag>>.Success(
                [new ExistingPhotoTag { PhotoTagId = HikingId, Name = "Hiking" }], HttpStatusCode.OK))
            .ReturnsAsync(ApiResult<List<ExistingPhotoTag>>.Failure(
                HttpStatusCode.InternalServerError, new ApiProblem { Title = "Server error" }));
        ctx.Services.AddSingleton(tags.Object);

        var cut = ctx.Render<JournalCard>();
        await cut.InvokeAsync(cut.Instance.EnsurePhotoRefsAsync);
        await cut.InvokeAsync(cut.Instance.OnPhotoSaved);

        Assert.Equal("Hiking", cut.Instance.PhotoTagName(HikingId));
    }
}
