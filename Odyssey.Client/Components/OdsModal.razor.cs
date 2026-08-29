using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using MudBlazor;

namespace Odyssey.Client.Components;

public partial class OdsModal
{
    /// <summary>Controls visibility. Bindable via @bind-Open.</summary>
    [Parameter] public bool Open { get; set; }

    [Parameter] public EventCallback<bool> OpenChanged { get; set; }

    [Parameter] public RenderFragment? Title { get; set; }

    [Parameter] public RenderFragment? Subtitle { get; set; }

    /// <summary>Optional lead-tile glyph left of the title — a Material Icons ligature, or any
    /// non-ligature character (e.g. "§") rendered as a typographic glyph (Odyssey Design System · Modal).</summary>
    [Parameter] public string? Icon { get; set; }

    /// <summary>Lead-tile tint. Warning / Error for destructive or confirm dialogs. Default Brand.</summary>
    [Parameter] public OdsModalTone IconTone { get; set; } = OdsModalTone.Brand;

    /// <summary>Called on Esc, scrim click, and the close button. Omit to hide the close button.</summary>
    [Parameter] public EventCallback OnClose { get; set; }

    /// <summary>Right-aligned footer actions (typically OdsButtons).</summary>
    [Parameter] public RenderFragment? Footer { get; set; }

    /// <summary>Optional header actions rendered in the title bar, just before the close button
    /// (e.g. a favourite toggle on the photo lightbox). Mirrors the design-system Modal head slot.</summary>
    [Parameter] public RenderFragment? HeaderActions { get; set; }

    /// <summary>Wide variant — for batch grids / file analysis.</summary>
    [Parameter] public bool Wide { get; set; }

    /// <summary>
    /// Suppresses OdsModal's built-in header (title bar + close button) entirely.
    /// For modals whose body brings its own bespoke chrome. Esc/scrim dismissal
    /// still works when <see cref="OnClose"/> is set.
    /// </summary>
    [Parameter] public bool HideHeader { get; set; }

    /// <summary>Extra CSS class(es) forwarded to the underlying dialog — for bespoke width/padding overrides.</summary>
    [Parameter] public string? Class { get; set; }

    /// <summary>Accessible name used when there is no Title.</summary>
    [Parameter] public string? AriaLabel { get; set; }

    [Parameter] public RenderFragment? ChildContent { get; set; }

    private string ToneClass => IconTone switch
    {
        OdsModalTone.Warning => "warning",
        OdsModalTone.Error => "error",
        _ => string.Empty,
    };

    // A Material Icons ligature is all-lowercase letters/digits/underscores (e.g.
    // "edit", "account_balance"); anything else (e.g. "§") is a typographic glyph.
    private static bool IsLigature(string? icon) =>
        !string.IsNullOrEmpty(icon) && icon.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '_');

    private DialogOptions _options = new();

    // An inline MudDialog renders nothing in place — when Visible flips true it
    // teleports itself into MudDialogProvider, and it only dismisses that teleported
    // instance when it re-renders with Visible=false (or via CloseAsync). Hosts must
    // therefore keep us mounted through the close (toggle Open, don't @if us away mid-
    // open) so that re-render happens; a changing @key gives fresh per-open state. The
    // teleported instance also snapshots its width Options at teleport time, so a host that
    // flips Wide mid-open (e.g. the file-analysis dialog widening from its consent gate to
    // the review grid) would otherwise stay at the original width — keying the dialog on
    // Wide re-teleports it with the new frame size. This disposal hook is the safety net for
    // the one case left — teardown while still open (e.g. navigating away) — so the
    // teleported dialog never lingers.
    private MudDialog? _dialog;

    public async ValueTask DisposeAsync()
    {
        if (_dialog is not null)
        {
            try
            { await _dialog.CloseAsync(); }
            catch { /* host tree is tearing down — best-effort dismiss */ }
        }
    }

    // Tracks the open→closed edge so we autofocus the first body field exactly once per open,
    // and the closed edge so we restore focus to whatever triggered the open (WCAG 2.4.3) —
    // without either, focus silently drops to <body> when the dialog closes.
    private bool wasOpen;
    private bool focusPending;
    private bool restorePending;

    protected override void OnParametersSet()
    {
        if (Open && !wasOpen)
            focusPending = true;
        else if (!Open && wasOpen)
            restorePending = true;
        wasOpen = Open;

        _options = new DialogOptions
        {
            MaxWidth = Wide ? MaxWidth.ExtraExtraLarge : MaxWidth.Small,
            FullWidth = true,
            CloseOnEscapeKey = OnClose.HasDelegate,
            BackdropClick = OnClose.HasDelegate,
        };
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        // The teleported dialog body is in the DOM by now — focus its first field (DS Modal behavior).
        if (focusPending)
        {
            focusPending = false;
            try
            {
                // Capture (synchronously, before MudBlazor's own deferred focus-trap can run) whatever
                // had focus — the row action that opened us — so the close edge below can restore it.
                await Js.InvokeVoidAsync("odsCaptureFocusOwner");
                await Js.InvokeVoidAsync("odsFocusFirstField");
            }
            catch { /* JS unavailable (e.g. prerender / teardown) */ }
        }

        if (restorePending)
        {
            restorePending = false;
            try
            { await Js.InvokeVoidAsync("odsRestoreFocusOwner"); }
            catch { /* JS unavailable (e.g. teardown) */ }
        }
    }

    private async Task OnVisibleChanged(bool visible)
    {
        Open = visible;
        await OpenChanged.InvokeAsync(visible);
        if (!visible)
            await OnClose.InvokeAsync();
    }

    private Task CloseAsync() => OnVisibleChanged(false);
}
