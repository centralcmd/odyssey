using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using MudBlazor;

namespace Odyssey.Client.Components;

public partial class OdsTagMultiSelect
{
    /// <summary>Visible label rendered above the control, and the popover group's name.</summary>
    [Parameter] public string? Label { get; set; }

    /// <summary>Accessible name for a label-less usage (e.g. a dense grid cell). Applied as the
    /// trigger's <c>aria-label</c> when no visible <see cref="Label"/> is set.</summary>
    [Parameter] public string? AriaLabel { get; set; }

    /// <summary>Id applied to the trigger button, so focus can be restored to this cell.</summary>
    [Parameter] public string? Id { get; set; }

    /// <summary>Selected ids. Bindable via @bind-Value.</summary>
    [Parameter] public IReadOnlyCollection<string> Value { get; set; } = [];

    [Parameter] public EventCallback<IReadOnlyCollection<string>> ValueChanged { get; set; }

    /// <summary>The selectable members.</summary>
    [Parameter, EditorRequired] public IReadOnlyList<OdsOption> Options { get; set; } = [];

    /// <summary>Placeholder shown in the control box when nothing is selected.</summary>
    [Parameter] public string Placeholder { get; set; } = "No tags";

    /// <summary>Label next to the "+" affordance when the box is empty.</summary>
    [Parameter] public string AddLabel { get; set; } = "Add tag";

    /// <summary>
    /// Enables an inline "Create …" row when the query matches no option.
    /// Receives the typed text; return the new option (or null to skip).
    /// </summary>
    [Parameter] public Func<string, OdsOption?>? OnCreate { get; set; }

    /// <summary>Prefix for the create row label.</summary>
    [Parameter] public string CreateLabel { get; set; } = "Create";

    /// <summary>Helper text below the control (replaced by <see cref="Error"/> when set).</summary>
    [Parameter] public string? Help { get; set; }

    /// <summary>Error message — flips the field to its error state, sets <c>aria-invalid</c> on the
    /// trigger and replaces the helper with an associated <c>role="alert"</c> message.</summary>
    [Parameter] public string? Error { get; set; }

    [Parameter] public bool Required { get; set; }

    /// <summary>Render an "Optional" marker next to the label.</summary>
    [Parameter] public bool Optional { get; set; }

    [Parameter] public bool Disabled { get; set; }

    /// <summary>Text shown when the search matches nothing and create is unavailable.</summary>
    [Parameter] public string EmptyText { get; set; } = "No tags match";

    /// <summary>
    /// The options are still loading — an announced row, deliberately distinct from "no match". A host
    /// that loads its options asynchronously needs this: without it, opening the picker early shows
    /// "No … match", which is indistinguishable from a genuinely empty address book.
    /// </summary>
    [Parameter] public bool Loading { get; set; }

    /// <summary>Copy for that row.</summary>
    [Parameter] public string LoadingText { get; set; } = "Loading…";

    /// <summary>Accessible name of the search field — name the entity being searched.</summary>
    [Parameter] public string SearchLabel { get; set; } = "Search tags";

    /// <summary>Visible placeholder of the search field. Defaults to the tag wording.</summary>
    [Parameter] public string? SearchPlaceholder { get; set; }

    /// <summary>
    /// Label for a selected id that is absent from <see cref="Options"/>, so a raw id is never
    /// rendered or announced. A host that knows the real state should pass
    /// <see cref="ChipTemplate"/> instead — this is the floor, not the answer.
    /// </summary>
    [Parameter] public string UnknownLabel { get; set; } = "Unknown";

    /// <summary>
    /// Renders the chip <b>body</b> for a member, keyed by its id — e.g. <c>OdsContactChip</c> with its
    /// Archived / Unavailable states.
    ///
    /// <para>
    /// The picker keeps owning the remove <c>&lt;button&gt;</c> and the default <c>.odc-chip</c>
    /// wrapper is not emitted (the template brings its own, and the two would double-apply). The
    /// context is the id string rather than the <see cref="OdsOption"/>, because the option carries no
    /// availability and should not grow one for a single consumer: the host already holds the record
    /// list it built the options from.
    /// </para>
    /// </summary>
    [Parameter] public RenderFragment<string>? ChipTemplate { get; set; }

    /// <summary>
    /// True for a member the picker must not remove: the bulk <b>Clear</b> keeps it (and reports how
    /// many were kept) <b>and</b> no remove control is rendered for it.
    ///
    /// <para>
    /// For a member with no row in the list to have been chosen from — an archived or unresolvable
    /// link. The write path refuses its removal, so a remove affordance would silently no-op and the
    /// member would reappear on reload; the honest thing is not to offer one.
    /// </para>
    /// </summary>
    [Parameter] public Func<string, bool>? PreserveOnClear { get; set; }

    /// <summary>Singular noun used in the live-region announcements.</summary>
    [Parameter] public string Noun { get; set; } = "tag";

    /// <summary>Plural of <see cref="Noun"/>. Defaults to <c>Noun + "s"</c>, which is wrong for
    /// "person" — the one existing instance that needs this.</summary>
    [Parameter] public string? NounPlural { get; set; }

    [Parameter] public string? Class { get; set; }

    private MudMenu _menu = default!;
    private bool _open;
    private string _query = string.Empty;
    private string _announcement = string.Empty;
    private int _announceNonce;

    private ElementReference _trigger;
    // One slot per rendered chip, so a removal can move focus to the NEXT chip's remove control.
    private ElementReference[] _removeButtons = [];

    // Set by a removal: the index of the remove control to land on after the re-render, or -1 for the
    // trigger (the last chip went). Focus is never lost to <body>.
    private int? _pendingFocus;

    // Mirror the controlled Value into a local set so toggling a row updates the
    // chips immediately; OnParametersSet re-syncs to the parent.
    private HashSet<string> _selected = [];

    private readonly string _autoId = $"tagms-{Guid.NewGuid():N}";

    private string TriggerId => Id ?? _autoId;
    private string LabelId => $"{TriggerId}-label";
    private string MessageId => $"{TriggerId}-help";

    protected override void OnParametersSet()
    {
        _selected = [.. Value];
        if (_removeButtons.Length != Value.Count)
        {
            _removeButtons = new ElementReference[Value.Count];
        }
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (_pendingFocus is not { } want)
        {
            return;
        }

        _pendingFocus = null;
        try
        {
            if (want >= 0 && want < _removeButtons.Length)
            {
                await _removeButtons[want].FocusAsync();
            }
            else
            {
                await _trigger.FocusAsync();
            }
        }
        catch (Exception)
        {
            // Best-effort: a chip removed as the dialog closes has nothing left to focus.
        }
    }

    /// <summary>
    /// Moves focus to this picker's trigger. The public focus API a host needs in order to send focus
    /// to the first invalid picker after a failed save (WCAG 3.3.1 with the associated message).
    /// </summary>
    public ValueTask FocusAsync() => _trigger.FocusAsync();

    private string FieldClass
    {
        get
        {
            var cls = "odc-field";
            if (!string.IsNullOrEmpty(Error))
                cls += " error";
            if (!string.IsNullOrEmpty(Class))
                cls += " " + Class;
            return cls;
        }
    }

    private string ControlClass
    {
        get
        {
            var cls = "odc-tagms-control";
            if (_open)
                cls += " open";
            if (_selected.Count == 0)
                cls += " placeholder";
            if (Disabled)
                cls += " disabled";
            return cls;
        }
    }

    private string? Message => string.IsNullOrEmpty(Error) ? Help : Error;

    private string? TriggerAriaLabel => string.IsNullOrEmpty(Label) ? AriaLabel : null;

    private string GroupLabel => !string.IsNullOrEmpty(Label) ? Label : (AriaLabel ?? AddLabel);

    private string EffectiveSearchPlaceholder => SearchPlaceholder
        ?? (OnCreate is not null ? "Search or add a tag…" : "Search tags…");

    private string CreateRowLabel => $"{CreateLabel} \"{_query.Trim()}\"";

    private bool IsLocked(string value) => PreserveOnClear?.Invoke(value) == true;

    private bool CanClear => Value.Any(v => !IsLocked(v));

    private string LabelOf(OdsOption option) => option.Label;

    private static string? IconStyle(OdsOption option) =>
        string.IsNullOrEmpty(option.IconColor) ? null : $"color:{option.IconColor};";

    private string RemoveLabel(OdsOption option) => $"Remove {LabelOf(option)}";

    // Selected options in the parent's order. An id absent from Options falls back to UnknownLabel —
    // never the raw id, which would render as a GUID chip announcing "Remove 3f2a1c9e-…". A host that
    // knows the real state passes ChipTemplate, which replaces this body entirely.
    private IReadOnlyList<OdsOption> Selected => Value
        .Select(v => Options.FirstOrDefault(o => o.Value == v) ?? new OdsOption(v, UnknownLabel))
        .ToList();

    private IEnumerable<OdsOption> Filtered => string.IsNullOrWhiteSpace(_query)
        ? Options
        : Options.Where(o => o.Label.Contains(_query.Trim(), StringComparison.OrdinalIgnoreCase));

    private bool ShowCreate => OnCreate is not null
        && !Loading
        && !string.IsNullOrWhiteSpace(_query)
        && !Options.Any(o => string.Equals(o.Label, _query.Trim(), StringComparison.OrdinalIgnoreCase));

    // Setting an identical live-region string twice does not re-fire it, so every message carries an
    // invisible zero-width counter token.
    private void Say(string text)
    {
        _announceNonce++;
        _announcement = text + new string('​', _announceNonce % 4 + 1);
    }

    private string Plural(int count) => count == 1 ? Noun : (NounPlural ?? Noun + "s");

    private Task Toggle(string value)
    {
        var adding = !_selected.Remove(value);
        if (adding)
            _selected.Add(value);

        var option = Options.FirstOrDefault(o => o.Value == value) ?? new OdsOption(value, UnknownLabel);
        Say($"{LabelOf(option)} {(adding ? "added" : "removed")}. {_selected.Count} {Plural(_selected.Count)} selected.");
        return Emit();
    }

    private Task Remove(string value, int index)
    {
        if (IsLocked(value))
            return Task.CompletedTask;

        var option = Selected.FirstOrDefault(o => o.Value == value) ?? new OdsOption(value, UnknownLabel);
        _selected.Remove(value);

        // Focus moves to the next chip's remove control, or the trigger when the last chip goes.
        _pendingFocus = _selected.Count == 0 ? -1 : Math.Min(index, _selected.Count - 1);

        Say($"{LabelOf(option)} removed. {_selected.Count} {Plural(_selected.Count)} selected.");
        return Emit();
    }

    private Task Clear()
    {
        // Preserves the members that have no row in the checkbox list to have been chosen from, and
        // says how many were kept — a bare Clear() would empty exactly the members the write path then
        // refuses to remove.
        var kept = Value.Where(IsLocked).ToList();
        _selected = [.. kept];

        Say(kept.Count > 0
            ? $"Selection cleared. {kept.Count} {Plural(kept.Count)} kept — "
              + (kept.Count == 1 ? "it cannot" : "they cannot") + " be removed here."
            : "Selection cleared.");
        return Emit();
    }

    private Task Create()
    {
        if (OnCreate is null || string.IsNullOrWhiteSpace(_query))
            return Task.CompletedTask;

        var created = OnCreate(_query.Trim());
        _query = string.Empty;
        if (created is null)
            return Task.CompletedTask;

        _selected.Add(created.Value);
        Say($"{created.Label} created and added.");
        return Emit();
    }

    private Task OnSearchKeyDown(KeyboardEventArgs e)
    {
        if (e.Key == "Enter" && ShowCreate)
            return Create();
        return Task.CompletedTask;
    }

    private Task Done() => _menu.CloseMenuAsync();

    // Emit the next set in the parent's existing order (preserving it) plus any
    // newly-added ids appended, so chip order stays stable across edits.
    private Task Emit()
    {
        var ordered = Value.Where(_selected.Contains)
            .Concat(_selected.Where(v => !Value.Contains(v)))
            .ToList();
        return ValueChanged.InvokeAsync(ordered);
    }
}
