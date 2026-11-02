using System.Net;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using MudBlazor;
using Odyssey.ApiClient;
using Odyssey.ApiClient.Resources;
using Odyssey.Client.Components;
using Odyssey.Dtos;
using Odyssey.Dtos.Finance;
using Odyssey.Dtos.Journal;

namespace Odyssey.Client.Pages.Finance;

/// <summary>
/// The add / edit dialog for one contact method. Parameters, state and the save path; the markup is in
/// <c>ContactMethodDialog.razor</c>. An ADDRESS is written by this dialog itself, through the shared
/// address block (issue #383 §3.3); an email or phone goes back to the host through <see cref="OnCommit"/>.
/// </summary>
public partial class ContactMethodDialog
{
    [Parameter] public bool Open { get; set; }
    [Parameter] public EventCallback<bool> OpenChanged { get; set; }

    /// <summary>"address" | "email" | "phone".</summary>
    [Parameter, EditorRequired] public string Kind { get; set; } = "address";
    [Parameter, EditorRequired] public ContactMethodDraft Draft { get; set; } = new();

    /// <summary>
    /// The parent contact's type — the second input deciding which labels the picker offers, and what
    /// <see cref="ContactMethodDraft.Validate"/> measures the draft against (issue #47 §3).
    ///
    /// <para>Captured, not live: a concurrent type switch does not rebuild the option set under the
    /// user — that is the WCAG 3.2.2 change-on-input hazard. The dialog keeps the set it opened with
    /// and the write fails server-side instead.</para>
    /// </summary>
    [Parameter, EditorRequired] public ContactType ContactType { get; set; } = ContactType.Person;
    [Parameter] public bool IsEdit { get; set; }
    /// <summary>First record in its collection — primary is forced on and the toggle disabled.</summary>
    [Parameter] public bool IsFirst { get; set; }

    /// <summary>
    /// Host callback performing the sub-resource API call; returns success. The second argument is the
    /// server-error channel (issue #47 §3): the host hands each field the server named to it, and it
    /// returns whether this form owns that field — the shape <c>ToastOrFields</c> expects, so a
    /// rejection the dialog cannot place is toasted rather than vanishing.
    /// </summary>
    [Parameter] public Func<ContactMethodDraft, Func<string, string, bool>, Task<bool>>? OnCommit { get; set; }

    /// <summary>The contact the address belongs to — the dialog writes an address itself (issue #383).</summary>
    [Parameter] public Guid ContactId { get; set; }

    /// <summary>What the caller may do with saved places; decides the picker, the usage line and apply-to.</summary>
    [Parameter] public AddressBlockPermissions Permissions { get; set; } = new(false, false, false);

    /// <summary>Raised after an address was saved, so the host reloads the contact's collections.</summary>
    [Parameter] public EventCallback OnAddressSaved { get; set; }

    private AddressBlockModel? _block;
    private OdsAddressBlock? _addressBlock;
    private string _announce = string.Empty;
    private string? _formAlert;
    private string? _applyToAlert;
    private bool _focusSummary;
    private readonly string _uid = Guid.NewGuid().ToString("N")[..8];

    private string SummaryId => $"cmd-errsum-{_uid}";
    private string ApplyGroupId => $"cmd-apply-{_uid}";

    // The summary lists the address block's errors in field order; the label's own error stays on its field.
    private IReadOnlyList<KeyValuePair<string, string>> SummaryErrors =>
    [
        .. new[] { AddressBlockModel.LocationIdKey, AddressBlockModel.Line1Key, AddressBlockModel.Line2Key, AddressBlockModel.PostalCodeKey,
                   AddressBlockModel.CityKey, AddressBlockModel.RegionKey, AddressBlockModel.CountryCodeKey, AddressBlockModel.ApplyToKey }
            .Where(_errors.ContainsKey)
            .Select(k => new KeyValuePair<string, string>(k, _errors[k])),
    ];

    private Dictionary<string, string> _errors = new(StringComparer.OrdinalIgnoreCase);

    private IReadOnlyDictionary<string, string> Errors => _errors;

    private string LabelFieldId = default!;

    private string? Err(string key) => _errors.GetValueOrDefault(key);

    // The fields this form can mark. Anything else the server names is toasted by the host instead.
    private IEnumerable<string> OwnedFields => ["label", "value"];

    protected override async Task OnInitializedAsync()
    {
        LabelFieldId = $"ods-cmd-label-{Guid.NewGuid():N}";
        if (Kind != "address")
            return;

        var stored = Draft.Stored is { } a ? await StoredFor(a) : null;
        _block = new AddressBlockModel(AddressBlockVariant.Contact, stored, Permissions);
    }

    /// <summary>The stored link, with the other uses broken down by kind when the caller may read the place.</summary>
    private async Task<AddressBlockStored> StoredFor(ExistingAddress a)
    {
        var stored = new AddressBlockStored
        {
            LocationId = a.LocationId,
            Fields = new AddressFields
            {
                Line1 = a.Line1, Line2 = a.Line2, PostalCode = a.PostalCode, City = a.City, Region = a.Region, CountryCode = a.CountryCode,
            },
            OtherUses = a.OtherUses,
            PlaceHasCoordinates = a.PlaceHasCoordinates,
        };
        stored.OthersBreakdown = await OthersBreakdownAsync(a.LocationId);
        return stored;
    }

    /// <summary>The place's per-kind usage less this contact — the breakdown of its OTHER uses.</summary>
    private async Task<LocationUsage?> OthersBreakdownAsync(Guid locationId)
    {
        if (!Permissions.CanReadLocations)
            return null;
        var place = await Locations.GetAsync(locationId);
        return place.IsSuccess && place.Value?.Usage is { } usage
            ? usage with { Contacts = usage.Contacts is { } n ? Math.Max(0, n - 1) : null }
            : null;
    }

    private void OnBlockChanged()
    {
        _formAlert = null;
        foreach (var key in new[] { AddressBlockModel.LocationIdKey, AddressBlockModel.Line1Key, AddressBlockModel.CityKey,
                                    AddressBlockModel.CountryCodeKey, AddressBlockModel.ApplyToKey })
            _errors.Remove(key);
        if (_block?.AsksApplyTo != true)
            _applyToAlert = null;
    }

    private Task FocusApplyToAsync() => _addressBlock?.FocusApplyToAsync() ?? Task.CompletedTask;

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!_focusSummary)
            return;
        _focusSummary = false;
        try { await Js.InvokeVoidAsync("odsFocusById", SummaryId); }
        catch (Exception) { /* best-effort: no JS runtime under bUnit */ }
    }

    /// <summary>
    /// The address write (issue #383 §5.1): the block decides the body; the reply is read by status, since
    /// #380's 403 carries no reason (§5.3) and its 409/422 keys each map to a field (§7).
    /// </summary>
    private async Task<bool> SubmitAddressAsync()
    {
        var block = _block!;
        _formAlert = null;
        _applyToAlert = null;
        _errors = Draft.Validate(Kind, ContactType);
        foreach (var (key, message) in block.Validate())
            _errors[key] = message;
        if (_errors.Count > 0)
        {
            _focusSummary = SummaryErrors.Count > 0;
            if (!_focusSummary)
                await FocusFirstErrorAsync();
            // The submit is the form dialog's, not this component's event: re-render so the summary takes focus.
            StateHasChanged();
            return false;
        }

        var write = block.ToWrite();
        var body = Draft.ToNewAddress(write);
        var result = Draft.Id is { } id
            ? await Contacts.UpdateAddressAsync(ContactId, id, body)
            : await Contacts.AddAddressAsync(ContactId, body);

        if (result.IsSuccess)
        {
            Snackbar.Add(Draft.Id is null ? "Address added." : "Address saved.", MudBlazor.Severity.Success);
            await OnAddressSaved.InvokeAsync();
            return true;
        }

        await ApplyAddressFailureAsync(result.Status, result.Problem, write);
        StateHasChanged();
        return false;
    }

    /// <summary>Puts a refused address write where it belongs — a field, the apply-to group, or the dialog's alert.</summary>
    private async Task ApplyAddressFailureAsync(System.Net.HttpStatusCode status, ApiProblem? problem, AddressWrite write)
    {
        var block = _block!;
        if (status == System.Net.HttpStatusCode.Forbidden)
        {
            _formAlert = AddressBlockModel.ForbiddenMessage(write.Kind, block.Noun);
            return;
        }

        var conflict = status == System.Net.HttpStatusCode.Conflict;
        if (problem?.ErrorFor("applyTo") is { } applyTo)
        {
            if (conflict)
            {
                // The uses changed under the user: reload the usage and ask again with nothing chosen.
                var addresses = await Contacts.ListAddressesAsync(ContactId);
                var fresh = addresses.Value?.FirstOrDefault(x => x.Id == Draft.Id);
                block.UsesChanged(fresh?.OtherUses ?? block.Stored?.OtherUses ?? 0,
                    block.Stored is { } s ? await OthersBreakdownAsync(s.LocationId) : null);
                _applyToAlert = AddressBlockModel.UsesChangedAlert;
                StateHasChanged();
                await FocusApplyToAsync();
                return;
            }

            _errors[AddressBlockModel.ApplyToKey] = applyTo;
        }

        if (problem?.ErrorFor("locationId") is { } locationId)
            _errors[AddressBlockModel.LocationIdKey] = conflict
                ? AddressBlockModel.ContactAlreadyHasAddress
                : AddressBlockModel.LocationIdMessage(locationId);
        if (problem?.ErrorFor("line1") is not null)
            _errors[AddressBlockModel.Line1Key] = conflict
                ? AddressBlockModel.DuplicateAddressMessage(block.Noun, Permissions.CanUpdateLocations)
                : AddressBlockModel.ContactsNeedFullAddress;
        foreach (var key in new[] { AddressBlockModel.CityKey, AddressBlockModel.CountryCodeKey, "label" })
            if (problem?.ErrorFor(key) is { } message)
                _errors[key] = message;

        if (_errors.Count == 0)
            _formAlert = problem?.Message ?? "The address couldn’t be saved.";
        _focusSummary = SummaryErrors.Count > 0;
    }

    private string KindNoun => Kind switch { "email" => "email", "phone" => "phone number", _ => "address" };
    private string KindIcon => Kind switch { "email" => "alternate_email", "phone" => "call", _ => "location_on" };
    private string DialogTitle => (IsEdit ? "Edit " : "New ") + KindNoun;
    private string SubmitLabel => IsEdit ? "Save changes" : $"Create {KindNoun}";

    protected override void OnParametersSet()
    {
        if (IsFirst)
            Draft.IsPrimary = true;
    }

    /// <summary>An address dialog whose block is still loading: Save is disabled until it exists.</summary>
    private bool AddressLoading => Kind == "address" && _block is null;

    private async Task<bool> SubmitAsync()
    {
        // An address is only ever written through its block. Before the block exists there is nothing to send,
        // and falling through to OnCommit (which refuses addresses) used to do nothing, silently (N2).
        if (Kind == "address")
            return _block is not null && await SubmitAddressAsync();

        _errors = Draft.Validate(Kind, ContactType);
        if (_errors.Count > 0)
        {
            await FocusFirstErrorAsync();
            return false;
        }

        if (OnCommit is null)
            return false;

        var ok = await OnCommit(Draft, AssignServerError);
        if (!ok && _errors.Count > 0)
            await FocusFirstErrorAsync();

        return ok;
    }

    // Places a server-named field error on the matching control. Returns false for a field this form
    // does not render, so the host toasts it rather than swallowing it.
    private bool AssignServerError(string field, string message)
    {
        if (!OwnedFields.Contains(field, StringComparer.OrdinalIgnoreCase))
            return false;

        _errors[field] = message;
        return true;
    }

    // Only the Label field has an id to move focus to; the OdsField-backed rows manage their own.
    // window.odsFocusById is a bare .focus(), so it is a silent no-op if the node has gone.
    private async Task FocusFirstErrorAsync()
    {
        if (!_errors.ContainsKey("label"))
            return;

        try { await Js.InvokeVoidAsync("odsFocusById", LabelFieldId); }
        catch (Exception) { /* best-effort: no JS runtime under bUnit, and focus is not load-bearing */ }
    }
}
