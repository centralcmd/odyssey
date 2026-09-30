using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using MudBlazor;

namespace Odyssey.Client.Components;

/// <summary>
/// The parameters, open state and keyboard contract behind <c>OdsPopupMenu</c>. See the .razor
/// file's header for why the trigger is a real button beside the popover (issue #255).
/// </summary>
public partial class OdsPopupMenu
{
    /// <summary>The popover's content.</summary>
    [Parameter] public RenderFragment? ChildContent { get; set; }

    /// <summary>The trigger button's content. Must not contain an interactive element.</summary>
    [Parameter] public RenderFragment? TriggerContent { get; set; }

    /// <summary>
    /// Content drawn inside the anchor box before the trigger, as a SIBLING of it — e.g.
    /// OdsTagMultiSelect's chips and their remove buttons, which must never sit inside a button.
    /// </summary>
    [Parameter] public RenderFragment? BeforeTrigger { get; set; }

    /// <summary>Extra class on the anchor box (the element the popover is positioned against).</summary>
    [Parameter] public string? Class { get; set; }

    /// <summary>The trigger's id. Generated when omitted.</summary>
    [Parameter] public string? TriggerId { get; set; }

    /// <summary>The trigger's own class(es), e.g. <c>odc-ms-trigger</c>.</summary>
    [Parameter] public string? TriggerClass { get; set; }

    /// <summary>
    /// The trigger's accessible name, when its visible text or a &lt;label for&gt; does not give one.
    /// It must START with the trigger's visible text (WCAG 2.5.3). Also names the panel.
    /// </summary>
    [Parameter] public string? AriaLabel { get; set; }

    [Parameter] public string? AriaDescribedBy { get; set; }

    /// <summary>
    /// The panel's own name, when it should say what the list is FOR rather than repeat the trigger
    /// (e.g. "Tags to watch" behind an "Add tag" button). The panel is the one named container: a
    /// consumer must not add a second labelled role="group" inside it, or the name is read twice.
    /// </summary>
    [Parameter] public string? PanelLabel { get; set; }

    [Parameter] public bool AriaInvalid { get; set; }

    [Parameter] public bool Disabled { get; set; }

    [Parameter] public Origin AnchorOrigin { get; set; } = Origin.BottomLeft;

    [Parameter] public Origin TransformOrigin { get; set; } = Origin.TopLeft;

    /// <summary>Extra class on the portaled popover paper.</summary>
    [Parameter] public string? PopoverClass { get; set; }

    /// <summary>Extra class on the panel (the popover's role="menu" / role="dialog" element).</summary>
    [Parameter] public string? ListClass { get; set; }

    [Parameter] public DropdownWidth RelativeWidth { get; set; } = DropdownWidth.Ignore;

    [Parameter] public int? MaxHeight { get; set; }

    /// <summary>Raised whenever the popover opens or closes.</summary>
    [Parameter] public EventCallback<bool> OpenChanged { get; set; }

    /// <summary>Raised for every keydown on the trigger, after the built-in handling.</summary>
    [Parameter] public EventCallback<KeyboardEventArgs> OnTriggerKeyDown { get; set; }

    /// <summary>
    /// Panel mode: the id of the element to focus when the popover opens (a search box, the selected
    /// option). When omitted, the panel's first focusable element takes focus.
    /// </summary>
    [Parameter] public string? FocusOnOpenId { get; set; }

    /// <summary>
    /// The DOM id of single-choice row <c>i</c>. Setting it (with <see cref="RowCount"/>) makes the
    /// panel a role="menu": opening focuses <see cref="SelectedRow"/>, and rows call
    /// <see cref="RowKeyDownAsync"/> to rove.
    /// </summary>
    [Parameter] public Func<int, string>? RowId { get; set; }

    [Parameter] public int RowCount { get; set; }

    /// <summary>The index of the chosen row, or -1 for none (opening then focuses the first row).</summary>
    [Parameter] public int SelectedRow { get; set; } = -1;

    [Parameter(CaptureUnmatchedValues = true)]
    public Dictionary<string, object>? UserAttributes { get; set; }

    private readonly string _autoId = $"odc-popmenu-{Guid.NewGuid():N}";

    private ElementReference _trigger;
    private bool _open;

    // Set when the next render should move focus into the freshly opened panel.
    private bool _focusInOnRender;

    /// <summary>The trigger's effective DOM id.</summary>
    public string EffectiveTriggerId => string.IsNullOrEmpty(TriggerId) ? _autoId : TriggerId;

    private string PanelId => $"{EffectiveTriggerId}-panel";

    // The panel's name: its own label, else the trigger's explicit aria-label, else (null) the
    // trigger itself via aria-labelledby.
    private string? PanelName =>
        !string.IsNullOrWhiteSpace(PanelLabel) ? PanelLabel
        : !string.IsNullOrWhiteSpace(AriaLabel) ? AriaLabel
        : null;

    private string WrapperId => $"{EffectiveTriggerId}-box";

    private bool SingleChoice => RowId is not null && RowCount > 0;

    private string HasPopup => SingleChoice ? "menu" : "dialog";

    private string WrapperClass =>
        string.IsNullOrWhiteSpace(Class) ? "odc-popmenu" : $"odc-popmenu {Class.Trim()}";

    private string TriggerClassName =>
        string.IsNullOrWhiteSpace(TriggerClass) ? "odc-popmenu-trigger" : $"odc-popmenu-trigger {TriggerClass.Trim()}";

    private string PanelClassName =>
        string.IsNullOrWhiteSpace(ListClass)
            ? "mud-list mud-menu-list odc-popmenu-panel"
            : $"mud-list mud-menu-list odc-popmenu-panel {ListClass.Trim()}";

    /// <summary>Opens the popover and moves focus into it. No-op when disabled or already open.</summary>
    public async Task OpenAsync()
    {
        if (Disabled || _open)
        {
            return;
        }
        _open = true;
        _focusInOnRender = true;
        await OpenChanged.InvokeAsync(true);
        StateHasChanged();
    }

    /// <summary>
    /// Closes the popover. <paramref name="restoreFocus"/> sends focus back to the trigger — pass
    /// false only when the caller already has focus where it belongs (a click on the trigger). Closing a closed
    /// popover changes nothing, but still restores focus when asked.
    /// </summary>
    public async Task CloseAsync(bool restoreFocus = true)
    {
        if (_open)
        {
            _open = false;
            _focusInOnRender = false;
            await OpenChanged.InvokeAsync(false);
            StateHasChanged();
        }
        if (restoreFocus)
        {
            await FocusByIdAsync(EffectiveTriggerId);
        }
    }

    /// <summary>Moves focus to the trigger.</summary>
    public ValueTask FocusTriggerAsync() => _trigger.FocusAsync();

    /// <summary>
    /// The single-choice rows' keydown: ↑/↓/Home/End rove (clamping at the ends rather than
    /// wrapping) and Tab closes it, returning focus to the trigger. Enter and Space are the row button's own activation;
    /// Esc is the panel's. Returns whether the key was handled.
    /// </summary>
    public async Task<bool> RowKeyDownAsync(KeyboardEventArgs e, int index)
    {
        switch (e.Key)
        {
            case "ArrowDown":
                await FocusRowAsync(index + 1);
                return true;
            case "ArrowUp":
                await FocusRowAsync(index - 1);
                return true;
            case "Home":
                await FocusRowAsync(0);
                return true;
            case "End":
                await FocusRowAsync(RowCount - 1);
                return true;
            case "Tab":
                // The rows are out of the tab order, so Tab (or Shift+Tab) is leaving the list. The
                // popover is portaled to the end of the document, so the browser's own Tab from here
                // lands wherever the DOM puts it next (in a modal, its close button; Shift+Tab,
                // <body>). Close and continue from the trigger's place in the page order instead
                // (WCAG 2.4.3).
                await CloseAsync(restoreFocus: true);
                return true;
            case "Escape":
                // Handled by the panel this row bubbles to; claimed here so a consumer's own
                // fallback (typeahead) does not also act on it.
                return true;
            default:
                return false;
        }
    }

    /// <summary>Moves focus to single-choice row <paramref name="index"/>, clamped to the list.</summary>
    public Task FocusRowAsync(int index)
    {
        if (RowId is null || RowCount <= 0)
        {
            return Task.CompletedTask;
        }
        return FocusByIdAsync(RowId(Math.Clamp(index, 0, RowCount - 1)));
    }

    private Task OnTriggerClickAsync(MouseEventArgs e)
    {
        if (Disabled)
        {
            return Task.CompletedTask;
        }
        // Focus is already on the trigger that was just clicked.
        return _open ? CloseAsync(restoreFocus: false) : OpenAsync();
    }

    private async Task OnTriggerKeyDownAsync(KeyboardEventArgs e)
    {
        if (!Disabled)
        {
            if (!_open && e.Key is "ArrowDown" or "ArrowUp")
            {
                await OpenAsync();
            }
            else if (_open && e.Key == "Escape")
            {
                // Propagation is stopped while open, so this Esc does not also reach a wrapping
                // OdsModal's key interceptor and cancel the dialog behind the popover.
                await CloseAsync(restoreFocus: true);
            }
        }
        await OnTriggerKeyDown.InvokeAsync(e);
    }

    private Task OnPanelKeyDownAsync(KeyboardEventArgs e) =>
        e.Key == "Escape" ? CloseAsync(restoreFocus: true) : Task.CompletedTask;

    /// <summary>
    /// Panel mode: Tab went past the last control or Shift+Tab before the first — the browser moved
    /// focus onto a sentinel. Close, and continue from the trigger's place in the page order.
    /// </summary>
    private Task OnSentinelFocusAsync(FocusEventArgs e) => CloseAsync(restoreFocus: true);

    /// <summary>
    /// MudOverlay's click-away. The overlay is modeless, so the click also reaches what was clicked:
    /// a text field the user clicked into must KEEP its focus, or their typing lands on the trigger.
    /// So this close restores focus only when it was lost — to &lt;body&gt; (a click on empty space, or
    /// the focused control unmounting with the panel) or still inside the closing panel — which
    /// odsFocusIfLost decides after the click's own focus change has happened. Esc and Tab closes
    /// still restore unconditionally.
    /// </summary>
    private async Task OnOverlayClosedAsync()
    {
        await CloseAsync(restoreFocus: false);
        await InvokeJsAsync("odsFocusIfLost", EffectiveTriggerId, PanelId);
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        await base.OnAfterRenderAsync(firstRender);

        if (!_focusInOnRender || !_open)
        {
            return;
        }
        _focusInOnRender = false;

        // All three go through odsFocusInPopover, which waits for MudBlazor to place the popover:
        // focusing into it while it still sits at top:-9999px scrolls the whole page to the top.
        if (SingleChoice && RowId is not null)
        {
            // Open onto the current choice, not the top of the list — the same place the check
            // glyph puts a sighted user's eye.
            await InvokeJsAsync("odsFocusInPopover", RowId(Math.Clamp(SelectedRow >= 0 ? SelectedRow : 0, 0, RowCount - 1)), false);
        }
        else if (!string.IsNullOrEmpty(FocusOnOpenId))
        {
            await InvokeJsAsync("odsFocusInPopover", FocusOnOpenId, false);
        }
        else
        {
            await InvokeJsAsync("odsFocusInPopover", PanelId, true);
        }
    }

    private Task FocusByIdAsync(string id) => InvokeJsAsync("odsFocusById", id);

    private async Task InvokeJsAsync(string identifier, params object[] args)
    {
        try
        {
            await Js.InvokeVoidAsync(identifier, args);
        }
        catch (Exception ex) when (ex is JSException or JSDisconnectedException or InvalidOperationException or TaskCanceledException)
        {
            // Focus is best-effort: no JS during prerender, or the circuit/page is tearing down.
        }
    }
}
