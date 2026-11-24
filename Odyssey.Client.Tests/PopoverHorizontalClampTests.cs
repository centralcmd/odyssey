using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using MudBlazor;
using MudBlazor.Services;
using Odyssey.Client.Components;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// The design system's ActionMenu gained a horizontal clamp (version 1795477868-c9a2): a menu that hangs from its
/// trigger's right edge and would run off the LEFT of the window — a photo tile's ⋯ near the left edge — hangs from
/// the trigger's left edge instead, inside the window. The client's counterparts (<see cref="OdsMenu"/>, the ⋯ menu,
/// and <see cref="OdsPopupMenu"/>, the select-style host) get this from MudPopover: with a flip overflow behaviour
/// its placement script swaps <c>-right</c> for <c>-left</c> (and back) when the popover would overflow that side and
/// the other has more room, then clamps <c>left</c> to the window's gutter. So no client code is added; these pin the
/// two things that behaviour depends on — a flip overflow class on the open popover, and the right-edge anchor pair
/// the flip table maps.
/// </summary>
public class PopoverHorizontalClampTests
{
    private static BunitContext NewContext()
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        return ctx;
    }

    private static AngleSharp.Dom.IElement OpenPopover(IRenderedComponent<IComponent> cut) =>
        cut.FindAll(".mud-popover").Single(p => p.ClassList.Contains("mud-popover-open"));

    [Fact]
    public async Task The_row_menu_flips_and_hangs_from_the_triggers_right_edge()
    {
        await using var ctx = NewContext();
        var cut = ctx.Render<Host>(p => p.Add(h => h.Popup, false));
        cut.Find("button.mud-icon-button").Click();

        var pop = OpenPopover(cut);
        Assert.Contains(pop.ClassList, c => c.StartsWith("mud-popover-overflow-flip-", StringComparison.Ordinal));
        Assert.Contains("mud-popover-top-right", pop.ClassList);
        Assert.Contains("mud-popover-anchor-bottom-right", pop.ClassList);
    }

    [Fact]
    public async Task The_popup_menu_flips_too()
    {
        await using var ctx = NewContext();
        var cut = ctx.Render<Host>(p => p.Add(h => h.Popup, true));
        cut.Find("button.popup-trigger").Click();

        var pop = OpenPopover(cut);
        Assert.Contains(pop.ClassList, c => c.StartsWith("mud-popover-overflow-flip-", StringComparison.Ordinal));
    }

    public sealed class Host : ComponentBase
    {
        [Parameter] public bool Popup { get; set; }

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<MudPopoverProvider>(0);
            builder.CloseComponent();
            if (Popup)
            {
                builder.OpenComponent<OdsPopupMenu>(1);
                builder.AddComponentParameter(2, nameof(OdsPopupMenu.TriggerClass), "popup-trigger");
                builder.AddComponentParameter(3, nameof(OdsPopupMenu.AriaLabel), "Sort");
                builder.AddComponentParameter(4, nameof(OdsPopupMenu.TriggerContent), (RenderFragment)(b => b.AddContent(0, "Sort")));
                builder.AddComponentParameter(5, nameof(OdsPopupMenu.ChildContent), (RenderFragment)(b => b.AddContent(0, "Panel")));
                builder.CloseComponent();
            }
            else
            {
                builder.OpenComponent<OdsMenu>(1);
                builder.AddComponentParameter(2, nameof(OdsMenu.Items), (IReadOnlyList<OdsMenuItem>)[new OdsMenuItem { Label = "Open" }]);
                builder.CloseComponent();
            }
        }
    }
}
