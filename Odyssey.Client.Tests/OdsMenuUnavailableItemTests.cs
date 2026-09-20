using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using Odyssey.Client.Components;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// OdsMenu's rule for an action a record cannot take: it is <b>not rendered</b> (Odyssey Design
/// System · components/Menu).
///
/// <para>
/// This replaced the earlier treatment, where such an item stayed in the focus order carrying a
/// sibling note wired through <c>aria-describedby</c> that said why it was unavailable. Both are
/// answers to the same question — never convey unavailability by the dimmed state alone — and the
/// design system settled on the stronger one: the reason belongs to the RECORD, not to one row of
/// one menu that has to be opened to find it. A surface that needs to say why a capability is
/// missing says so in its own copy (an <c>OdsRecordSection</c> notice, a helper line), which is
/// reachable without opening anything.
/// </para>
///
/// <para>
/// The consequence a naive filter gets wrong is the separators. Removing an item can leave a
/// divider leading the menu, trailing it, or doubled against another divider, and can leave a group
/// header with nothing under it — so the orphan pass is asserted here in every one of those shapes,
/// not just the simple middle-of-the-list case.
/// </para>
/// </summary>
public class OdsMenuUnavailableItemTests
{
    private static IRenderedComponent<MenuHost> RenderMenu(params OdsMenuItem[] items)
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();

        var cut = ctx.Render<MenuHost>(p => p.Add(h => h.Items, (IReadOnlyList<OdsMenuItem>)items));
        cut.Find("button.mud-icon-button").Click();
        return cut;
    }

    /// <summary>
    /// OdsMenu next to a MudPopoverProvider. MudBlazor portals the open menu into that provider, so
    /// without it in the same tree the items render nowhere and every assertion here would pass
    /// vacuously against an empty popover.
    /// </summary>
    public sealed class MenuHost : ComponentBase
    {
        [Parameter] public IReadOnlyList<OdsMenuItem> Items { get; set; } = [];

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<MudPopoverProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<OdsMenu>(1);
            builder.AddComponentParameter(2, nameof(OdsMenu.Items), Items);
            builder.CloseComponent();
        }
    }

    private static OdsMenuItem Enabled(string label) => new() { Icon = "edit", Label = label };

    private static OdsMenuItem Unavailable(string label) =>
        new() { Icon = "upload_file", Label = label, Disabled = true };

    private static OdsMenuItem Divider() => new() { Divider = true };

    private static OdsMenuItem Header(string label) => new() { Header = label };

    /// <summary>
    /// The labels on the open menu. An item's TextContent also carries its leading icon ligature, so
    /// the label is read off the body span — matching the whole item's text would make an absence
    /// assertion pass for the wrong reason.
    /// </summary>
    private static IReadOnlyList<string> Labels(IRenderedComponent<MenuHost> cut) =>
        [.. cut.FindAll(".odc-menu-item-body > span:first-child").Select(i => i.TextContent.Trim())];

    private static int DividerCount(IRenderedComponent<MenuHost> cut) => cut.FindAll(".mud-divider").Count;

    [Fact]
    public void An_unavailable_item_is_not_rendered_at_all()
    {
        var cut = RenderMenu(Enabled("Edit policy"), Unavailable("Attach document"), Enabled("Delete"));

        Assert.Equal(["Edit policy", "Delete"], Labels(cut));

        // Not merely hidden or dimmed: there is no element to find, so nothing conveys the state by
        // styling alone and nothing sits in the focus order announcing an action that cannot run.
        Assert.DoesNotContain("Attach document", cut.Markup);
        Assert.Empty(cut.FindAll("[aria-disabled='true']"));
        Assert.Empty(cut.FindAll(".mud-menu-item.mud-disabled"));
    }

    /// <summary>
    /// The positive control for every absence assertion above: an available item renders, carries its
    /// handler and is not marked unavailable. Without it, a filter that dropped everything would
    /// satisfy the rest of this file.
    /// </summary>
    [Fact]
    public void An_available_item_is_rendered_with_its_handler()
    {
        var item = Enabled("Edit policy");
        item.OnClick = EventCallback.Factory.Create(new object(), () => { });

        var cut = RenderMenu(item);
        var rendered = cut.Find(".mud-menu-item");

        Assert.Equal(["Edit policy"], Labels(cut));
        Assert.NotNull(rendered.GetAttribute("blazor:onclick"));
        Assert.NotEqual("true", rendered.GetAttribute("aria-disabled"));
        Assert.DoesNotContain("mud-disabled", rendered.ClassName ?? "");
    }

    [Fact]
    public void A_divider_left_leading_the_menu_is_dropped_with_the_item_above_it()
    {
        var cut = RenderMenu(Unavailable("Pause"), Divider(), Enabled("Delete"));

        Assert.Equal(["Delete"], Labels(cut));
        Assert.Equal(0, DividerCount(cut));
    }

    [Fact]
    public void A_divider_left_trailing_the_menu_is_dropped_with_the_item_below_it()
    {
        var cut = RenderMenu(Enabled("Edit policy"), Divider(), Unavailable("Delete"));

        Assert.Equal(["Edit policy"], Labels(cut));
        Assert.Equal(0, DividerCount(cut));
    }

    /// <summary>
    /// Two dividers that become adjacent must collapse to one, not to two. This is why the orphan
    /// pass runs over the list that REMAINS rather than over the original — reading the original,
    /// each divider would see a real item across the removed one and both would survive.
    /// </summary>
    [Fact]
    public void Two_dividers_left_adjacent_collapse_to_one()
    {
        var cut = RenderMenu(
            Enabled("Edit policy"),
            Divider(),
            Unavailable("Pause"),
            Divider(),
            Enabled("Delete"));

        Assert.Equal(["Edit policy", "Delete"], Labels(cut));
        Assert.Equal(1, DividerCount(cut));
    }

    [Fact]
    public void A_header_left_with_nothing_under_it_is_dropped()
    {
        var cut = RenderMenu(
            Enabled("Edit policy"),
            Divider(),
            Header("Lifecycle"),
            Unavailable("Archive"));

        Assert.Equal(["Edit policy"], Labels(cut));
        Assert.DoesNotContain("Lifecycle", cut.Markup);
        Assert.Equal(0, DividerCount(cut));
    }

    [Fact]
    public void A_header_that_still_has_an_item_under_it_survives()
    {
        var cut = RenderMenu(
            Enabled("Edit policy"),
            Header("Lifecycle"),
            Unavailable("Archive"),
            Enabled("Delete"));

        Assert.Equal(["Edit policy", "Delete"], Labels(cut));
        Assert.Contains("Lifecycle", cut.Markup);
    }

    /// <summary>
    /// Every item unavailable is a real state — a read-only reader on a record whose every action is
    /// a write — and it must render an empty menu rather than a stack of separators.
    /// </summary>
    [Fact]
    public void A_menu_whose_every_action_is_unavailable_renders_no_items()
    {
        var cut = RenderMenu(
            Unavailable("Pause"),
            Divider(),
            Header("Lifecycle"),
            Unavailable("Archive"));

        Assert.Empty(cut.FindAll(".mud-menu-item"));
        Assert.Equal(0, DividerCount(cut));
    }
}
