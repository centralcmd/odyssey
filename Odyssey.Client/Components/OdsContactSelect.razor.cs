using Microsoft.AspNetCore.Components;
using Odyssey.Dtos.Journal;

namespace Odyssey.Client.Components;

public partial class OdsContactSelect
{
    /// <summary>Selected contact id, or null/empty for none. Bindable via @bind-Value.</summary>
    [Parameter] public string? Value { get; set; }

    [Parameter] public EventCallback<string?> ValueChanged { get; set; }

    /// <summary>
    /// The candidate contacts. Archived ones are filtered out of the selectable options (an archived
    /// contact must not be linkable); a value already pointing at one stays resolvable so the trigger
    /// keeps showing its name.
    /// </summary>
    [Parameter] public IReadOnlyList<ExistingContact> Contacts { get; set; } = [];

    /// <summary>
    /// Pre-built display options — for a caller that already holds them and has its own notion of
    /// which contacts are still linkable (the policy/contract party dialogs exclude the ones already
    /// linked in the role). Wins over <see cref="Contacts"/>; the caller owns the archived filter.
    /// </summary>
    [Parameter] public IReadOnlyList<OdsOption>? Options { get; set; }

    [Parameter] public string Label { get; set; } = "Contact";

    [Parameter] public bool Optional { get; set; }

    [Parameter] public bool Required { get; set; }

    [Parameter] public string Placeholder { get; set; } = "Search contacts…";

    /// <summary>Text shown when the query matches no contact. Defaults to wording that reflects
    /// whether inline create is on.</summary>
    [Parameter] public string? EmptyText { get; set; }

    /// <summary>Helper text below the field when there is no error.</summary>
    [Parameter] public string? Help { get; set; }

    /// <summary>Inline validation error — announced via <c>role="alert"</c>, linked to the input and
    /// flipping <c>aria-invalid</c>.</summary>
    [Parameter] public string? Error { get; set; }

    /// <summary>The contact list is still loading — an announced loading row, deliberately distinct
    /// from "no matches", which would read as a genuinely empty address book.</summary>
    [Parameter] public bool Loading { get; set; }

    [Parameter] public bool Disabled { get; set; }

    /// <summary>Drop the label / help chrome — for a table cell or a caller's own OdsFieldShell. The
    /// control then names itself with <see cref="AriaLabel"/>, defaulting to <see cref="Label"/>.</summary>
    [Parameter] public bool Bare { get; set; }

    /// <summary>Offer the inline create rows. Gate on the caller's <c>contacts.create</c> claim — a
    /// reviewer who would meet a 403 must never be shown the control.</summary>
    [Parameter] public bool AllowCreate { get; set; }

    /// <summary>
    /// Create the contact and return the option to select: <c>(name, kind) =&gt; option</c>, where
    /// <c>kind</c> is the ContactType key of the create row that was picked. Returning null skips.
    /// </summary>
    [Parameter] public Func<string, string, OdsOption?>? OnCreate { get; set; }

    /// <summary>Prefix on the create rows.</summary>
    [Parameter] public string CreateLabel { get; set; } = "Add";

    /// <summary>Override the create rows. Defaults to Organization, then Person.</summary>
    [Parameter] public IReadOnlyList<OdsCreateKind>? CreateKinds { get; set; }

    /// <summary>Accessible name for a <see cref="Bare"/> control; ignored when the visible label is
    /// rendered (a second name would double it).</summary>
    [Parameter] public string? AriaLabel { get; set; }

    [Parameter] public string? Class { get; set; }

    /// <summary>Id applied to the inner input, so a caller's own <c>&lt;label for&gt;</c> or focus
    /// restoration can address it. Auto-generated otherwise.</summary>
    [Parameter] public string? Id { get; set; }

    private readonly string _autoId = $"contact-select-{Guid.NewGuid():N}";

    private string _inputId = string.Empty;
    private string _statusId = string.Empty;
    private string _alertId = string.Empty;

    // Contacts created from the create rows are kept here as well as handed to the caller: a surface
    // that passes pre-built Options — or whose list is rebuilt on its own schedule — would not carry
    // the new contact yet, and the field would clear itself the moment it was created.
    private readonly List<OdsOption> _created = [];

    private IReadOnlyList<OdsOption> _options = [];

    private bool _isEmpty;

    private const string EmptyHint = "No contacts yet.";
    private const string EmptyCreateHint = "No contacts yet — type a name to add one.";

    private bool HasError => !string.IsNullOrWhiteSpace(Error);

    private bool CanCreate => AllowCreate && OnCreate is not null;

    private IReadOnlyList<OdsCreateKind> EffectiveCreateKinds =>
        CreateKinds is { Count: > 0 } kinds ? kinds : OdsTypeRegistries.ContactCreateKinds;

    private string EffectiveEmptyText => EmptyText
        ?? (CanCreate ? "No matches — type to add one" : "No contacts match");

    private string? BareAriaLabel => Bare ? (AriaLabel ?? Label) : null;

    private string FieldClass =>
        $"odc-field{(HasError ? " error" : string.Empty)}{(string.IsNullOrWhiteSpace(Class) ? string.Empty : " " + Class)}";

    // Both regions are always referenced so the association is stable as their text comes and goes.
    private string _describedBy => $"{_statusId} {_alertId}";

    // An empty list is still operable when a contact can be created from it — disabling the field
    // would make the create row unreachable, which is the dead end this picker exists to close.
    private bool PickerDisabled => Disabled || Loading || (_isEmpty && string.IsNullOrEmpty(Value) && !CanCreate);

    private string StatusMessage =>
        HasError ? string.Empty
        : Loading ? "Loading contacts…"
        : !string.IsNullOrWhiteSpace(Help) ? Help!
        : _isEmpty ? (CanCreate ? EmptyCreateHint : EmptyHint)
        : string.Empty;

    private string AlertMessage => HasError ? Error! : string.Empty;

    protected override void OnParametersSet()
    {
        _inputId = Id ?? _autoId;
        _statusId = $"{_inputId}-status";
        _alertId = $"{_inputId}-alert";

        var options = Options is not null ? [.. Options] : BuildFromContacts();

        // Prepend anything created here that the caller's list hasn't picked up yet, so the trigger
        // can still resolve the value it was just handed.
        if (_created.Count > 0)
        {
            var known = options.Select(o => o.Value).ToHashSet(StringComparer.Ordinal);
            options.InsertRange(0, _created.Where(o => !known.Contains(o.Value)));
        }

        _isEmpty = options.Count == 0;
        _options = options;
    }

    private List<OdsOption> BuildFromContacts()
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
                options.Insert(0, OdsContactOptions.From(linked));
        }

        return options;
    }

    // The picked kind, or the leading create row's when a caller supplied none — never a guess made
    // after the fact, which is the whole reason the rows are typed.
    private OdsOption? HandleCreate(string text, string? kind)
    {
        if (OnCreate is null)
            return null;

        var created = OnCreate(text, kind ?? EffectiveCreateKinds[0].Key);
        if (created is null)
            return null;

        if (!_created.Any(o => string.Equals(o.Value, created.Value, StringComparison.Ordinal)))
            _created.Add(created);
        return created;
    }

    private Task OnValueChanged(string? value) => ValueChanged.InvokeAsync(value);
}
