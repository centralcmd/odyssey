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

    private static Mock<IAlbumsApiClient> Clients(BunitContext ctx) => Clients(ctx, out _);

    private static Mock<IAlbumsApiClient> Clients(BunitContext ctx, out Mock<IPhotosApiClient> photosMock)
    {
        var photos = new Mock<IPhotosApiClient> { DefaultValueProvider = new PageTestContext.EmptySuccess() };
        photosMock = photos;
        ctx.Services.AddSingleton(new Mock<IPhotoTagsApiClient> { DefaultValueProvider = new PageTestContext.EmptySuccess() }.Object);
        ctx.Services.AddSingleton(new Mock<IContactsApiClient> { DefaultValueProvider = new PageTestContext.EmptySuccess() }.Object);
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
        CoverPhotoId = cover,
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
        // An album WITH a cover keeps OdsPhotoImage even when no image source resolves (its placeholder path).
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


    // ── Overview rows (B), filter options (D1), open-album header (D2) ─────────

    [Fact]
    public void A_filter_option_carries_its_overview_rows_glyph_and_tone()
    {
        foreach (var album in new[] { Summary("Lofoten", "landscape", Teal, null), Summary("Plain", null, null, null) })
        {
            var row = AlbumVisuals.OverviewRow(album);
            var option = AlbumVisuals.FilterOption(album);
            Assert.Equal(row.Icon, option.Icon);
            Assert.Equal(row.IconColor, option.IconColor);
            Assert.Equal(album.Name, option.Label);
        }

        Assert.Equal(AlbumVisuals.OverviewFallback, AlbumVisuals.OverviewRow(Summary("Plain", null, null, null)).IconColor);
        Assert.Equal(PhotoAlbumIcons.Default, AlbumVisuals.FilterOption(Summary("Plain", null, null, null)).Icon);
    }

    [Fact]
    public async Task The_overview_album_rows_draw_each_albums_glyph_in_its_ink()
    {
        await using var ctx = PageTestContext.Create(Claims);
        var albums = Clients(ctx);
        albums.Setup(a => a.ListAllAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResult<List<PhotoAlbumSummary>>.Success(
                [Summary("Lofoten", "landscape", Teal, null), Summary("Plain", null, null, null)], HttpStatusCode.OK));

        var cut = ctx.Render<PhotoLibraryOverview>();
        cut.WaitForState(() => cut.Markup.Contains("Lofoten", StringComparison.Ordinal));

        var glyphs = cut.FindAll("span.material-icons[aria-hidden=true]");
        var lofoten = glyphs.Single(g => g.TextContent == "landscape");
        Assert.Contains("#006B5A", lofoten.GetAttribute("style"), StringComparison.Ordinal);
        var plain = glyphs.Single(g => g.TextContent == PhotoAlbumIcons.Default);
        Assert.Contains("--tide-400", plain.GetAttribute("style"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_open_album_header_leads_with_the_glyph_and_removing_photos_keeps_icon_and_colour()
    {
        await using var ctx = PageTestContext.Create(Claims);
        var albums = Clients(ctx, out var photos);
        var albumId = Guid.NewGuid();
        var photo = new PhotoSummary { PhotoId = Guid.NewGuid(), Title = "Beach", PersonCount = 0, AlbumCount = 1 };
        var existing = Existing(albumId) with { PhotoIds = [photo.PhotoId] };
        albums.Setup(a => a.ListAllAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResult<List<PhotoAlbumSummary>>.Success([Summary("Lofoten", "landscape", Teal, null) with { PhotoAlbumId = albumId }], HttpStatusCode.OK));
        albums.Setup(a => a.GetAsync(albumId, It.IsAny<CancellationToken>())).ReturnsAsync(existing);
        photos.Setup(p => p.ListAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string?>(),
                It.IsAny<IReadOnlyCollection<string>?>(), It.IsAny<IReadOnlyCollection<string>?>(), It.IsAny<IReadOnlyCollection<string>?>(),
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<bool>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResult<PagedResult<PhotoSummary>>.Success(
                new PagedResult<PhotoSummary> { Items = [photo], TotalCount = 1, Offset = 0, Limit = 96 }, HttpStatusCode.OK));
        UpdatePhotoAlbum? sent = null;
        albums.Setup(a => a.UpdateAsync(albumId, It.IsAny<UpdatePhotoAlbum>(), It.IsAny<CancellationToken>()))
            .Callback((Guid _, UpdatePhotoAlbum body, CancellationToken _) => sent = body)
            .ReturnsAsync(Ok());

        ctx.Render<MudPopoverProvider>();
        var cut = ctx.Render<AlbumsPage>();
        cut.WaitForState(() => cut.FindAll("button.pl-albcard-hit").Count == 1);
        cut.Find("button.pl-albcard-hit").Click();
        cut.WaitForState(() => cut.FindAll(".pl-backrow .pl-albglyph").Count == 1);

        var header = cut.Find(".pl-backrow .pl-albglyph");
        Assert.Equal("landscape", header.TextContent);
        Assert.Equal("true", header.GetAttribute("aria-hidden"));
        Assert.Contains("#006B5A", header.GetAttribute("style"), StringComparison.Ordinal);

        cut.WaitForState(() => cut.FindAll("button.pl-check").Count == 1);
        cut.Find("button.pl-check").Click();
        cut.FindAll("button").Single(b => b.TextContent.Contains("Remove from album", StringComparison.Ordinal)).Click();
        cut.WaitForState(() => sent is not null);

        Assert.Empty(sent!.PhotoIds);
        Assert.Equal("landscape", sent.Icon);
        Assert.Equal(Teal, sent.Color);
        Assert.Equal(existing.Name, sent.Name);
    }

    [Fact]
    public void AlbumWrites_keeps_an_unarchived_album_unarchived()
    {
        var update = AlbumWrites.From(Existing(Guid.NewGuid(), icon: null, color: null));
        Assert.False(update.Archived);
        Assert.Null(update.Icon);
        Assert.Null(update.Color);
    }

    [Fact]
    public async Task The_quick_create_in_add_to_album_sends_no_icon_and_no_colour_and_shows_neither_field()
    {
        await using var ctx = PageTestContext.Create(Claims);
        var albums = Clients(ctx);
        NewPhotoAlbum? sent = null;
        albums.Setup(a => a.CreateAsync(It.IsAny<NewPhotoAlbum>(), It.IsAny<CancellationToken>()))
            .Callback((NewPhotoAlbum body, CancellationToken _) => sent = body)
            .ReturnsAsync(Ok());

        var cut = InDialogHost<AddToAlbumDialog>(ctx, b =>
        {
            b.AddComponentParameter(1, nameof(AddToAlbumDialog.Open), true);
            b.AddComponentParameter(2, nameof(AddToAlbumDialog.PhotoIds), (IReadOnlyCollection<Guid>)[Guid.NewGuid()]);
        });
        cut.Find("button.pl-albadd").Click();
        Assert.Empty(cut.FindAll("[role=radiogroup]"));
        cut.Find(".pl-albcreate input").Input("Recipes");
        cut.FindAll(".pl-albcreate button").Single(b => b.TextContent.Contains("Create", StringComparison.Ordinal)).Click();
        cut.WaitForState(() => sent is not null);

        Assert.Equal("Recipes", sent!.Name);
        Assert.Null(sent.Icon);
        Assert.Null(sent.Color);
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
    public void A_tag_pickers_markup_is_unchanged_by_the_new_parameters()
    {
        using var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        var implicitDefaults = ctx.Render<OdsTagIconPicker>(p => p.Add(x => x.Id, "tag-icon"));
        var explicitTagStrings = ctx.Render<OdsTagIconPicker>(p => p
            .Add(x => x.Id, "tag-icon")
            .Add(x => x.DefaultIcon, "local_offer")
            .Add(x => x.DefaultLabel, "Default (tag icon)")
            .Add(x => x.DefaultCaption, "the tag icon"));

        implicitDefaults.MarkupMatches(explicitTagStrings.Markup);
        Assert.Contains("aria-label=\"Default (tag icon)\"", implicitDefaults.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reopening_the_form_for_another_album_shows_none_of_the_previous_albums_state()
    {
        await using var ctx = PageTestContext.Create(Claims);
        var albums = Clients(ctx);
        var first = Summary("First", "landscape", Teal, null);
        var second = Summary("Second", null, null, null);
        albums.Setup(a => a.ListAllAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResult<List<PhotoAlbumSummary>>.Success([first, second], HttpStatusCode.OK));
        albums.Setup(a => a.GetAsync(first.PhotoAlbumId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Existing(first.PhotoAlbumId, "landscape", Teal) with { Name = "First" });
        albums.Setup(a => a.GetAsync(second.PhotoAlbumId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Existing(second.PhotoAlbumId, null, null) with { Name = "Second" });

        var cut = ctx.Render(b =>
        {
            b.OpenComponent<MudDialogProvider>(0);
            b.CloseComponent();
            b.OpenComponent<MudPopoverProvider>(10);
            b.CloseComponent();
            b.OpenComponent<AlbumsPage>(20);
            b.CloseComponent();
        });
        cut.WaitForState(() => cut.FindAll("button.pl-albedit").Count == 2);

        cut.FindAll("button.pl-albedit")[0].Click();
        cut.WaitForState(() => cut.FindAll("#album-icon [role=radio][aria-checked=true]").Count == 1);
        Assert.Equal("Landscape", cut.Find("#album-icon [role=radio][aria-checked=true]").GetAttribute("aria-label"));
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Cancel").Click();

        cut.WaitForState(() => cut.FindAll("button.pl-albedit").Count == 2);
        cut.FindAll("button.pl-albedit")[1].Click();
        cut.WaitForState(() => cut.FindAll("#album-icon [role=radio][aria-checked=true]").Count == 1
            && cut.Find("#album-icon [role=radio][aria-checked=true]").GetAttribute("aria-label") == "Default (album icon)");
        Assert.Equal("No color", cut.Find("#album-color [role=radio][aria-checked=true]").GetAttribute("aria-label"));
        Assert.Single(cut.FindAll("#album-icon"));
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

    /// <summary>AC 13: Name → Description → Icon (one stop) → Color (one stop) → Photos, by DOM order and tabindex.</summary>
    [Fact]
    public async Task The_form_tab_order_is_name_description_icon_colour_then_photos()
    {
        await using var ctx = PageTestContext.Create(Claims);
        Clients(ctx);
        var cut = Form(ctx, null);
        cut.WaitForState(() => cut.FindAll("#album-color [role=radio]").Count > 1);

        var body = cut.Find(".pl-albform");
        var stops = body.QuerySelectorAll("input, textarea, button, [tabindex]")
            .Where(e => e.GetAttribute("tabindex") != "-1" && !e.HasAttribute("disabled") && e.GetAttribute("type") != "hidden")
            .ToList();
        string Kind(AngleSharp.Dom.IElement e) =>
            e.Closest("#album-icon") is not null ? "icon"
            : e.Closest("#album-color") is not null ? "color"
            : e.TagName == "TEXTAREA" ? "description"
            : e.TagName == "INPUT" ? "name"
            : e.TextContent.Contains("Add photos", StringComparison.Ordinal) ? "photos" : "other";
        var order = stops.Select(Kind).Where(k => k != "other").ToList();

        Assert.Equal(["name", "description", "icon", "color", "photos"], order);
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
    public async Task An_invalid_custom_hex_blocks_create_with_the_hex_message_and_sends_nothing()
    {
        await using var ctx = PageTestContext.Create(Claims);
        var albums = Clients(ctx);
        var cut = Form(ctx, null);
        cut.WaitForState(() => cut.FindAll("#album-color [role=radio]").Count > 1);

        cut.Find("input[placeholder='e.g. Summer 2026']").Input("Trip");
        cut.FindAll("#album-color [role=radio]").Last().Click();
        cut.WaitForState(() => cut.FindAll("#album-color-hex, input[placeholder='#RRGGBB']").Count == 1);
        cut.Find("input[placeholder='#RRGGBB']").Input("#12");
        SaveButton(cut, "Create").Click();

        cut.WaitForState(() => cut.Markup.Contains(HexColor.InvalidMessage, StringComparison.Ordinal));
        Assert.Equal("true", cut.Find("input[placeholder='#RRGGBB']").GetAttribute("aria-invalid"));
        albums.Verify(a => a.CreateAsync(It.IsAny<NewPhotoAlbum>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Contains(ctx.JSInterop.Invocations, i => i.Identifier.Contains("focus", StringComparison.OrdinalIgnoreCase));
    }

    public static TheoryData<string> ColorSelections => new() { "swatch", "none", "custom" };

    [Theory]
    [MemberData(nameof(ColorSelections))]
    public async Task A_server_colour_error_marks_the_picker_invalid_whatever_the_selection(string selection)
    {
        await using var ctx = PageTestContext.Create(Claims);
        var albums = Clients(ctx);
        albums.Setup(a => a.CreateAsync(It.IsAny<NewPhotoAlbum>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResult.Failure(HttpStatusCode.BadRequest, new ApiProblem
            {
                Errors = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase) { ["Color"] = [HexColor.InvalidMessage] },
            }));

        var cut = Form(ctx, null);
        cut.WaitForState(() => cut.FindAll("#album-color [role=radio]").Count > 1);
        cut.Find("input[placeholder='e.g. Summer 2026']").Input("Trip");
        var cells = cut.FindAll("#album-color [role=radio]");
        switch (selection)
        {
            case "swatch": cells[1].Click(); break;
            case "custom":
                cells.Last().Click();
                cut.WaitForState(() => cut.FindAll("input[placeholder='#RRGGBB']").Count == 1);
                cut.Find("input[placeholder='#RRGGBB']").Input("#77AA33");
                break;
        }

        SaveButton(cut, "Create").Click();
        cut.WaitForState(() => cut.Markup.Contains(HexColor.InvalidMessage, StringComparison.Ordinal));

        Assert.Equal(1, CountOf(cut.Markup, HexColor.InvalidMessage));
        var invalid = selection == "custom"
            ? cut.Find("input[placeholder='#RRGGBB']").GetAttribute("aria-invalid")
            : cut.Find("#album-color").GetAttribute("aria-invalid");
        Assert.Equal("true", invalid);
        Assert.Empty(ctx.Services.GetRequiredService<ISnackbar>().ShownSnackbars);
        Assert.Contains(ctx.JSInterop.Invocations, i => i.Identifier.Contains("focus", StringComparison.OrdinalIgnoreCase));
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
        Assert.Empty(ctx.Services.GetRequiredService<ISnackbar>().ShownSnackbars);
        // Focus moves into the form (the icon cell when both fail, first in DOM order).
        Assert.Contains(ctx.JSInterop.Invocations, i => i.Identifier.Contains("focus", StringComparison.OrdinalIgnoreCase));
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
