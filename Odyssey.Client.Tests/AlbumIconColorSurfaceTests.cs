using System.Net;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using Moq;
using Odyssey.ApiClient;
using Odyssey.ApiClient.Resources;
using Odyssey.Client.Components;
using Odyssey.Client.Pages.Photos;
using Odyssey.Dtos;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos.Journal;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// An album's icon and colour on the client (issue #406): the surfaces that draw it, the album form's
/// pickers, load gate and error wiring, and the partial-edit save paths that must send both back.
/// </summary>
public class AlbumIconColorSurfaceTests
{
    private static readonly string[] Claims =
    [
        PermissionClaims.PhotosRead, PermissionClaims.FilesRead, PermissionClaims.PhotoAlbumsRead,
        PermissionClaims.PhotoAlbumsUpdate, PermissionClaims.PhotoAlbumsCreate, PermissionClaims.PhotoAlbumsDelete,
    ];

    private const string Teal = "#006B5A";

    private static Mock<IAlbumsApiClient> Clients(BunitContext ctx)
    {
        var photos = new Mock<IPhotosApiClient> { DefaultValueProvider = new PageTestContext.EmptySuccess() };
        var files = new Mock<IFilesApiClient> { DefaultValueProvider = new PageTestContext.EmptySuccess() };
        var albums = new Mock<IAlbumsApiClient> { DefaultValueProvider = new PageTestContext.EmptySuccess() };
        ctx.Services.AddSingleton(photos.Object);
        ctx.Services.AddSingleton(files.Object);
        ctx.Services.AddSingleton(albums.Object);
        return albums;
    }

    private static IRenderedComponent<Bunit.Rendering.ContainerFragment> InDialogHost<TDialog>(
        BunitContext ctx, Action<Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder> parameters)
        where TDialog : IComponent =>
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

    private static PhotoAlbumSummary Summary(string name, string? icon, string? color, Guid? cover) => new()
    {
        PhotoAlbumId = Guid.NewGuid(), Name = name, PhotoCount = cover is null ? 0 : 1, Icon = icon, Color = color,
        CoverPhotoId = cover, CoverFileId = cover is null ? null : Guid.NewGuid(),
    };

    private static ExistingPhotoAlbum Existing(Guid id, string? icon = "landscape", string? color = Teal) => new()
    {
        PhotoAlbumId = id, Name = "Lofoten", Description = "North", PhotoIds = [Guid.NewGuid()], Icon = icon, Color = color,
        CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
    };

    private static ApiResult Ok() => ApiResult.Success(HttpStatusCode.OK);

    // ── Surfaces (AC 1, 2, 3, 8) ──────────────────────────────────────────────

    [Fact]
    public async Task Album_cards_lead_with_the_glyph_in_its_ink_and_a_coverless_album_shows_the_glyph_instead_of_an_image()
    {
        await using var ctx = PageTestContext.Create(Claims);
        var albums = Clients(ctx);
        var withCover = Summary("Lofoten", "landscape", Teal, Guid.NewGuid());
        var noCover = Summary("Recipes", "restaurant_menu", null, null);
        var plain = Summary("Plain", null, null, Guid.NewGuid());
        albums.Setup(a => a.ListAllAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResult<List<PhotoAlbumSummary>>.Success([withCover, noCover, plain], HttpStatusCode.OK));

        ctx.Render<MudPopoverProvider>();
        var cut = ctx.Render<AlbumsPage>();
        cut.WaitForState(() => cut.FindAll(".pl-albcard").Count == 3);

        var glyphs = cut.FindAll(".pl-albname-row .pl-albglyph");
        Assert.Equal(["landscape", "restaurant_menu", PhotoAlbumIcons.Default], glyphs.Select(g => g.TextContent));
        Assert.All(glyphs, g => Assert.Equal("true", g.GetAttribute("aria-hidden")));
        Assert.Contains("oklch(from #006B5A var(--user-glyph-l", glyphs[0].GetAttribute("style"), StringComparison.Ordinal);
        Assert.Equal($"color:{AlbumVisuals.SurfaceFallback}", glyphs[2].GetAttribute("style"));

        var cards = cut.FindAll(".pl-albcard");
        Assert.Single(cards[1].QuerySelectorAll(".pl-albcover-empty .pl-albglyph-xl"));
        Assert.Empty(cards[1].QuerySelectorAll("[class*=odc-photoimg]"));
        Assert.NotEmpty(cards[0].QuerySelectorAll(".pl-albcover-cell [class*=odc-photoimg]"));
        Assert.Empty(cards[0].QuerySelectorAll(".pl-albcover-empty"));

        // The card's accessible name is unchanged: the glyph adds none.
        Assert.Equal("Open album Lofoten", cards[0].QuerySelector("button.pl-albcard-hit")!.GetAttribute("aria-label"));
    }

    [Fact]
    public async Task The_add_to_album_row_of_a_coverless_album_shows_its_glyph_and_the_rest_keep_their_cover()
    {
        await using var ctx = PageTestContext.Create(Claims);
        var albums = Clients(ctx);
        albums.Setup(a => a.ListAllAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResult<List<PhotoAlbumSummary>>.Success(
                [Summary("Lofoten", "landscape", Teal, Guid.NewGuid()), Summary("Recipes", "restaurant_menu", "#4A5670", null)], HttpStatusCode.OK));

        var cut = InDialogHost<AddToAlbumDialog>(ctx, b =>
        {
            b.AddComponentParameter(1, nameof(AddToAlbumDialog.Open), true);
            b.AddComponentParameter(2, nameof(AddToAlbumDialog.PhotoIds), (IReadOnlyCollection<Guid>)[Guid.NewGuid()]);
        });
        cut.WaitForState(() => cut.FindAll("button.pl-albrow").Count == 2);

        var rows = cut.FindAll("button.pl-albrow");
        Assert.NotEmpty(rows[0].QuerySelectorAll(".pl-albrow-cover [class*=odc-photoimg]"));
        Assert.Empty(rows[0].QuerySelectorAll(".pl-albrow-glyph"));
        var glyph = rows[1].QuerySelector(".pl-albrow-glyph .pl-albglyph")!;
        Assert.Equal("restaurant_menu", glyph.TextContent);
        Assert.Equal("true", glyph.GetAttribute("aria-hidden"));
        Assert.Contains("#4A5670", glyph.GetAttribute("style"), StringComparison.Ordinal);
    }

    [Fact]
    public void Visuals_fall_back_to_the_album_glyph_and_the_surface_ink()
    {
        Assert.Equal(PhotoAlbumIcons.Default, AlbumVisuals.Glyph(null));
        Assert.Equal(PhotoAlbumIcons.Default, AlbumVisuals.Glyph(TagIcons.Default));
        Assert.Equal("yard", AlbumVisuals.Glyph("yard"));
        Assert.Equal(AlbumVisuals.OverviewFallback, AlbumVisuals.Ink(null, AlbumVisuals.OverviewFallback));
        Assert.Equal(AlbumVisuals.OverviewFallback, AlbumVisuals.Ink("#12345Z", AlbumVisuals.OverviewFallback));
        Assert.StartsWith("oklch(from #ABCDEF", AlbumVisuals.Ink("#abcdef", AlbumVisuals.OverviewFallback), StringComparison.Ordinal);
    }

    // ── Partial edits keep icon and colour (AC 10) ────────────────────────────

    [Fact]
    public void AlbumWrites_copies_every_field_including_icon_and_colour()
    {
        var album = Existing(Guid.NewGuid()) with { CoverPhotoId = Guid.NewGuid(), Archived = DateTime.UtcNow };
        var update = AlbumWrites.From(album);

        Assert.Equal(album.Name, update.Name);
        Assert.Equal(album.Description, update.Description);
        Assert.Equal(album.PhotoIds, update.PhotoIds);
        Assert.Equal(album.CoverPhotoId, update.CoverPhotoId);
        Assert.True(update.Archived);
        Assert.Equal("landscape", update.Icon);
        Assert.Equal(Teal, update.Color);
    }

    [Fact]
    public async Task Adding_photos_to_an_album_sends_its_icon_and_colour_back_unchanged()
    {
        await using var ctx = PageTestContext.Create(Claims);
        var albums = Clients(ctx);
        var albumId = Guid.NewGuid();
        var summary = Summary("Lofoten", "landscape", Teal, Guid.NewGuid()) with { PhotoAlbumId = albumId };
        albums.Setup(a => a.ListAllAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResult<List<PhotoAlbumSummary>>.Success([summary], HttpStatusCode.OK));
        albums.Setup(a => a.GetAsync(albumId, It.IsAny<CancellationToken>())).ReturnsAsync(Existing(albumId));
        UpdatePhotoAlbum? sent = null;
        albums.Setup(a => a.UpdateAsync(albumId, It.IsAny<UpdatePhotoAlbum>(), It.IsAny<CancellationToken>()))
            .Callback((Guid _, UpdatePhotoAlbum body, CancellationToken _) => sent = body)
            .ReturnsAsync(Ok());

        var added = Guid.NewGuid();
        var cut = InDialogHost<AddToAlbumDialog>(ctx, b =>
        {
            b.AddComponentParameter(1, nameof(AddToAlbumDialog.Open), true);
            b.AddComponentParameter(2, nameof(AddToAlbumDialog.PhotoIds), (IReadOnlyCollection<Guid>)[added]);
        });
        cut.WaitForState(() => cut.FindAll("button.pl-albrow").Count == 1);
        cut.Find("button.pl-albrow").Click();
        cut.FindAll("button").Single(b => b.TextContent.Contains("Done", StringComparison.Ordinal)).Click();
        cut.WaitForState(() => sent is not null);

        Assert.Equal("landscape", sent!.Icon);
        Assert.Equal(Teal, sent.Color);
        Assert.Contains(added, sent.PhotoIds);
    }

    // ── Album form (AC 4, 5, 6, 7, 12) ───────────────────────────────────────

    private static IRenderedComponent<Bunit.Rendering.ContainerFragment> Form(BunitContext ctx, Guid? albumId) =>
        InDialogHost<AlbumFormDialog>(ctx, b =>
        {
            b.AddComponentParameter(1, nameof(AlbumFormDialog.Open), true);
            b.AddComponentParameter(2, nameof(AlbumFormDialog.AlbumId), albumId);
        });

    private static AngleSharp.Dom.IElement SaveButton(IRenderedComponent<Bunit.Rendering.ContainerFragment> cut, string label) =>
        cut.FindAll("button").Single(b => b.TextContent.Trim().EndsWith(label, StringComparison.Ordinal));

    [Fact]
    public async Task The_icon_pickers_default_cell_names_the_album_glyph()
    {
        await using var ctx = PageTestContext.Create(Claims);
        Clients(ctx);
        var cut = Form(ctx, null);
        cut.WaitForState(() => cut.FindAll("#album-icon [role=radio]").Count > 1);

        var defaultCell = cut.FindAll("#album-icon [role=radio]")[0];
        Assert.Equal("Default (album icon)", defaultCell.GetAttribute("aria-label"));
        Assert.Equal(PhotoAlbumIcons.Default, defaultCell.QuerySelector(".material-icons")!.TextContent);
        Assert.Equal("true", defaultCell.GetAttribute("aria-checked"));
        Assert.Equal("the album icon", cut.Find(".odc-iconpick-caption .odc-iconpick-sub").TextContent);
    }

    [Fact]
    public void A_tag_picker_keeps_the_tag_default_cell()
    {
        using var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        var cut = ctx.Render<OdsTagIconPicker>();

        var defaultCell = cut.FindAll("[role=radio]")[0];
        Assert.Equal("Default (tag icon)", defaultCell.GetAttribute("aria-label"));
        Assert.Equal(TagIcons.Default, defaultCell.QuerySelector(".material-icons")!.TextContent);
        Assert.Equal("the tag icon", cut.Find(".odc-iconpick-sub").TextContent);
    }

    [Fact]
    public async Task Editing_seeds_both_pickers_and_saving_sends_the_same_icon_and_colour_back()
    {
        await using var ctx = PageTestContext.Create(Claims);
        var albums = Clients(ctx);
        var albumId = Guid.NewGuid();
        albums.Setup(a => a.GetAsync(albumId, It.IsAny<CancellationToken>())).ReturnsAsync(Existing(albumId, "landscape", "#77AA33"));
        UpdatePhotoAlbum? sent = null;
        albums.Setup(a => a.UpdateAsync(albumId, It.IsAny<UpdatePhotoAlbum>(), It.IsAny<CancellationToken>()))
            .Callback((Guid _, UpdatePhotoAlbum body, CancellationToken _) => sent = body)
            .ReturnsAsync(Ok());

        var cut = Form(ctx, albumId);
        cut.WaitForState(() => cut.FindAll("#album-icon [role=radio]").Count > 1);

        Assert.Equal("Landscape", cut.Find("#album-icon [role=radio][aria-checked=true]").GetAttribute("aria-label"));
        // A colour that is not a swatch opens on Custom with the hex filled in.
        Assert.StartsWith("Custom", cut.Find("#album-color [role=radio][aria-checked=true]").GetAttribute("aria-label"), StringComparison.Ordinal);

        SaveButton(cut, "Save").Click();
        cut.WaitForState(() => sent is not null);
        Assert.Equal("landscape", sent!.Icon);
        Assert.Equal("#77AA33", sent.Color);
    }

    [Fact]
    public async Task While_the_album_loads_nothing_is_editable_and_a_failed_load_offers_retry_and_never_saves()
    {
        await using var ctx = PageTestContext.Create(Claims);
        var albums = Clients(ctx);
        var albumId = Guid.NewGuid();
        var pending = new TaskCompletionSource<ExistingPhotoAlbum?>();
        albums.SetupSequence(a => a.GetAsync(albumId, It.IsAny<CancellationToken>()))
            .Returns(pending.Task)
            .ReturnsAsync(Existing(albumId));

        var cut = Form(ctx, albumId);
        Assert.Empty(cut.FindAll("#album-icon"));
        Assert.Empty(cut.FindAll("#album-color"));
        Assert.True(SaveButton(cut, "Save").HasAttribute("disabled"));
        Assert.True(cut.Find("button.pl-danger-link").HasAttribute("disabled"));

        pending.SetResult(null);
        cut.WaitForState(() => cut.Markup.Contains("Couldn't load this album.", StringComparison.Ordinal));
        Assert.Empty(cut.FindAll("#album-icon"));
        Assert.True(SaveButton(cut, "Save").HasAttribute("disabled"));
        albums.Verify(a => a.UpdateAsync(It.IsAny<Guid>(), It.IsAny<UpdatePhotoAlbum>(), It.IsAny<CancellationToken>()), Times.Never);

        cut.FindAll("button").Single(b => b.TextContent.Contains("Retry", StringComparison.Ordinal)).Click();
        cut.WaitForState(() => cut.FindAll("#album-icon [role=radio]").Count > 1);
        Assert.Equal("Landscape", cut.Find("#album-icon [role=radio][aria-checked=true]").GetAttribute("aria-label"));
        Assert.False(SaveButton(cut, "Save").HasAttribute("disabled"));
    }

    [Fact]
    public async Task An_empty_custom_colour_blocks_create_and_sends_nothing()
    {
        await using var ctx = PageTestContext.Create(Claims);
        var albums = Clients(ctx);
        var cut = Form(ctx, null);
        cut.WaitForState(() => cut.FindAll("#album-color [role=radio]").Count > 1);

        cut.Find("input[placeholder='e.g. Summer 2026']").Input("Trip");
        cut.FindAll("#album-color [role=radio]").Last().Click(); // Custom, left empty
        SaveButton(cut, "Create").Click();

        cut.WaitForState(() => cut.Markup.Contains("Enter a 6-digit hex color", StringComparison.Ordinal));
        albums.Verify(a => a.CreateAsync(It.IsAny<NewPhotoAlbum>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task A_server_colour_error_lands_once_on_the_colour_picker_and_an_icon_error_under_icon_with_no_toast()
    {
        await using var ctx = PageTestContext.Create(Claims);
        var albums = Clients(ctx);
        var problem = new ApiProblem
        {
            Title = "One or more validation errors occurred.",
            Errors = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
            {
                ["Icon"] = [TagIcons.InvalidIconMessage],
                ["Color"] = [HexColor.InvalidMessage],
            },
        };
        albums.Setup(a => a.CreateAsync(It.IsAny<NewPhotoAlbum>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResult.Failure(HttpStatusCode.BadRequest, problem));

        var cut = Form(ctx, null);
        cut.WaitForState(() => cut.FindAll("#album-color [role=radio]").Count > 1);
        cut.Find("input[placeholder='e.g. Summer 2026']").Input("Trip");
        cut.FindAll("#album-color [role=radio]")[1].Click(); // a swatch
        SaveButton(cut, "Create").Click();

        cut.WaitForState(() => cut.Markup.Contains(HexColor.InvalidMessage, StringComparison.Ordinal));
        Assert.Equal(1, CountOf(cut.Markup, HexColor.InvalidMessage));
        Assert.Equal("true", cut.Find("#album-color").GetAttribute("aria-invalid"));
        Assert.Equal(TagIcons.InvalidIconMessage, cut.Find("#album-icon-help-error").TextContent.Trim());
        Assert.Contains("album-icon-help-error", cut.Find("#album-icon").GetAttribute("aria-describedby"), StringComparison.Ordinal);
        Assert.Equal("true", cut.Find("#album-icon").GetAttribute("aria-invalid"));
        Assert.Empty(cut.FindAll(".mud-snackbar"));
        // The dialog stays open with the typed name intact.
        Assert.Equal("Trip", cut.Find("input[placeholder='e.g. Summer 2026']").GetAttribute("value"));
    }

    private static int CountOf(string text, string value)
    {
        var count = 0;
        for (var at = text.IndexOf(value, StringComparison.Ordinal); at >= 0; at = text.IndexOf(value, at + value.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }
}
