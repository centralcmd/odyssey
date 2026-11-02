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

    /// <summary>Puts the control in the error state — MudBlazor owns the resulting
    /// <c>aria-invalid</c> on the input and the error outline.</summary>
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
        OdsAsyncSearchStatus.Done => _async.Results.Count > 0
            ? ResultsText?.Invoke(_async.Results.Count) ?? $"{_async.Results.Count} {(_async.Results.Count == 1 ? "result" : "results")} found"
            : _createRows.Count > 0 || _firstCreateValue is not null ? string.Empty : EmptyText,
        _ => string.Empty,
    };

    /// <summary>What the popover shows when there are no rows to list.</summary>
    private string AsyncPopoverText => _async?.Status switch
    {
        OdsAsyncSearchStatus.Done => EmptyText,
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
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender || string.IsNullOrEmpty(InputId))
            return;

        try
        {
            if (!string.IsNullOrEmpty(AriaDescribedBy))
                await Js.InvokeVoidAsync("odsSetAttr", InputId, "aria-describedby", AriaDescribedBy);
            if (Required)
                await Js.InvokeVoidAsync("odsSetAttr", InputId, "aria-required", "true");
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
    private string WrapClass => SelectedIcon is null ? "odc-combo-wrap" : "odc-combo-wrap has-lead";

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
        var rows = await _async!.RunAsync(query, token);
        if (rows is null)
            return [];

        var matches = rows.ToList();
        var trimmed = query.Trim();
        if (OnCreate is not null && _async.Status == OdsAsyncSearchStatus.Done && trimmed.Length >= MinChars
            && !rows.Any(o => string.Equals(o.Label, trimmed, StringComparison.OrdinalIgnoreCase)))
        {
            var value = $"{CreatePrefix}{KindSeparator}{trimmed}";
            _firstCreateValue = value;
            matches.Add(CreateRow(value, trimmed));
        }

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
        var matches = Options
            .Where(o => string.IsNullOrWhiteSpace(query)
                        || o.Label.Contains(query, StringComparison.OrdinalIgnoreCase))
            .ToList();

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

        return matches;
    }

    private async Task OnSelected(OdsOption? option)
    {
        // ItemDisabledFunc greys the row and blocks the click, but a disabled option can still arrive
        // here — MudAutocomplete coerces a typed value that matches one by label. Ignoring it keeps the
        // selection where it was rather than silently accepting a row the user was told they can't pick.
        if (option is { Disabled: true })
            return;

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
}
