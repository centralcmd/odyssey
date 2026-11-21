using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using MudBlazor;
using Odyssey.Dtos.Journal;

namespace Odyssey.Client.Components;

public partial class OdsCustodianSelect
{
    /// <summary>Selected contact id (string), or empty/null when there is no custodian.
    /// Bindable via @bind-Value.</summary>
    [Parameter] public string? Value { get; set; }

    [Parameter] public EventCallback<string?> ValueChanged { get; set; }

    /// <summary>The contacts to populate the picker from. Archived ones are filtered out of the
    /// selectable options client-side (FE-6); a linked-but-archived value is still kept resolvable.</summary>
    [Parameter] public IReadOnlyList<ExistingContact> Contacts { get; set; } = [];

    [Parameter] public string Label { get; set; } = "Custodian";

    [Parameter] public string Placeholder { get; set; } = "Search contacts…";

    /// <summary>Optional helper text shown under the field when there is no error.</summary>
    [Parameter] public string? Help { get; set; }

    /// <summary>Inline validation error (e.g. a rejected save). Announced via role="alert" and linked
    /// to the input (aria-describedby + aria-invalid).</summary>
    [Parameter] public string? Error { get; set; }

    /// <summary>The contact list is still loading — announces a live loading message and
    /// disables the control.</summary>
    [Parameter] public bool Loading { get; set; }

    [Parameter] public bool Disabled { get; set; }

    /// <summary>
    /// Create a contact from the inline create rows and return the option to select:
    /// <c>(name, kind) =&gt; option</c>, where <c>kind</c> is the ContactType key of the row that was
    /// picked. Supplying it turns the create rows on (gate on <c>contacts.create</c>); omit it for a
    /// pick-only field.
    /// </summary>
    [Parameter] public Func<string, string, OdsOption?>? OnCreate { get; set; }

    /// <summary>Prefix on the create rows.</summary>
    [Parameter] public string CreateLabel { get; set; } = "Add";

    /// <summary>Override the create rows. Defaults to Organization, then Person.</summary>
    [Parameter] public IReadOnlyList<OdsCreateKind>? CreateKinds { get; set; }

    [Parameter] public string? Class { get; set; }

    private readonly string _inputId = $"custodian-input-{Guid.NewGuid():N}";
    private readonly string _statusId = $"custodian-status-{Guid.NewGuid():N}";
    private readonly string _alertId = $"custodian-alert-{Guid.NewGuid():N}";

    private const string EmptyHint = "No contacts yet — create one first to link a custodian.";
    private const string EmptyCreateHint = "No contacts yet — type a name to add one.";

    // Contacts created here that the parent's list hasn't picked up yet, so the trigger can still
    // resolve the value the create row just handed it.
    private readonly List<OdsOption> _created = [];

    private IReadOnlyList<OdsOption> _options = [];
    private bool _isEmpty;

    private bool HasError => !string.IsNullOrWhiteSpace(Error);
    private string ClassSuffix => string.IsNullOrWhiteSpace(Class) ? string.Empty : $" {Class}";

    private string FieldClass => $"odc-field{(HasError ? " error" : string.Empty)}{ClassSuffix}";

    // Both regions are always referenced so the association is stable as their text comes and goes.
    private string _describedBy => $"{_statusId} {_alertId}";

    // An empty list is still operable when a contact can be created from it — disabling the field
    // would make the create row unreachable, which is the dead end inline create exists to close.
    private bool PickerDisabled => Disabled || Loading || (_isEmpty && string.IsNullOrEmpty(Value) && OnCreate is null);

    private IReadOnlyList<OdsCreateKind> EffectiveCreateKinds =>
        CreateKinds is { Count: > 0 } kinds ? kinds : OdsTypeRegistries.ContactCreateKinds;

    private string EffectiveEmptyText => OnCreate is null ? "No contacts match" : "No matches — type to add one";

    private OdsOption? HandleCreate(string text, string? kind)
    {
        if (OnCreate is null)
        {
            return null;
        }

        var created = OnCreate(text, kind ?? EffectiveCreateKinds[0].Key);
        if (created is null)
        {
            return null;
        }

        if (!_created.Any(o => string.Equals(o.Value, created.Value, StringComparison.Ordinal)))
        {
            _created.Add(created);
        }
        return created;
    }

    // Non-error status (loading / empty-list hint / helper text) → the polite live region.
    private string StatusMessage =>
        HasError ? string.Empty
        : Loading ? "Loading contacts…"
        : !string.IsNullOrWhiteSpace(Help) ? Help!
        : _isEmpty ? (OnCreate is null ? EmptyHint : EmptyCreateHint)
        : string.Empty;

    // The validation error → the assertive (role="alert") live region.
    private string AlertMessage => HasError ? Error! : string.Empty;

    protected override void OnParametersSet()
    {
        var active = Contacts.Where(c => c.Archived is null).ToList();

        var options = active
            .OrderBy(c => c.ResolvedDisplayName, StringComparer.CurrentCultureIgnoreCase)
            .Select(OdsContactOptions.From)
            .ToList();

        // Keep a linked-but-now-archived value resolvable so the trigger still shows its name.
        if (!string.IsNullOrEmpty(Value) && options.All(o => o.Value != Value))
        {
            var linked = Contacts.FirstOrDefault(c => c.ContactId.ToString() == Value);
            if (linked is not null)
            {
                options.Insert(0, OdsContactOptions.From(linked));
            }
        }

        if (_created.Count > 0)
        {
            var known = options.Select(o => o.Value).ToHashSet(StringComparer.Ordinal);
            options.InsertRange(0, _created.Where(o => !known.Contains(o.Value)));
        }

        _isEmpty = options.Count == 0;
        _options = options;
    }

    private Task OnValueChanged(string? value) => ValueChanged.InvokeAsync(value);

    // Wire aria-describedby onto the inner combobox input. MudBlazor renders its own
    // aria-describedby after the splatted attributes, so a passed-through value is clobbered; set it
    // directly on the input by id instead. The id pair is constant, so this is idempotent.
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!OperatingSystem.IsBrowser())
        {
            return;
        }

        try
        {
            await JS.InvokeVoidAsync("odsSetAttr", _inputId, "aria-describedby", _describedBy);
        }
        catch (Exception)
        {
            // Best-effort a11y wiring; never break the field if interop is unavailable.
        }
    }
}
