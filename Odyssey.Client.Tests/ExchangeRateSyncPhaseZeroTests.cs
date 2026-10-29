using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using Odyssey.Client.Components;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// The component changes the exchange-rate sync UI depends on (issue #351 §4, Phase 0; Odyssey Design
/// System · Switch, RecordTable, Modal, Alert, Button). Each one is backwards-compatible — the defaults
/// keep today's behaviour — so every test pins the new state AND the default it must not disturb.
/// </summary>
public class ExchangeRateSyncPhaseZeroTests
{
    private sealed record Row(string Id, string Name);

    private static readonly Row[] Rows = [new("a", "Alpha"), new("b", "Bravo")];

    private static BunitContext NewContext()
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        return ctx;
    }

    // ── OdsSwitch ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Switch_Busy_IsAriaDisabledAndBusy_AndIgnoresChanges()
    {
        await using var ctx = NewContext();
        var changes = 0;
        var cut = ctx.Render<OdsSwitch>(p => p
            .Add(s => s.Checked, false)
            .Add(s => s.AriaLabel, "Sync USD")
            .Add(s => s.Busy, true)
            .Add(s => s.CheckedChanged, (bool _) => changes++));

        var input = cut.Find("input");
        Assert.Equal("true", input.GetAttribute("aria-disabled"));
        Assert.Equal("true", input.GetAttribute("aria-busy"));
        // Focusable: a native disabled would drop focus out of the control.
        Assert.False(input.HasAttribute("disabled"));

        input.Change(true);
        Assert.Equal(0, changes);
        // In flight, not inactive: no dimming class, so the track keeps its non-text contrast (1.4.11).
        Assert.Empty(cut.FindAll(".odc-switch-locked"));
        Assert.NotEmpty(cut.FindAll(".odc-switch-busy"));
    }

    [Fact]
    public async Task Switch_AriaDisabled_IsNotBusy()
    {
        await using var ctx = NewContext();
        var cut = ctx.Render<OdsSwitch>(p => p
            .Add(s => s.Checked, true)
            .Add(s => s.AriaLabel, "Sync NOK")
            .Add(s => s.AriaDisabled, true));

        var input = cut.Find("input");
        Assert.Equal("true", input.GetAttribute("aria-disabled"));
        Assert.Null(input.GetAttribute("aria-busy"));
    }

    [Fact]
    public async Task Switch_Default_CarriesNeitherState_AndRaisesChanges()
    {
        await using var ctx = NewContext();
        bool? raised = null;
        var cut = ctx.Render<OdsSwitch>(p => p
            .Add(s => s.Checked, false)
            .Add(s => s.AriaLabel, "Dark mode")
            .Add(s => s.CheckedChanged, (bool v) => raised = v));

        var input = cut.Find("input");
        Assert.Null(input.GetAttribute("aria-disabled"));
        input.Change(true);
        Assert.True(raised);
    }

    [Fact]
    public async Task Switch_ResetToModel_RebuildsTheInputAndFocusesIt()
    {
        await using var ctx = NewContext();
        var cut = ctx.Render<OdsSwitch>(p => p.Add(s => s.Checked, false).Add(s => s.AriaLabel, "Sync USD"));
        var before = cut.Find("input");
        var id = before.Id;

        await cut.InvokeAsync(() => cut.Instance.ResetToModel());

        Assert.NotSame(before, cut.Find("input"));
        Assert.Contains(ctx.JSInterop.Invocations, i => i.Identifier == "odsFocusById" && Equals(i.Arguments[0], id));
    }

    [Fact]
    public async Task Switch_ResetKey_RebuildsTheInputOnChangeOnly()
    {
        await using var ctx = NewContext();
        var cut = ctx.Render<OdsSwitch>(p => p.Add(s => s.Checked, false).Add(s => s.AriaLabel, "Sync USD").Add(s => s.ResetKey, 0));
        var first = cut.Find("input");
        Assert.DoesNotContain(ctx.JSInterop.Invocations, i => i.Identifier == "odsFocusById");

        cut.Render(p => p.Add(s => s.ResetKey, 1));

        Assert.NotSame(first, cut.Find("input"));
        Assert.Contains(ctx.JSInterop.Invocations, i => i.Identifier == "odsFocusById");
    }

    // ── OdsAlert ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(Severity.Warning, null, "alert")]
    [InlineData(Severity.Warning, OdsAlertLive.Status, "status")]
    [InlineData(Severity.Info, OdsAlertLive.Alert, "alert")]
    [InlineData(Severity.Error, null, "alert")]
    [InlineData(Severity.Info, null, "status")]
    public async Task Alert_Live_OverridesTheSeverityDefault(Severity severity, OdsAlertLive? live, string role)
    {
        await using var ctx = NewContext();
        var cut = ctx.Render<OdsAlert>(p => p.Add(a => a.Severity, severity).Add(a => a.Live, live)
            .AddChildContent("Banner"));

        Assert.Equal(role, cut.Find(".mud-alert").GetAttribute("role"));
    }

    // ── OdsButton ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Button_Href_RendersALink()
    {
        await using var ctx = NewContext();
        var cut = ctx.Render<OdsButton>(p => p.Add(b => b.Href, "/settings").AddChildContent("System settings"));

        var link = cut.Find("a");
        Assert.Equal("/settings", link.GetAttribute("href"));
        Assert.Empty(cut.FindAll("button"));
    }

    [Fact]
    public async Task Button_AriaDisabled_StaysFocusable_AndSwallowsClicks()
    {
        await using var ctx = NewContext();
        var clicks = 0;
        var cut = ctx.Render<OdsButton>(p => p
            .Add(b => b.AriaDisabled, true)
            .Add(b => b.OnClick, () => clicks++)
            .AddChildContent("Run now"));

        var button = cut.Find("button");
        Assert.Equal("true", button.GetAttribute("aria-disabled"));
        Assert.False(button.HasAttribute("disabled"));
        button.Click();
        Assert.Equal(0, clicks);
    }

    [Fact]
    public async Task Button_AriaDisabled_DropsTheHref_SoTheLinkCannotNavigate()
    {
        await using var ctx = NewContext();
        var cut = ctx.Render<OdsButton>(p => p
            .Add(b => b.Href, "/settings")
            .Add(b => b.AriaDisabled, true)
            .AddChildContent("System settings"));

        Assert.Empty(cut.FindAll("[href]"));
        Assert.Equal("true", cut.Find("[aria-disabled]").GetAttribute("aria-disabled"));
    }

    // ── OdsDatePicker ────────────────────────────────────────────────────────

    /// <summary>
    /// Text elsewhere in the form is added to the input's description after MudBlazor's own helper, whose
    /// element id the picker derives — so the id it names must be one that is really rendered.
    /// </summary>
    [Fact]
    public async Task DatePicker_AriaDescribedBy_KeepsTheHelperAndAddsTheOuterText()
    {
        await using var ctx = NewContext();
        ctx.Render<MudPopoverProvider>();
        var cut = ctx.Render<OdsDatePicker>(p => p
            .Add(d => d.HelperText, "Defaults to now.")
            .Add(d => d.AriaDescribedBy, "outer-help"));

        var set = Assert.Single(ctx.JSInterop.Invocations, i => i.Identifier == "odsSetAttr" && Equals(i.Arguments[1], "aria-describedby"));
        var ids = ((string)set.Arguments[2]!).Split(' ');
        Assert.Equal("outer-help", ids[^1]);
        Assert.Contains("Defaults to now.", cut.Find($"[id='{ids[0]}']").TextContent);
    }

    // ── OdsLiveAnnouncer ─────────────────────────────────────────────────────

    [Fact]
    public async Task LiveAnnouncer_Announce_ReannouncesAnIdenticalMessage()
    {
        await using var ctx = NewContext();
        var cut = ctx.Render<OdsLiveAnnouncer>();

        await cut.InvokeAsync(() => cut.Instance.Announce("Rate deleted"));
        cut.WaitForAssertion(() => Assert.Equal("Rate deleted", cut.Find("[role=status]").TextContent));

        var cleared = false;
        var second = cut.InvokeAsync(() => cut.Instance.Announce("Rate deleted"));
        // The region empties before the same text returns — that is what makes it speak again.
        cut.WaitForAssertion(() => { if (cut.Find("[role=status]").TextContent == string.Empty) cleared = true; Assert.True(cleared); });
        await second;
        cut.WaitForAssertion(() => Assert.Equal("Rate deleted", cut.Find("[role=status]").TextContent));
    }

    [Fact]
    public async Task LiveAnnouncer_Announce_PlaysOverlappingMessagesInTurn()
    {
        await using var ctx = NewContext();
        var cut = ctx.Render<OdsLiveAnnouncer>();
        var shown = new List<string>();
        cut.OnMarkupUpdated += (_, _) =>
        {
            var text = cut.Find("[role=status]").TextContent;
            if (text.Length > 0 && (shown.Count == 0 || shown[^1] != text))
                shown.Add(text);
        };

        // Called back to back, as a row save and a run finishing can be.
        var first = cut.InvokeAsync(() => cut.Instance.Announce("Sync started for USD"));
        var second = cut.InvokeAsync(() => cut.Instance.Announce("Run finished"));
        await Task.WhenAll(first, second);

        cut.WaitForAssertion(() => Assert.Equal(["Sync started for USD", "Run finished"], shown));
        Assert.Equal("Run finished", cut.Find("[role=status]").TextContent);
    }

    [Fact]
    public async Task LiveAnnouncer_MessageParameter_StillAnnounces()
    {
        await using var ctx = NewContext();
        var cut = ctx.Render<OdsLiveAnnouncer>(p => p.Add(a => a.Message, "Showing 1–2 of 2"));

        Assert.Equal("Showing 1–2 of 2", cut.Find("[role=status]").TextContent);
    }

    // ── OdsRecordTable ───────────────────────────────────────────────────────

    private static IRenderedComponent<OdsRecordTable<Row>> RenderTable(
        BunitContext ctx, Func<Row, OdsRecordActionContext, IReadOnlyList<OdsMenuItem>>? actions = null,
        Func<Row, OdsRecordEditContext, RenderFragment>? edit = null) =>
        ctx.Render<OdsRecordTable<Row>>(p => p
            .Add(t => t.Rows, Rows)
            .Add(t => t.Columns, (IReadOnlyList<OdsRecordColumn<Row>>)
                [new() { Key = "name", HeaderText = "Name", Cell = (r, _) => b => b.AddContent(0, r.Name) }])
            .Add(t => t.RowKey, r => (object)r.Id)
            .Add(t => t.AriaLabel, "Rows")
            .Add(t => t.Actions, actions)
            .Add(t => t.RenderEdit, edit)
            .Add(t => t.RenderDetail, (RenderFragment<Row>)(row => b => b.AddContent(0, $"detail:{row.Id}"))));

    [Fact]
    public async Task RecordTable_IgnoresARowClickThatStartedOnAControl()
    {
        await using var ctx = NewContext();
        ctx.JSInterop.Setup<bool>("odsRecClickWasInteractive", _ => true).SetResult(true);
        var cut = RenderTable(ctx);

        cut.FindAll("tbody tr")[0].Click();

        Assert.Empty(cut.FindAll("tr.expanded"));
    }

    [Fact]
    public async Task RecordTable_PlainRowClickStillExpands_AndLeavesFocusAlone()
    {
        await using var ctx = NewContext();
        var cut = RenderTable(ctx);

        cut.FindAll("tbody tr")[0].Click();

        Assert.Single(cut.FindAll("tr.expanded"));
        Assert.DoesNotContain(ctx.JSInterop.Invocations, i => i.Identifier == "odsFocusFirstIn");
    }

    /// <summary>
    /// The chevron is the aria-expanded control the user just pressed: focus stays on it both ways, so
    /// pressing it again undoes the expand (WCAG 2.4.3, 3.2.2).
    /// </summary>
    [Fact]
    public async Task RecordTable_TheChevronKeepsFocusWhenItExpandsAndCollapses()
    {
        await using var ctx = NewContext();
        var cut = RenderTable(ctx);

        cut.FindAll("button.odc-rec-expand")[0].Click();
        Assert.Single(cut.FindAll("tr.expanded"));
        cut.FindAll("button.odc-rec-expand")[0].Click();

        Assert.Empty(cut.FindAll("tr.expanded"));
        Assert.DoesNotContain(ctx.JSInterop.Invocations, i => i.Identifier == "odsFocusFirstIn");
    }

    /// <summary>The row menu's View details and Collapse unmount their item, so those move focus.</summary>
    [Fact]
    public async Task RecordTable_TheMenusToggleMovesFocusIntoThePanelAndBackToTheChevron()
    {
        await using var ctx = NewContext();
        var contexts = new Dictionary<string, OdsRecordActionContext>();
        var cut = RenderTable(ctx, actions: (row, c) => { contexts[row.Id] = c; return []; });
        var chevronId = cut.FindAll("button.odc-rec-expand")[0].Id;

        await cut.InvokeAsync(contexts["a"].Toggle);
        cut.Render();
        var panel = cut.Find(".odc-rec-panel");
        Assert.Equal("-1", panel.GetAttribute("tabindex"));
        cut.WaitForAssertion(() => Assert.Contains(ctx.JSInterop.Invocations, i => i.Identifier == "odsFocusFirstIn" && Equals(i.Arguments[0], panel.Id)));

        await cut.InvokeAsync(contexts["a"].Toggle);
        cut.Render();
        cut.WaitForAssertion(() => Assert.Contains(ctx.JSInterop.Invocations, i => i.Identifier == "odsFocusFirstIn" && Equals(i.Arguments[0], chevronId)));
    }

    [Fact]
    public async Task RecordTable_ActionContext_NamesTheRowsMenuTrigger_AndDoneEndsTheEdit()
    {
        await using var ctx = NewContext();
        var contexts = new Dictionary<string, OdsRecordActionContext>();
        OdsRecordEditContext? editCtx = null;
        var cut = RenderTable(ctx,
            actions: (row, c) => { contexts[row.Id] = c; return [new OdsMenuItem { Label = "Edit", OnClick = EventCallback.Factory.Create(new object(), c.StartEdit) }]; },
            edit: (row, c) => { editCtx = c; return b => b.AddMarkupContent(0, "<input class='edit-field' />"); });

        var trigger = contexts["a"].MenuTriggerId;
        Assert.False(string.IsNullOrEmpty(trigger));
        Assert.NotNull(cut.Find($"#{trigger}"));
        Assert.NotEqual(trigger, contexts["b"].MenuTriggerId);

        // In a page the menu item's callback re-renders the host; here the test does it.
        await cut.InvokeAsync(contexts["a"].StartEdit);
        cut.Render();
        cut.WaitForElement(".edit-field");
        await cut.InvokeAsync(editCtx!.Done);

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".edit-field")));
        Assert.Contains(ctx.JSInterop.Invocations, i => i.Identifier == "odsFocusFirstIn" && Equals(i.Arguments[0], trigger));
    }

    // ── OdsModal ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Modal_InitialFocusCancel_FocusesTheSafeChoice()
    {
        await using var ctx = NewContext();
        ctx.Render<MudPopoverProvider>();
        ctx.Render<MudDialogProvider>();
        ctx.Render<OdsModal>(p => p
            .Add(m => m.Open, true)
            .Add(m => m.InitialFocus, OdsModalInitialFocus.Cancel)
            .Add(m => m.Title, (RenderFragment)(b => b.AddContent(0, "Delete fetched rates for DKK?"))));

        Assert.Contains(ctx.JSInterop.Invocations, i => i.Identifier == "odsFocusDialogCancel");
        Assert.DoesNotContain(ctx.JSInterop.Invocations, i => i.Identifier == "odsFocusFirstField");
    }

    [Fact]
    public async Task Modal_ReturnFocusToId_IsHandedToTheRestore()
    {
        await using var ctx = NewContext();
        ctx.Render<MudPopoverProvider>();
        ctx.Render<MudDialogProvider>();
        var cut = ctx.Render<OdsModal>(p => p
            .Add(m => m.Open, true)
            .Add(m => m.ReturnFocusToId, "row-a-menu")
            .Add(m => m.Title, (RenderFragment)(b => b.AddContent(0, "Delete this fetched rate?"))));

        cut.Render(p => p.Add(m => m.Open, false));

        Assert.Contains(ctx.JSInterop.Invocations,
            i => i.Identifier == "odsRestoreFocusOwner" && i.Arguments.Count == 1 && Equals(i.Arguments[0], "row-a-menu"));
    }
}
