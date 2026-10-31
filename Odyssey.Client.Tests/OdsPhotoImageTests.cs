using Bunit;
using Microsoft.AspNetCore.Components;
using MudBlazor.Services;
using Odyssey.Client.Components;
using Odyssey.Client.Services;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>Issue #365 AC 3, 4, 6, 7, 15: the thumbnail → original → placeholder chain.</summary>
public class OdsPhotoImageTests
{
    private static BunitContext NewContext()
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        return ctx;
    }

    private static IRenderedComponent<OdsPhotoImage> Render(BunitContext ctx, PhotoImageSource source) =>
        ctx.Render<OdsPhotoImage>(p => p
            .Add(c => c.Source, source)
            .Add(c => c.Alt, "")
            .Add(c => c.Placeholder, (RenderFragment)(b => b.AddMarkupContent(0, "<span class=\"glyph\">image</span>"))));

    private static List<string?> FailUntilPlaceholder(IRenderedComponent<OdsPhotoImage> cut)
    {
        var seen = new List<string?>();
        for (var i = 0; i < 5 && cut.FindAll("img:not(.img-failed)").Count > 0; i++)
        {
            seen.Add(cut.Find("img").GetAttribute("src"));
            cut.Find("img").TriggerEvent("onerror", EventArgs.Empty);
        }

        return seen;
    }

    [Fact]
    public void The_thumbnail_renders_lazily_and_async_with_no_crossorigin()
    {
        using var ctx = NewContext();
        var cut = Render(ctx, new PhotoImageSource("/thumb", "/original"));

        var img = cut.Find("img");
        Assert.Equal("/thumb", img.GetAttribute("src"));
        Assert.Equal("lazy", img.GetAttribute("loading"));
        Assert.Equal("async", img.GetAttribute("decoding"));
        Assert.False(img.HasAttribute("crossorigin"));
        Assert.Empty(cut.FindAll(".odc-photoimg-ph"));
    }

    [Fact]
    public void Both_sources_failing_requests_exactly_thumb_then_original_then_shows_the_placeholder()
    {
        using var ctx = NewContext();
        var cut = Render(ctx, new PhotoImageSource("/thumb", "/original"));

        Assert.Equal(["/thumb", "/original"], FailUntilPlaceholder(cut));

        var img = cut.Find("img");
        Assert.Contains("img-failed", img.ClassList);
        Assert.Equal("/original", img.GetAttribute("src"));
        var ph = cut.Find(".odc-photoimg-ph");
        Assert.Equal("true", ph.GetAttribute("aria-hidden"));
        Assert.NotNull(ph.QuerySelector(".glyph"));
        Assert.Empty(cut.FindAll("[role=status], [role=alert], [aria-live]"));
    }

    [Fact]
    public void Without_the_original_a_failed_thumbnail_goes_straight_to_the_placeholder()
    {
        using var ctx = NewContext();
        var cut = Render(ctx, new PhotoImageSource("/thumb", null));

        Assert.Equal(["/thumb"], FailUntilPlaceholder(cut));
        Assert.DoesNotContain("/api/files/", cut.Markup);
    }

    [Fact]
    public void With_neither_source_only_the_placeholder_renders_and_nothing_is_requested()
    {
        using var ctx = NewContext();
        var cut = Render(ctx, PhotoImageSource.None);

        Assert.Empty(cut.FindAll("img"));
        Assert.Single(cut.FindAll(".odc-photoimg-ph"));
    }

    [Fact]
    public void A_new_source_resets_the_chain_and_an_equal_one_does_not()
    {
        using var ctx = NewContext();
        var cut = Render(ctx, new PhotoImageSource("/a-thumb", "/a-original"));
        FailUntilPlaceholder(cut);

        cut.Render(p => p.Add(c => c.Source, new PhotoImageSource("/a-thumb", "/a-original")));
        Assert.Contains("img-failed", cut.Find("img").ClassList);

        cut.Render(p => p.Add(c => c.Source, new PhotoImageSource("/b-thumb", "/b-original")));
        Assert.Equal("/b-thumb", cut.Find("img").GetAttribute("src"));
        Assert.DoesNotContain("img-failed", cut.Find("img").ClassList);
        Assert.Empty(cut.FindAll(".odc-photoimg-ph"));
    }
}
