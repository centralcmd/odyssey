using AngleSharp.Dom;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Web;
using MudBlazor;
using Odyssey.Client.Components;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// The keyboard and focus contract of every control built on <see cref="OdsPopupMenu"/> (issue #255,
/// WCAG 2.1.1, 2.4.3, 1.4.13), asserted per host rather than through one of them.
///
/// <para>
/// The popover is portaled to the end of the document, so a popup that opened with focus left on its
/// trigger could not be reached with Tab at all, and one that could not be dismissed with Esc would
/// strand the user. Every host is therefore held to: ↓/↑ or the button's own Enter/Space click opens
/// it AND moves focus in; a second click closes it; Esc inside, or on the trigger while open, closes
/// it and returns focus to the trigger; a click outside closes it and returns focus. Single-choice
/// hosts additionally rove their rows. Focus is observed through the JS interop that moves it
/// (<c>odsFocusById</c> / <c>odsFocusInPopover</c>), which is the only thing bUnit can see — a browser
/// run is what observes <c>document.activeElement</c>.
/// </para>
/// </summary>
public class OdsPopupMenuKeyboardTests
{
    private static readonly string[] FocusCalls = ["odsFocusById", "odsFocusInPopover"];

    /// <summary>Every select-style host — OdsMenu is MudMenu's own activator and is covered by MudBlazor.</summary>
    public static TheoryData<string> Hosts =>
        [.. NestedInteractiveControlTests.CaseTable.Keys.Where(k => k != nameof(OdsMenu))];

    public static TheoryData<string> SingleChoiceHosts =>
        [.. NestedInteractiveControlTests.CaseTable
            .Where(c => c.Key != nameof(OdsMenu) && c.Value.Popup == "menu")
            .Select(c => c.Key)];

    public static TheoryData<string> PanelHosts =>
        [.. NestedInteractiveControlTests.CaseTable.Where(c => c.Value.Popup == "dialog").Select(c => c.Key)];

    private static (BunitContext Ctx, IRenderedComponent<IComponent> Cut) Render(string host)
    {
        var ctx = NestedInteractiveControlTests.NewContext();
        return (ctx, NestedInteractiveControlTests.Render(ctx, host));
    }

    private static IElement Trigger(IRenderedComponent<IComponent> cut, string host)
    {
        cut.Render();
        return cut.Find(NestedInteractiveControlTests.CaseTable[host].Trigger);
    }

    /// <summary>The trigger's opening tag as bUnit renders it, Blazor event directives included
    /// (AngleSharp's own serialisation drops the <c>blazor:</c> attributes).</summary>
    private static string TriggerTag(IRenderedComponent<IComponent> cut, string host)
    {
        var id = Trigger(cut, host).Id;
        var markup = cut.Markup;
        var at = markup.IndexOf($"id=\"{id}\"", StringComparison.Ordinal);
        var start = markup.LastIndexOf('<', at);
        return markup[start..(markup.IndexOf('>', at) + 1)];
    }

    private static bool IsOpen(IRenderedComponent<IComponent> cut, string host) =>
        Trigger(cut, host).GetAttribute("aria-expanded") == "true";

    private static IElement Panel(IRenderedComponent<IComponent> cut, string host)
    {
        var id = Trigger(cut, host).GetAttribute("aria-controls");
        Assert.False(string.IsNullOrEmpty(id));
        return cut.Find($"#{id}");
    }

    /// <summary>The last focus move, as (interop identifier, target id).</summary>
    private static (string Identifier, string? Target) LastFocus(BunitContext ctx) =>
        ctx.JSInterop.Invocations
            .Where(i => FocusCalls.Contains(i.Identifier))
            .Select(i => (i.Identifier, i.Arguments[0] as string))
            .LastOrDefault();

    private static int FocusCount(BunitContext ctx) =>
        ctx.JSInterop.Invocations.Count(i => FocusCalls.Contains(i.Identifier));

    /// <summary>Focus went INTO the open panel: onto an element inside it, or to its first focusable.</summary>
    private static void AssertFocusMovedIn(BunitContext ctx, IRenderedComponent<IComponent> cut, string host)
    {
        var panel = Panel(cut, host);
        var call = ctx.JSInterop.Invocations.Last(i => FocusCalls.Contains(i.Identifier));
        // Opening focuses through odsFocusInPopover, which waits for the popover to be placed.
        Assert.Equal("odsFocusInPopover", call.Identifier);
        var target = call.Arguments[0] as string;
        if (call.Arguments[1] is true)
        {
            // "The panel's first focusable control".
            Assert.Equal(panel.Id, target);
            Assert.NotNull(panel.QuerySelector("button:not([disabled]), input:not([disabled])"));
        }
        else
        {
            Assert.NotNull(panel.QuerySelector($"#{target}"));
        }
    }

    private static void AssertFocusReturnedToTrigger(BunitContext ctx, IRenderedComponent<IComponent> cut, string host) =>
        Assert.Equal(("odsFocusById", Trigger(cut, host).Id), LastFocus(ctx));

    [Theory]
    [MemberData(nameof(Hosts))]
    public void An_arrow_on_the_closed_trigger_opens_it_and_moves_focus_in(string host)
    {
        var (ctx, cut) = Render(host);

        Trigger(cut, host).KeyDown(new KeyboardEventArgs { Key = "ArrowDown" });

        Assert.True(IsOpen(cut, host));
        AssertFocusMovedIn(ctx, cut, host);
    }

    /// <summary>Enter and Space reach a button as its own click, with <c>Detail == 0</c>.</summary>
    [Theory]
    [MemberData(nameof(Hosts))]
    public void The_keyboard_click_opens_it_and_moves_focus_in(string host)
    {
        var (ctx, cut) = Render(host);

        Trigger(cut, host).Click(new MouseEventArgs { Detail = 0 });

        Assert.True(IsOpen(cut, host));
        AssertFocusMovedIn(ctx, cut, host);
    }

    [Theory]
    [MemberData(nameof(Hosts))]
    public void A_second_click_closes_it_without_moving_focus(string host)
    {
        var (ctx, cut) = Render(host);
        Trigger(cut, host).Click();
        var before = FocusCount(ctx);

        Trigger(cut, host).Click();

        Assert.False(IsOpen(cut, host));
        Assert.Empty(cut.FindAll(".mud-popover-open"));
        // Focus is already on the trigger that was clicked — no second move.
        Assert.Equal(before, FocusCount(ctx));
    }

    [Theory]
    [MemberData(nameof(Hosts))]
    public void Escape_inside_the_panel_closes_it_and_returns_focus(string host)
    {
        var (ctx, cut) = Render(host);
        Trigger(cut, host).Click();

        Panel(cut, host).KeyDown(new KeyboardEventArgs { Key = "Escape" });

        Assert.False(IsOpen(cut, host));
        AssertFocusReturnedToTrigger(ctx, cut, host);
    }

    /// <summary>
    /// Esc from a control deep in the panel bubbles to the panel — the checkbox, the search box, the
    /// row — so the dismissal does not depend on where focus happens to be.
    /// </summary>
    [Theory]
    [MemberData(nameof(Hosts))]
    public void Escape_from_a_control_inside_the_panel_closes_it_and_returns_focus(string host)
    {
        var (ctx, cut) = Render(host);
        Trigger(cut, host).Click();

        var inner = Panel(cut, host).QuerySelector("input, button")!;
        inner.KeyDown(new KeyboardEventArgs { Key = "Escape" });

        Assert.False(IsOpen(cut, host));
        AssertFocusReturnedToTrigger(ctx, cut, host);
    }

    /// <summary>
    /// Esc on the trigger while open closes it — and while open the trigger stops its keys bubbling,
    /// so the same Esc cannot also cancel a wrapping OdsModal. Closed, keys bubble as normal.
    /// </summary>
    [Theory]
    [MemberData(nameof(Hosts))]
    public void Escape_on_the_open_trigger_closes_it_and_only_it(string host)
    {
        var (ctx, cut) = Render(host);
        Assert.DoesNotContain("onkeydown:stopPropagation", TriggerTag(cut, host), StringComparison.Ordinal);

        Trigger(cut, host).Click();
        Assert.Contains("onkeydown:stopPropagation", TriggerTag(cut, host), StringComparison.Ordinal);

        Trigger(cut, host).KeyDown(new KeyboardEventArgs { Key = "Escape" });

        Assert.False(IsOpen(cut, host));
        AssertFocusReturnedToTrigger(ctx, cut, host);
        Assert.DoesNotContain("onkeydown:stopPropagation", TriggerTag(cut, host), StringComparison.Ordinal);
    }

    /// <summary>
    /// A click outside is MudOverlay's close. The overlay is modeless, so the click also lands on
    /// whatever was clicked — a text field the user clicked into must keep its focus, or their
    /// typing goes to the trigger. So this close never focuses the trigger unconditionally: it asks
    /// <c>odsFocusIfLost</c>, which refocuses the trigger only when focus fell to the body or is
    /// still in the closing panel (the live run checks both outcomes in Chromium).
    /// </summary>
    [Theory]
    [MemberData(nameof(Hosts))]
    public void A_click_outside_closes_it_and_restores_focus_only_if_lost(string host)
    {
        var (ctx, cut) = Render(host);
        Trigger(cut, host).Click();
        var panelId = Panel(cut, host).Id;
        var before = ctx.JSInterop.Invocations.Count(i => i.Identifier == "odsFocusById");

        cut.Find(".mud-overlay").Click();

        Assert.False(IsOpen(cut, host));
        Assert.Empty(cut.FindAll(".mud-popover-open"));
        // No unconditional refocus...
        Assert.Equal(before, ctx.JSInterop.Invocations.Count(i => i.Identifier == "odsFocusById"));
        // ...exactly one conditional one, naming this trigger and this panel.
        var call = Assert.Single(ctx.JSInterop.Invocations, i => i.Identifier == "odsFocusIfLost");
        Assert.Equal(Trigger(cut, host).Id, call.Arguments[0]);
        Assert.Equal(panelId, call.Arguments[1]);
    }

    /// <summary>Esc and Tab closes still restore unconditionally — only the click-away is conditional.</summary>
    [Theory]
    [MemberData(nameof(Hosts))]
    public void Escape_never_goes_through_the_conditional_restore(string host)
    {
        var (ctx, cut) = Render(host);
        Trigger(cut, host).Click();

        Panel(cut, host).KeyDown(new KeyboardEventArgs { Key = "Escape" });

        AssertFocusReturnedToTrigger(ctx, cut, host);
        Assert.DoesNotContain(ctx.JSInterop.Invocations, i => i.Identifier == "odsFocusIfLost");
    }

    /// <summary>The sentinels are presentational: focusable only so the browser's Tab can land there.</summary>
    [Theory]
    [MemberData(nameof(PanelHosts))]
    public void The_tab_sentinels_are_presentational(string host)
    {
        var (_, cut) = Render(host);
        Trigger(cut, host).Click();

        Assert.All(Panel(cut, host).QuerySelectorAll("[data-popmenu-sentinel]"),
            s => Assert.Equal("presentation", s.GetAttribute("role")));
    }

    [Theory]
    [MemberData(nameof(SingleChoiceHosts))]
    public void Opening_a_single_choice_list_focuses_the_selected_row(string host)
    {
        var (ctx, cut) = Render(host);

        Trigger(cut, host).Click();

        var rows = Panel(cut, host).QuerySelectorAll("[role='menuitemradio']");
        Assert.NotEmpty(rows);
        var selected = rows.Single(r => r.GetAttribute("aria-checked") == "true");
        Assert.Equal(("odsFocusInPopover", selected.Id), LastFocus(ctx));
    }

    [Theory]
    [MemberData(nameof(SingleChoiceHosts))]
    public void Single_choice_rows_rove_and_clamp(string host)
    {
        var (ctx, cut) = Render(host);
        Trigger(cut, host).Click();

        IReadOnlyList<string> Ids() => [.. Panel(cut, host).QuerySelectorAll("[role='menuitemradio']").Select(r => r.Id!)];
        void Press(string id, string key) => cut.Find($"#{id}").KeyDown(new KeyboardEventArgs { Key = key });

        var ids = Ids();
        Press(ids[0], "ArrowDown");
        Assert.Equal(ids[1], LastFocus(ctx).Target);
        Press(ids[0], "ArrowUp");
        Assert.Equal(ids[0], LastFocus(ctx).Target);
        Press(ids[0], "End");
        Assert.Equal(ids[^1], LastFocus(ctx).Target);
        Press(ids[^1], "ArrowDown");
        Assert.Equal(ids[^1], LastFocus(ctx).Target);
        Press(ids[^1], "Home");
        Assert.Equal(ids[0], LastFocus(ctx).Target);
    }

    /// <summary>
    /// Tab or Shift+Tab leaves the list: it closes and focus continues from the trigger. Left to the
    /// browser, Tab from a portaled row lands on whatever the DOM puts next (a modal's close button)
    /// and Shift+Tab on the body — both seen in a live Chromium run.
    /// </summary>
    [Theory]
    [MemberData(nameof(SingleChoiceHosts))]
    public void Tab_or_shift_tab_from_a_row_closes_and_returns_focus(string host)
    {
        foreach (var shift in new[] { false, true })
        {
            var (ctx, cut) = Render(host);
            Trigger(cut, host).Click();

            Panel(cut, host).QuerySelector("[role='menuitemradio']")!
                .KeyDown(new KeyboardEventArgs { Key = "Tab", ShiftKey = shift });

            Assert.False(IsOpen(cut, host));
            AssertFocusReturnedToTrigger(ctx, cut, host);
        }
    }

    /// <summary>OdsMoneyField's currency list keeps its own roving handler; Tab there does the same.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Tab_from_a_currency_option_closes_and_returns_focus(bool shift)
    {
        var (ctx, cut) = Render(nameof(OdsMoneyField));
        Trigger(cut, nameof(OdsMoneyField)).Click();

        Panel(cut, nameof(OdsMoneyField)).QuerySelector("[role='option']")!
            .KeyDown(new KeyboardEventArgs { Key = "Tab", ShiftKey = shift });

        Assert.False(IsOpen(cut, nameof(OdsMoneyField)));
        AssertFocusReturnedToTrigger(ctx, cut, nameof(OdsMoneyField));
    }

    [Theory]
    [MemberData(nameof(SingleChoiceHosts))]
    public void Choosing_a_row_closes_the_list_and_returns_focus(string host)
    {
        var (ctx, cut) = Render(host);
        Trigger(cut, host).Click();

        Panel(cut, host).QuerySelectorAll("[role='menuitemradio']").Last().Click();

        Assert.False(IsOpen(cut, host));
        AssertFocusReturnedToTrigger(ctx, cut, host);
    }

    /// <summary>
    /// A checkbox panel is a non-modal dialog: Tab moves between its controls, so it must not close
    /// on Tab the way a single-choice list does.
    /// </summary>
    [Theory]
    [MemberData(nameof(PanelHosts))]
    public void Tab_inside_a_panel_keeps_it_open(string host)
    {
        var (_, cut) = Render(host);
        Trigger(cut, host).Click();

        Panel(cut, host).KeyDown(new KeyboardEventArgs { Key = "Tab" });

        Assert.True(IsOpen(cut, host));
    }

    private const string Tabbable =
        "a[href], button:not([disabled]), input:not([disabled]), select:not([disabled]), textarea:not([disabled]), [tabindex]:not([tabindex='-1'])";

    /// <summary>
    /// The panel's tab order is bracketed by the two sentinels, with the real controls between: the
    /// browser's own Tab from the last control (Done, Clear, a checkbox) lands on the end sentinel,
    /// and Shift+Tab from the first lands on the start one.
    /// </summary>
    [Theory]
    [MemberData(nameof(PanelHosts))]
    public void A_panel_tab_order_is_bracketed_by_the_sentinels(string host)
    {
        var (_, cut) = Render(host);
        Trigger(cut, host).Click();

        var stops = Panel(cut, host).QuerySelectorAll(Tabbable).ToList();

        Assert.True(stops.Count >= 3, "a real control between the sentinels");
        Assert.Equal("start", stops[0].GetAttribute("data-popmenu-sentinel"));
        Assert.Equal("end", stops[^1].GetAttribute("data-popmenu-sentinel"));
        Assert.All(stops.Skip(1).SkipLast(1), c => Assert.False(c.HasAttribute("data-popmenu-sentinel")));
    }

    /// <summary>
    /// Tab past the last control must not leave the portaled panel for the end of the page with the
    /// popup still open (WCAG 2.4.3): it closes and focus continues from the trigger.
    /// </summary>
    [Theory]
    [MemberData(nameof(PanelHosts))]
    public void Tab_past_the_last_control_closes_the_panel_and_returns_focus(string host)
    {
        var (ctx, cut) = Render(host);
        Trigger(cut, host).Click();

        Panel(cut, host).QuerySelector("[data-popmenu-sentinel='end']")!.Focus();

        Assert.False(IsOpen(cut, host));
        Assert.Empty(cut.FindAll(".mud-popover-open"));
        AssertFocusReturnedToTrigger(ctx, cut, host);
    }

    [Theory]
    [MemberData(nameof(PanelHosts))]
    public void Shift_tab_before_the_first_control_closes_the_panel_and_returns_focus(string host)
    {
        var (ctx, cut) = Render(host);
        Trigger(cut, host).Click();

        Panel(cut, host).QuerySelector("[data-popmenu-sentinel='start']")!.Focus();

        Assert.False(IsOpen(cut, host));
        AssertFocusReturnedToTrigger(ctx, cut, host);
    }

    /// <summary>A single-choice list has no sentinels: its rows are out of the tab order and Tab from
    /// a row already closes it behind itself.</summary>
    [Theory]
    [MemberData(nameof(SingleChoiceHosts))]
    public void A_single_choice_list_has_no_tab_sentinels(string host)
    {
        var (_, cut) = Render(host);
        Trigger(cut, host).Click();

        Assert.Empty(Panel(cut, host).QuerySelectorAll("[data-popmenu-sentinel]"));
    }

    // ── OdsPopupMenu's own API ──────────────────────────────────────────────────────────────

    private sealed class ApiHost : ComponentBase
    {
        [Parameter] public bool Disabled { get; set; }
        [Parameter] public EventCallback<bool> OpenChanged { get; set; }
        [Parameter] public EventCallback<KeyboardEventArgs> OnTriggerKeyDown { get; set; }

        public OdsPopupMenu Popup = default!;

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<MudPopoverProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<OdsPopupMenu>(1);
            builder.AddComponentParameter(2, nameof(OdsPopupMenu.TriggerId), "probe");
            builder.AddComponentParameter(3, nameof(OdsPopupMenu.Disabled), Disabled);
            builder.AddComponentParameter(4, nameof(OdsPopupMenu.OpenChanged), OpenChanged);
            builder.AddComponentParameter(5, nameof(OdsPopupMenu.OnTriggerKeyDown), OnTriggerKeyDown);
            builder.AddComponentParameter(6, nameof(OdsPopupMenu.TriggerContent), (RenderFragment)(b => b.AddContent(0, "Probe")));
            builder.AddComponentParameter(7, nameof(OdsPopupMenu.ChildContent), (RenderFragment)(b => b.AddMarkupContent(0, "<input id=\"probe-search\" />")));
            builder.AddComponentReferenceCapture(8, r => Popup = (OdsPopupMenu)r);
            builder.CloseComponent();
        }
    }

    [Fact]
    public void Every_trigger_keystroke_is_forwarded_to_the_consumer()
    {
        var ctx = NestedInteractiveControlTests.NewContext();
        var keys = new List<string>();
        var cut = ctx.Render<ApiHost>(p => p.Add(h => h.OnTriggerKeyDown,
            EventCallback.Factory.Create<KeyboardEventArgs>(new object(), e => keys.Add(e.Key))));

        cut.Find("#probe").KeyDown(new KeyboardEventArgs { Key = "a" });
        cut.Find("#probe").KeyDown(new KeyboardEventArgs { Key = "ArrowDown" });

        Assert.Equal(["a", "ArrowDown"], keys);
    }

    [Fact]
    public async Task OpenAsync_does_nothing_while_disabled()
    {
        var ctx = NestedInteractiveControlTests.NewContext();
        var changes = new List<bool>();
        var cut = ctx.Render<ApiHost>(p => p
            .Add(h => h.Disabled, true)
            .Add(h => h.OpenChanged, EventCallback.Factory.Create<bool>(new object(), changes.Add)));

        await cut.InvokeAsync(() => cut.Instance.Popup.OpenAsync());
        cut.Find("#probe").KeyDown(new KeyboardEventArgs { Key = "ArrowDown" });

        Assert.Empty(changes);
        Assert.Equal("false", cut.Find("#probe").GetAttribute("aria-expanded"));
    }

    [Fact]
    public async Task OpenAsync_twice_opens_once_and_CloseAsync_on_a_closed_popup_reports_nothing()
    {
        var ctx = NestedInteractiveControlTests.NewContext();
        var changes = new List<bool>();
        var cut = ctx.Render<ApiHost>(p => p
            .Add(h => h.OpenChanged, EventCallback.Factory.Create<bool>(new object(), changes.Add)));
        var popup = cut.Instance.Popup;

        await cut.InvokeAsync(() => popup.OpenAsync());
        await cut.InvokeAsync(() => popup.OpenAsync());
        await cut.InvokeAsync(() => popup.CloseAsync(restoreFocus: false));
        var focusBefore = FocusCount(ctx);
        await cut.InvokeAsync(() => popup.CloseAsync(restoreFocus: false));

        Assert.Equal([true, false], changes);
        Assert.Equal(focusBefore, FocusCount(ctx));
    }

    /// <summary>
    /// A close that restores focus moves it exactly once — the close does not ALSO pass through the
    /// click-away path and focus the trigger a second time.
    /// </summary>
    [Fact]
    public async Task CloseAsync_restores_focus_exactly_once()
    {
        var ctx = NestedInteractiveControlTests.NewContext();
        var cut = ctx.Render<ApiHost>();
        var popup = cut.Instance.Popup;
        await cut.InvokeAsync(() => popup.OpenAsync());
        var before = ctx.JSInterop.Invocations.Count(i => i.Identifier == "odsFocusById");

        await cut.InvokeAsync(() => popup.CloseAsync());
        cut.Render();

        Assert.Equal(before + 1, ctx.JSInterop.Invocations.Count(i => i.Identifier == "odsFocusById"));
        Assert.Equal("probe", ctx.JSInterop.Invocations["odsFocusById"].Last().Arguments[0]);
    }

    /// <summary>With no FocusOnOpenId, a panel opens onto its first focusable control.</summary>
    [Fact]
    public void A_panel_without_a_focus_target_focuses_its_first_control()
    {
        var ctx = NestedInteractiveControlTests.NewContext();
        var cut = ctx.Render<ApiHost>();

        cut.Find("#probe").Click();

        Assert.Equal(("odsFocusInPopover", "probe-panel"), LastFocus(ctx));
        Assert.Equal(true, ctx.JSInterop.Invocations["odsFocusInPopover"].Last().Arguments[1]);
        Assert.Equal("dialog", cut.Find("#probe-panel").GetAttribute("role"));
        Assert.Equal("dialog", cut.Find("#probe").GetAttribute("aria-haspopup"));
    }

    [Theory]
    [InlineData(null, new[] { "Show", "25" }, "Show 25")]
    [InlineData("Rows per page", new[] { "Show", "25", "" }, "Show 25, Rows per page")]
    [InlineData("View", new[] { "View", "Board" }, "View Board")]
    [InlineData("currency", new[] { "NOK" }, "NOK, currency")]
    public void The_accessible_name_starts_with_the_visible_text(string? label, string[] visible, string expected) =>
        Assert.Equal(expected, OdsPopupMenuText.AccessibleName(label, visible));
}
