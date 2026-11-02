using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Odyssey.Client.Components;

public partial class OdsCombobox : IDisposable
{
    /// <summary>Selected option value. Bindable via @bind-Value.</summary>
    [Parameter] public string? Value { get; set; }

    [Parameter] public EventCallback<string?> ValueChanged { get; set; }

    /// <summary>The options.</summary>
    [Parameter, EditorRequired] public IReadOnlyList<OdsOption> Options { get; set; } = [];

    [Parameter] public string? Placeholder { get; set; }

    /// <summary>
    /// Enables an inline "Create …" row when the query matches no option. Receives the typed text and
    /// — with <see cref="CreateKinds"/> — the key of the kind whose row was picked (null for the
    /// single unqualified row). Return the new option, or null to skip.
    /// </summary>
    [Parameter] public Func<string, string?, OdsOption?>? OnCreate { get; set; }

    /// <summary>Prefix for the create row label.</summary>
    [Parameter] public string CreateLabel { get; set; } = "Create";

    /// <summary>
    /// Offer <b>one create row per kind</b> — for a record whose type has to be chosen at creation (a
    /// contact is a person or a company). Each row reads <c>Add "‹query›"</c> with the kind as a muted
    /// trailing icon + label, and the picked key reaches <see cref="OnCreate"/>. Omit for a single
    /// unqualified create row.
    /// </summary>
    [Parameter] public IReadOnlyList<OdsCreateKind>? CreateKinds { get; set; }

    /// <summary>Show a trailing clear (✕) affordance that empties the selection. Default true.</summary>
    [Parameter] public bool Clearable { get; set; } = true;

    /// <summary>The clear control's accessible name and tooltip, e.g. <c>Clear place</c>. Default <c>Clear</c>.</summary>
    [Parameter] public string ClearLabel { get; set; } = "Clear";

    /// <summary>Text shown in the popover when the query matches no option. Default "No matches".</summary>
    [Parameter] public string EmptyText { get; set; } = "No matches";

    /// <summary>Show an announced "Loading…" row in place of the empty text while results are being
    /// (re)fetched by the host.</summary>
    [Parameter] public bool Loading { get; set; }

    [Parameter] public bool Disabled { get; set; }

    [Parameter] public string? Class { get; set; }

    /// <summary>Id applied to the inner input element, so a host's <c>&lt;label for&gt;</c> can
    /// associate with it (a real label association, stronger than aria-labelledby).</summary>
    [Parameter] public string? InputId { get; set; }

    /// <summary>Accessible name for a label-less combobox (e.g. a dense grid cell). Rendered as a
    /// visually-hidden <c>&lt;label for=InputId&gt;</c>, so it requires <see cref="InputId"/> to be set.</summary>
    [Parameter] public string? AriaLabel { get; set; }

    /// <summary>Id of a helper/description element to associate with the input via aria-describedby
    /// (e.g. an OdsFieldShell helper's <c>&lt;HtmlFor&gt;-help</c> id). Requires <see cref="InputId"/>.
    /// Applied through JS because MudAutocomplete splats unmatched attributes onto its wrapper, not the
    /// inner input.</summary>
    [Parameter] public string? AriaDescribedBy { get; set; }

    /// <summary>Puts the control in the error state: the error outline (MudBlazor's) and <c>aria-invalid</c>
    /// on the inner input, which <see cref="OnAfterRenderAsync"/> keeps in step on every render.</summary>
    [Parameter] public bool Error { get; set; }

    /// <summary>
    /// Marks the control required, as <c>aria-required</c> on the inner input.
    /// </summary>
    /// <remarks>
    /// Set through JS for the same reason <see cref="AriaDescribedBy"/> is: MudAutocomplete splats
    /// unmatched attributes onto its wrapper, not the <c>role="combobox"</c> input. MudBlazor's own
    /// <c>Required</c> is deliberately not used — it would switch on its built-in validation and its
    /// error styling, which the host's own <see cref="Error"/> already owns.
    /// </remarks>
    [Parameter] public bool Required { get; set; }

    /// <summary>
    /// Suggest, don't constrain: the value is a free string and the options are recognised names, not
    /// the only legal answers. A value matching no option still displays, and the typed query commits
    /// on blur so a typed name is never silently dropped. A typed name matching an option's label
    /// (case-insensitively) commits that option's value. Pair with <see cref="OnCreate"/> when the new
    /// name deserves a visible row of its own.
    /// </summary>
    [Parameter] public bool FreeText { get; set; }

    // ── Async mode (issue #383 §4) ─────────────────────────────────────────────

    /// <summary>
    /// ASYNC MODE: resolves one server page of rows for the trimmed query, after <see cref="MinChars"/>
    /// characters and a <see cref="DebounceMs"/> pause. Each keystroke cancels the search before it.
    /// </summary>
    [Parameter] public Func<string, CancellationToken, Task<IReadOnlyList<OdsOption>>>? SearchAsync { get; set; }

    /// <summary>Renders a value that is in neither <see cref="Options"/> nor the loaded rows.</summary>
    [Parameter] public Func<string, Task<OdsOption?>>? ResolveAsync { get; set; }

    /// <summary>Async: the pause before a request is sent. Default 250 ms.</summary>
    [Parameter] public int DebounceMs { get; set; } = 250;

    /// <summary>Async: characters before a request is sent. Default 2.</summary>
    [Parameter] public int MinChars { get; set; } = 2;

    /// <summary>Async announcement below <see cref="MinChars"/>. Default "Type at least N characters".</summary>
    [Parameter] public string? MinCharsText { get; set; }

    /// <summary>Async announcement while a request is pending.</summary>
    [Parameter] public string SearchingText { get; set; } = "Searching…";

    /// <summary>Async announcement for N rows, e.g. <c>n => $"{n} places found"</c>.</summary>
    [Parameter] public Func<int, string>? ResultsText { get; set; }

    /// <summary>Async announcement when a request fails.</summary>
    [Parameter] public string ErrorText { get; set; } = "Couldn’t load results";

    /// <summary>
    /// A muted second line on the create row that is also part of its accessible name — a disclosure
    /// of who will see what is created (issue #383 §3.2).
    /// </summary>
    [Parameter] public string? CreateDescription { get; set; }

    // ── Groups and the footer row (issue #383 v5) ──────────────────────────────

    /// <summary>
    /// Group the rows (design system · Combobox <c>groups</c>): an option whose <see cref="OdsOption.Group"/>
    /// names one of these is listed under that group's visible heading, in this order, after the ungrouped rows.
    /// A group with no rows and no note is left out. A note (<see cref="OdsOption.IsNote"/>) shows in its group
    /// and is read on the status line.
    /// </summary>
    [Parameter] public IReadOnlyList<OdsOptionGroup>? Groups { get; set; }

    /// <summary>One final row, after a rule, present whenever the list is open (Combobox <c>footerOption</c>).</summary>
    [Parameter] public OdsComboboxFooter? FooterOption { get; set; }

    [Parameter(CaptureUnmatchedValues = true)]
    public Dictionary<string, object>? UserAttributes { get; set; }

    private bool IsAsync => SearchAsync is not null;

    private OdsAsyncSearch? _async;

    // Options this component has seen that are not in Options: a row picked from a server page, a value
    // resolved through ResolveAsync, a staged create. Keyed by value, so the trigger keeps its label.
    private readonly Dictionary<string, OdsOption> _known = new(StringComparer.Ordinal);

    private string? _resolving;

    protected override async Task OnParametersSetAsync()
    {
        if (IsAsync && _async is null)
        {
            _async = new OdsAsyncSearch((q, ct) => SearchAsync!(q, ct)) { DebounceMs = DebounceMs, MinChars = MinChars };
            _async.Changed += OnAsyncChanged;
        }

        if (ResolveAsync is not null && !string.IsNullOrEmpty(Value) && Selected is null && _resolving != Value)
        {
            var value = Value;
            _resolving = value;
            var option = await ResolveAsync(value);
            if (option is not null)
                _known[value] = option;
        }
    }

    private void OnAsyncChanged() => _ = InvokeAsync(StateHasChanged);

    /// <summary>The one polite status line of async mode — empty while the field has nothing to say.</summary>
    private string AsyncStatusText => _async?.Status switch
    {
        OdsAsyncSearchStatus.Short => MinCharsText ?? $"Type at least {MinChars} characters",
        OdsAsyncSearchStatus.Searching => SearchingText,
        OdsAsyncSearchStatus.Failed => ErrorText,
        OdsAsyncSearchStatus.Done => JoinStatus(
            _resultCount > 0
                ? ResultsText?.Invoke(_resultCount) ?? $"{_resultCount} {(_resultCount == 1 ? "result" : "results")} found"
                : _createRows.Count > 0 || _firstCreateValue is not null || _notes.Count > 0 ? string.Empty : EmptyText,
            string.Join(" ", _notes)),
        _ => string.Empty,
    };

    private static string JoinStatus(string first, string notes) =>
        string.Join(". ", new[] { first, notes }.Where(v => !string.IsNullOrEmpty(v)));

    /// <summary>What the popover shows when there are no rows to list.</summary>
    private string AsyncPopoverText => _async?.Status switch
    {
        OdsAsyncSearchStatus.Done => _notes.Count > 0 ? string.Empty : EmptyText,
        null or OdsAsyncSearchStatus.Idle => MinCharsText ?? $"Type at least {MinChars} characters",
        _ => AsyncStatusText,
    };

    public void Dispose()
    {
        if (_async is null)
            return;
        _async.Changed -= OnAsyncChanged;
        _async.Dispose();
    }

    // Sentinel marking a synthetic "Create …" row. The typed rows share the prefix and differ by the
    // kind key, so each is its own distinct option value (MudAutocomplete keys rows by value).
    private const string CreatePrefix = " create ";
    private const char KindSeparator = '\u001f';

    // The kind behind each create row this search produced, keyed by its sentinel value — the option
    // record itself stays the shared OdsOption, which carries no create-kind concept.
    private readonly Dictionary<string, OdsCreateKind> _createRows = new(StringComparer.Ordinal);

    // Wire the input's aria-describedby / aria-required once (MudAutocomplete won't put either on the
    // inner input itself). Idempotent; JS-unavailable environments no-op.
    // What the inner input last received through JS. Null/false is "nothing applied", which is also the
    // input's own initial state, so the first render sends only what is actually set.
    private string? _appliedDescribedBy;
    private bool _appliedRequired;
    private bool _appliedInvalid;

    /// <summary>
    /// Keeps the inner input's <c>aria-describedby</c>, <c>aria-required</c> and <c>aria-invalid</c> in step
    /// with the parameters on EVERY render, not just the first: an error that appears after the field is shown
    /// (a 422 keyed to it, a merge target left empty) changes <see cref="AriaDescribedBy"/> and
    /// <see cref="Error"/> later, and must still be linked to the input (frontend review N1). Only a changed
    /// value is sent, so a re-render costs no interop.
    /// </summary>
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (string.IsNullOrEmpty(InputId))
            return;

        var describedBy = string.IsNullOrEmpty(AriaDescribedBy) ? null : AriaDescribedBy;
        try
        {
            if (!string.Equals(describedBy, _appliedDescribedBy, StringComparison.Ordinal))
            {
                await Js.InvokeVoidAsync("odsSetAttr", InputId, "aria-describedby", describedBy);
                _appliedDescribedBy = describedBy;
            }

            if (Required != _appliedRequired)
            {
                await Js.InvokeVoidAsync("odsSetAttr", InputId, "aria-required", Required ? "true" : null);
                _appliedRequired = Required;
            }

            if (Error != _appliedInvalid)
            {
                await Js.InvokeVoidAsync("odsSetAttr", InputId, "aria-invalid", Error ? "true" : "false");
                _appliedInvalid = Error;
            }
        }
        catch { /* JS unavailable (e.g. prerender / teardown) */ }
    }

    // In FreeText mode a value that matches no option is still the value — it is a name the user
    // typed, not an invalid selection.
    private OdsOption? Selected =>
        Options.FirstOrDefault(o => o.Value == Value)
        ?? (Value is not null ? _known.GetValueOrDefault(Value) : null)
        ?? (FreeText && !string.IsNullOrEmpty(Value) ? new OdsOption(Value, Value) : null);

    // The text as last typed into the input — what a FreeText blur commits. Null once a row is picked
    // or the typed text has been committed, so a stale keystroke is never committed twice.
    private string? _typed;

    private void OnTyped(ChangeEventArgs e)
    {
        if (FreeText)
            _typed = e.Value?.ToString();
    }

    // FreeText: the query IS an answer. Committing it on blur is what separates "suggests" from
    // "constrains". An empty query commits nothing — clearing is the ✕ affordance's job.
    private Task CommitTyped(Microsoft.AspNetCore.Components.Web.FocusEventArgs _)
    {
        if (!FreeText)
            return Task.CompletedTask;
        var text = _typed?.Trim();
        // Typing the current value's own label again is not a change, and a "Create …" caption is never
        // a name.
        if (string.IsNullOrEmpty(text)
            || string.Equals(text, Selected?.Label, StringComparison.OrdinalIgnoreCase)
            || text.StartsWith($"{CreateLabel} \"", StringComparison.Ordinal))
            return Task.CompletedTask;
        var hit = Options.FirstOrDefault(o => !o.Disabled
                                              && string.Equals(o.Label, text, StringComparison.OrdinalIgnoreCase));
        var next = hit?.Value ?? text;
        if (string.Equals(next, Value, StringComparison.Ordinal))
            return Task.CompletedTask;
        _typed = null;
        Value = next;
        return ValueChanged.InvokeAsync(Value);
    }

    // Leading glyph on the trigger once a real value is selected (mirrors the DS .odc-input-icon).
    private string? SelectedIcon => Selected?.Icon;
    private string? SelectedIconStyle => OptionIconStyle(Selected);
    private string WrapClass => string.Join(' ', new[]
    {
        "odc-combo-wrap",
        SelectedIcon is null ? null : "has-lead",
        ShowClear ? "has-clear" : null,
    }.Where(c => c is not null));

    /// <summary>The clear control shows while there is a value to clear and the field can be changed.</summary>
    private bool ShowClear => Clearable && !Disabled && Selected is not null;

    /// <summary>
    /// Clears the value the same way picking nothing does, then puts focus back in the input — the button
    /// itself disappears with the value, and focus must not fall to the page.
    /// </summary>
    private async Task ClearAsync()
    {
        await OnSelected(null);
        if (string.IsNullOrEmpty(InputId))
            return;
        try { await Js.InvokeVoidAsync("odsFocusById", InputId); }
        catch { /* JS unavailable (e.g. teardown) */ }
    }

    private static string? OptionIconStyle(OdsOption? option) =>
        string.IsNullOrEmpty(option?.IconColor) ? null : $"color:{OdsGlyphInk.Glyph(option.IconColor)};";

    private bool IsCreateRow(OdsOption option) =>
        option.Value.StartsWith(CreatePrefix, StringComparison.Ordinal);

    private OdsCreateKind? KindOf(OdsOption option) =>
        _createRows.GetValueOrDefault(option.Value);

    // The rule + gap belong to the FIRST create row only, so a stack of typed rows reads as one group
    // under the results rather than as three separately-ruled sections.
    private string ItemClass(OdsOption option)
    {
        if (!IsCreateRow(option))
            return "odc-combo-item";
        var first = _firstCreateValue is not null && string.Equals(_firstCreateValue, option.Value, StringComparison.Ordinal);
        return first ? "odc-combo-item odc-combo-create first" : "odc-combo-item odc-combo-create";
    }

    private string? _firstCreateValue;

    // ── Grouped rows, notes and the footer (issue #383 v5) ─────────────────────

    // Sentinels for the synthetic rows: a group's heading and the footer. Like the create prefix they start with a
    // character no option value can sensibly carry, and MudAutocomplete keys rows by value.
    private const string HeadingPrefix = "\u001fgroup\u001f";
    private const string FooterValue = "\u001ffooter";

    // The selectable rows of the latest search (no heading, note, create or footer row), the notes it carried, and
    // which row opens each group — that row's accessible name starts with the group's name.
    private int _resultCount;
    private readonly List<string> _notes = [];
    private readonly Dictionary<string, string> _groupOpeners = new(StringComparer.Ordinal);

    // The text the last search ran for — what the footer row hands to its callback.
    private string _lastQuery = string.Empty;

    // Set while a footer pick restores the field's own value, so the restore is not taken for a pick.
    private bool _restoring;

    private MudBlazor.MudAutocomplete<OdsOption>? _auto;

    private bool HasRowExtras => Groups is { Count: > 0 } || FooterOption is not null;

    /// <summary>Whether the rows are capped at MudAutocomplete's default ten — never with groups or a footer, which
    /// would otherwise cut off the last group and the footer row itself.</summary>
    private int? MaxItems => HasRowExtras ? null : 10;

    internal static bool IsHeadingRow(OdsOption option) => option.Value.StartsWith(HeadingPrefix, StringComparison.Ordinal);

    internal static bool IsFooterRow(OdsOption option) => string.Equals(option.Value, FooterValue, StringComparison.Ordinal);

    /// <summary>A row the user can never pick: a disabled one, a group heading or a group note.</summary>
    private static bool Unpickable(OdsOption? option) =>
        option is not null && (option.Disabled || option.IsNote || IsHeadingRow(option));

    /// <summary>The group a row opens, when it is the first row listed under a heading.</summary>
    private string? GroupOpenedBy(OdsOption option) => _groupOpeners.GetValueOrDefault(option.Value);

    /// <summary>Whether the popover lists no row the user came for — only a heading, a note or the footer.</summary>
    private bool NoResultRows => _resultCount == 0 && _createRows.Count == 0 && _firstCreateValue is null;

    /// <summary>
    /// Orders the rows the way the design does — ungrouped first, then each group under its heading (its rows, then
    /// its note), in <see cref="Groups"/> order — and records the count, the notes and each group's first row.
    /// The keyboard order is the visual order, because both are this list.
    /// </summary>
    private List<OdsOption> Arrange(IEnumerable<OdsOption> rows)
    {
        var list = rows.ToList();
        _notes.Clear();
        _groupOpeners.Clear();
        _resultCount = list.Count(o => !o.IsNote);
        if (Groups is not { Count: > 0 } groups)
        {
            _notes.AddRange(list.Where(o => o.IsNote).Select(o => o.Label));
            return list;
        }

        var known = groups.Select(g => g.Key).ToHashSet(StringComparer.Ordinal);
        var arranged = list.Where(o => o.Group is null || !known.Contains(o.Group)).ToList();
        foreach (var group in groups)
        {
            var members = list.Where(o => string.Equals(o.Group, group.Key, StringComparison.Ordinal)).ToList();
            if (members.Count == 0)
                continue;
            arranged.Add(new OdsOption($"{HeadingPrefix}{group.Key}", group.Label));
            var ordered = members.Where(o => !o.IsNote).Concat(members.Where(o => o.IsNote)).ToList();
            _groupOpeners[ordered[0].Value] = group.Label;
            _notes.AddRange(members.Where(o => o.IsNote).Select(o => o.Label));
            arranged.AddRange(ordered);
        }

        return arranged;
    }

    /// <summary>The footer row, last, whenever the list is open.</summary>
    private void AppendFooter(List<OdsOption> rows)
    {
        if (FooterOption is { } footer)
            rows.Add(new OdsOption(FooterValue, footer.Label) { Icon = footer.Icon, AriaLabel = footer.AriaLabel });
    }

    private Task<IEnumerable<OdsOption>> Search(string? text, CancellationToken token) =>
        IsAsync ? SearchRemoteAsync(text, token) : Task.FromResult(SearchLocal(text));

    // Async mode: the server's rows ARE the options (no client filtering), then the create row.
    private async Task<IEnumerable<OdsOption>> SearchRemoteAsync(string? text, CancellationToken token)
    {
        // Opening on a chosen value searches its label; that is not a query the user typed.
        var query = text ?? string.Empty;
        if (Selected is { } current && string.Equals(query, current.Label, StringComparison.Ordinal))
            query = string.Empty;

        _createRows.Clear();
        _firstCreateValue = null;
        _lastQuery = query.Trim();
        var rows = await _async!.RunAsync(query, token);
        if (rows is null)
        {
            var superseded = new List<OdsOption>();
            AppendFooter(superseded);
            return superseded;
        }

        var matches = Arrange(rows);
        var trimmed = query.Trim();
        if (OnCreate is not null && _async.Status == OdsAsyncSearchStatus.Done && trimmed.Length >= MinChars
            && !rows.Any(o => string.Equals(o.Label, trimmed, StringComparison.OrdinalIgnoreCase)))
        {
            var value = $"{CreatePrefix}{KindSeparator}{trimmed}";
            _firstCreateValue = value;
            matches.Add(CreateRow(value, trimmed));
        }

        AppendFooter(matches);
        await InvokeAsync(StateHasChanged);
        return matches;
    }

    /// <summary>The create row: the label, the disclosure as its second line, and both as its name.</summary>
    private OdsOption CreateRow(string value, string query) => CreateRow(CreateLabel, CreateDescription, value, query);

    internal static OdsOption CreateRow(string createLabel, string? description, string value, string query)
    {
        var label = $"{createLabel} \"{query}\"";
        return new OdsOption(value, label)
        {
            Icon = "add",
            IsCreate = true,
            Sub = description,
            AriaLabel = string.IsNullOrEmpty(description)
                ? null
                : $"{label} — {char.ToLowerInvariant(description[0])}{description[1..]}",
        };
    }

    private IEnumerable<OdsOption> SearchLocal(string? text)
    {
        var query = text ?? string.Empty;
        _lastQuery = query.Trim();
        var matches = Arrange(Options
            .Where(o => o.IsNote || string.IsNullOrWhiteSpace(query)
                        || o.Label.Contains(query, StringComparison.OrdinalIgnoreCase)));

        _createRows.Clear();
        _firstCreateValue = null;

        if (OnCreate is not null
            && !string.IsNullOrWhiteSpace(query)
            && !Options.Any(o => string.Equals(o.Label, query, StringComparison.OrdinalIgnoreCase)))
        {
            var label = $"{CreateLabel} \"{query}\"";
            if (CreateKinds is { Count: > 0 } kinds)
            {
                foreach (var kind in kinds)
                {
                    var value = $"{CreatePrefix}{kind.Key}{KindSeparator}{query}";
                    _createRows[value] = kind;
                    _firstCreateValue ??= value;
                    matches.Add(new OdsOption(value, label) { Icon = "add", IsCreate = true });
                }
            }
            else
            {
                var value = $"{CreatePrefix}{KindSeparator}{query}";
                _firstCreateValue = value;
                matches.Add(CreateRow(value, query));
            }
        }

        AppendFooter(matches);
        return matches;
    }

    private async Task OnSelected(OdsOption? option)
    {
        // ItemDisabledFunc greys the row and blocks the click, but a disabled option can still arrive
        // here — MudAutocomplete coerces a typed value that matches one by label. Ignoring it keeps the
        // selection where it was rather than silently accepting a row the user was told they can't pick.
        if (_restoring || Unpickable(option))
            return;

        // The footer row is an action, not a value: it hands over the typed text and leaves the value alone, so the
        // field is put back to what it held.
        if (option is not null && IsFooterRow(option))
        {
            _typed = null;
            var text = _lastQuery;
            await RestoreAsync();
            if (FooterOption is { } footer)
                await footer.OnSelect(text);
            return;
        }

        // A pick supersedes whatever was typed to find it.
        _typed = null;

        if (option is not null && IsCreateRow(option))
        {
            var payload = option.Value[CreatePrefix.Length..];
            var split = payload.IndexOf(KindSeparator);
            var kind = split <= 0 ? null : payload[..split];
            var text = payload[(split + 1)..];
            var created = OnCreate?.Invoke(text, kind);
            if (created is not null)
                _known[created.Value] = created;
            Value = created?.Value;
        }
        else
        {
            // A row from a server page is not in Options; keep it so the trigger can still name it.
            if (option is not null && IsAsync)
                _known[option.Value] = option;
            Value = option?.Value;
        }
        await ValueChanged.InvokeAsync(Value);
    }

    /// <summary>Puts MudAutocomplete back on the field's own value after a footer pick selected the footer row.</summary>
    private async Task RestoreAsync()
    {
        if (_auto is null)
            return;
        _restoring = true;
        try { await _auto.SelectOptionAsync(Selected!); }
        catch (Exception) { /* best-effort: teardown, or no JS runtime under bUnit */ }
        finally { _restoring = false; }
    }
}
