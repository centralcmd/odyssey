using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Odyssey.Client.Components;

public partial class OdsCombobox
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

    [Parameter(CaptureUnmatchedValues = true)]
    public Dictionary<string, object>? UserAttributes { get; set; }

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

    private OdsOption? Selected => Options.FirstOrDefault(o => o.Value == Value);

    // Leading glyph on the trigger once a real value is selected (mirrors the DS .odc-input-icon).
    private string? SelectedIcon => Selected?.Icon;
    private string? SelectedIconStyle => OptionIconStyle(Selected);
    private string WrapClass => SelectedIcon is null ? "odc-combo-wrap" : "odc-combo-wrap has-lead";

    private static string? OptionIconStyle(OdsOption? option) =>
        string.IsNullOrEmpty(option?.IconColor) ? null : $"color:{option.IconColor};";

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

    private Task<IEnumerable<OdsOption>> Search(string? text, CancellationToken token)
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
                    matches.Add(new OdsOption(value, label) { Icon = "add" });
                }
            }
            else
            {
                var value = $"{CreatePrefix}{KindSeparator}{query}";
                _firstCreateValue = value;
                matches.Add(new OdsOption(value, label) { Icon = "add" });
            }
        }

        return Task.FromResult<IEnumerable<OdsOption>>(matches);
    }

    private async Task OnSelected(OdsOption? option)
    {
        if (option is not null && IsCreateRow(option))
        {
            var payload = option.Value[CreatePrefix.Length..];
            var split = payload.IndexOf(KindSeparator);
            var kind = split <= 0 ? null : payload[..split];
            var text = payload[(split + 1)..];
            var created = OnCreate?.Invoke(text, kind);
            Value = created?.Value;
        }
        else
        {
            Value = option?.Value;
        }
        await ValueChanged.InvokeAsync(Value);
    }
}
