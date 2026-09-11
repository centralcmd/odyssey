using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using MudBlazor;

namespace Odyssey.Client.Components;

/// <summary>
/// The parameters, state and keyboard contract behind OdsTypeSelect. The option row itself stays in
/// the markup file, which is the only place a render fragment can live; everything it calls is here.
/// See the .razor file's header for why the popup is assembled the way it is (issue #51).
/// </summary>
public partial class OdsTypeSelect
{
    /// <summary>Selected enum key. Bindable via @bind-Value.</summary>
    [Parameter] public string? Value { get; set; }

    [Parameter] public EventCallback<string> ValueChanged { get; set; }

    /// <summary>Flat list of options. Ignored when <see cref="Groups"/> is set.</summary>
    [Parameter] public IReadOnlyList<OdsTypeOption> Types { get; set; } = [];

    /// <summary>Labelled sections (e.g. Assets / Liabilities). When set, takes precedence over Types.</summary>
    [Parameter] public IReadOnlyList<OdsTypeSelectGroup>? Groups { get; set; }

    [Parameter] public string? Label { get; set; }

    [Parameter] public string Placeholder { get; set; } = "Select type…";

    /// <summary>Accessible name for the trigger when used without a visible <see cref="Label"/>
    /// (e.g. the compact inline file-kind picker). Rendered as aria-label on the trigger button.</summary>
    [Parameter] public string? AriaLabel { get; set; }

    [Parameter] public string? Help { get; set; }

    [Parameter] public string? Error { get; set; }

    [Parameter] public bool Required { get; set; }

    [Parameter] public bool Optional { get; set; }

    [Parameter] public bool Disabled { get; set; }

    [Parameter] public string? Class { get; set; }

    /// <summary>Extra class on the portaled popover — e.g. to widen the dropdown for a compact trigger
    /// whose own width would otherwise pin the list too narrow.</summary>
    [Parameter] public string? PopoverClass { get; set; }

    [Parameter] public string? Id { get; set; }

    [Parameter(CaptureUnmatchedValues = true)]
    public Dictionary<string, object>? UserAttributes { get; set; }

    /// <summary>How long a typeahead buffer survives between keystrokes, matching the design system.</summary>
    private const int TypeaheadWindowMs = 600;

    private string FieldId = default!;

    private MudMenu? _menu;

    // Open state, so the trigger reports aria-expanded. Driven by MudMenu.OpenChanged
    // (reading MudMenu.Open directly trips MUD0012).
    private bool _open;

    // Set when the popover opens so the next render can move focus into the list. MudBlazor's own
    // "focus the first item" pass is inert here: it only walks registered MudMenuItems.
    private bool _focusOnOpen;

    private string _typeahead = string.Empty;
    private DateTime _typeaheadAt;

    protected override void OnInitialized() => FieldId = Id ?? $"odc-typesel-{Guid.NewGuid():N}";

    /// <summary>Every option in render order — the index space the keyboard roves over.</summary>
    private IReadOnlyList<OdsTypeOption> Ordered =>
        Groups is not null ? [.. Groups.SelectMany(g => g.Items)] : Types;

    private OdsTypeOption? Selected => Ordered.FirstOrDefault(o => o.Key == Value);

    private string? DescribedBy =>
        (string.IsNullOrEmpty(Error) && string.IsNullOrEmpty(Help)) ? null : $"{FieldId}-help";

    private string OptionId(string key) => $"{FieldId}-opt-{key}";

    private int IndexOf(string? key)
    {
        var ordered = Ordered;
        for (var i = 0; i < ordered.Count; i++)
        {
            if (ordered[i].Key == key)
            {
                return i;
            }
        }
        return -1;
    }

    private void OnOpenChanged(bool open)
    {
        _open = open;
        if (open)
        {
            _focusOnOpen = true;
            _typeahead = string.Empty;
        }
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        await base.OnAfterRenderAsync(firstRender);

        if (!_focusOnOpen || !_open)
        {
            return;
        }
        _focusOnOpen = false;

        // Open onto the current choice, not the top of the list — the same place the check glyph
        // puts a sighted user's eye.
        var ordered = Ordered;
        if (ordered.Count == 0)
        {
            return;
        }
        var index = IndexOf(Value);
        await FocusAsync(OptionId(ordered[index >= 0 ? index : 0].Key));
    }

    private async Task OnTriggerKeyAsync(KeyboardEventArgs e)
    {
        // Enter and Space are left to the button's own activation, which reaches ToggleAsync.
        if (Disabled || _open || _menu is null)
        {
            return;
        }
        if (e.Key is "ArrowDown" or "ArrowUp")
        {
            await _menu.OpenMenuAsync(EventArgs.Empty);
        }
    }

    private async Task OnOptionKeyAsync(KeyboardEventArgs e, OdsTypeOption option)
    {
        var ordered = Ordered;
        var index = IndexOf(option.Key);
        switch (e.Key)
        {
            case "ArrowDown":
                await FocusIndexAsync(index + 1);
                break;
            case "ArrowUp":
                await FocusIndexAsync(index - 1);
                break;
            case "Home":
                await FocusIndexAsync(0);
                break;
            case "End":
                await FocusIndexAsync(ordered.Count - 1);
                break;
            case "Tab":
                // The options are out of the tab order, so Tab is leaving the list — close behind it
                // rather than stranding an open popover over the form.
                await CloseAsync(restoreFocus: false);
                break;
            case "Escape":
                await CloseAsync(restoreFocus: true);
                break;
            default:
                // Enter and Space are the button's own activation, which reaches PickAsync.
                if (e.Key.Length == 1 && e.Key != " " && !e.CtrlKey && !e.MetaKey && !e.AltKey)
                {
                    await TypeaheadAsync(e.Key, index);
                }
                break;
        }
    }

    private async Task TypeaheadAsync(string character, int from)
    {
        var now = DateTime.UtcNow;
        _typeahead = (now - _typeaheadAt).TotalMilliseconds < TypeaheadWindowMs
            ? _typeahead + character
            : character;
        _typeaheadAt = now;

        var ordered = Ordered;
        if (ordered.Count == 0)
        {
            return;
        }

        // One letter pressed repeatedly cycles through the options starting with it, so "s s" reaches
        // the second S option rather than searching for "ss" and matching nothing. A growing buffer
        // instead re-tests the option already focused, so typing "sw" after "s" landed on
        // "Switchboard" stays put rather than skipping to the next match.
        var search = _typeahead.Length > 1 && _typeahead.All(c => c == _typeahead[0])
            ? _typeahead[..1]
            : _typeahead;
        var begin = search.Length > 1 ? Math.Max(from, 0) : Math.Max(from, -1) + 1;
        for (var step = 0; step < ordered.Count; step++)
        {
            var candidate = ordered[(begin + step) % ordered.Count];
            if (candidate.Label.StartsWith(search, StringComparison.CurrentCultureIgnoreCase))
            {
                await FocusAsync(OptionId(candidate.Key));
                return;
            }
        }
    }

    private async Task FocusIndexAsync(int index)
    {
        var ordered = Ordered;
        if (ordered.Count == 0)
        {
            return;
        }
        await FocusAsync(OptionId(ordered[Math.Clamp(index, 0, ordered.Count - 1)].Key));
    }

    private async Task CloseAsync(bool restoreFocus)
    {
        if (_menu is not null)
        {
            await _menu.CloseMenuAsync();
        }
        if (restoreFocus)
        {
            await FocusAsync(FieldId);
        }
    }

    private async Task FocusAsync(string id)
    {
        try { await Js.InvokeVoidAsync("odsFocusById", id); }
        catch { /* JS unavailable (e.g. prerender / teardown) */ }
    }

    private async Task PickAsync(OdsTypeOption option)
    {
        if (option.Key != Value)
        {
            Value = option.Key;
            await ValueChanged.InvokeAsync(option.Key);
        }
        // Always close: these rows are not MudMenuItems, so nothing closes the popover for us.
        await CloseAsync(restoreFocus: true);
    }
}
