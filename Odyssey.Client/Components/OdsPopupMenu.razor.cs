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

    /// <summary>The index of the first item matching <paramref name="match"/>, or -1 — without allocating.</summary>
    public static int IndexOf<T>(IReadOnlyList<T> items, Func<T, bool> match)
    {
        for (var i = 0; i < items.Count; i++)
        {
            if (match(items[i]))
                return i;
        }
        return -1;
    }

    /// <summary>
    /// An accessible name that STARTS with the trigger's visible text (WCAG 2.5.3 Label in Name), so a
    /// speech-input user can say what they see: the visible parts joined by spaces, then the extra
    /// <paramref name="label"/> after a comma unless the visible text already contains it.
    /// </summary>
    public static string AccessibleName(string? label, params string?[] visibleParts)
    {
        var visible = string.Join(' ', visibleParts.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p!.Trim()));
        return string.IsNullOrWhiteSpace(label) || visible.Contains(label.Trim(), StringComparison.CurrentCultureIgnoreCase)
            ? visible
            : $"{visible}, {label.Trim()}";
    }

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
    /// false only when the keystroke that closed it (Tab) is itself moving focus on. Closing a closed
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
    /// wrapping) and Tab closes behind itself. Enter and Space are the row button's own activation;
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
                // The rows are out of the tab order, so Tab is leaving the list — close behind it
                // rather than stranding an open popover over the form.
                await CloseAsync(restoreFocus: false);
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
    /// MudOverlay's click-away. Whatever had focus inside the popover is about to unmount, so hand it
    /// back to the trigger rather than letting it fall to &lt;body&gt;.
    /// </summary>
    private Task OnOverlayClosedAsync() => CloseAsync(restoreFocus: true);

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        await base.OnAfterRenderAsync(firstRender);

        if (!_focusInOnRender || !_open)
        {
            return;
        }
        _focusInOnRender = false;

        if (SingleChoice)
        {
            // Open onto the current choice, not the top of the list — the same place the check
            // glyph puts a sighted user's eye.
            await FocusRowAsync(SelectedRow >= 0 ? SelectedRow : 0);
        }
        else if (!string.IsNullOrEmpty(FocusOnOpenId))
        {
            await FocusByIdAsync(FocusOnOpenId);
        }
        else
        {
            await InvokeJsAsync("odsFocusFirstIn", PanelId);
        }
    }

    private Task FocusByIdAsync(string id) => InvokeJsAsync("odsFocusById", id);

    private async Task InvokeJsAsync(string identifier, string id)
    {
        try
        {
            await Js.InvokeVoidAsync(identifier, id);
        }
        catch (Exception ex) when (ex is JSException or JSDisconnectedException or InvalidOperationException or TaskCanceledException)
        {
            // Focus is best-effort: no JS during prerender, or the circuit/page is tearing down.
        }
    }
}
