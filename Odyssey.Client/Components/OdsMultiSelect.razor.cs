using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using MudBlazor;

namespace Odyssey.Client.Components;

/// <summary>
/// The parameters, search, selection and keyboard wiring behind <c>OdsMultiSelect</c>. See the .razor
/// file's header for the control's contract (Odyssey Design System · components/MultiSelect).
/// </summary>
public partial class OdsMultiSelect
{
    [Inject] private IJSRuntime Js { get; set; } = default!;

    /// <summary>Trigger label (the filter's name, e.g. "Status").</summary>
    [Parameter] public string? Label { get; set; }

    /// <summary>Selected values. Bindable via @bind-Values.</summary>
    [Parameter] public IReadOnlyCollection<string> Values { get; set; } = [];

    [Parameter] public EventCallback<IReadOnlyCollection<string>> ValuesChanged { get; set; }

    /// <summary>The options.</summary>
    [Parameter, EditorRequired] public IReadOnlyList<OdsOption> Options { get; set; } = [];

    /// <summary>Leading Material Icons ligature on the trigger.</summary>
    [Parameter] public string? Icon { get; set; }

    /// <summary>
    /// A literal character used as the trigger's mark instead of an icon ligature — for marks the icon
    /// font has no glyph for (§, №, ‰). Typeset in the UI font at icon size; wins over <see cref="Icon"/>.
    /// </summary>
    [Parameter] public string? Glyph { get; set; }

    /// <summary>
    /// Withhold the trigger's border and fill until hover, focus or open — for a trigger that is a
    /// heading as much as a control (a chart's own series picker). Padding is unchanged, so nothing
    /// shifts when the chrome appears.
    /// </summary>
    [Parameter] public bool Quiet { get; set; }

    /// <summary>
    /// The row's content when it needs more than one voice (a coloured figure beside muted qualifying
    /// text) — the DS <c>label</c>-as-node. The option's <see cref="OdsOption.Label"/> stays its plain
    /// text form (the DS <c>text</c>).
    /// </summary>
    [Parameter] public RenderFragment<OdsOption>? OptionContent { get; set; }

    /// <summary>Show the search field. Defaults to on once there are more than 8 options.</summary>
    [Parameter] public bool? Searchable { get; set; }

    /// <summary>Accessible name of the search field. Defaults to "Search {label}".</summary>
    [Parameter] public string? SearchLabel { get; set; }

    /// <summary>Visible placeholder of the search field.</summary>
    [Parameter] public string SearchPlaceholder { get; set; } = "Search…";

    /// <summary>Options are still loading — an announced row, distinct from "no matches".</summary>
    [Parameter] public bool Loading { get; set; }

    /// <summary>Copy for the loading row.</summary>
    [Parameter] public string LoadingText { get; set; } = "Loading…";

    /// <summary>Row shown when the search matches nothing.</summary>
    [Parameter] public string EmptyText { get; set; } = "No matches";

    /// <summary>Which edge the popover anchors to.</summary>
    [Parameter] public OdsAlign Align { get; set; } = OdsAlign.Start;

    /// <summary>The trigger button's id — set this and point an OdsFieldShell's HtmlFor at it when
    /// using this as a labelled form field rather than a filter-bar pill, so the accessible name comes
    /// from the field's own label instead of every instance sharing this trigger's visible text.</summary>
    [Parameter] public string? Id { get; set; }

    /// <summary>Id(s) of element(s) describing this control (e.g. an OdsFieldShell's help line) —
    /// rendered as aria-describedby on the trigger.</summary>
    [Parameter] public string? DescribedBy { get; set; }

    [Parameter] public string? Class { get; set; }

    [Parameter(CaptureUnmatchedValues = true)]
    public Dictionary<string, object>? UserAttributes { get; set; }

    private OdsPopupMenu _popup = default!;

    /// <summary>The option count above which the search field turns on by default.</summary>
    public const int SearchLimit = 8;

    private string _query = string.Empty;
    private bool _open;
    private bool _wireKeys;

    private bool SearchOn => Searchable ?? Options.Count > SearchLimit;

    private readonly string _autoId = $"odc-ms-{Guid.NewGuid():N}";

    // One stable trigger id for the popup (the caller's Id, else a generated one), so the search
    // field's and the panel's ids are known before the popover first renders.
    private string TriggerId => string.IsNullOrEmpty(Id) ? _autoId : Id;

    private string SearchId => $"{TriggerId}-search";

    // OdsPopupMenu names its panel "{trigger id}-panel".
    private string PanelId => $"{TriggerId}-panel";

    private string EffectiveSearchLabel =>
        !string.IsNullOrWhiteSpace(SearchLabel) ? SearchLabel : $"Search {(Label ?? string.Empty).ToLowerInvariant()}".TrimEnd();

    private IEnumerable<OdsOption> Filtered
    {
        get
        {
            var q = _query.Trim();
            return q.Length == 0
                ? Options
                : Options.Where(o => (o.Label ?? string.Empty).Contains(q, StringComparison.CurrentCultureIgnoreCase));
        }
    }

    /// <summary>
    /// The longest option list the trigger sizes itself to. A short, fixed vocabulary (a type, a
    /// status, a direction) gets a trigger exactly as wide as its menu; an open-ended data list (accounts,
    /// tags, merchants) keeps its floor and lets the popover grow past it instead of widening the bar.
    /// </summary>
    public const int FitLimit = 12;

    /// <summary>
    /// Whether the trigger is sized to its widest row. Left <c>null</c>, a short list (1 to
    /// <see cref="FitLimit"/> rows) fits and a longer one does not. Set it for a list whose nature the
    /// count does not tell: <c>true</c> for a fixed vocabulary that runs past the limit (the 13 account
    /// types), <c>false</c> for user data that happens to be short today (tags, accounts, merchants), so
    /// the control's width does not change as the data grows. <c>false</c> is also what turns on the
    /// open-ended mode (a fixed-width trigger whose popover opens at exactly its width); leaving it
    /// <c>null</c> on a long list keeps the popover sized to its content.
    /// </summary>
    [Parameter] public bool? Fit { get; set; }

    private bool Fits => !Quiet && Options.Count > 0 && (Fit ?? Options.Count <= FitLimit);

    // An open-ended list keeps the trigger's own width (the filter bar's floor) and the popover opens at
    // exactly that width, with a long label wrapping inside it rather than truncating — so trigger and menu still
    // share both edges without the trigger growing to the longest account or merchant name.
    // Opt-in: only an explicit Fit="false" asks for it, so a list that merely grows past FitLimit somewhere
    // outside a filter bar (a dialog, a journal page) keeps its content-sized popover rather than being
    // capped at a trigger that was never sized for it. It holds with no options too: accounts, merchants and
    // tags load after first render, and a mode that waited for rows would widen the trigger when they arrive.
    private bool OpenEnded => Fit == false && !Quiet;


    // Mirror the controlled Values into a local set so toggling a row is a single
    // round-trip through ValuesChanged; OnParametersSet re-syncs to the parent.
    private HashSet<string> _selected = [];

    protected override void OnParametersSet() => _selected = [.. Values];

    private Origin Anchor => Align == OdsAlign.End ? Origin.BottomRight : Origin.BottomLeft;
    private Origin Transform => Align == OdsAlign.End ? Origin.TopRight : Origin.TopLeft;
    private string AlignClass => Align == OdsAlign.End ? "end" : "start";

    private static string? IconStyle(OdsOption option) =>
        string.IsNullOrEmpty(option.IconColor) ? null : $"color:{OdsGlyphInk.Glyph(option.IconColor)};";

    private Task Toggle(string value)
    {
        if (!_selected.Remove(value))
            _selected.Add(value);
        return ValuesChanged.InvokeAsync([.. _selected]);
    }

    private Task Clear()
    {
        _selected.Clear();
        return ValuesChanged.InvokeAsync([]);
    }

    private Task Done() => _popup.CloseAsync();

    private void OnOpenChanged(bool open)
    {
        _open = open;
        _wireKeys = open;
        if (!open)
        {
            _query = string.Empty;
        }
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!_wireKeys || !_open)
        {
            return;
        }
        _wireKeys = false;
        try
        {
            await Js.InvokeVoidAsync("odsCheckListKeys", PanelId);
        }
        catch (Exception ex) when (ex is JSException or JSDisconnectedException or InvalidOperationException or TaskCanceledException)
        {
            // Best-effort: no JS during prerender, or the page is tearing down.
        }
    }
}
