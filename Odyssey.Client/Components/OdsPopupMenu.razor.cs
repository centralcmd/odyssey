using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using MudBlazor;

namespace Odyssey.Client.Components;

/// <summary>
/// The parameters, open state and keyboard contract behind <c>OdsPopupMenu</c>. See the .razor
/// file's header for why the trigger sits beside the MudMenu rather than inside it (issue #255).
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

    /// <summary>The trigger's accessible name, when its visible text or a &lt;label for&gt; does not give one.</summary>
    [Parameter] public string? AriaLabel { get; set; }

    [Parameter] public string? AriaDescribedBy { get; set; }

    [Parameter] public bool AriaInvalid { get; set; }

    /// <summary>The trigger's <c>aria-haspopup</c>. The popover is MudMenu's <c>role="menu"</c> list, so "menu" by default.</summary>
    [Parameter] public string HasPopup { get; set; } = "menu";

    [Parameter] public bool Disabled { get; set; }

    [Parameter] public Origin AnchorOrigin { get; set; } = Origin.BottomLeft;

    [Parameter] public Origin TransformOrigin { get; set; } = Origin.TopLeft;

    [Parameter] public string? PopoverClass { get; set; }

    [Parameter] public string? ListClass { get; set; }

    [Parameter] public DropdownWidth RelativeWidth { get; set; } = DropdownWidth.Ignore;

    [Parameter] public int? MaxHeight { get; set; }

    /// <summary>Raised whenever the popover opens or closes.</summary>
    [Parameter] public EventCallback<bool> OpenChanged { get; set; }

    /// <summary>
    /// Raised for every keydown on the trigger, after the built-in handling — for a consumer that
    /// keeps a keyboard contract of its own.
    /// </summary>
    [Parameter] public EventCallback<KeyboardEventArgs> OnTriggerKeyDown { get; set; }

    /// <summary>
    /// The DOM id of single-choice row <c>i</c>. Setting it (with <see cref="RowCount"/>) turns on the
    /// single-choice keyboard contract: arrows open the closed trigger, opening focuses
    /// <see cref="SelectedRow"/>, and rows call <see cref="RowKeyDownAsync"/> to rove.
    /// </summary>
    [Parameter] public Func<int, string>? RowId { get; set; }

    [Parameter] public int RowCount { get; set; }

    /// <summary>The index of the chosen row, or -1 for none (opening then focuses the first row).</summary>
    [Parameter] public int SelectedRow { get; set; } = -1;

    [Parameter(CaptureUnmatchedValues = true)]
    public Dictionary<string, object>? UserAttributes { get; set; }

    private readonly string _autoId = $"odc-popmenu-{Guid.NewGuid():N}";

    private MudMenu? _menu;
    private ElementReference _trigger;
    private bool _open;

    // Set when the next render should move focus onto the selected row (single-choice mode).
    private bool _focusRowOnOpen;

    // Set while a close this component asked for is in flight, so the OpenChanged it produces is not
    // mistaken for an outside click, and says whether that close should give the trigger focus back.
    private bool? _closeRestoresFocus;

    /// <summary>Whether the popover is open.</summary>
    public bool IsOpen => _open;

    /// <summary>The trigger's effective DOM id.</summary>
    public string EffectiveTriggerId => string.IsNullOrEmpty(TriggerId) ? _autoId : TriggerId;

    private bool SingleChoice => RowId is not null && RowCount > 0;

    private string WrapperClass =>
        string.IsNullOrWhiteSpace(Class) ? "odc-popmenu" : $"odc-popmenu {Class.Trim()}";

    private string TriggerClassName =>
        string.IsNullOrWhiteSpace(TriggerClass) ? "odc-popmenu-trigger" : $"odc-popmenu-trigger {TriggerClass.Trim()}";

    /// <summary>Opens the popover.</summary>
    public async Task OpenAsync()
    {
        if (Disabled || _open || _menu is null)
        {
            return;
        }
        await _menu.OpenMenuAsync(EventArgs.Empty);
    }

    /// <summary>
    /// Closes the popover. <paramref name="restoreFocus"/> sends focus back to the trigger — pass
    /// false only when the keystroke that closed it (Tab) is itself moving focus on.
    /// </summary>
    public async Task CloseAsync(bool restoreFocus = true)
    {
        if (_menu is null)
        {
            return;
        }
        if (!_open)
        {
            if (restoreFocus)
            {
                await FocusByIdAsync(EffectiveTriggerId);
            }
            return;
        }
        _closeRestoresFocus = restoreFocus;
        try
        {
            await _menu.CloseMenuAsync();
        }
        finally
        {
            _closeRestoresFocus = null;
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
    /// wrapping), Esc closes and restores focus, Tab closes behind itself. Enter and Space are the
    /// row button's own activation. Returns whether the key was handled.
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
                await CloseAsync(restoreFocus: true);
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// Keydown handler for a popover whose content is not single-choice rows (a checkbox list, a
    /// search box): Esc closes it and gives the trigger its focus back.
    /// </summary>
    public Task PopoverKeyDownAsync(KeyboardEventArgs e) =>
        e.Key == "Escape" ? CloseAsync(restoreFocus: true) : Task.CompletedTask;

    /// <summary>Moves focus to single-choice row <paramref name="index"/>, clamped to the list.</summary>
    public Task FocusRowAsync(int index)
    {
        if (RowId is null || RowCount <= 0)
        {
            return Task.CompletedTask;
        }
        return FocusByIdAsync(RowId(Math.Clamp(index, 0, RowCount - 1)));
    }

    private async Task OnTriggerClickAsync(MouseEventArgs e)
    {
        if (Disabled || _menu is null)
        {
            return;
        }
        if (_open)
        {
            // Focus is already on the trigger that was just clicked.
            await CloseAsync(restoreFocus: false);
        }
        else
        {
            await _menu.OpenMenuAsync(e);
        }
    }

    private async Task OnTriggerKeyDownAsync(KeyboardEventArgs e)
    {
        // Escape is deliberately NOT handled here: Blazor cannot stop propagation per key, so it
        // would also reach a wrapping OdsModal's key interceptor and cancel the dialog behind it.
        if (!Disabled && _menu is not null && !_open && SingleChoice && e.Key is "ArrowDown" or "ArrowUp")
        {
            await _menu.OpenMenuAsync(EventArgs.Empty);
        }
        await OnTriggerKeyDown.InvokeAsync(e);
    }

    private async Task OnMenuOpenChangedAsync(bool open)
    {
        _open = open;
        if (open)
        {
            _focusRowOnOpen = SingleChoice;
        }
        await OpenChanged.InvokeAsync(open);

        // A close this component did not ask for is MudMenu's click-away overlay. Whatever had focus
        // inside the popover has just unmounted, so hand it back to the trigger rather than <body>.
        if (!open && _closeRestoresFocus is null)
        {
            await FocusByIdAsync(EffectiveTriggerId);
        }
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        await base.OnAfterRenderAsync(firstRender);

        if (!_focusRowOnOpen || !_open)
        {
            return;
        }
        _focusRowOnOpen = false;

        // Open onto the current choice, not the top of the list — the same place the check glyph
        // puts a sighted user's eye.
        await FocusRowAsync(SelectedRow >= 0 ? SelectedRow : 0);
    }

    private async Task FocusByIdAsync(string id)
    {
        try { await Js.InvokeVoidAsync("odsFocusById", id); }
        catch { /* JS unavailable (e.g. prerender / teardown) */ }
    }
}
