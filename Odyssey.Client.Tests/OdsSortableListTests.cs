using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using MudBlazor.Services;
using Odyssey.Client.Components;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// <see cref="OdsSortableList{TItem}"/> (Odyssey Design System · components/SortableList, version 1795477868-c9a2):
/// rows as fieldsets, the handle's name and description, the keyboard lift (Space lifts, arrows move, Space drops,
/// Escape cancels, Tab drops) with its announcements, the ⋯ menu's moves and Remove, a pointer drag's drop line and
/// drop, and heading rows. The pointer geometry and the key-dependent event control are JS
/// (<c>wwwroot/js/sortable-list.js</c>); its contract is pinned by a source check here.
/// </summary>
public class OdsSortableListTests
{
    private static BunitContext NewContext()
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        return ctx;
    }

    private static IRenderedComponent<ListHost> Render(BunitContext ctx, bool removable = true, params string[] items) =>
        ctx.Render<ListHost>(p => p
            .Add(h => h.Initial, items.Length == 0 ? ["salt", "flour", "water"] : items)
            .Add(h => h.Removable, removable));

    private static IRenderedComponent<OdsSortableList<string>> List(IRenderedComponent<ListHost> cut) =>
        cut.FindComponent<OdsSortableList<string>>();

    private static AngleSharp.Dom.IElement Handle(IRenderedComponent<ListHost> cut, int i) => cut.FindAll(".odc-sort-handle")[i];

    [Fact]
    public async Task Each_row_is_a_fieldset_named_by_its_legend_with_a_described_handle()
    {
        await using var ctx = NewContext();
        var cut = Render(ctx);

        var rows = cut.FindAll(".odc-sortable > fieldset.odc-sortable-row[data-sort-row]");
        Assert.Equal(3, rows.Count);
        Assert.Equal("Ingredient 1", rows[0].QuerySelector("legend.odc-sr-only")!.TextContent);
        var handle = Handle(cut, 0);
        Assert.Equal("button", handle.GetAttribute("type"));
        Assert.Equal("Move ingredient 1", handle.GetAttribute("aria-label"));
        Assert.Equal("false", handle.GetAttribute("aria-pressed"));
        var help = cut.Find($"#{handle.GetAttribute("aria-describedby")}");
        Assert.StartsWith("Drag to move.", help.TextContent);
        Assert.Equal("assertive", cut.Find("[role=status]").GetAttribute("aria-live"));
        Assert.Equal("salt", rows[0].QuerySelector(".cell")!.TextContent);
    }

    [Fact]
    public async Task Space_lifts_arrows_move_and_space_drops_with_each_step_announced()
    {
        await using var ctx = NewContext();
        var cut = Render(ctx);

        Handle(cut, 0).KeyDown(" ");
        Assert.Equal("true", Handle(cut, 0).GetAttribute("aria-pressed"));
        Assert.Contains("on", Handle(cut, 0).ClassList);
        Assert.Contains("is-raised", cut.FindAll("fieldset")[0].ClassList);
        Assert.Equal("Ingredient 1 lifted, position 1 of 3. Arrow keys move it, Space drops it, Escape cancels.", List(cut).Instance.Announcement);

        Handle(cut, 0).KeyDown("ArrowDown");
        Assert.Equal(["flour", "salt", "water"], cut.Instance.Items);
        Assert.Equal("Position 2 of 3.", List(cut).Instance.Announcement);
        // The handle keeps focus after the move (it was re-ordered in the DOM).
        var focusTarget = ctx.JSInterop.Invocations.Last(i => i.Identifier == "odsFocusById").Arguments[0] as string;
        Assert.Equal(Handle(cut, 1).Id, focusTarget);
        Assert.Equal("true", Handle(cut, 1).GetAttribute("aria-pressed"));

        Handle(cut, 1).KeyDown("Enter");
        Assert.Equal("false", Handle(cut, 1).GetAttribute("aria-pressed"));
        Assert.Equal("Dropped at position 2 of 3.", List(cut).Instance.Announcement);
        Assert.Contains("is-settled", cut.FindAll("fieldset")[1].ClassList);
    }

    [Fact]
    public async Task Escape_puts_the_row_back_where_it_started()
    {
        await using var ctx = NewContext();
        var cut = Render(ctx);

        Handle(cut, 0).KeyDown(" ");
        Handle(cut, 0).KeyDown("ArrowDown");
        Handle(cut, 1).KeyDown("ArrowDown");
        Assert.Equal(["flour", "water", "salt"], cut.Instance.Items);

        // What the JS module calls after stopping the key short of the dialog.
        await cut.InvokeAsync(() => List(cut).Instance.CancelLift());

        Assert.Equal(["salt", "flour", "water"], cut.Instance.Items);
        Assert.Equal("Move cancelled. Back at position 1 of 3.", List(cut).Instance.Announcement);
        Assert.All(cut.FindAll(".odc-sort-handle"), h => Assert.Equal("false", h.GetAttribute("aria-pressed")));
    }

    [Fact]
    public async Task Escape_on_the_handle_cancels_too_when_the_module_is_not_attached()
    {
        await using var ctx = NewContext();
        var cut = Render(ctx);

        Handle(cut, 2).KeyDown(" ");
        Handle(cut, 2).KeyDown("ArrowUp");
        Handle(cut, 1).KeyDown("Escape");

        Assert.Equal(["salt", "flour", "water"], cut.Instance.Items);
        Assert.Equal("Move cancelled. Back at position 3 of 3.", List(cut).Instance.Announcement);
    }

    [Fact]
    public async Task Moving_past_an_end_is_refused_in_words()
    {
        await using var ctx = NewContext();
        var cut = Render(ctx);

        Handle(cut, 0).KeyDown(" ");
        Handle(cut, 0).KeyDown("ArrowUp");
        Assert.Equal("Already first.", List(cut).Instance.Announcement);

        Handle(cut, 0).KeyDown(" ");
        Handle(cut, 2).KeyDown(" ");
        Handle(cut, 2).KeyDown("ArrowDown");
        Assert.Equal("Already last.", List(cut).Instance.Announcement);
        Assert.Equal(["salt", "flour", "water"], cut.Instance.Items);
    }

    [Fact]
    public async Task Arrows_do_nothing_until_the_row_is_lifted_and_tab_drops_it()
    {
        await using var ctx = NewContext();
        var cut = Render(ctx);

        Handle(cut, 0).KeyDown("ArrowDown");
        Assert.Equal(["salt", "flour", "water"], cut.Instance.Items);

        Handle(cut, 0).KeyDown(" ");
        Handle(cut, 0).KeyDown("Tab");
        Assert.Equal("false", Handle(cut, 0).GetAttribute("aria-pressed"));
    }

    [Fact]
    public async Task The_row_menu_offers_only_the_moves_a_row_can_make_and_remove()
    {
        await using var ctx = NewContext();
        var cut = Render(ctx);

        var menus = cut.FindComponents<OdsMenu>();
        Assert.Equal("Actions for ingredient 1", menus[0].Instance.AriaLabel);
        string[] Shown(int i) => [.. menus[i].Instance.Items.Where(m => !m.Disabled).Select(m => m.Divider ? "—" : m.Label!)];
        Assert.Equal(["Move down", "—", "Remove"], Shown(0));
        Assert.Equal(["Move up", "Move down", "—", "Remove"], Shown(1));
        Assert.Equal(["Move up", "—", "Remove"], Shown(2));
        Assert.Equal("Remove ingredient 2", menus[1].Instance.Items.Single(m => m.Label == "Remove").AriaLabel);
        Assert.True(menus[1].Instance.Items.Single(m => m.Label == "Remove").Danger);

        await cut.InvokeAsync(() => menus[0].Instance.Items.Single(m => m.Label == "Move down").OnClick.InvokeAsync());
        Assert.Equal(["flour", "salt", "water"], cut.Instance.Items);

        menus = cut.FindComponents<OdsMenu>();
        await cut.InvokeAsync(() => menus[2].Instance.Items.Single(m => m.Label == "Remove").OnClick.InvokeAsync());
        Assert.Equal(["flour", "salt"], cut.Instance.Items);
    }

    [Fact]
    public async Task Without_on_remove_the_menu_has_no_remove_or_divider()
    {
        await using var ctx = NewContext();
        var cut = Render(ctx, removable: false);

        Assert.All(cut.FindComponents<OdsMenu>(), m =>
            Assert.DoesNotContain(m.Instance.Items, i => !i.Disabled && (i.Divider || i.Label == "Remove")));
    }

    [Fact]
    public async Task A_pointer_drag_draws_the_drop_line_and_moves_the_row_on_release()
    {
        await using var ctx = NewContext();
        var cut = Render(ctx);
        var list = List(cut).Instance;

        await cut.InvokeAsync(() => list.DragOver(0, 0));
        Assert.Contains("is-lifted", cut.FindAll("fieldset")[0].ClassList);
        Assert.DoesNotContain("is-raised", cut.FindAll("fieldset")[0].ClassList);
        // Over its own gap the drop is a no-op, so no line is drawn.
        Assert.Empty(cut.FindAll(".drop-before, .drop-after"));

        await cut.InvokeAsync(() => list.DragOver(0, 3));
        Assert.Contains("drop-after", cut.FindAll("fieldset")[2].ClassList);
        await cut.InvokeAsync(() => list.DragOver(0, 2));
        Assert.Contains("drop-before", cut.FindAll("fieldset")[2].ClassList);

        await cut.InvokeAsync(() => list.DragEnd(0, 3));
        Assert.Equal(["flour", "water", "salt"], cut.Instance.Items);
        Assert.Empty(cut.FindAll(".is-lifted, .drop-before, .drop-after"));
        Assert.Contains("is-settled", cut.FindAll("fieldset")[2].ClassList);
    }

    [Fact]
    public async Task A_heading_row_is_a_band_and_steps_the_others_in()
    {
        await using var ctx = NewContext();
        var cut = Render(ctx, true, "# Dough", "flour", "water");

        Assert.Contains("has-headings", cut.Find(".odc-sortable").ClassList);
        Assert.Contains("heading", cut.FindAll("fieldset")[0].ClassList);
        Assert.DoesNotContain("heading", cut.FindAll("fieldset")[1].ClassList);
        Assert.Equal("Remove heading", cut.FindComponents<OdsMenu>()[0].Instance.Items.Single(m => m.Danger).Label);
    }

    [Fact]
    public async Task The_module_is_attached_to_the_list()
    {
        await using var ctx = NewContext();
        Render(ctx);

        Assert.Contains(ctx.JSInterop.Invocations, i => i.Identifier == "import" && (i.Arguments[0] as string) == "./js/sortable-list.js");
    }

    [Fact]
    public void The_module_stops_a_lifted_escape_before_the_dialog_sees_it()
    {
        var js = File.ReadAllText(Path.Combine(ClientSource.Root, "wwwroot/js/sortable-list.js"));

        // Escape on a LIFTED handle: stopped at the list (before MudDialog's container interceptor) and handed to .NET.
        Assert.Matches(@"lifted && e\.key === 'Escape'\)\s*\{[^}]*e\.stopPropagation\(\);[^}]*e\.stopImmediatePropagation\(\);[^}]*invokeMethodAsync\('CancelLift'\)", js);
        // Space / Enter never activate the handle as a button; arrows on a lifted handle never scroll.
        Assert.Contains("if (e.key === ' ' || e.key === 'Enter') {", js);
        Assert.Contains("lifted && (e.key === 'ArrowUp' || e.key === 'ArrowDown')", js);
        Assert.Contains("invokeMethodAsync('DragOver'", js);
        Assert.Contains("invokeMethodAsync('DragEnd'", js);
    }

    [Fact]
    public void The_module_keeps_scrolling_while_the_pointer_is_held_still_at_an_edge()
    {
        var js = File.ReadAllText(Path.Combine(ClientSource.Root, "wwwroot/js/sortable-list.js"));

        // Auto-scroll is an animation-frame loop, not a pointermove side effect, and it is cancelled on release.
        Assert.Contains("frame = requestAnimationFrame(autoScroll);", js);
        Assert.Contains("cancelAnimationFrame(frame);", js);
        // The unused focus export is gone; .NET focuses a handle through the global odsFocusById.
        Assert.DoesNotContain("focusHandle", js, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Tab_drops_a_lifted_row_and_says_where()
    {
        await using var ctx = NewContext();
        var cut = Render(ctx);

        Handle(cut, 0).KeyDown(" ");
        Handle(cut, 0).KeyDown("ArrowDown");
        Handle(cut, 1).KeyDown("Tab");

        Assert.Equal(["flour", "salt", "water"], cut.Instance.Items);
        Assert.Equal("false", Handle(cut, 1).GetAttribute("aria-pressed"));
        Assert.Equal("Dropped at position 2 of 3.", List(cut).Instance.Announcement);
        Assert.Contains("is-settled", cut.FindAll("fieldset")[1].ClassList);
    }

    [Fact]
    public async Task A_menu_move_announces_the_new_position_in_the_same_live_region()
    {
        await using var ctx = NewContext();
        var cut = Render(ctx);

        var menus = cut.FindComponents<OdsMenu>();
        await cut.InvokeAsync(() => menus[0].Instance.Items.Single(m => m.Label == "Move down").OnClick.InvokeAsync());
        Assert.Equal(["flour", "salt", "water"], cut.Instance.Items);
        Assert.Equal("Ingredient 1 moved to position 2 of 3.", List(cut).Instance.Announcement);
        Assert.Equal("Ingredient 1 moved to position 2 of 3.", cut.Find("[role=status][aria-live=assertive]").TextContent.TrimEnd('\u200B'));
        Assert.Contains("is-settled", cut.FindAll("fieldset")[1].ClassList);

        menus = cut.FindComponents<OdsMenu>();
        await cut.InvokeAsync(() => menus[2].Instance.Items.Single(m => m.Label == "Move up").OnClick.InvokeAsync());
        Assert.Equal(["flour", "water", "salt"], cut.Instance.Items);
        Assert.Equal("Ingredient 3 moved to position 2 of 3.", List(cut).Instance.Announcement);
    }

    [Fact]
    public async Task A_settle_timer_outliving_the_list_does_not_throw()
    {
        var ctx = NewContext();
        var cut = Render(ctx);
        var list = List(cut).Instance;

        await cut.InvokeAsync(() => list.DragEnd(0, 3));
        await list.DisposeAsync();
        // A late JS DragEnd after disposal: Settle must not read the disposed source's token.
        await cut.InvokeAsync(() => list.DragEnd(0, 2));
        await list.DisposeAsync();
        await ctx.DisposeAsync();
    }

    [Fact]
    public void Get_key_is_required_because_the_item_is_not_a_safe_key()
    {
        var property = typeof(OdsSortableList<string>).GetProperty(nameof(OdsSortableList<string>.GetKey))!;

        Assert.NotNull(property.GetCustomAttributes(typeof(EditorRequiredAttribute), inherit: false).SingleOrDefault());
    }

    /// <summary>A form-style host that owns the list and reorders it on every move, as a dialog does.</summary>
    public sealed class ListHost : ComponentBase
    {
        [Parameter] public IReadOnlyList<string> Initial { get; set; } = [];

        [Parameter] public bool Removable { get; set; } = true;

        public List<string> Items { get; private set; } = [];

        protected override void OnInitialized() => Items = [.. Initial];

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<OdsSortableList<string>>(0);
            builder.AddComponentParameter(1, nameof(OdsSortableList<string>.Items), Items.ToList());
            builder.AddComponentParameter(2, nameof(OdsSortableList<string>.GetLabel), (Func<string, int, string>)((s, i) => s.StartsWith('#') ? $"section {i + 1}" : $"ingredient {i + 1}"));
            builder.AddComponentParameter(3, nameof(OdsSortableList<string>.IsHeading), (Func<string, bool>)(s => s.StartsWith('#')));
            builder.AddComponentParameter(4, nameof(OdsSortableList<string>.OnMove), EventCallback.Factory.Create<OdsSortMove>(this, m =>
            {
                var item = Items[m.From];
                Items.RemoveAt(m.From);
                Items.Insert(m.To, item);
            }));
            if (Removable)
            {
                builder.AddComponentParameter(5, nameof(OdsSortableList<string>.OnRemove), EventCallback.Factory.Create<int>(this, i => Items.RemoveAt(i)));
            }

            builder.AddComponentParameter(6, nameof(OdsSortableList<string>.RenderItem), (RenderFragment<OdsSortableItem<string>>)(row => b =>
            {
                b.OpenElement(0, "span");
                b.AddAttribute(1, "class", "cell");
                b.AddContent(2, row.Item);
                b.CloseElement();
            }));
            // The test rows are distinct strings, so the value is a unique key here; a real list passes a per-row id.
            builder.AddComponentParameter(7, nameof(OdsSortableList<string>.GetKey), (Func<string, object>)(s => s));
            builder.CloseComponent();
        }
    }
}
