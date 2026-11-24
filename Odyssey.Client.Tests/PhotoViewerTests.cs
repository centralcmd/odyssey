using System.Net;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MudBlazor;
using Odyssey.ApiClient;
using Odyssey.ApiClient.Resources;
using Odyssey.Client.Pages.Photos;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos.Journal;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// <see cref="PhotoViewer"/> — THE way a library photo opens on every surface (Odyssey Design System ·
/// <c>ui_kits/web/Photos.jsx</c> PhotoViewer, version 1795477868-c9a2): the detail dialog with ← / →, the heart, and
/// Edit · Download · Archive · Delete (owner decision: Archive and Delete on every surface, gated by claims), the
/// edit dialog from Edit, and a photo with no date or size rendering without those rows.
/// </summary>
public sealed class PhotoViewerTests
{
    static PhotoViewerTests() => BunitContext.DefaultWaitTimeout = TimeSpan.FromSeconds(10);

    private static readonly Guid A = Guid.NewGuid();
    private static readonly Guid B = Guid.NewGuid();

    private static PhotoSummary Summary(Guid id, string title, DateTime? takenAt = null) =>
        new() { PhotoId = id, Title = title, TakenAt = takenAt, PersonCount = 0, AlbumCount = 0 };

    private static ExistingPhoto Full(Guid id, string title, DateTime? takenAt = null, int? w = null, int? h = null) => new()
    {
        PhotoId = id,
        Title = title,
        TakenAt = takenAt,
        PixelWidth = w,
        PixelHeight = h,
        CreatedAt = new DateTime(2026, 9, 1),
        UpdatedAt = new DateTime(2026, 9, 1),
    };

    private static (BunitContext Ctx, Mock<IPhotosApiClient> Api) NewContext(params string[] claims)
    {
        var ctx = PageTestContext.Create([PermissionClaims.PhotosRead, .. claims]);
        var api = new Mock<IPhotosApiClient> { DefaultValueProvider = new PageTestContext.EmptySuccess() };
        api.Setup(p => p.GetAsync(A, It.IsAny<CancellationToken>())).ReturnsAsync(Full(A, "Summit", new DateTime(2026, 7, 3, 14, 5, 0), 4000, 3000));
        api.Setup(p => p.GetAsync(B, It.IsAny<CancellationToken>())).ReturnsAsync(Full(B, "Undated"));
        api.Setup(p => p.UpdateAsync(It.IsAny<Guid>(), It.IsAny<UpdatePhoto>(), It.IsAny<CancellationToken>())).ReturnsAsync(ApiResult.Success(HttpStatusCode.OK));
        api.Setup(p => p.DeleteAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync(ApiResult.Success(HttpStatusCode.NoContent));
        api.Setup(p => p.ContentUrl(It.IsAny<Guid>())).Returns((Guid id) => $"/api/photos/{id}/content");
        ctx.Services.AddSingleton(api.Object);
        return (ctx, api);
    }

    private static IRenderedComponent<Host> Render(BunitContext ctx, int? index = 0) =>
        ctx.Render<Host>(p => p.Add(h => h.StartIndex, index));

    private static IReadOnlyList<string> FooterButtons(IRenderedComponent<Host> cut) =>
        [.. cut.FindAll(".ods-modal-foot button").Select(b => b.TextContent.Trim())];

    [Fact]
    public async Task An_owner_sees_edit_download_archive_and_delete()
    {
        var (ctx, _) = NewContext(PermissionClaims.PhotosUpdate, PermissionClaims.PhotosDelete);
        await using var _ctx = ctx;
        var cut = Render(ctx);

        cut.WaitForAssertion(() => Assert.Equal(["editEdit", "downloadDownload", "inventory_2Archive", "deleteDelete"],
            FooterButtons(cut).Select(t => t.Replace(" ", "").Replace("\n", ""))));
        Assert.Single(cut.FindAll(".pl-headheart"));
    }

    [Fact]
    public async Task A_reader_sees_download_only()
    {
        var (ctx, _) = NewContext();
        await using var _ctx = ctx;
        var cut = Render(ctx);

        cut.WaitForAssertion(() => Assert.Single(FooterButtons(cut)));
        Assert.Contains("Download", FooterButtons(cut)[0]);
    }

    [Fact]
    public async Task Arrows_walk_the_list_and_wrap()
    {
        var (ctx, _) = NewContext();
        await using var _ctx = ctx;
        var cut = Render(ctx);
        cut.WaitForAssertion(() => Assert.Contains("1 of 2", cut.Find(".ods-modal-sub").TextContent));

        cut.Find(".pl-mnav.next").Click();
        cut.WaitForAssertion(() => Assert.Equal(1, cut.Instance.Index));
        cut.Find(".pl-ods2").KeyDown("ArrowRight");
        cut.WaitForAssertion(() => Assert.Equal(0, cut.Instance.Index));
        cut.Find(".pl-ods2").KeyDown("ArrowLeft");
        cut.WaitForAssertion(() => Assert.Equal(1, cut.Instance.Index));
    }

    [Fact]
    public async Task A_photo_with_no_date_or_size_hides_those_rows()
    {
        var (ctx, _) = NewContext();
        await using var _ctx = ctx;
        var cut = Render(ctx, index: 1);

        cut.WaitForAssertion(() => Assert.Contains("Added by", cut.Find(".pl-vtech").TextContent));
        Assert.Empty(cut.FindAll(".pl-vwhen"));
        Assert.DoesNotContain("×", cut.Find(".pl-vtech").TextContent);
        Assert.Equal("2 of 2", cut.Find(".ods-modal-sub").TextContent.Trim());
    }

    [Fact]
    public async Task A_dated_sized_photo_shows_both_rows()
    {
        var (ctx, _) = NewContext();
        await using var _ctx = ctx;
        var cut = Render(ctx);

        cut.WaitForAssertion(() => Assert.Contains("3 Jul 2026", cut.Find(".pl-vwhen").TextContent));
        Assert.Contains("4000×3000", cut.Find(".pl-vtech").TextContent);
    }

    [Fact]
    public async Task The_heart_saves_the_favourite_updates_in_place_and_tells_the_host()
    {
        var (ctx, api) = NewContext(PermissionClaims.PhotosUpdate);
        await using var _ctx = ctx;
        var cut = Render(ctx);

        cut.WaitForAssertion(() => Assert.Equal("false", cut.Find(".pl-headheart").GetAttribute("aria-pressed")));
        cut.Find(".pl-headheart").Click();

        cut.WaitForAssertion(() => Assert.Equal("true", cut.Find(".pl-headheart").GetAttribute("aria-pressed")));
        api.Verify(p => p.UpdateAsync(A, It.Is<UpdatePhoto>(u => u.Favourite), It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal([new PhotoViewerChange(A, PhotoViewerChangeKind.Favourite)], cut.Instance.Changes);
        Assert.Equal(0, cut.Instance.Index);
    }

    [Fact]
    public async Task Archive_saves_closes_the_viewer_and_tells_the_host()
    {
        var (ctx, api) = NewContext(PermissionClaims.PhotosUpdate);
        await using var _ctx = ctx;
        var cut = Render(ctx);

        cut.WaitForAssertion(() => Assert.Contains(cut.FindAll(".ods-modal-foot button"), b => b.TextContent.Contains("Archive")));
        cut.FindAll(".ods-modal-foot button").First(b => b.TextContent.Contains("Archive")).Click();

        cut.WaitForAssertion(() => Assert.Null(cut.Instance.Index));
        api.Verify(p => p.UpdateAsync(A, It.Is<UpdatePhoto>(u => u.Archived), It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal([new PhotoViewerChange(A, PhotoViewerChangeKind.Archive)], cut.Instance.Changes);
    }

    [Fact]
    public async Task Delete_removes_the_photo_closes_the_viewer_and_tells_the_host()
    {
        var (ctx, api) = NewContext(PermissionClaims.PhotosDelete);
        await using var _ctx = ctx;
        var cut = Render(ctx);

        cut.WaitForAssertion(() => Assert.Contains(cut.FindAll(".ods-modal-foot button"), b => b.TextContent.Contains("Delete")));
        cut.FindAll(".ods-modal-foot button").First(b => b.TextContent.Contains("Delete")).Click();

        cut.WaitForAssertion(() => Assert.Null(cut.Instance.Index));
        api.Verify(p => p.DeleteAsync(A, It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal([new PhotoViewerChange(A, PhotoViewerChangeKind.Delete)], cut.Instance.Changes);
    }

    [Fact]
    public async Task Edit_closes_the_detail_dialog_and_opens_the_editor_for_that_photo()
    {
        var (ctx, _) = NewContext(PermissionClaims.PhotosUpdate);
        await using var _ctx = ctx;
        var cut = Render(ctx);

        cut.WaitForAssertion(() => Assert.Contains(cut.FindAll(".ods-modal-foot button"), b => b.TextContent.Contains("Edit")));
        cut.FindAll(".ods-modal-foot button").First(b => b.TextContent.Contains("Edit")).Click();

        cut.WaitForAssertion(() => Assert.Null(cut.Instance.Index));
        cut.WaitForAssertion(() => Assert.Contains("Edit details", cut.Markup));
        Assert.Empty(cut.FindAll(".pl-ods2"));
    }

    [Fact]
    public async Task Closed_it_renders_no_dialog()
    {
        var (ctx, _) = NewContext();
        await using var _ctx = ctx;
        var cut = Render(ctx, index: null);

        Assert.Empty(cut.FindAll(".mud-dialog-container"));
    }

    [Fact]
    public async Task Esc_closes_the_viewer_through_the_host_index()
    {
        var (ctx, _) = NewContext();
        await using var _ctx = ctx;
        var cut = Render(ctx);
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".mud-dialog-container")));

        cut.Find(".ods-modal-x").Click();

        cut.WaitForAssertion(() => Assert.Null(cut.Instance.Index));
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".mud-dialog-container")));
    }

    /// <summary>A surface listing two photos with the viewer mounted beside them, as a page does.</summary>
    public sealed class Host : ComponentBase
    {
        [Parameter] public int? StartIndex { get; set; }

        public int? Index { get; private set; }

        public List<PhotoViewerChange> Changes { get; } = [];

        private readonly List<PhotoSummary> photos = [Summary(A, "Summit", new DateTime(2026, 7, 3, 14, 5, 0)), Summary(B, "Undated")];
        private bool started;

        protected override void OnParametersSet()
        {
            if (!started)
            {
                started = true;
                Index = StartIndex;
            }
        }

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<MudDialogProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<MudPopoverProvider>(1);
            builder.CloseComponent();
            builder.OpenComponent<PhotoViewer>(2);
            builder.AddComponentParameter(3, nameof(PhotoViewer.Photos), photos);
            builder.AddComponentParameter(4, nameof(PhotoViewer.Index), Index);
            builder.AddComponentParameter(5, nameof(PhotoViewer.IndexChanged), EventCallback.Factory.Create<int?>(this, i => Index = i));
            builder.AddComponentParameter(6, nameof(PhotoViewer.OnChanged), EventCallback.Factory.Create<PhotoViewerChange>(this, c => Changes.Add(c)));
            builder.CloseComponent();
        }
    }
}
