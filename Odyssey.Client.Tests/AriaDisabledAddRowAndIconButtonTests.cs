using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using MudBlazor.Services;
using Odyssey.Client.Components;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// Issue #526 · design system "Component changes": <c>OdsAddRow</c> and <c>OdsIconButton</c> take
/// <c>AriaDisabled</c> + <c>DisabledReason</c> — focusable, <c>aria-disabled</c>, the click swallowed, the reason
/// linked by <c>aria-describedby</c> (on AddRow also the visible second line). Existing callers are unchanged.
/// </summary>
public class AriaDisabledAddRowAndIconButtonTests
{
    private static BunitContext NewContext()
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        return ctx;
    }

    [Fact]
    public async Task An_aria_disabled_add_row_stays_focusable_swallows_the_click_and_shows_its_reason()
    {
        await using var ctx = NewContext();
        var clicks = 0;
        var cut = ctx.Render<OdsAddRow>(p => p
            .Add(x => x.Title, "Add ingredient")
            .Add(x => x.Sub, "Lines with the same Section are grouped under it.")
            .Add(x => x.AriaDisabled, true)
            .Add(x => x.DisabledReason, "A recipe holds at most 100 ingredients.")
            .Add(x => x.OnClick, EventCallback.Factory.Create(this, () => clicks++)));

        var button = cut.Find("button");
        Assert.False(button.HasAttribute("disabled"));
        Assert.Equal("true", button.GetAttribute("aria-disabled"));
        var reason = cut.Find(".odc-add-row-sub");
        Assert.Equal("A recipe holds at most 100 ingredients.", reason.TextContent);
        Assert.Equal(reason.Id, button.GetAttribute("aria-describedby"));
        Assert.DoesNotContain("grouped under it", cut.Markup, StringComparison.Ordinal);

        button.Click();
        Assert.Equal(0, clicks);
    }

    [Fact]
    public async Task An_enabled_add_row_keeps_its_sub_line_and_its_click()
    {
        await using var ctx = NewContext();
        var clicks = 0;
        var cut = ctx.Render<OdsAddRow>(p => p
            .Add(x => x.Title, "Add step")
            .Add(x => x.Sub, "Steps number on across sections.")
            .Add(x => x.DisabledReason, "A recipe holds at most 100 steps.")
            .Add(x => x.OnClick, EventCallback.Factory.Create(this, () => clicks++)));

        var button = cut.Find("button");
        Assert.Null(button.GetAttribute("aria-disabled"));
        Assert.Null(button.GetAttribute("aria-describedby"));
        Assert.Equal("Steps number on across sections.", cut.Find(".odc-add-row-sub").TextContent);
        button.Click();
        Assert.Equal(1, clicks);
    }

    [Fact]
    public async Task An_aria_disabled_icon_button_keeps_its_name_and_links_its_reason()
    {
        await using var ctx = NewContext();
        var clicks = 0;
        var cut = ctx.Render<OdsIconButton>(p => p
            .Add(x => x.Icon, MudBlazor.Icons.Material.Filled.ArrowUpward)
            .Add(x => x.AriaLabel, "Move step 1 up")
            .Add(x => x.AriaDisabled, true)
            .Add(x => x.DisabledReason, "Step 1 is already first.")
            .Add(x => x.OnClick, EventCallback.Factory.Create<MouseEventArgs>(this, () => clicks++)));

        var button = cut.Find("button");
        Assert.False(button.HasAttribute("disabled"));
        Assert.Equal("Move step 1 up", button.GetAttribute("aria-label"));
        Assert.Equal("true", button.GetAttribute("aria-disabled"));
        var reason = cut.Find(".odc-sr-only");
        Assert.Equal("Step 1 is already first.", reason.TextContent);
        Assert.Equal(reason.Id, button.GetAttribute("aria-describedby"));

        button.Click();
        Assert.Equal(0, clicks);
    }

    [Fact]
    public async Task An_icon_button_without_aria_disabled_renders_no_reason()
    {
        await using var ctx = NewContext();
        var clicks = 0;
        var cut = ctx.Render<OdsIconButton>(p => p
            .Add(x => x.Icon, MudBlazor.Icons.Material.Filled.ArrowDownward)
            .Add(x => x.AriaLabel, "Move step 1 down")
            .Add(x => x.DisabledReason, "Step 1 is already last.")
            .Add(x => x.OnClick, EventCallback.Factory.Create<MouseEventArgs>(this, () => clicks++)));

        var button = cut.Find("button");
        Assert.Null(button.GetAttribute("aria-disabled"));
        Assert.Null(button.GetAttribute("aria-describedby"));
        Assert.Empty(cut.FindAll(".odc-sr-only"));
        button.Click();
        Assert.Equal(1, clicks);
    }
}
