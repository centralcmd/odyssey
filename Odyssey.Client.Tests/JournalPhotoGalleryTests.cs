using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using Odyssey.Client.Components;
using Odyssey.Client.Services;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// JournalPhotoGallery (Odyssey Design System · components/JournalPhotoGallery, version 1795477868-c9a2) — a
/// heading + count + empty line over an OdsPhotoGrid of the shared OdsPhotoTile. The tile itself is pinned by
/// <see cref="OdsPhotoTileTests"/>; these pin what the gallery adds and what it hands each tile.
/// </summary>
public class JournalPhotoGalleryTests
{
    private static BunitContext NewContext()
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        return ctx;
    }

    private static IRenderedComponent<JournalPhotoGallery> Render(
        BunitContext ctx, params JournalPhotoGallery.Photo[] photos) =>
        ctx.Render<JournalPhotoGallery>(p => p.Add(c => c.Photos, photos));

    private static PhotoImageSource Original(string url) => new(null, url);

    [Fact]
    public void The_heading_names_the_section_with_its_count_in_text()
    {
        using var ctx = NewContext();
        var cut = Render(ctx,
            new JournalPhotoGallery.Photo("1", "a.jpg", Original("/files/1")),
            new JournalPhotoGallery.Photo("2", "b.jpg", Original("/files/2")));

        Assert.Equal("Photos (2)", cut.Find("section.odc-photogrid").GetAttribute("aria-label"));
        Assert.Equal("2", cut.Find(".odc-photogrid-count").TextContent);
        Assert.Equal(2, cut.FindAll("ul.odc-photogrid2 > li.odc-photogrid2-item > .odc-phototile").Count);
    }

    [Fact]
    public void With_no_title_the_grid_is_named_by_its_count()
    {
        using var ctx = NewContext();
        var cut = ctx.Render<JournalPhotoGallery>(p => p
            .Add(c => c.Photos, [new JournalPhotoGallery.Photo("1", "a.jpg", Original("/files/1"))])
            .Add(c => c.Title, null));

        Assert.Empty(cut.FindAll(".odc-photogrid-head"));
        Assert.Null(cut.Find("section").GetAttribute("aria-label"));
        Assert.Equal("1 photo", cut.Find("ul.odc-photogrid2").GetAttribute("aria-label"));
    }

    [Fact]
    public void An_empty_gallery_shows_its_empty_line_and_no_grid()
    {
        using var ctx = NewContext();
        var cut = ctx.Render<JournalPhotoGallery>(p => p.Add(c => c.Photos, []).Add(c => c.EmptyText, "Nothing yet."));

        Assert.Equal("Nothing yet.", cut.Find(".odc-photogrid-empty").TextContent);
        Assert.Empty(cut.FindAll("ul"));
    }

    [Fact]
    public void Min_tile_reaches_the_grid_columns()
    {
        using var ctx = NewContext();
        var cut = ctx.Render<JournalPhotoGallery>(p => p
            .Add(c => c.Photos, [new JournalPhotoGallery.Photo("1", "a.jpg", Original("/files/1"))])
            .Add(c => c.MinTile, 140));

        Assert.Contains("minmax(min(140px, 100%), 1fr)", cut.Find("ul.odc-photogrid2").GetAttribute("style"));
    }

    [Fact]
    public void A_ready_thumbnail_is_tried_first_and_the_original_after_an_error()
    {
        using var ctx = NewContext();
        var cut = Render(ctx, new JournalPhotoGallery.Photo("1", "holiday.jpg", new PhotoImageSource("/thumb/1", "/files/1/content")));

        Assert.Equal("/thumb/1", cut.Find("img.odc-phototile-img").GetAttribute("src"));
        cut.Find("img").TriggerEvent("onerror", EventArgs.Empty);
        Assert.Equal("/files/1/content", cut.Find("img.odc-phototile-img").GetAttribute("src"));
        Assert.Empty(cut.FindAll(".odc-phototile-ph"));
    }

    [Fact]
    public void An_unnamed_photo_is_named_by_its_position()
    {
        using var ctx = NewContext();
        var cut = Render(ctx, new JournalPhotoGallery.Photo("7f3a", "", new PhotoImageSource("/thumb/1", "/files/1/content")));

        Assert.Equal("Open 1 of 1", cut.Find(".odc-phototile-open").GetAttribute("aria-label"));
        Assert.DoesNotContain("7f3a", cut.Markup);
    }

    [Fact]
    public void A_tile_whose_sources_both_fail_keeps_the_img_marked_failed_with_the_placeholder_after_it()
    {
        using var ctx = NewContext();
        var cut = Render(ctx, new JournalPhotoGallery.Photo("1", "holiday.jpg", new PhotoImageSource("/thumb/1", "/files/1/content")));

        cut.Find("img").TriggerEvent("onerror", EventArgs.Empty);
        cut.Find("img").TriggerEvent("onerror", EventArgs.Empty);

        var img = cut.Find("img.odc-phototile-img");
        Assert.Contains("img-failed", img.ClassList);
        Assert.Contains("odc-phototile-ph", img.NextElementSibling!.ClassList);
        Assert.Equal("true", img.NextElementSibling!.GetAttribute("aria-hidden"));
        // A runtime fallback changes no name, and nothing is announced.
        Assert.Equal("Open holiday.jpg", cut.Find(".odc-phototile-open").GetAttribute("aria-label"));
        Assert.Empty(cut.FindAll("[role=status], [role=alert], [aria-live]"));
    }

    [Fact]
    public void A_photos_read_only_caller_with_a_ready_thumbnail_sees_it_and_no_no_access_label()
    {
        using var ctx = NewContext();
        var cut = Render(ctx, new JournalPhotoGallery.Photo("1", "holiday.jpg", new PhotoImageSource("/thumb/1", null), Unavailable: false));

        Assert.Equal("/thumb/1", cut.Find("img").GetAttribute("src"));
        Assert.DoesNotContain("no access", cut.Markup);
    }

    [Fact]
    public void With_neither_source_the_no_access_state_is_in_the_accessible_name()
    {
        using var ctx = NewContext();
        var cut = Render(ctx, new JournalPhotoGallery.Photo("1", "holiday.jpg", PhotoImageSource.None, Unavailable: true));

        Assert.Empty(cut.FindAll("img"));
        Assert.Equal("Open holiday.jpg (no access)", cut.Find(".odc-phototile-open").GetAttribute("aria-label"));
        Assert.Equal("no access", cut.Find(".odc-phototile-ph").TextContent.Trim());
    }

    [Fact]
    public void Reordering_keyed_tiles_keeps_each_tiles_own_image_stage()
    {
        using var ctx = NewContext();
        var a = new JournalPhotoGallery.Photo("a", "a.jpg", new PhotoImageSource("/thumb/a", "/files/a"));
        var b = new JournalPhotoGallery.Photo("b", "b.jpg", new PhotoImageSource("/thumb/b", "/files/b"));
        var cut = Render(ctx, a, b);
        cut.FindAll("img")[0].TriggerEvent("onerror", EventArgs.Empty);
        Assert.Equal(["/files/a", "/thumb/b"], cut.FindAll("img").Select(i => i.GetAttribute("src")));

        cut.Render(p => p.Add(c => c.Photos, new[] { b, a }));

        // Keyed by id, so "a" keeps the fallback it reached and "b" its thumbnail; nothing is re-requested.
        Assert.Equal(["/thumb/b", "/files/a"], cut.FindAll("img").Select(i => i.GetAttribute("src")));
    }

    [Fact]
    public void Activating_a_tile_hands_the_host_that_photo()
    {
        using var ctx = NewContext();
        JournalPhotoGallery.Photo? opened = null;
        var b = new JournalPhotoGallery.Photo("b", "b.jpg", Original("/files/b"));
        var cut = ctx.Render<JournalPhotoGallery>(p => p
            .Add(c => c.Photos, [new JournalPhotoGallery.Photo("a", "a.jpg", Original("/files/a")), b])
            .Add(c => c.OnOpen, (JournalPhotoGallery.Photo ph) => opened = ph));

        cut.FindAll(".odc-phototile-open")[1].Click();

        Assert.Equal(b, opened);
    }

    [Fact]
    public void The_gallery_tile_has_no_select_heart_or_menu()
    {
        using var ctx = NewContext();
        var cut = Render(ctx, new JournalPhotoGallery.Photo("1", "holiday.jpg", Original("/files/1/content")));

        Assert.Empty(cut.FindAll(".odc-phototile-check, .odc-phototile-fav, .odc-phototile-menu"));
        Assert.Equal("holiday.jpg", cut.Find(".odc-phototile-open").GetAttribute("title"));
    }
}
