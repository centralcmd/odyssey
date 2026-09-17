using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using Odyssey.Client.Components;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// OdsRowActions (Odyssey Design System · components/RowActions) — the end-of-row icon cluster, live on
/// the account terms and estimates history tables. It replaced three duplicated per-surface copies, so a
/// regression here is a regression on every one of them at once.
///
/// <para>
/// What these pin is the part a refactor is most likely to lose quietly: the cluster is hidden by a
/// CLASS, not by being unrendered, because every button has to stay in the DOM and in tab order for the
/// stylesheet's <c>:focus-within</c> half of the reveal to be reachable at all. A "tidier"
/// implementation that rendered the buttons only on hover would look identical to a sighted mouse user
/// and be unreachable by keyboard.
/// </para>
/// </summary>
public class RowActionsTests
{
    private static BunitContext NewContext()
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        return ctx;
    }

    private static IReadOnlyList<OdsRowAction> EditDelete(Action? onEdit = null, Action? onDelete = null) =>
    [
        new() { Icon = "edit", Label = "Edit term", OnClick = Callback(onEdit) },
        new() { Icon = "delete", Label = "Delete term", Danger = true, OnClick = Callback(onDelete) },
    ];

    private static EventCallback<MouseEventArgs> Callback(Action? action) =>
        action is null ? default : EventCallback.Factory.Create<MouseEventArgs>(new object(), action);

    [Fact]
    public void Renders_one_button_per_action_each_with_an_accessible_name()
    {
        using var ctx = NewContext();

        var cut = ctx.Render<OdsRowActions>(p => p.Add(c => c.Actions, EditDelete()));

        var buttons = cut.FindAll(".odc-rowactions button");
        Assert.Equal(2, buttons.Count);
        Assert.Equal(["Edit term", "Delete term"], buttons.Select(b => b.GetAttribute("aria-label")));
    }

    // The reveal is a class the stylesheet acts on, and the buttons stay rendered underneath it. If this
    // ever becomes conditional rendering, keyboard users lose the actions entirely.
    [Fact]
    public void Defaults_to_the_reveal_class_while_keeping_its_buttons_rendered()
    {
        using var ctx = NewContext();

        var cut = ctx.Render<OdsRowActions>(p => p.Add(c => c.Actions, EditDelete()));

        var root = cut.Find(".odc-rowactions");
        Assert.Contains("reveal", root.ClassList);
        Assert.Equal(2, root.QuerySelectorAll("button").Length);
    }

    [Fact]
    public void Reveal_false_pins_the_cluster_visible()
    {
        using var ctx = NewContext();

        var cut = ctx.Render<OdsRowActions>(p => p
            .Add(c => c.Actions, EditDelete())
            .Add(c => c.Reveal, false));

        Assert.DoesNotContain("reveal", cut.Find(".odc-rowactions").ClassList);
    }

    // Danger is the destructive tint and Disabled has to reach the real control — a disabled-looking
    // button that still fires is the worse half of getting this wrong.
    [Fact]
    public void Carries_danger_and_disabled_through_to_the_rendered_buttons()
    {
        using var ctx = NewContext();

        var cut = ctx.Render<OdsRowActions>(p => p.Add(c => c.Actions,
        [
            new OdsRowAction { Icon = "edit", Label = "Edit term", Disabled = true },
            new OdsRowAction { Icon = "delete", Label = "Delete term", Danger = true },
        ]));

        var buttons = cut.FindAll(".odc-rowactions button");
        Assert.True(buttons[0].HasAttribute("disabled"));
        Assert.False(buttons[1].HasAttribute("disabled"));
        Assert.Contains("mud-error-text", buttons[1].ClassList);
    }

    [Fact]
    public void Invokes_the_action_its_button_was_built_from()
    {
        using var ctx = NewContext();
        var edited = 0;
        var deleted = 0;

        var cut = ctx.Render<OdsRowActions>(p => p.Add(c => c.Actions,
            EditDelete(onEdit: () => edited++, onDelete: () => deleted++)));

        cut.FindAll(".odc-rowactions button")[1].Click();

        Assert.Equal(0, edited);
        Assert.Equal(1, deleted);
    }

    // A timeline row is not a <tr>, so the stylesheet hangs its reveal off .odc-rowactions-host instead.
    // OdsTimelineItem carries that marker; without it the actions on a timeline entry never appear.
    [Fact]
    public void TimelineItem_is_its_own_rowactions_host()
    {
        using var ctx = NewContext();

        var cut = ctx.Render<OdsTimelineItem>(p => p
            .Add(c => c.Label, (RenderFragment)(b => b.AddContent(0, "Interest rate")))
            .Add(c => c.Actions, (RenderFragment)(b =>
            {
                b.OpenComponent<OdsRowActions>(0);
                b.AddAttribute(1, nameof(OdsRowActions.Actions), EditDelete());
                b.CloseComponent();
            })));

        var item = cut.Find(".odc-tl-item");
        Assert.Contains("odc-rowactions-host", item.ClassList);
        Assert.NotNull(item.QuerySelector(".odc-rowactions"));
    }
}
