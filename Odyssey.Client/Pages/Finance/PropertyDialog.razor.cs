using Microsoft.JSInterop;
using Microsoft.AspNetCore.Components;
using Odyssey.ApiClient;
using Odyssey.Client.Components;
using Odyssey.Client.Services;
using Odyssey.Dtos;
using Odyssey.Dtos.Finance;
using Odyssey.Dtos.Journal;

namespace Odyssey.Client.Pages.Finance;

/// <summary>
/// The "New property" dialog, reused in edit mode when a <see cref="Property"/> is supplied.
/// Parameters, state and the save path; the markup is in <c>PropertyDialog.razor</c>.
/// </summary>
public partial class PropertyDialog
{
    [Parameter] public bool Open { get; set; }
    [Parameter] public EventCallback<bool> OpenChanged { get; set; }

    /// <summary>Raised after a successful create or update so the host can refresh.</summary>
    [Parameter] public EventCallback OnSaved { get; set; }

    /// <summary>Raised with the new record after a create, so the host can open it.</summary>
    [Parameter] public EventCallback<ExistingProperty> OnCreated { get; set; }

    /// <summary>When set, the dialog edits this property. Null = create mode.</summary>
    [Parameter] public ExistingProperty? Property { get; set; }

    private bool IsEdit => Property is not null;

    private static int ThisYear => DateTime.UtcNow.Year;

    private static readonly IReadOnlyList<OdsCardSelectOption> TypeOptions =
    [
        .. OdsTypeRegistries.PropertyTypes.Select(t => new OdsCardSelectOption
        {
            Value = t.Key,
            Label = t.Label,
            Icon = t.Icon,
            Color = t.Color,
            Soft = t.Soft,
        }),
    ];

    private PropertyType _type = PropertyType.RealEstate;
    private string? _name;
    private string? _description;
    private string _currencyCode = string.Empty;
    private DateTime? _acquired;
    private DateTime? _disposed;
    private string? _notes;

    // Both drafts are kept, so flipping the type on create and back loses nothing typed.
    private RealEstateKind _realEstateKind = RealEstateKind.House;

    // The address is the shared address block's (issue #383 §3.3): a saved place or a typed address.
    private AddressBlockModel _address = new(AddressBlockVariant.Property, null, new(false, false, false));
    private OdsAddressBlock? _addressBlock;

    // False until _address is the real model (the stored place and its usage loaded). The block is not
    // rendered and Save is disabled before then, so nothing is typed into the placeholder above (N2).
    private bool _addressReady;
    private AddressBlockPermissions _addressPermissions = new(false, false, false);
    private string _announce = string.Empty;
    private string? _formAlert;
    private string? _applyToAlert;
    private bool _focusSummary;
    private readonly string _uid = Guid.NewGuid().ToString("N")[..8];
    private string SummaryId => $"prop-errsum-{_uid}";
    private string ApplyGroupId => $"prop-apply-{_uid}";

    private string? _cadastralNumber;
    private decimal? _livingArea;
    private decimal? _plotArea;
    private decimal? _buildYear;

    // Issue #217. PUT is a full replace, so the stored id is loaded here and round-tripped; clearing
    // the picker sends null and removes the link.
    private string? _homeownerAssociationId;
    private IReadOnlyList<ExistingContact> _contacts = [];
    private bool _contactsLoading;

    private VehicleKind _vehicleKind = VehicleKind.Car;
    private string? _registration;
    private string? _vin;
    private string? _make;
    private string? _model;
    private decimal? _modelYear;
    private DateTime? _firstRegistered;

    private IReadOnlyList<OdsOption> _currencyOptions = [];

    // Keyed by the NewProperty / detail JSON property name, so a server 400 lands on the same control
    // a client pre-check would have marked.
    private readonly Dictionary<string, string> _errors = new(StringComparer.OrdinalIgnoreCase);

    private bool IsVehicle => _type == PropertyType.Vehicle;

    private OdsTypeOption TypeInfo => OdsTypeRegistries.PropertyTypeOf(_type);

    private string CurrentKind => IsVehicle ? _vehicleKind.ToString() : _realEstateKind.ToString();

    /// <summary>
    /// The server refuses a currency change while the property has estimates (they are recorded in
    /// the old one), so the dialog says so before the round trip rather than after it.
    /// </summary>
    private bool CurrencyChangeBlocked =>
        Property is { EstimateCount: > 0 } p
        && !string.Equals(p.CurrencyCode, _currencyCode, StringComparison.OrdinalIgnoreCase);

    private string? StoredAssociationId => Property?.RealEstateDetails?.HomeownerAssociationId?.ToString();

    // The design system's copy for the three server refusals on a changed id (400 not found, 400
    // archived, 422 not an organization). None echoes more than the user picked.
    internal const string AssociationNotFound = "That contact no longer exists — it may have been deleted. Pick another association.";
    internal const string AssociationArchived = "That organization is archived. Restore it in Contacts or pick another.";
    internal const string AssociationNotOrganization = "That contact isn’t an organization, so it can’t be a homeowner association.";

    private string? Err(string key) => _errors.TryGetValue(key, out var message) ? message : null;

    protected override async Task OnInitializedAsync()
    {
        var user = (await AuthenticationStateProvider.GetAuthenticationStateAsync()).User;
        _addressPermissions = AddressBlockPermissions.For(user);
        InitializeFields();
        var stored = Property?.RealEstateDetails is { LocationId: { } locationId } re ? await StoredFor(locationId, re) : null;
        var unlinked = Property?.RealEstateDetails is { LocationId: null } legacy
            ? new AddressFields { Line1 = legacy.AddressLine, PostalCode = legacy.PostalCode, City = legacy.City, CountryCode = legacy.CountryCode }
            : null;
        _address = new AddressBlockModel(AddressBlockVariant.Property, stored, _addressPermissions, unlinked);
        _addressReady = true;
        await LoadContactsAsync();
    }

    /// <summary>
    /// The stored link. The place's Line 2 and Region are not on the property DTO, so the place is read
    /// (under <c>locations.read</c>) for them and for the per-kind breakdown of its other uses.
    /// </summary>
    private async Task<AddressBlockStored> StoredFor(Guid locationId, RealEstateDetailsDto re)
    {
        ExistingLocation? place = null;
        if (_addressPermissions.CanReadLocations && (await Locations.GetAsync(locationId)) is { IsSuccess: true, Value: { } found })
            place = found;
        return new AddressBlockStored
        {
            LocationId = locationId,
            Fields = new AddressFields
            {
                Line1 = re.AddressLine, PostalCode = re.PostalCode, City = re.City, CountryCode = re.CountryCode,
                Line2 = place?.Line2, Region = place?.Region,
            },
            OtherUses = re.OtherUses ?? 0,
            PlaceHasCoordinates = re.PlaceHasCoordinates ?? false,
            OthersBreakdown = OthersOf(place),
        };
    }

    /// <summary>The place's per-kind usage less this property — the breakdown of its OTHER uses.</summary>
    private static LocationUsage? OthersOf(ExistingLocation? place) =>
        place?.Usage is { } usage ? usage with { Properties = usage.Properties is { } n ? Math.Max(0, n - 1) : null } : null;

    private void InitializeFields()
    {
        if (Property is not { } p)
        {
            _currencyCode = UserPreferences.DefaultCurrency ?? string.Empty;
            return;
        }

        _type = p.Type;
        _name = p.Name;
        _description = p.Description;
        _currencyCode = p.CurrencyCode;
        _acquired = p.AcquiredDate;
        _disposed = p.DisposedDate;
        _notes = p.Notes;

        if (p.RealEstateDetails is { } re)
        {
            _realEstateKind = re.Kind;
            _cadastralNumber = re.CadastralNumber;
            _livingArea = re.LivingAreaSqm;
            _plotArea = re.PlotAreaSqm;
            _buildYear = re.BuildYear;
            _homeownerAssociationId = re.HomeownerAssociationId?.ToString();
        }

        if (p.VehicleDetails is { } ve)
        {
            _vehicleKind = ve.Kind;
            _registration = ve.RegistrationNumber;
            _vin = ve.Vin;
            _make = ve.Make;
            _model = ve.Model;
            _modelYear = ve.ModelYear;
            _firstRegistered = ve.FirstRegisteredDate;
        }
    }

    private async Task LoadContactsAsync()
    {
        if (!OperatingSystem.IsBrowser())
            return;

        _currencyOptions = await ReferenceData.CurrencyOptionsAsync();
        if (string.IsNullOrEmpty(_currencyCode) && _currencyOptions.Count > 0)
            _currencyCode = _currencyOptions[0].Value;

        _contactsLoading = true;
        try
        {
            _contacts = await ReferenceData.ContactsAsync();
        }
        finally
        {
            _contactsLoading = false;
        }
    }

    private void OnAssociationChanged(string? value)
    {
        _homeownerAssociationId = string.IsNullOrWhiteSpace(value) ? null : value;
        _errors.Remove("homeownerAssociationId");
    }

    private void OnTypeChanged(string value)
    {
        if (Enum.TryParse<PropertyType>(value, out var type))
            _type = type;
    }

    private void OnKindChanged(string value)
    {
        if (IsVehicle && Enum.TryParse<VehicleKind>(value, out var vehicleKind))
            _vehicleKind = vehicleKind;
        else if (!IsVehicle && Enum.TryParse<RealEstateKind>(value, out var realEstateKind))
            _realEstateKind = realEstateKind;
    }

    private void OnCurrencyChanged(string value)
    {
        _currencyCode = value;
        _errors.Remove("currencyCode");
    }

    private void OnDisposedChanged(DateTime? value)
    {
        _disposed = value;
        _errors.Remove("disposedDate");
    }

    private void OnAddressChanged()
    {
        _formAlert = null;
        foreach (var key in new[] { AddressBlockModel.LocationIdKey, AddressBlockModel.Line1Key, AddressBlockModel.PostalCodeKey,
                                    AddressBlockModel.CityKey, AddressBlockModel.CountryCodeKey, AddressBlockModel.ApplyToKey })
            _errors.Remove(key);
        if (!_address.AsksApplyTo)
            _applyToAlert = null;
    }

    private Task FocusApplyToAsync() => _addressBlock?.FocusApplyToAsync() ?? Task.CompletedTask;

    // The summary lists the address block's errors in field order (issue #383 §6.8).
    private IReadOnlyList<KeyValuePair<string, string>> SummaryErrors =>
    [
        .. new[] { "name", "description", "currencyCode", "disposedDate", AddressBlockModel.LocationIdKey, AddressBlockModel.Line1Key,
                   AddressBlockModel.PostalCodeKey, AddressBlockModel.CityKey, AddressBlockModel.CountryCodeKey, AddressBlockModel.ApplyToKey,
                   "buildYear", "livingAreaSqm", "plotAreaSqm", "homeownerAssociationId", "modelYear", "registrationNumber", "vin" }
            .Where(_errors.ContainsKey)
            .Select(k => new KeyValuePair<string, string>(k, _errors[k])),
    ];

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!_focusSummary)
            return;
        _focusSummary = false;
        try { await Js.InvokeVoidAsync("odsFocusById", SummaryId); }
        catch (Exception) { /* best-effort: no JS runtime under bUnit */ }
    }

    /// <summary>
    /// The helper under an identifier field: what the server will store, when that differs from what
    /// was typed, so the saved form is never a surprise; the rule otherwise.
    /// </summary>
    private static string IdentifierHelp(string? typed, string rule)
    {
        var normalized = PropertyVisuals.NormalizePlate(typed);
        return normalized.Length > 0 && normalized != typed ? $"Saved as {normalized}" : rule;
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private void Validate()
    {
        _errors.Clear();

        if (string.IsNullOrWhiteSpace(_name))
            _errors["name"] = "Give the property a name.";
        if (string.IsNullOrWhiteSpace(_description))
            _errors["description"] = "Add a short description.";
        if (string.IsNullOrWhiteSpace(_currencyCode))
            _errors["currencyCode"] = "Pick a currency.";
        else if (CurrencyChangeBlocked)
            _errors["currencyCode"] = "The currency can’t change while the property has estimates.";
        if (_acquired is { } a && _disposed is { } d && d.Date < a.Date)
            _errors["disposedDate"] = "Disposed can’t be before Acquired.";

        if (IsVehicle)
        {
            if (_modelYear is { } my && (my < 1900 || my > ThisYear + 1))
                _errors["modelYear"] = my > ThisYear + 1 ? $"No later than {ThisYear + 1}." : "Enter a year after 1900.";
            if (PropertyVisuals.NormalizePlate(_registration).Length > 16)
                _errors["registrationNumber"] = "At most 16 characters.";
            if (PropertyVisuals.NormalizePlate(_vin).Length > 32)
                _errors["vin"] = "At most 32 characters.";
        }
        else
        {
            foreach (var (key, message) in _address.Validate())
                _errors[key] = message;
            if (_buildYear is { } by && (by < 1000 || by > ThisYear))
                _errors["buildYear"] = by > ThisYear ? "Build year can’t be in the future." : "Enter a year after 1000.";
            if (_livingArea is { } la && (la < 0 || la > 1_000_000))
                _errors["livingAreaSqm"] = "Between 0 and 1,000,000 m².";
            if (_plotArea is { } pa && (pa < 0 || pa > 1_000_000))
                _errors["plotAreaSqm"] = "Between 0 and 1,000,000 m².";
            if (AssociationPreCheck(_homeownerAssociationId, StoredAssociationId, _contacts) is { } associationError)
                _errors["homeownerAssociationId"] = associationError;
        }
    }

    /// <summary>
    /// The server's R2–R4 against the loaded contact list, run only when the id changed — a kept link is
    /// never re-checked, matching the server's change-only rule (§8.3). An id the list does not carry
    /// is left to the server rather than guessed at, since the list may simply have failed to load.
    /// </summary>
    internal static string? AssociationPreCheck(
        string? associationId, string? storedAssociationId, IReadOnlyList<ExistingContact> contacts)
    {
        if (string.IsNullOrEmpty(associationId) || associationId == storedAssociationId)
            return null;

        var contact = contacts.FirstOrDefault(c => c.ContactId.ToString() == associationId);
        return contact switch
        {
            null => null,
            { Archived: not null } => AssociationArchived,
            { Type: not ContactType.Organization } => AssociationNotOrganization,
            _ => null,
        };
    }

    private NewProperty BuildRequest() => new()
    {
        Name = _name!.Trim(),
        Description = _description!.Trim(),
        Type = _type,
        CurrencyCode = _currencyCode,
        AcquiredDate = _acquired,
        DisposedDate = _disposed,
        Notes = Blank(_notes),
        // Archive / Restore is the row menu's; PUT is a full replacement, so the stored flag rides
        // along or an ordinary edit would silently restore an archived property.
        Archived = Property?.Archived is not null,
        RealEstateDetails = IsVehicle ? null : new RealEstateDetailsDto
        {
            Kind = _realEstateKind,
            // Per the address block's state (issue #383 §5.1): a place by id, the four fields, or — Cleared —
            // neither, which unlinks the property and leaves the place on the Locations page.
            LocationId = _addressWrite.LocationId,
            AddressLine = _addressWrite.Fields?.Line1,
            PostalCode = _addressWrite.Fields?.PostalCode,
            City = _addressWrite.Fields?.City,
            CountryCode = _addressWrite.Fields?.CountryCode,
            ApplyTo = _addressWrite.ApplyTo,
            ExpectedOtherUses = _addressWrite.ExpectedOtherUses,
            CadastralNumber = Blank(_cadastralNumber),
            LivingAreaSqm = _livingArea,
            PlotAreaSqm = _plotArea,
            BuildYear = _buildYear is { } by ? (int)by : null,
            HomeownerAssociationId = Guid.TryParse(_homeownerAssociationId, out var association) ? association : null,
        },
        VehicleDetails = !IsVehicle ? null : new VehicleDetailsDto
        {
            Kind = _vehicleKind,
            RegistrationNumber = Blank(PropertyVisuals.NormalizePlate(_registration)),
            Vin = Blank(PropertyVisuals.NormalizePlate(_vin)),
            Make = Blank(_make),
            Model = Blank(_model),
            ModelYear = _modelYear is { } my ? (int)my : null,
            FirstRegisteredDate = _firstRegistered,
        },
    };

    private AddressWrite _addressWrite = AddressWrite.None;

    private async Task<bool> SaveAsync()
    {
        if (!_addressReady)
            return false;

        _formAlert = null;
        _applyToAlert = null;
        Validate();
        if (_errors.Count > 0)
        {
            // The submit is the form dialog's, not this component's event: re-render so the summary takes focus.
            _focusSummary = true;
            StateHasChanged();
            return false;
        }

        _addressWrite = IsVehicle ? AddressWrite.None : _address.ToWrite();
        var request = BuildRequest();

        if (IsEdit)
        {
            var updated = await Properties.UpdateAsync(Property!.PropertyId, request);
            if (!updated.IsSuccess && await AddressFailureAsync(updated.Status, updated.Problem))
            {
                StateHasChanged();
                return false;
            }

            var ok = updated.ToastOrFields(Snackbar, "Unable to update property", AssignServerError, "Property updated.");
            _focusSummary = !ok && SummaryErrors.Count > 0;
            StateHasChanged();
            return ok;
        }

        var created = await Properties.CreateAsync(request);
        if (!created.IsSuccess && await AddressFailureAsync(created.Status, created.Problem))
        {
            StateHasChanged();
            return false;
        }

        var outcome = created.IsSuccess
            ? ApiResult.Success(created.Status)
            : ApiResult.Failure(created.Status, created.Problem!);
        if (!outcome.ToastOrFields(Snackbar, "Unable to create property", AssignServerError, "Property created."))
        {
            _focusSummary = SummaryErrors.Count > 0;
            StateHasChanged();
            return false;
        }

        if (created.Value is { } property)
            await OnCreated.InvokeAsync(property);

        return true;
    }

    /// <summary>
    /// A refusal of the ADDRESS half (issue #383 §5.3, §7), placed where it belongs; true when it was one.
    /// The 403 carries no reason, so its message is chosen from the request that was sent.
    /// </summary>
    private async Task<bool> AddressFailureAsync(System.Net.HttpStatusCode status, ApiProblem? problem)
    {
        if (IsVehicle)
            return false;
        if (status == System.Net.HttpStatusCode.Forbidden)
        {
            _formAlert = AddressBlockModel.ForbiddenMessage(_addressWrite.Kind, _address.Noun);
            return true;
        }

        string? Leaf(string key) => problem?.Errors.FirstOrDefault(e =>
            string.Equals(e.Key[(e.Key.LastIndexOf('.') + 1)..], key, StringComparison.OrdinalIgnoreCase)).Value?.FirstOrDefault();

        var conflict = status == System.Net.HttpStatusCode.Conflict;
        if (conflict && Leaf("applyTo") is not null && Property is { } p)
        {
            // The uses changed under the user: reload the usage and ask again with nothing chosen.
            var fresh = await Properties.GetAsync(p.PropertyId);
            var locationId = _address.Stored?.LocationId;
            ExistingLocation? place = null;
            if (locationId is { } id && _addressPermissions.CanReadLocations && (await Locations.GetAsync(id)) is { IsSuccess: true, Value: { } found })
                place = found;
            _address.UsesChanged(fresh.Value?.RealEstateDetails?.OtherUses ?? _address.Stored?.OtherUses ?? 0, OthersOf(place));
            _applyToAlert = AddressBlockModel.UsesChangedAlert;
            StateHasChanged();
            await FocusApplyToAsync();
            return true;
        }

        var handled = false;
        if (Leaf("locationId") is not null)
        {
            _errors[AddressBlockModel.LocationIdKey] = _address.LocationIdMessage;
            handled = true;
        }

        if (Leaf("addressLine") is not null || Leaf("line1") is not null)
        {
            _errors[AddressBlockModel.Line1Key] = conflict
                ? AddressBlockModel.DuplicateAddressMessage(_address.Noun, _addressPermissions.CanUpdateLocations)
                : Leaf("addressLine") ?? Leaf("line1")!;
            handled = true;
        }

        if (!conflict && Leaf("applyTo") is { } applyTo)
        {
            _errors[AddressBlockModel.ApplyToKey] = applyTo;
            handled = true;
        }

        _focusSummary = handled && SummaryErrors.Count > 0;
        return handled;
    }

    /// <summary>
    /// Places a server field error on its control. The keys arrive as JSON paths
    /// (<c>RealEstateDetails.BuildYear</c>), so the leaf names the field.
    /// </summary>
    private bool AssignServerError(string field, string message)
    {
        var leaf = field[(field.LastIndexOf('.') + 1)..];
        string[] known =
        [
            "name", "description", "currencyCode", "disposedDate", "countryCode", "city", "postalCode", "buildYear",
            "livingAreaSqm", "plotAreaSqm", "modelYear", "registrationNumber", "vin",
        ];

        // The server's messages name the contact by id only; the field shows the design system's copy,
        // chosen by which of the three refusals it was.
        if (string.Equals(leaf, "homeownerAssociationId", StringComparison.OrdinalIgnoreCase))
        {
            _errors["homeownerAssociationId"] =
                message.Contains("archived", StringComparison.OrdinalIgnoreCase) ? AssociationArchived
                : message.Contains("not an organization", StringComparison.OrdinalIgnoreCase) ? AssociationNotOrganization
                : AssociationNotFound;
            return true;
        }

        var key = known.FirstOrDefault(k => string.Equals(k, leaf, StringComparison.OrdinalIgnoreCase));
        if (key is null)
            return false;

        _errors[key] = message;
        return true;
    }
}
