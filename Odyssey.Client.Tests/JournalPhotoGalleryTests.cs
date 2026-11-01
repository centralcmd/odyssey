using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using Odyssey.Client.Components;
using Odyssey.Client.Services;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// JournalPhotoGallery (Odyssey Design System · components/JournalPhotoGallery) — the thumbnail grid
/// over a journal entry's photos.
/// </summary>
/// <remarks>
/// <para>
/// The design system removed the per-tile filename caption: the name is the tile's accessible name and
/// its tooltip, and is <em>never</em> a visible caption. These RENDER the component, because the whole
/// property is about what markup a tile does and does not carry — a derivation test could not tell an
/// absent caption apart from one that is merely styled away.
/// </para>
/// <para>
/// The empty-name branch is pinned separately. A photo with no name must omit <c>title</c> outright
/// rather than carry an empty one, mirroring the design system's <c>title={p.name || undefined}</c>;
/// that is a one-line conditional with nothing else to catch it if a later edit inverts or drops it.
/// </para>
/// </remarks>
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
    public void A_ready_thumbnail_is_tried_first_and_the_original_after_an_error()
    {
        using var ctx = NewContext();
        var cut = Render(ctx, new JournalPhotoGallery.Photo("1", "holiday.jpg", new PhotoImageSource("/thumb/1", "/files/1/content")));

        Assert.Equal("/thumb/1", cut.Find("img.odc-photogrid-img").GetAttribute("src"));
        cut.Find("img").TriggerEvent("onerror", EventArgs.Empty);
        Assert.Equal("/files/1/content", cut.Find("img.odc-photogrid-img").GetAttribute("src"));
        Assert.Empty(cut.FindAll(".odc-photogrid-ph-fallback"));
    }

    [Fact]
    public void An_unnamed_photo_is_named_by_its_position()
    {
        using var ctx = NewContext();
        var cut = Render(ctx, new JournalPhotoGallery.Photo("7f3a", "", new PhotoImageSource("/thumb/1", "/files/1/content")));

        var tile = cut.Find(".odc-photogrid-tile");
        Assert.Equal("Open photo 1 of 1", tile.GetAttribute("aria-label"));
        Assert.Null(tile.GetAttribute("title"));
    }

    [Fact]
    public void A_tile_whose_sources_both_fail_keeps_the_img_marked_failed_with_the_fallback_after_it()
    {
        using var ctx = NewContext();
        var cut = Render(ctx, new JournalPhotoGallery.Photo("1", "holiday.jpg", new PhotoImageSource("/thumb/1", "/files/1/content")));

        cut.Find("img").TriggerEvent("onerror", EventArgs.Empty);
        cut.Find("img").TriggerEvent("onerror", EventArgs.Empty);

        var img = cut.Find("img.odc-photogrid-img");
        Assert.Contains("img-failed", img.ClassList);
        Assert.Equal("odc-photogrid-ph-fallback", img.NextElementSibling!.ClassList.Last());
        Assert.Equal("true", img.NextElementSibling!.GetAttribute("aria-hidden"));
        // A runtime fallback changes no name, and nothing is announced.
        Assert.Equal("Open photo holiday.jpg", cut.Find(".odc-photogrid-tile").GetAttribute("aria-label"));
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
        Assert.Equal("Open photo holiday.jpg (no access)", cut.Find(".odc-photogrid-tile").GetAttribute("aria-label"));
        Assert.Equal("no access", cut.Find(".odc-photogrid-ph-label").TextContent);
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
    public void A_tile_carries_no_visible_filename_caption()
    {
        using var ctx = NewContext();

        var cut = Render(ctx, new JournalPhotoGallery.Photo("1", "holiday.jpg", Original("/files/1/content")));

        Assert.Empty(cut.FindAll(".odc-photogrid-name"));
        // The filename must not reach the tile as text at all — the image and the placeholder are the
        // only things a sighted reader sees.
        Assert.DoesNotContain("holiday.jpg", cut.Find(".odc-photogrid-tile").TextContent);
    }

    [Fact]
    public void A_named_photo_puts_its_filename_in_the_tiles_tooltip()
    {
        using var ctx = NewContext();

        var cut = Render(ctx, new JournalPhotoGallery.Photo("1", "holiday.jpg", Original("/files/1/content")));

        Assert.Equal("holiday.jpg", cut.Find(".odc-photogrid-tile").GetAttribute("title"));
    }

    [Fact]
    public void An_unnamed_photo_omits_the_tooltip_rather_than_carrying_an_empty_one()
    {
        using var ctx = NewContext();

        var cut = Render(ctx, new JournalPhotoGallery.Photo("1", "", Original("/files/1/content")));

        Assert.False(cut.Find(".odc-photogrid-tile").HasAttribute("title"));
    }

    [Fact]
    public void The_accessible_name_survives_the_caption_removal()
    {
        using var ctx = NewContext();

        var cut = Render(ctx, new JournalPhotoGallery.Photo("1", "holiday.jpg", Original("/files/1/content")));

        // With no visible caption left, aria-label is the only thing naming the tile.
        Assert.Equal("Open photo holiday.jpg", cut.Find(".odc-photogrid-tile").GetAttribute("aria-label"));
    }
}
