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

    private OdsPopupMenu? _popup;

    // Open state, for the trigger's open styling. OdsPopupMenu owns aria-expanded.
    private bool _open;

    private string _typeahead = string.Empty;
    private DateTime _typeaheadAt;

    protected override void OnInitialized() => FieldId = Id ?? $"odc-typesel-{Guid.NewGuid():N}";

    /// <summary>Every option in render order — the index space the keyboard roves over.</summary>
    private IReadOnlyList<OdsTypeOption> Ordered =>
        Groups is not null ? [.. Groups.SelectMany(g => g.Items)] : Types;

    private OdsTypeOption? Selected => Ordered.FirstOrDefault(o => o.Key == Value);

    private string RequiredId => $"{FieldId}-req";

    private string? DescribedBy
    {
        get
        {
            var ids = string.Join(' ', new[]
            {
                string.IsNullOrEmpty(Error) && string.IsNullOrEmpty(Help) ? null : $"{FieldId}-help",
                Required ? RequiredId : null,
            }.Where(id => id is not null));
            return ids.Length == 0 ? null : ids;
        }
    }

    private string OptionId(string key) => $"{FieldId}-opt-{key}";

    // OdsPopupMenu's single-choice mode addresses rows by index: opening focuses the selected one,
    // and ↑/↓/Home/End rove over this same render order.
    private string RowIdOf(int index) => OptionId(Ordered[index].Key);

    private string TriggerClass => string.Join(' ', new[]
    {
        "odc-select-trigger",
        _open ? "open" : null,
        Selected is null ? "placeholder" : null,
    }.Where(c => c is not null));

    private string GroupId(int index) => $"{FieldId}-grp-{index}";

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
            _typeahead = string.Empty;
        }
    }

    private async Task OnOptionKeyAsync(KeyboardEventArgs e, OdsTypeOption option)
    {
        var index = IndexOf(option.Key);
        if (_popup is not null && await _popup.RowKeyDownAsync(e, index))
        {
            return;
        }
        // Enter and Space are the button's own activation, which reaches PickAsync.
        if (e.Key.Length == 1 && e.Key != " " && !e.CtrlKey && !e.MetaKey && !e.AltKey)
        {
            await TypeaheadAsync(e.Key, index);
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
        if (_popup is not null)
        {
            await _popup.CloseAsync(restoreFocus: true);
        }
    }
}
