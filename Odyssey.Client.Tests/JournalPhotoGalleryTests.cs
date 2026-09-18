using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using Odyssey.Client.Components;
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

    [Fact]
    public void A_tile_carries_no_visible_filename_caption()
    {
        using var ctx = NewContext();

        var cut = Render(ctx, new JournalPhotoGallery.Photo("1", "holiday.jpg", "/files/1/content"));

        Assert.Empty(cut.FindAll(".odc-photogrid-name"));
        // The filename must not reach the tile as text at all — the image and the placeholder are the
        // only things a sighted reader sees.
        Assert.DoesNotContain("holiday.jpg", cut.Find(".odc-photogrid-tile").TextContent);
    }

    [Fact]
    public void A_named_photo_puts_its_filename_in_the_tiles_tooltip()
    {
        using var ctx = NewContext();

        var cut = Render(ctx, new JournalPhotoGallery.Photo("1", "holiday.jpg", "/files/1/content"));

        Assert.Equal("holiday.jpg", cut.Find(".odc-photogrid-tile").GetAttribute("title"));
    }

    [Fact]
    public void An_unnamed_photo_omits_the_tooltip_rather_than_carrying_an_empty_one()
    {
        using var ctx = NewContext();

        var cut = Render(ctx, new JournalPhotoGallery.Photo("1", "", "/files/1/content"));

        Assert.False(cut.Find(".odc-photogrid-tile").HasAttribute("title"));
    }

    [Fact]
    public void The_accessible_name_survives_the_caption_removal()
    {
        using var ctx = NewContext();

        var cut = Render(ctx, new JournalPhotoGallery.Photo("1", "holiday.jpg", "/files/1/content"));

        // With no visible caption left, aria-label is the only thing naming the tile.
        Assert.Equal("Open photo holiday.jpg", cut.Find(".odc-photogrid-tile").GetAttribute("aria-label"));
    }
}
