using AngleSharp.Dom;
using Bunit;
using Odyssey.Client.Components;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// <see cref="OdsImageCarousel"/> (issue #473 §6.1, AC 9): the APG carousel's roles and names, no
/// auto-rotation, ends announced instead of wrapping, arrow keys on either button, the counter hidden,
/// the live region filled only by a user action, the no-alt fallback, one frame ratio, eager
/// neighbours, "Image unavailable" on failure, and a plain figure for one image.
/// </summary>
public class OdsImageCarouselTests
{
    private static readonly IReadOnlyList<OdsCarouselImage> Five =
    [
        new("1", "/img/1", "A harbour at dawn", 1600, 900),
        new("2", "/img/2", null, 800, 600),
        new("3", "/img/3", "A long caption " + new string('x', 200), 800, 600),
        new("4", "/img/4", "Boats", 800, 600),
        new("5", "/img/5", "Gulls", 800, 600),
    ];

    private static IRenderedComponent<OdsImageCarousel> Render(BunitContext ctx, IReadOnlyList<OdsCarouselImage> images,
        int initial = 0) =>
        ctx.Render<OdsImageCarousel>(p => p
            .Add(c => c.Images, images)
            .Add(c => c.Label, "Images for “Harbour news”")
            .Add(c => c.Lang, "nb")
            .Add(c => c.InitialIndex, initial));

    private static IElement Prev(IRenderedComponent<OdsImageCarousel> cut) => cut.Find("button.odc-carousel-prev");

    private static IElement Next(IRenderedComponent<OdsImageCarousel> cut) => cut.Find("button.odc-carousel-next");

    private static string Spoken(IRenderedComponent<OdsImageCarousel> cut) =>
        cut.Find(".odc-carousel [role='status']").TextContent.Trim();

    [Fact]
    public void The_carousel_is_a_named_group_with_slides_and_no_landmark()
    {
        using var ctx = NestedInteractiveControlTests.NewContext();
        var cut = Render(ctx, Five);

        var root = cut.Find(".odc-carousel");
        Assert.Equal("group", root.GetAttribute("role"));
        Assert.Equal("carousel", root.GetAttribute("aria-roledescription"));
        Assert.Equal("Images for “Harbour news”", root.GetAttribute("aria-label"));
        Assert.Empty(cut.FindAll("section, [role='region'], nav, aside"));

        var slides = cut.FindAll(".odc-carousel-slide");
        Assert.Equal(5, slides.Count);
        Assert.All(slides, s => Assert.Equal("slide", s.GetAttribute("aria-roledescription")));
        Assert.Equal("Image 2 of 5", slides[1].GetAttribute("aria-label"));
        Assert.False(slides[0].HasAttribute("hidden"));
        Assert.All(slides.Skip(1), s => Assert.True(s.HasAttribute("hidden")));
    }

    [Fact]
    public void The_controls_are_named_and_aria_disabled_at_the_ends_never_disabled()
    {
        using var ctx = NestedInteractiveControlTests.NewContext();
        var cut = Render(ctx, Five);

        Assert.Equal("Previous image", Prev(cut).GetAttribute("aria-label"));
        Assert.Equal("Next image", Next(cut).GetAttribute("aria-label"));
        Assert.Equal("true", Prev(cut).GetAttribute("aria-disabled"));
        Assert.False(Prev(cut).HasAttribute("disabled"));
        Assert.False(Next(cut).HasAttribute("aria-disabled"));
    }

    [Fact]
    public void The_controls_are_the_design_systems_40_px_buttons()
    {
        using var ctx = NestedInteractiveControlTests.NewContext();
        var cut = Render(ctx, Five);

        // The artifact's ImageCarousel: 40 × 40 (--space-10), from the medium button grown by .odc-carousel-btn.
        Assert.All([Prev(cut), Next(cut)], b =>
        {
            Assert.Contains("odc-carousel-btn", b.ClassList);
            Assert.DoesNotContain("mud-icon-button-size-large", b.ClassList);
        });

        var css = File.ReadAllText(Path.Combine(ClientSource.Root, "wwwroot", "css", "odyssey-components.css"));
        Assert.Contains(".mud-icon-button.odc-carousel-btn { inline-size: var(--space-10); block-size: var(--space-10);", css, StringComparison.Ordinal);
    }

    [Fact]
    public void The_counter_is_hidden_and_nothing_is_spoken_on_first_render()
    {
        using var ctx = NestedInteractiveControlTests.NewContext();
        var cut = Render(ctx, Five);

        var counter = cut.Find(".odc-carousel-count");
        Assert.Equal("true", counter.GetAttribute("aria-hidden"));
        Assert.Equal("1 / 5", counter.TextContent);
        Assert.Equal(string.Empty, Spoken(cut));
        Assert.Equal("polite", cut.Find(".odc-carousel [role='status']").GetAttribute("aria-live"));
    }

    [Fact]
    public void Next_moves_one_slide_and_speaks_the_position_and_alt()
    {
        using var ctx = NestedInteractiveControlTests.NewContext();
        var cut = Render(ctx, Five, initial: 2);

        Next(cut).Click();

        Assert.Equal(3, cut.Instance.Index);
        Assert.Equal("4 / 5", cut.Find(".odc-carousel-count").TextContent);
        cut.WaitForAssertion(() => Assert.Equal("Image 4 of 5: Boats", Spoken(cut)));
    }

    [Fact]
    public void An_image_without_alt_speaks_its_position_alone()
    {
        using var ctx = NestedInteractiveControlTests.NewContext();
        var cut = Render(ctx, Five);

        Next(cut).Click();

        cut.WaitForAssertion(() => Assert.Equal("Image 2 of 5", Spoken(cut)));
    }

    [Fact]
    public void Spoken_alt_is_cut_to_140_characters() =>
        Assert.Equal("Image 3 of 5: " + Five[2].Alt![..OdsImageCarousel.SpokenAltLimit].TrimEnd() + "…",
            OdsImageCarousel.SpokenFor(Five[2], 2, 5));

    [Fact]
    public void At_the_first_image_previous_says_first_image_and_does_not_wrap()
    {
        using var ctx = NestedInteractiveControlTests.NewContext();
        var cut = Render(ctx, Five);

        Prev(cut).Click();

        Assert.Equal(0, cut.Instance.Index);
        cut.WaitForAssertion(() => Assert.Equal("First image", Spoken(cut)));
    }

    [Fact]
    public void At_the_last_image_next_says_last_image_and_does_not_wrap()
    {
        using var ctx = NestedInteractiveControlTests.NewContext();
        var cut = Render(ctx, Five, initial: 4);

        Assert.Equal("true", Next(cut).GetAttribute("aria-disabled"));
        Next(cut).Click();

        Assert.Equal(4, cut.Instance.Index);
        cut.WaitForAssertion(() => Assert.Equal("Last image", Spoken(cut)));
    }

    [Theory]
    [InlineData("ArrowRight", 2)]
    [InlineData("ArrowLeft", 0)]
    [InlineData("Enter", 1)]
    public void Arrow_keys_move_from_either_button(string key, int expected)
    {
        using var ctx = NestedInteractiveControlTests.NewContext();
        var cut = Render(ctx, Five, initial: 1);

        Prev(cut).KeyDown(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = key });
        Assert.Equal(expected, cut.Instance.Index);
    }

    [Fact]
    public void Arrow_keys_work_on_the_next_button_too()
    {
        using var ctx = NestedInteractiveControlTests.NewContext();
        var cut = Render(ctx, Five, initial: 1);

        Next(cut).KeyDown(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "ArrowLeft" });

        Assert.Equal(0, cut.Instance.Index);
    }

    [Fact]
    public void An_image_without_alt_gets_the_fallback_alt_and_a_hidden_caption()
    {
        using var ctx = NestedInteractiveControlTests.NewContext();
        var cut = Render(ctx, Five);

        var slide = cut.FindAll(".odc-carousel-slide")[1];
        Assert.Equal("Image 2 of 5, no description from the publisher", slide.QuerySelector("img")!.GetAttribute("alt"));
        Assert.False(slide.QuerySelector("img")!.HasAttribute("lang"));
        var caption = slide.QuerySelector("figcaption")!;
        Assert.Equal("No description from the publisher", caption.TextContent);
        Assert.Equal("true", caption.GetAttribute("aria-hidden"));

        var first = cut.FindAll(".odc-carousel-slide")[0].QuerySelector("img")!;
        Assert.Equal("A harbour at dawn", first.GetAttribute("alt"));
        Assert.Equal("nb", first.GetAttribute("lang"));
    }

    [Fact]
    public void The_current_slide_and_its_neighbours_load_eagerly()
    {
        using var ctx = NestedInteractiveControlTests.NewContext();
        var cut = Render(ctx, Five, initial: 2);

        var loading = cut.FindAll(".odc-carousel-img").Select(i => i.GetAttribute("loading")).ToList();
        Assert.Equal(["lazy", "eager", "eager", "eager", "lazy"], loading);

        Next(cut).Click();
        Assert.Equal("eager", cut.FindAll(".odc-carousel-img")[4].GetAttribute("loading"));
    }

    [Theory]
    [InlineData(1600, 900, "16 / 9")]
    [InlineData(4000, 1000, "16 / 9")]
    [InlineData(800, 600, "4 / 3")]
    [InlineData(600, 900, "4 / 3")]
    [InlineData(1500, 1000, "1500 / 1000")]
    [InlineData(0, 0, "4 / 3")]
    public void One_frame_ratio_comes_from_the_first_image_clamped(int width, int height, string expected) =>
        Assert.Equal(expected, OdsImageCarousel.FrameRatio(new OdsCarouselImage("1", "/x", null, width, height)));

    [Fact]
    public void Every_slide_shares_the_first_images_frame()
    {
        using var ctx = NestedInteractiveControlTests.NewContext();
        var cut = Render(ctx, Five);

        Assert.All(cut.FindAll(".odc-carousel-frame"),
            f => Assert.Equal("aspect-ratio: 16 / 9", f.GetAttribute("style")));
    }

    [Fact]
    public void A_failed_image_shows_image_unavailable_and_stays_in_the_count()
    {
        using var ctx = NestedInteractiveControlTests.NewContext();
        var cut = Render(ctx, Five);

        cut.FindAll(".odc-carousel-img")[0].TriggerEvent("onerror", new Microsoft.AspNetCore.Components.Web.ErrorEventArgs());

        var first = cut.FindAll(".odc-carousel-slide")[0];
        Assert.Equal("Image unavailable", first.QuerySelector(".odc-carousel-ph-text")!.TextContent);
        Assert.Null(first.QuerySelector("img"));
        Assert.Equal(5, cut.FindAll(".odc-carousel-slide").Count);
        Assert.Equal("1 / 5", cut.Find(".odc-carousel-count").TextContent);
    }

    [Fact]
    public void A_single_image_is_a_plain_figure_without_controls()
    {
        using var ctx = NestedInteractiveControlTests.NewContext();
        var cut = Render(ctx, [Five[0]]);

        var figure = cut.Find("figure.odc-carousel");
        Assert.False(figure.HasAttribute("role"));
        Assert.Empty(cut.FindAll("button"));
        Assert.Empty(cut.FindAll("[aria-live]"));
        Assert.Empty(cut.FindAll(".odc-carousel-count"));
    }

    [Fact]
    public void No_images_render_nothing()
    {
        using var ctx = NestedInteractiveControlTests.NewContext();
        Assert.Equal(string.Empty, Render(ctx, []).Markup.Trim());
    }

    [Fact]
    public void Nothing_advances_on_its_own()
    {
        var source = File.ReadAllText(Path.Combine(ClientSource.Root, "Components", "OdsImageCarousel.razor"));

        Assert.DoesNotContain("Timer", source);
        Assert.DoesNotContain("Task.Delay", source);
    }

    [Fact]
    public void The_fade_is_removed_under_reduced_motion()
    {
        var css = File.ReadAllText(Path.Combine(ClientSource.Root, "wwwroot", "css", "odyssey-components.css"));

        Assert.Matches(@"prefers-reduced-motion: reduce\)\s*\{\s*\.odc-carousel\.animate \.odc-carousel-slide:not\(\[hidden\]\) \{ animation: none; \}", css);
    }
}
