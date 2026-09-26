using System.Globalization;
using Microsoft.AspNetCore.Components;
using Odyssey.ApiClient;
using Odyssey.Client.Authorization;
using Odyssey.Client.Components;
using Odyssey.Client.Services;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos.Finance;

namespace Odyssey.Client.Pages.Finance;

public partial class AddContractPartyDialog
{
    [Parameter] public bool Open { get; set; }
    [Parameter] public EventCallback<bool> OpenChanged { get; set; }

    [Parameter, EditorRequired] public ExistingContract Contract { get; set; } = default!;

    /// <summary>The party being edited, or null for the one-off add (issue #121's new PUT).</summary>
    [Parameter] public ExistingContractParty? Party { get; set; }

    /// <summary>Pre-loaded, active account options.</summary>
    [Parameter] public IReadOnlyList<OdsOption> Accounts { get; set; } = [];

    /// <summary>Pre-loaded, active contact options (the Institution party kind).</summary>
    [Parameter] public IReadOnlyList<OdsOption> Institutions { get; set; } = [];

    /// <summary>
    /// Pre-loaded property options (issue #208) — archived and disposed ones included, since a
    /// contract's history must stay recordable.
    /// </summary>
    [Parameter] public IReadOnlyList<OdsOption> Properties { get; set; } = [];

    /// <summary>Option value → "archived"/"disposed" for the properties that are not simply owned.</summary>
    [Parameter] public IReadOnlyDictionary<string, string> PropertyStates { get; set; } = new Dictionary<string, string>();

    /// <summary>Raised after a successful write so the host re-fetches the contract.</summary>
    [Parameter] public EventCallback OnSaved { get; set; }

    /// <summary>
    /// Raised with the line to read out when a change to Role or kind discards an already-chosen
    /// record. Clearing it silently leaves a screen-reader user with no cue that a completed field
    /// went blank (WCAG 3.2.2), and the live region belongs to the host page.
    /// </summary>
    [Parameter] public EventCallback<string> OnAnnounce { get; set; }

    private ContractPartyKind _kind = ContractPartyKind.Account;

    // No default (issue #157 §8.1). Unspecified was retired, so there is no member meaning "nobody
    // has said" to start on — the control starts empty and Save stays disabled until a role is
    // picked, which is the client half of the server's [Required].
    private ContractPartyRole? _role;
    private string? _value;
    private string? _error;
    private string? _roleError;
    private DateTime? _fromDate;
    private DateTime? _toDate;
    private readonly Dictionary<string, string> _errors = [];
    private bool _isSaving;

    private bool IsEdit => Party is not null;

    /// <param name="Article">"an account", "a contact", "a property" — for the nothing-selected error.</param>
    /// <param name="Plural">The picker's plural noun: "accounts", "contacts", "properties".</param>
    /// <param name="Field">The request field the server keys this kind's inline errors on.</param>
    private sealed record KindDef(ContractPartyKind Kind, string Label, string Icon, string Article, string Plural, string Field);

    private static readonly IReadOnlyList<KindDef> Kinds =
    [
        new(ContractPartyKind.Account, "Account", "account_balance_wallet", "an account", "accounts",
            nameof(ContractPartyRequest.AccountId)),
        // The enum member keeps its name — it is a serialized wire value. Only the label and
        // glyph the reader sees were renamed.
        new(ContractPartyKind.Institution, "Contact", "groups", "a contact", "contacts",
            nameof(ContractPartyRequest.ContactId)),
        // Issue #208. The kind implies no role and no role implies the kind.
        new(ContractPartyKind.Property, "Property", "home_work", "a property", "properties",
            nameof(ContractPartyRequest.PropertyId)),
    ];

    private static readonly IReadOnlyList<OdsCardSelectOption> KindOptions =
        [.. Kinds.Select(k => new OdsCardSelectOption { Value = k.Kind.ToString(), Label = k.Label, Icon = k.Icon })];

    private KindDef Current => Kinds.First(k => k.Kind == _kind);

    /// <summary>The Contact kind links a CONTACT — the one that gets the canonical picker.</summary>
    private bool IsContactKind => _kind == ContractPartyKind.Institution;

    private bool _canCreateContact;

    // Contacts created inline, ahead of the host's option-cache refresh, so the picker can still
    // resolve the value it was just handed.
    private readonly List<OdsOption> _createdContacts = [];

    protected override async Task OnInitializedAsync()
    {
        ContactCreator.OnCreateFailed = OnContactCreateFailed;
        if (OperatingSystem.IsBrowser())
        {
            var user = await AuthenticationStateProvider.GetUserAsync();
            _canCreateContact = user.HasPermission(PermissionClaims.ContactsCreate);
        }

        if (Party is { } party)
        {
            _kind = party.Kind;
            _role = party.Role;
            _value = (party.Account?.AccountId ?? party.Institution?.ContactId ?? party.Property?.PropertyId)?.ToString();
            _fromDate = party.FromDate?.Date;
            _toDate = party.ToDate?.Date;
        }
    }

    /// <summary>
    /// The contract's own start, when it has one. A party cannot be in the role before the contract
    /// began — the one tie between the party's term and the contract's, and only a LOWER bound: an
    /// open-started term contract and a one-off (completion date only) take none, exactly as the
    /// server does (issue #121 §8 rule 3).
    /// </summary>
    private DateTime? ContractStarted => Contract.StartDate?.Date;

    private string FromHelp => ContractStarted is { } started
        ? $"Leave empty to start with the contract ({started.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)})."
        : "Leave empty to follow the contract's own dates.";

    /// <summary>
    /// What the chosen role means, so the field explains itself — and, before one is chosen, that the
    /// list is the ones this contract's type accepts rather than an arbitrary subset.
    /// </summary>
    private string RoleHelp => _role switch
    {
        null => $"The roles a {TypeLabel} contract can have, the usual ones first.",
        ContractPartyRole.Other => "A deliberate role that is none of the listed ones.",
        { } role => $"What this record does in the agreement: {PartyRoleLabel.For(role).ToLowerInvariant()}.",
    };

    private string TypeLabel => OdsTypeRegistries.ContractTypeOf(Contract.Type).Label.ToLowerInvariant();

    /// <summary>
    /// A role the contract's type does not accept, held by the party being edited. Only reachable on
    /// an EDIT — of a party written before the matrix, or before the contract's type changed — and it
    /// is exactly the party this dialog exists to correct, so the role is shown rather than silently
    /// dropped. The picker still offers only legal roles, so choosing anything resolves it.
    /// </summary>
    private bool RoleRejectedByType =>
        _role is { } role && !ContractPartyRoleMatrix.IsLegal(Contract.Type, role);

    /// <summary>
    /// Save is held until a role is picked (the server would answer <c>400</c>) and while the held
    /// role is one this contract's type rejects (the server would answer <c>422</c>) — the dialog
    /// refuses where the decision is made rather than after a round trip.
    /// </summary>
    private bool CanSave => _role is not null && !RoleRejectedByType;

    private IReadOnlyList<OdsOption> AllForKind => _kind switch
    {
        ContractPartyKind.Account => Accounts,
        ContractPartyKind.Property => Properties,
        _ => _createdContacts.Count == 0
            ? Institutions
            : [.. _createdContacts.Where(c => Institutions.All(o => o.Value != c.Value)), .. Institutions],
    };

    /// <summary>
    /// Ids already linked to this contract <b>for the chosen kind AND in the chosen role</b>. Both
    /// halves matter: uniqueness is <i>(contract, target, role)</i>, so a record linked as Seller is
    /// still offerable as Service provider — and a contract role, unlike an insurance one, implies
    /// nothing about which kind of record holds it. The party BEING EDITED is not "already linked" as
    /// far as its own picker is concerned; only its siblings are.
    /// </summary>
    private HashSet<string> LinkedForKindAndRole
    {
        get
        {
            var set = Contract.Parties
                .Where(p => p.Role == _role && p.ContractPartyId != Party?.ContractPartyId)
                .Select(p => _kind switch
                {
                    ContractPartyKind.Account => p.Account?.AccountId.ToString(),
                    ContractPartyKind.Property => p.Property?.PropertyId.ToString(),
                    _ => p.Institution?.ContactId.ToString(),
                })
                .Where(id => id is not null)
                .Select(id => id!)
                .ToHashSet(StringComparer.Ordinal);

            return set;
        }
    }

    private IReadOnlyList<OdsOption> Available => [.. AllForKind.Where(o => !LinkedForKindAndRole.Contains(o.Value))];

    /// <summary>The picker's helper line: how many records are still linkable IN THIS ROLE, so
    /// "nothing to choose" reads as a stated fact rather than as an empty list.</summary>
    private string PickerHelp
    {
        get
        {
            // A picked archived or disposed property is still linkable — said here, where the choice
            // is made, so the reader does not take the label's qualifier for a refusal (issue #208).
            if (_kind == ContractPartyKind.Property && _value is not null
                && PropertyStates.TryGetValue(_value, out var state))
            {
                return $"This property is {state} — it can still be linked, so the contract’s history stays complete.";
            }

            var noun = Current.Label.ToLowerInvariant();
            if (Available.Count > 0)
            {
                return $"{Available.Count} {(Available.Count == 1 ? noun : Current.Plural)} available in this role.";
            }

            return $"Every {noun} already holds {RoleNoun} on this contract"
                + (IsContactKind && _canCreateContact ? " — or add a new one below." : ".");
        }
    }

    private string RoleNoun => _role is { } role ? PartyRoleLabel.For(role).ToLowerInvariant() : "this role";

    // The picker hands its option back synchronously; the shared creator POSTs behind it and the temp
    // id is mapped at save. A failed create drops the option and the selection with it.
    private OdsOption? CreateContactOption(string text, string kind)
    {
        var option = ContactCreator.Begin(text, kind);
        if (option is not null)
            _createdContacts.Add(option);
        return option;
    }

    private void OnContactCreateFailed(string tempId)
    {
        _createdContacts.RemoveAll(o => o.Value == tempId);
        if (_value == tempId)
            _value = null;
        StateHasChanged();
    }

    private Task PickKind(string kind)
    {
        var next = Enum.Parse<ContractPartyKind>(kind);
        if (_kind == next) return Task.CompletedTask;
        _kind = next;
        _error = null;
        // A record is kind-specific, so a kind change all but always discards the selection. The one
        // case it survives is the edited party's own record back on its own kind and role, which the
        // picker filter already leaves eligible — there is no separate restore path, and nothing
        // re-selects a record that was already cleared.
        return DiscardSelectionUnlessStillEligible();
    }

    private Task PickRole(string value)
    {
        var next = Enum.Parse<ContractPartyRole>(value);
        if (_role == next) return Task.CompletedTask;
        _role = next;
        _error = null;
        _roleError = null;
        return DiscardSelectionUnlessStillEligible();
    }

    /// <summary>
    /// Re-checks the chosen record against the new (kind, role) filter and, when it no longer
    /// qualifies, clears it <b>and says so</b>. Focus is deliberately left on the control the user
    /// just changed: moving it would take them away from the decision they are making.
    /// </summary>
    private async Task DiscardSelectionUnlessStillEligible()
    {
        if (_value is null)
        {
            return;
        }

        // On the edited party's own (kind, role) the party itself is still selectable, which is what
        // the picker filter already allows for.
        if (Available.Any(o => o.Value == _value))
        {
            return;
        }

        var discarded = AllForKind.FirstOrDefault(o => o.Value == _value)?.Label;
        _value = null;
        if (discarded is not null)
        {
            await OnAnnounce.InvokeAsync($"{discarded} cleared — not available as {RoleNoun}.");
        }
    }

    private void OnValueChanged(string? value)
    {
        _value = value;
        _error = null;
    }

    private void OnFromChanged(DateTime? value)
    {
        _fromDate = value;
        _errors.Remove("fromDate");
    }

    private void OnToChanged(DateTime? value)
    {
        _toDate = value;
        _errors.Remove("toDate");
    }

    private async Task CloseAsync() => await OpenChanged.InvokeAsync(false);

    private async Task SubmitAsync()
    {
        if (_isSaving) return;
        _error = null;
        _roleError = null;
        _errors.Clear();

        // Required on every write since issue #157 §8.1. Refused here with a message about the ROLE,
        // rather than posting and taking the server's — which is the same message, but a round trip
        // later and on a control the user has already left.
        if (_role is not { } role)
        {
            _roleError = "Pick the role this record plays in the agreement.";
            return;
        }

        if (RoleRejectedByType)
        {
            _roleError = $"A {TypeLabel} contract cannot have a {PartyRoleLabel.For(role).ToLowerInvariant()}. "
                + "Pick one of the roles offered.";
            return;
        }

        // Let any in-flight inline contact create land, then map the staged id to the real one; a
        // create that failed resolves to null and reads as "nothing selected" rather than posting an
        // id no server issued.
        if (IsContactKind)
            await ContactCreator.WhenSettledAsync();

        if (!Guid.TryParse(IsContactKind ? ContactCreator.Resolve(_value) : _value, out var id))
        {
            _error = $"Select {Current.Article} to link.";
            return;
        }

        // Both rules MIRROR the server's (issue #121 §8) and never replace them: every one is re-run
        // server-side, and the server is what actually refuses.
        if (_fromDate is { } from && ContractStarted is { } started && from.Date < started)
        {
            _errors["fromDate"] =
                $"This contract began {started.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)} — a party can't be in the role before that.";
        }

        if (_fromDate is { } f && _toDate is { } t && t.Date < f.Date)
        {
            _errors["toDate"] = "End date can't be before the start date.";
        }

        if (_errors.Count > 0) return;

        // The body is a FULL replacement on the edit, so the dialog always sends the role and both
        // dates — an emptied date goes as null and clears.
        var body = new ContractPartyRequest
        {
            AccountId = _kind == ContractPartyKind.Account ? id : null,
            ContactId = _kind == ContractPartyKind.Institution ? id : null,
            PropertyId = _kind == ContractPartyKind.Property ? id : null,
            Role = role,
            FromDate = _fromDate is { } fd ? DateTime.SpecifyKind(fd.Date, DateTimeKind.Utc) : null,
            ToDate = _toDate is { } td ? DateTime.SpecifyKind(td.Date, DateTimeKind.Utc) : null,
        };

        _isSaving = true;
        try
        {
            var result = Party is { } party
                ? await Contracts.UpdatePartyAsync(Contract.ContractId, party.ContractPartyId, body)
                : await Contracts.AddPartyAsync(Contract.ContractId, body);

            // The inline classes are keyed on the FIELD KEY the server returns, never on message text:
            // three failure classes share status 404 and only one of them belongs on this control
            // (issue #121 §9). The same key carries the 409 and the cap 422.
            if (!result.IsSuccess && InlineErrorFor(result.Problem) is { } inline)
            {
                _error = inline;
                return;
            }

            var ok = result.Toast(Snackbar,
                IsEdit ? "Unable to update party" : "Unable to add party",
                IsEdit ? "Party updated." : "Party added.");
            if (!ok) return;

            await OnAnnounce.InvokeAsync(IsEdit
                ? $"{SelectedLabel} updated: {PartyRoleLabel.For(role)}{TermSuffix}."
                : $"{SelectedLabel} linked as {PartyRoleLabel.For(role)}.");

            await OnSaved.InvokeAsync();
            await OpenChanged.InvokeAsync(false);
        }
        finally
        {
            _isSaving = false;
        }
    }

    /// <summary>
    /// The server's message for a failure that belongs on the record picker, or null to fall through
    /// to the toast. Matched on the problem-details <c>errors</c> key — which is exactly why issue
    /// #121 gives <c>DomainNotFoundException</c> a field overload.
    /// </summary>
    private string? InlineErrorFor(ApiProblem? problem) =>
        problem?.ErrorFor(Current.Field);

    private string SelectedLabel =>
        AllForKind.FirstOrDefault(o => o.Value == _value)?.Label ?? Current.Label;

    private string TermSuffix =>
        PartyTerm.Format(_fromDate, _toDate) is { } term ? $", {term}" : "";
}
