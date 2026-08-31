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
/// OdsMenu's disabled-item semantics, brought into conformance with the design system's Menu
/// (issue #26 Goal 9).
///
/// <para>
/// The defect this pins was invisible: a disabled item carrying a <c>Description</c> was rendered
/// with MudBlazor's disabled treatment, which takes it out of the focus order — so the one thing the
/// description exists for, telling a user WHY the action is unavailable, could not be reached by a
/// keyboard or screen-reader user at all. The reason was also inside the item's own content, making
/// it part of the accessible NAME rather than a description.
/// </para>
///
/// <para>
/// The design system's rule is conditional, and both halves are asserted here: an item disabled
/// <em>with</em> a note stays reachable and gets <c>aria-disabled</c> + <c>aria-describedby</c>; an
/// item disabled with no note keeps the ordinary disabled treatment, because there is nothing there
/// to reach.
/// </para>
///
/// <para>
/// There was no OdsMenu test before this, so the two existing <c>Description</c> consumers
/// (ContractsCard's and SubscriptionCard's "Analyze" items) had no coverage either. Their shape is
/// exercised by <see cref="A_disabled_item_with_a_note_stays_reachable_and_describes_itself"/> —
/// what changes for them is asserted as new behaviour, not as an absence of regression.
/// </para>
/// </summary>
public class OdsMenuDisabledItemTests
{
    private const string Reason = "Add a renewal period first";

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

    private static OdsMenuItem Enabled(string label) =>
        new() { Icon = "edit", Label = label };

    private static OdsMenuItem DisabledWithNote(string label, string note) =>
        new() { Icon = "upload_file", Label = label, Disabled = true, Description = note };

    private static OdsMenuItem DisabledWithoutNote(string label) =>
        new() { Icon = "upload_file", Label = label, Disabled = true };

    [Fact]
    public void A_disabled_item_with_a_note_stays_reachable_and_describes_itself()
    {
        var cut = RenderMenu(Enabled("Edit policy"), DisabledWithNote("Attach document", Reason));

        var item = cut.FindAll(".mud-menu-item")[1];

        Assert.Equal("true", item.GetAttribute("aria-disabled"));

        // The whole point: it is NOT removed from the focus order, so the description is reachable.
        Assert.NotEqual("-1", item.GetAttribute("tabindex"));
        Assert.DoesNotContain("mud-disabled", item.ClassName ?? "");

        var describedBy = item.GetAttribute("aria-describedby");
        Assert.False(string.IsNullOrEmpty(describedBy));

        // A SIBLING element with that id — not content inside the item, which would make the reason
        // part of the item's accessible name instead of its description.
        var note = cut.Find($"#{describedBy}");
        Assert.Equal(Reason, note.TextContent.Trim());
        Assert.NotSame(item, note.ParentElement);
        Assert.DoesNotContain(Reason, item.TextContent);

        // Still visible, so the reason is never carried by the dimmed styling alone.
        Assert.Contains("odc-menu-note", note.ClassName ?? "");
    }

    [Fact]
    public void A_disabled_item_with_no_note_keeps_the_ordinary_disabled_treatment()
    {
        var cut = RenderMenu(Enabled("Edit policy"), DisabledWithoutNote("Attach document"));

        var item = cut.FindAll(".mud-menu-item")[1];

        Assert.Equal("true", item.GetAttribute("aria-disabled"));
        Assert.Contains("mud-disabled", item.ClassName ?? "");
        Assert.Null(item.GetAttribute("aria-describedby"));
        Assert.Empty(cut.FindAll(".odc-menu-note"));
    }

    [Fact]
    public void An_enabled_item_is_neither_aria_disabled_nor_described()
    {
        var cut = RenderMenu(Enabled("Edit policy"));

        var item = cut.Find(".mud-menu-item");

        Assert.NotEqual("true", item.GetAttribute("aria-disabled"));
        Assert.Null(item.GetAttribute("aria-describedby"));
        Assert.Empty(cut.FindAll(".odc-menu-note"));
    }

    /// <summary>
    /// A reachable item is a clickable one unless something stops it, and the guard has to suppress
    /// BOTH halves. Firing the action would be the obvious bug; closing the menu without doing
    /// anything is the quieter one, and it reads to the user as the action having run. Both are
    /// prevented structurally: the shell carries no handler at all, so there is no ordering or
    /// early-return to get wrong.
    /// </summary>
    [Fact]
    public void A_disabled_item_with_a_note_carries_no_click_handler_at_all()
    {
        var item = DisabledWithNote("Attach document", Reason);
        item.OnClick = EventCallback.Factory.Create(new object(), () => Assert.Fail("The action ran."));

        var cut = RenderMenu(Enabled("Edit policy"), item);

        var items = cut.FindAll(".mud-menu-item");
        var disabled = items.Single(i => i.GetAttribute("aria-disabled") == "true");

        Assert.Null(disabled.GetAttribute("blazor:onclick"));

        // The enabled sibling still carries one, so this is about the disabled item and not about a
        // menu that rendered no handlers at all.
        Assert.NotNull(items.Single(i => i.GetAttribute("aria-disabled") != "true").GetAttribute("blazor:onclick"));
    }

    /// <summary>Two menus on one page must not hand out the same note id, or aria-describedby on the
    /// second would resolve to the first one's note.</summary>
    [Fact]
    public void Note_ids_are_unique_per_menu_instance()
    {
        var first = RenderMenu(DisabledWithNote("Attach document", Reason))
            .Find(".odc-menu-note").GetAttribute("id");
        var second = RenderMenu(DisabledWithNote("Attach document", Reason))
            .Find(".odc-menu-note").GetAttribute("id");

        Assert.NotEqual(first, second);
    }
}
