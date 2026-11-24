using Bunit;
using Microsoft.AspNetCore.Components;
using MudBlazor.Services;
using Odyssey.Client.Components;
using Odyssey.Client.Services;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// <see cref="OdsPhotoTile"/> and <see cref="OdsPhotoGrid{TItem}"/> (Odyssey Design System · components/PhotoTile,
/// version 1795477868-c9a2): the one library photo tile — its controls, their names and states, the one-tab-stop
/// selection mode, and the disabled / unavailable states.
/// </summary>
public class OdsPhotoTileTests
{
    private static readonly PhotoImageSource Both = new("/thumb/1", "/files/1");

    private static BunitContext NewContext()
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        return ctx;
    }

    private static IRenderedComponent<OdsPhotoTile> Render(BunitContext ctx, Action<ComponentParameterCollectionBuilder<OdsPhotoTile>>? more = null) =>
        ctx.Render<OdsPhotoTile>(p =>
        {
            p.Add(t => t.Name, "Summit").Add(t => t.Image, Both);
            more?.Invoke(p);
        });

    [Fact]
    public async Task The_image_is_its_own_open_button_named_with_the_photo()
    {
        await using var ctx = NewContext();
        var opened = 0;
        var cut = Render(ctx, p => p.Add(t => t.OnOpen, () => opened++));

        var open = cut.Find("div.odc-phototile > button.odc-phototile-open");
        Assert.Equal("Open Summit", open.GetAttribute("aria-label"));
        Assert.Equal("Summit", open.GetAttribute("title"));
        Assert.False(open.HasAttribute("aria-pressed"));
        Assert.Equal("", cut.Find("img").GetAttribute("alt"));
        open.Click();
        Assert.Equal(1, opened);
    }

    [Fact]
    public async Task Every_control_is_a_sibling_so_none_is_nested_in_another()
    {
        await using var ctx = NewContext();
        var cut = Render(ctx, p => p
            .Add(t => t.OnStartSelect, () => { })
            .Add(t => t.OnToggleFav, () => { })
            .Add(t => t.Actions, [new OdsMenuItem { Label = "Open" }]));

        Assert.Empty(NestedInteractiveControlTests.NestedInteractive([cut.Find(".odc-phototile")]));
        Assert.Equal(["odc-phototile-open", "odc-phototile-scrim", "odc-phototile-foot", "odc-phototile-check", "odc-phototile-fav", "odc-phototile-menu"],
            cut.Find(".odc-phototile").Children.Select(c => c.ClassList.First()));
    }

    [Fact]
    public async Task Outside_selection_mode_the_check_starts_selecting_and_is_in_the_tab_order()
    {
        await using var ctx = NewContext();
        var started = 0;
        var cut = Render(ctx, p => p.Add(t => t.OnStartSelect, () => started++));

        var check = cut.Find(".odc-phototile-check");
        Assert.Equal("Select Summit", check.GetAttribute("aria-label"));
        Assert.Equal("false", check.GetAttribute("aria-pressed"));
        Assert.False(check.HasAttribute("tabindex"));
        Assert.False(check.HasAttribute("aria-hidden"));
        check.Click();
        Assert.Equal(1, started);
    }

    [Fact]
    public async Task In_selection_mode_the_image_is_the_one_select_control_and_the_check_leaves_the_tab_order()
    {
        await using var ctx = NewContext();
        var toggled = 0;
        var opened = 0;
        var cut = Render(ctx, p => p
            .Add(t => t.Selectable, true)
            .Add(t => t.OnToggleSelect, () => toggled++)
            .Add(t => t.OnOpen, () => opened++));

        Assert.Contains("is-selectable", cut.Find(".odc-phototile").ClassList);
        var open = cut.Find(".odc-phototile-open");
        Assert.Equal("Select Summit", open.GetAttribute("aria-label"));
        Assert.Equal("false", open.GetAttribute("aria-pressed"));
        var check = cut.Find(".odc-phototile-check");
        Assert.Equal("-1", check.GetAttribute("tabindex"));
        Assert.Equal("true", check.GetAttribute("aria-hidden"));
        // One tab stop: the only focusable select control is the image button.
        Assert.Single(cut.FindAll("button:not([tabindex='-1'])"));

        open.Click();
        Assert.Equal(1, toggled);
        Assert.Equal(0, opened);
    }

    [Fact]
    public async Task A_selected_tile_says_so_on_the_image_and_the_check()
    {
        await using var ctx = NewContext();
        var cut = Render(ctx, p => p.Add(t => t.Selectable, true).Add(t => t.Selected, true).Add(t => t.OnToggleSelect, () => { }));

        Assert.Contains("is-selected", cut.Find(".odc-phototile").ClassList);
        Assert.Equal("true", cut.Find(".odc-phototile-open").GetAttribute("aria-pressed"));
        var check = cut.Find(".odc-phototile-check");
        Assert.Contains("on", check.ClassList);
        Assert.Equal("Deselect Summit", check.GetAttribute("aria-label"));
    }

    [Fact]
    public async Task Without_a_select_handler_there_is_no_check()
    {
        await using var ctx = NewContext();
        var cut = Render(ctx);

        Assert.Empty(cut.FindAll(".odc-phototile-check, .odc-phototile-fav, .odc-phototile-menu"));
    }

    [Theory]
    [InlineData(false, "Favourite Summit", "favorite_border")]
    [InlineData(true, "Unfavourite Summit", "favorite")]
    public async Task The_heart_names_its_action_and_states_its_value(bool fav, string label, string glyph)
    {
        await using var ctx = NewContext();
        var toggled = 0;
        var cut = Render(ctx, p => p.Add(t => t.Fav, fav).Add(t => t.OnToggleFav, () => toggled++));

        var heart = cut.Find(".odc-phototile-fav");
        Assert.Equal(label, heart.GetAttribute("aria-label"));
        Assert.Equal(fav ? "true" : "false", heart.GetAttribute("aria-pressed"));
        Assert.Equal(fav, heart.ClassList.Contains("on"));
        Assert.Equal(glyph, heart.TextContent.Trim());
        heart.Click();
        Assert.Equal(1, toggled);
    }

    [Fact]
    public async Task Actions_draw_the_menu_named_for_the_photo_and_shift_the_heart()
    {
        await using var ctx = NewContext();
        var cut = Render(ctx, p => p
            .Add(t => t.OnToggleFav, () => { })
            .Add(t => t.Actions, [new OdsMenuItem { Label = "Open" }]));

        Assert.Contains("has-menu", cut.Find(".odc-phototile").ClassList);
        Assert.Equal("Actions for photo: Summit", cut.Find(".odc-phototile-menu button").GetAttribute("aria-label"));

        cut.Render(p => p.Add(t => t.ActionsLabel, "More for Summit"));
        Assert.Equal("More for Summit", cut.Find(".odc-phototile-menu button").GetAttribute("aria-label"));
    }

    [Fact]
    public async Task Badges_are_text_and_empty_ones_are_skipped()
    {
        await using var ctx = NewContext();
        var cut = Render(ctx, p => p.Add(t => t.Badges, ["Cover", null, "", "Archived"]));

        Assert.Equal(["Cover", "Archived"], cut.FindAll(".odc-phototile-badge").Select(b => b.TextContent));
        Assert.Equal("Summit", cut.Find(".odc-phototile-cap").TextContent);
        Assert.Equal("true", cut.Find(".odc-phototile-foot").GetAttribute("aria-hidden"));
    }

    [Fact]
    public async Task The_badges_end_the_image_buttons_name_because_the_badge_row_is_hidden()
    {
        // Design system 1795483642-28bb: "{Open|Select} {name}{ (no access)}, {badge1}, {badge2}".
        await using var ctx = NewContext();

        var open = Render(ctx, p => p.Add(t => t.Badges, ["Cover", null, "Archived"]));
        Assert.Equal("Open Summit, Cover, Archived", open.Find(".odc-phototile-open").GetAttribute("aria-label"));

        var select = Render(ctx, p => p.Add(t => t.Selectable, true).Add(t => t.Badges, ["Already added"]));
        Assert.Equal("Select Summit, Already added", select.Find(".odc-phototile-open").GetAttribute("aria-label"));

        var noAccess = ctx.Render<OdsPhotoTile>(p => p.Add(t => t.Name, "Summit").Add(t => t.Unavailable, true).Add(t => t.Badges, ["Cover"]));
        Assert.Equal("Open Summit (no access), Cover", noAccess.Find(".odc-phototile-open").GetAttribute("aria-label"));

        // The other controls keep the bare photo name.
        var heart = Render(ctx, p => p.Add(t => t.Badges, ["Cover"]).Add(t => t.OnToggleFav, () => { }));
        Assert.Equal("Favourite Summit", heart.Find(".odc-phototile-fav").GetAttribute("aria-label"));
    }

    [Fact]
    public async Task Id_names_the_image_button_and_nothing_else()
    {
        await using var ctx = NewContext();

        var cut = Render(ctx, p => p.Add(t => t.Id, "tile-1"));
        Assert.Equal("tile-1", cut.Find(".odc-phototile-open").Id);
        Assert.Single(cut.FindAll("[id]"));
        Assert.False(Render(ctx).Find(".odc-phototile-open").HasAttribute("id"));
    }

    [Fact]
    public async Task A_disabled_tile_is_dimmed_and_inert()
    {
        await using var ctx = NewContext();
        var calls = 0;
        var cut = Render(ctx, p => p
            .Add(t => t.Disabled, true)
            .Add(t => t.Badges, ["Already added"])
            .Add(t => t.OnOpen, () => calls++)
            .Add(t => t.OnStartSelect, () => calls++)
            .Add(t => t.OnToggleFav, () => calls++)
            .Add(t => t.Actions, [new OdsMenuItem { Label = "Open" }]));

        Assert.Contains("is-disabled", cut.Find(".odc-phototile").ClassList);
        Assert.DoesNotContain("has-menu", cut.Find(".odc-phototile").ClassList);
        Assert.True(cut.Find(".odc-phototile-open").HasAttribute("disabled"));
        Assert.Empty(cut.FindAll(".odc-phototile-check, .odc-phototile-fav, .odc-phototile-menu"));
        Assert.Equal("Already added", cut.Find(".odc-phototile-badge").TextContent);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task An_unavailable_photo_with_no_source_says_no_access_in_text_and_in_its_name()
    {
        await using var ctx = NewContext();
        var cut = ctx.Render<OdsPhotoTile>(p => p.Add(t => t.Name, "Summit").Add(t => t.Unavailable, true));

        Assert.Empty(cut.FindAll("img"));
        Assert.Equal("Open Summit (no access)", cut.Find(".odc-phototile-open").GetAttribute("aria-label"));
        Assert.Equal("no access", cut.Find(".odc-phototile-ph").TextContent.Trim());
    }

    [Fact]
    public async Task Unavailable_is_ignored_when_a_source_exists()
    {
        await using var ctx = NewContext();
        var cut = Render(ctx, p => p.Add(t => t.Unavailable, true));

        Assert.Equal("Open Summit", cut.Find(".odc-phototile-open").GetAttribute("aria-label"));
        Assert.DoesNotContain("no access", cut.Markup);
    }

    [Fact]
    public async Task A_photo_with_no_source_shows_the_photo_placeholder()
    {
        await using var ctx = NewContext();
        var cut = ctx.Render<OdsPhotoTile>(p => p.Add(t => t.Name, "Summit"));

        Assert.Equal("photo", cut.Find(".odc-phototile-ph").TextContent.Trim());
        Assert.Equal("true", cut.Find(".odc-phototile-ph").GetAttribute("aria-hidden"));
    }

    [Theory]
    [InlineData(false, null, 158)]
    [InlineData(true, null, 112)]
    [InlineData(false, 140, 140)]
    public async Task The_grid_is_a_named_list_that_auto_fills_its_minimum(bool dense, int? min, int expected)
    {
        await using var ctx = NewContext();
        var cut = ctx.Render<OdsPhotoGrid<string>>(p => p
            .Add(g => g.Items, ["a", "b"])
            .Add(g => g.ItemTemplate, s => b => b.AddContent(0, s))
            .Add(g => g.Dense, dense)
            .Add(g => g.MinTile, min)
            .Add(g => g.AriaLabel, "2 photos"));

        var ul = cut.Find("ul.odc-photogrid2");
        Assert.Equal("2 photos", ul.GetAttribute("aria-label"));
        Assert.Equal(dense, ul.ClassList.Contains("dense"));
        Assert.Contains($"repeat(auto-fill, minmax(min({expected}px, 100%), 1fr))", ul.GetAttribute("style"));
        Assert.Equal(["a", "b"], cut.FindAll("ul > li.odc-photogrid2-item").Select(li => li.TextContent));
    }
}
