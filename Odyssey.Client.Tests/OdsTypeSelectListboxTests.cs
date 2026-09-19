using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using Odyssey.Client.Components;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// OdsTypeSelect's popup semantics and keyboard contract (issue #51).
///
/// <para>
/// The defect these pin was announced rather than visible: the trigger promised a popup and the
/// popup delivered a container — that part MudBlazor gets right — holding <em>no exposed rows at
/// all</em>, because a MudMenuItem carried no role and no selected state. A screen-reader user
/// opened a named control and was read an empty list, with the current choice carried only by a
/// check glyph that is <c>aria-hidden</c> (WCAG 4.1.2 Name, Role, Value; 1.1.1 for the glyph).
/// </para>
///
/// <para>
/// MudBlazor owns the container's role and changed it: up to 9.9.0 its menu list took MudList's
/// default <c>role="listbox"</c>, so the rows were <c>role="option"</c> with <c>aria-selected</c>;
/// from 9.10.0 MudMenu passes <c>role="menu"</c>, so the same rows are <c>role="menuitemradio"</c>
/// with <c>aria-checked</c> and the trigger's <c>aria-haspopup</c> reads <c>menu</c>. What these
/// assert is unchanged in substance — every row exposed, exactly one carrying the chosen state —
/// and that is the level to keep them at. MudMenuItem gained a role in 9.10.0 but still has no
/// checked state, so the rows stay hand-rendered.
/// </para>
///
/// <para>
/// There was no OdsTypeSelect test before this, so the nine wrapper components that delegate to it
/// had no coverage either. The keyboard half is asserted the same way the component implements it —
/// through <c>odsFocusById</c>, the interop the rest of the client already uses for roving focus —
/// so these run on the fast tier without a browser.
/// </para>
/// </summary>
public class OdsTypeSelectListboxTests
{
    private static readonly IReadOnlyList<OdsTypeOption> Types =
    [
        Option("Home", "Home"),
        Option("Work", "Work"),
        Option("Switchboard", "Switchboard"),
        Option("Support", "Support"),
    ];

    private static OdsTypeOption Option(string key, string label) =>
        new() { Key = key, Label = label, Icon = "home", Color = "red", Soft = "pink" };

    /// <summary>
    /// The component next to a MudPopoverProvider. MudBlazor portals the open popover into that
    /// provider, so without it in the same tree the options render nowhere and every assertion here
    /// would pass vacuously against an empty popover.
    /// </summary>
    private sealed class SelectHost : ComponentBase
    {
        [Parameter] public string? Value { get; set; }
        [Parameter] public EventCallback<string> ValueChanged { get; set; }
        [Parameter] public IReadOnlyList<OdsTypeOption> Types { get; set; } = [];
        [Parameter] public IReadOnlyList<OdsTypeSelectGroup>? Groups { get; set; }
        [Parameter] public bool Disabled { get; set; }

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<MudPopoverProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<OdsTypeSelect>(1);
            builder.AddComponentParameter(2, nameof(OdsTypeSelect.Label), "Label");
            builder.AddComponentParameter(3, nameof(OdsTypeSelect.Value), Value);
            builder.AddComponentParameter(4, nameof(OdsTypeSelect.ValueChanged), ValueChanged);
            builder.AddComponentParameter(5, nameof(OdsTypeSelect.Types), Types);
            builder.AddComponentParameter(6, nameof(OdsTypeSelect.Groups), Groups);
            builder.AddComponentParameter(7, nameof(OdsTypeSelect.Disabled), Disabled);
            builder.CloseComponent();
        }
    }

    private static BunitContext NewContext()
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        return ctx;
    }

    private static IRenderedComponent<SelectHost> RenderOpen(
        BunitContext ctx,
        string? value = "Work",
        IReadOnlyList<OdsTypeSelectGroup>? groups = null,
        EventCallback<string> onChanged = default)
    {
        var cut = ctx.Render<SelectHost>(p => p
            .Add(h => h.Value, value)
            .Add(h => h.ValueChanged, onChanged)
            .Add(h => h.Types, groups is null ? Types : [])
            .Add(h => h.Groups, groups));
        cut.Find("button.odc-select-trigger").Click();
        return cut;
    }

    /// <summary>
    /// Option ids, re-queried per action — and the render that precedes the query is load-bearing.
    /// MudBlazor portals the open popover into MudPopoverProvider, so the options live in a subtree
    /// that is not re-diffed when only OdsTypeSelect renders: bUnit's DOM then still carries the
    /// event-handler ids the renderer has already retired, and the next keystroke dispatches against
    /// a handler that no longer exists. Rendering the host first refreshes that markup. The same is
    /// why "the list is closed" is asserted through this helper rather than a bare FindAll.
    /// </summary>
    private static IReadOnlyList<string> OptionIds(IRenderedComponent<SelectHost> cut)
    {
        cut.Render();
        return [.. cut.FindAll("[role='menuitemradio']").Select(option => option.Id!)];
    }

    private static void Press(IRenderedComponent<SelectHost> cut, string id, string key, bool ctrl = false)
    {
        cut.Render();
        cut.Find($"#{id}").KeyDown(new KeyboardEventArgs { Key = key, CtrlKey = ctrl });
    }

    private static void Choose(IRenderedComponent<SelectHost> cut, string id)
    {
        cut.Render();
        cut.Find($"#{id}").Click();
    }

    private static string? LastFocusTarget(BunitContext ctx) =>
        ctx.JSInterop.Invocations["odsFocusById"]
            .Select(invocation => invocation.Arguments[0] as string)
            .LastOrDefault();

    [Fact]
    public void The_popup_is_a_menu_of_radio_rows_and_exactly_one_is_checked()
    {
        var ctx = NewContext();
        var cut = RenderOpen(ctx);

        // MudBlazor owns this role; the trigger below has to agree with whatever it is.
        var list = cut.Find(".mud-menu-list");
        Assert.Equal("menu", list.GetAttribute("role"));
        Assert.Equal("menu", cut.Find("button.odc-select-trigger").GetAttribute("aria-haspopup"));

        var options = cut.FindAll("[role='menuitemradio']");
        Assert.Equal(Types.Count, options.Count);

        // Every row carries the state, not only the chosen one: aria-checked="false" is what
        // tells a screen reader the others are selectable and currently are not.
        Assert.All(options, option =>
            Assert.Contains(option.GetAttribute("aria-checked"), new[] { "true", "false" }));

        var selected = options.Where(o => o.GetAttribute("aria-checked") == "true").ToList();
        Assert.Single(selected);
        Assert.Contains("Work", selected[0].TextContent);
    }

    /// <summary>
    /// The rows must be children of the one container MudBlazor already renders. Wrapping them in a
    /// container role of our own would reproduce the original defect one level up: an outer widget
    /// owning no rows.
    /// </summary>
    [Fact]
    public void The_options_are_children_of_the_one_menu()
    {
        var ctx = NewContext();
        var cut = RenderOpen(ctx);

        Assert.Single(cut.FindAll("[role='menu']"));
        Assert.Empty(cut.FindAll("[role='listbox']"));

        // Compared by role rather than by instance: bUnit hands out a wrapper for Find results, so
        // the same DOM node is not the same object twice.
        Assert.All(cut.FindAll("[role='menuitemradio']"),
            option => Assert.Equal("menu", option.ParentElement!.GetAttribute("role")));
    }

    /// <summary>
    /// Selection is conveyed programmatically, so the check glyph stays decorative. Un-hiding it
    /// would make the selected row announce its state twice.
    /// </summary>
    [Fact]
    public void The_selected_row_keeps_its_check_glyph_hidden_from_assistive_technology()
    {
        var ctx = NewContext();
        var cut = RenderOpen(ctx);

        var check = cut.Find(".odc-typesel-check");
        Assert.Equal("true", check.GetAttribute("aria-hidden"));
        Assert.Equal("true", check.Closest("[role='menuitemradio']")!.GetAttribute("aria-checked"));
    }

    [Fact]
    public void A_value_matching_no_option_selects_nothing_rather_than_the_first_row()
    {
        var ctx = NewContext();
        var cut = RenderOpen(ctx, value: "Gone");

        Assert.All(cut.FindAll("[role='menuitemradio']"),
            option => Assert.Equal("false", option.GetAttribute("aria-checked")));
    }

    [Fact]
    public void Grouped_options_are_options_and_their_headings_are_not()
    {
        var ctx = NewContext();
        IReadOnlyList<OdsTypeSelectGroup> groups =
        [
            new("Personal", [Option("Home", "Home"), Option("Work", "Work")]),
            new("Shared", [Option("Switchboard", "Switchboard")]),
        ];

        var cut = RenderOpen(ctx, value: "Switchboard", groups: groups);

        Assert.Equal(3, cut.FindAll("[role='menuitemradio']").Count);
        Assert.Single(cut.FindAll("[role='menuitemradio'][aria-checked='true']"));

        // A heading is not a selectable thing; it stays out of the accessibility tree so the list
        // length a screen reader announces matches the number of choices.
        Assert.Equal("presentation", cut.Find(".odc-typesel-group").GetAttribute("role"));
        Assert.Equal("presentation", cut.Find(".odc-typesel-sep").GetAttribute("role"));

        // But the section label still has to REACH that tree, as the group's name — otherwise a
        // screen-reader user gets one flat list where a sighted user sees two labelled sections
        // (WCAG 1.3.1 Info and Relationships).
        var sections = cut.FindAll("[role='group']");
        Assert.Equal(2, sections.Count);

        var names = sections
            .Select(section => cut.Find($"#{section.GetAttribute("aria-labelledby")}").TextContent.Trim())
            .ToList();
        Assert.Equal(new[] { "Personal", "Shared" }, names);

        // Each group owns its own options, so the grouping is real rather than decorative.
        Assert.Equal(2, sections[0].QuerySelectorAll("[role='menuitemradio']").Length);
        Assert.Single(sections[1].QuerySelectorAll("[role='menuitemradio']"));
    }

    [Fact]
    public void The_trigger_reports_the_popup_and_whether_it_is_open()
    {
        var ctx = NewContext();
        var cut = ctx.Render<SelectHost>(p => p.Add(h => h.Value, "Work").Add(h => h.Types, Types));

        var trigger = cut.Find("button.odc-select-trigger");
        Assert.Equal("menu", trigger.GetAttribute("aria-haspopup"));
        Assert.Equal("false", trigger.GetAttribute("aria-expanded"));

        trigger.Click();
        Assert.Equal("true", cut.Find("button.odc-select-trigger").GetAttribute("aria-expanded"));
    }

    [Fact]
    public void Opening_moves_focus_to_the_selected_option()
    {
        var ctx = NewContext();
        var cut = RenderOpen(ctx, value: "Switchboard");

        var id = cut.Find("[role='menuitemradio'][aria-checked='true']").Id;
        Assert.Equal(id, LastFocusTarget(ctx));
    }

    [Fact]
    public void Opening_with_no_selection_focuses_the_first_option()
    {
        var ctx = NewContext();
        var cut = RenderOpen(ctx, value: null);

        Assert.Equal(cut.FindAll("[role='menuitemradio']")[0].Id, LastFocusTarget(ctx));
    }

    [Theory]
    [InlineData("ArrowDown", 2)]
    [InlineData("ArrowUp", 0)]
    [InlineData("Home", 0)]
    [InlineData("End", 3)]
    public void Arrows_and_the_ends_rove_over_the_options(string key, int expected)
    {
        var ctx = NewContext();
        var cut = RenderOpen(ctx);

        var ids = OptionIds(cut);
        Press(cut, ids[1], key);

        Assert.Equal(ids[expected], LastFocusTarget(ctx));
    }

    /// <summary>
    /// Roving stops at the ends rather than wrapping, matching the design system and OdsMoneyField:
    /// a wrap past the last option reads as the list having restarted.
    /// </summary>
    [Theory]
    [InlineData(0, "ArrowUp", 0)]
    [InlineData(3, "ArrowDown", 3)]
    public void Roving_clamps_at_the_ends(int from, string key, int expected)
    {
        var ctx = NewContext();
        var cut = RenderOpen(ctx);

        var ids = OptionIds(cut);
        Press(cut, ids[from], key);

        Assert.Equal(ids[expected], LastFocusTarget(ctx));
    }

    [Fact]
    public void Typeahead_jumps_to_the_next_option_that_starts_with_the_letter()
    {
        var ctx = NewContext();
        var cut = RenderOpen(ctx);

        var ids = OptionIds(cut);

        // From "Home": S matches Switchboard, then Support — the next one each time, so a repeated
        // letter cycles rather than sticking.
        Press(cut, ids[0], "s");
        Assert.Equal(ids[2], LastFocusTarget(ctx));

        Press(cut, ids[2], "s");
        Assert.Equal(ids[3], LastFocusTarget(ctx));
    }

    [Fact]
    public void Typeahead_ignores_a_modified_keystroke()
    {
        var ctx = NewContext();
        var cut = RenderOpen(ctx);

        var before = LastFocusTarget(ctx);
        Press(cut, OptionIds(cut)[0], "s", ctrl: true);

        Assert.Equal(before, LastFocusTarget(ctx));
    }

    [Fact]
    public void Escape_closes_the_list_and_gives_the_trigger_its_focus_back()
    {
        var ctx = NewContext();
        var cut = RenderOpen(ctx);

        var triggerId = cut.Find("button.odc-select-trigger").Id;
        Press(cut, OptionIds(cut)[1], "Escape");

        Assert.Empty(OptionIds(cut));
        Assert.Equal(triggerId, LastFocusTarget(ctx));
        Assert.Equal("false", cut.Find("button.odc-select-trigger").GetAttribute("aria-expanded"));
    }

    /// <summary>
    /// The options are out of the tab order, so Tab is leaving the list — it must not strand an open
    /// popover over the form. Focus is left alone: Tab's own default is what moves it on.
    /// </summary>
    [Fact]
    public void Tab_closes_the_list_behind_itself_without_pulling_focus_back()
    {
        var ctx = NewContext();
        var cut = RenderOpen(ctx);

        var before = LastFocusTarget(ctx);
        Press(cut, OptionIds(cut)[1], "Tab");

        Assert.Empty(OptionIds(cut));
        Assert.Equal(before, LastFocusTarget(ctx));
    }

    [Fact]
    public void Choosing_an_option_reports_it_closes_the_list_and_restores_focus()
    {
        var ctx = NewContext();
        string? picked = null;
        var cut = RenderOpen(ctx, onChanged: EventCallback.Factory.Create<string>(
            new object(), value => picked = value));

        var triggerId = cut.Find("button.odc-select-trigger").Id;
        Choose(cut, OptionIds(cut)[2]);

        Assert.Equal("Switchboard", picked);
        Assert.Empty(OptionIds(cut));
        Assert.Equal(triggerId, LastFocusTarget(ctx));
    }

    /// <summary>
    /// Re-choosing the current value raises nothing — but it still has to close. These rows are not
    /// MudMenuItems, so MudBlazor no longer closes the popover for us.
    /// </summary>
    [Fact]
    public void Re_choosing_the_current_option_closes_without_reporting_a_change()
    {
        var ctx = NewContext();
        var changes = 0;
        var cut = RenderOpen(ctx, onChanged: EventCallback.Factory.Create<string>(
            new object(), _ => changes++));

        Choose(cut, cut.Find("[role='menuitemradio'][aria-checked='true']").Id!);

        Assert.Equal(0, changes);
        Assert.Empty(OptionIds(cut));
    }

    /// <summary>
    /// The closed trigger's own keyboard path, which had no coverage at all even though "the control
    /// could not be opened from the keyboard" is one of the defects this component was fixed for.
    /// </summary>
    [Theory]
    [InlineData("ArrowDown")]
    [InlineData("ArrowUp")]
    public void An_arrow_on_the_closed_trigger_opens_the_list(string key)
    {
        var ctx = NewContext();
        var cut = ctx.Render<SelectHost>(p => p.Add(h => h.Value, "Work").Add(h => h.Types, Types));

        Assert.Empty(cut.FindAll("[role='menuitemradio']"));

        cut.Find("button.odc-select-trigger").KeyDown(new KeyboardEventArgs { Key = key });

        Assert.Equal(Types.Count, OptionIds(cut).Count);
        Assert.Equal("true", cut.Find("button.odc-select-trigger").GetAttribute("aria-expanded"));
    }

    [Fact]
    public void An_unrelated_key_on_the_closed_trigger_leaves_it_closed()
    {
        var ctx = NewContext();
        var cut = ctx.Render<SelectHost>(p => p.Add(h => h.Value, "Work").Add(h => h.Types, Types));

        cut.Find("button.odc-select-trigger").KeyDown(new KeyboardEventArgs { Key = "a" });

        Assert.Empty(OptionIds(cut));
    }

    /// <summary>
    /// MudBlazor renders the activator wrapper disabled too, so this guards the component's own
    /// early return rather than the rendered markup — a disabled field must not open on an arrow.
    /// </summary>
    [Fact]
    public void A_disabled_trigger_does_not_open_on_an_arrow()
    {
        var ctx = NewContext();
        var cut = ctx.Render<SelectHost>(p => p
            .Add(h => h.Value, "Work")
            .Add(h => h.Types, Types)
            .Add(h => h.Disabled, true));

        cut.Find("button.odc-select-trigger").KeyDown(new KeyboardEventArgs { Key = "ArrowDown" });

        Assert.Empty(OptionIds(cut));
    }

    /// <summary>
    /// Enter and Space on the trigger reach it twice: MudMenu's activator wrapper toggles on the
    /// keydown, and the browser then synthesises a click from the same keystroke. Honouring both
    /// toggled twice and left the popup shut, which is the keyboard-inoperability bug itself.
    ///
    /// <para>
    /// A synthesised click is the one that reports <c>Detail == 0</c>. The earlier fix — a blanket
    /// <c>@onkeydown:stopPropagation</c> on the trigger — also worked, but Blazor evaluates that
    /// directive once per render rather than per key, so it swallowed Escape as well and stopped it
    /// cancelling a wrapping dialog. This pins the narrow guard so that regression cannot come back.
    /// </para>
    /// </summary>
    [Fact]
    public void A_keyboard_synthesised_click_on_the_trigger_is_ignored()
    {
        var ctx = NewContext();
        var cut = ctx.Render<SelectHost>(p => p.Add(h => h.Value, "Work").Add(h => h.Types, Types));

        // Detail == 0 is a click the keyboard produced; the wrapper has already acted on it.
        cut.Find("button.odc-select-trigger").Click(new MouseEventArgs { Detail = 0 });
        Assert.Empty(OptionIds(cut));

        // Detail == 1 is a real pointer click, and still opens.
        cut.Find("button.odc-select-trigger").Click(new MouseEventArgs { Detail = 1 });
        Assert.Equal(Types.Count, OptionIds(cut).Count);
    }

    /// <summary>
    /// The trigger must not swallow keys it has no use for: its Escape has to keep bubbling, or it
    /// stops reaching the key interceptor that cancels a wrapping OdsModal.
    /// </summary>
    [Fact]
    public void The_trigger_does_not_stop_keys_from_propagating()
    {
        var ctx = NewContext();
        var cut = ctx.Render<SelectHost>(p => p.Add(h => h.Value, "Work").Add(h => h.Types, Types));

        Assert.DoesNotContain("onkeydown:stopPropagation", cut.Markup, StringComparison.Ordinal);
    }

    /// <summary>
    /// The other typeahead branch: a buffer that GROWS re-tests the option already focused, so
    /// refining "s" to "sw" stays on Switchboard rather than stepping past it.
    /// </summary>
    [Fact]
    public void Typeahead_refines_onto_the_option_it_is_already_on()
    {
        var ctx = NewContext();
        var cut = RenderOpen(ctx);

        var ids = OptionIds(cut);

        Press(cut, ids[0], "s");
        Assert.Equal(ids[2], LastFocusTarget(ctx));

        Press(cut, ids[2], "w");
        Assert.Equal(ids[2], LastFocusTarget(ctx));
    }
}
