using Microsoft.AspNetCore.Components;
using Odyssey.ApiClient;
using Odyssey.Client.Components;
using Odyssey.Client.Services;
using Odyssey.Dtos.Finance;

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
    private string? _addressLine;
    private string? _postalCode;
    private string? _city;
    private string? _countryCode;
    private string? _cadastralNumber;
    private decimal? _livingArea;
    private decimal? _plotArea;
    private decimal? _buildYear;

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

    private string? Err(string key) => _errors.TryGetValue(key, out var message) ? message : null;

    protected override void OnInitialized()
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
            _addressLine = re.AddressLine;
            _postalCode = re.PostalCode;
            _city = re.City;
            _countryCode = re.CountryCode;
            _cadastralNumber = re.CadastralNumber;
            _livingArea = re.LivingAreaSqm;
            _plotArea = re.PlotAreaSqm;
            _buildYear = re.BuildYear;
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

    protected override async Task OnInitializedAsync()
    {
        if (!OperatingSystem.IsBrowser())
            return;

        _currencyOptions = await ReferenceData.CurrencyOptionsAsync();
        if (string.IsNullOrEmpty(_currencyCode) && _currencyOptions.Count > 0)
            _currencyCode = _currencyOptions[0].Value;
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

    private void OnCountryChanged(string value)
    {
        _countryCode = (value ?? string.Empty).Trim().ToUpperInvariant();
        _errors.Remove("countryCode");
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
            if (!string.IsNullOrEmpty(_countryCode) && !(_countryCode.Length == 2 && _countryCode.All(char.IsAsciiLetter)))
                _errors["countryCode"] = "Two letters, e.g. NO or US.";
            if (_buildYear is { } by && (by < 1000 || by > ThisYear))
                _errors["buildYear"] = by > ThisYear ? "Build year can’t be in the future." : "Enter a year after 1000.";
            if (_livingArea is { } la && (la < 0 || la > 1_000_000))
                _errors["livingAreaSqm"] = "Between 0 and 1,000,000 m².";
            if (_plotArea is { } pa && (pa < 0 || pa > 1_000_000))
                _errors["plotAreaSqm"] = "Between 0 and 1,000,000 m².";
        }
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
            AddressLine = Blank(_addressLine),
            PostalCode = Blank(_postalCode),
            City = Blank(_city),
            CountryCode = Blank(_countryCode)?.ToUpperInvariant(),
            CadastralNumber = Blank(_cadastralNumber),
            LivingAreaSqm = _livingArea,
            PlotAreaSqm = _plotArea,
            BuildYear = _buildYear is { } by ? (int)by : null,
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

    private async Task<bool> SaveAsync()
    {
        Validate();
        if (_errors.Count > 0)
            return false;

        var request = BuildRequest();

        if (IsEdit)
        {
            var updated = await Properties.UpdateAsync(Property!.PropertyId, request);
            var ok = updated.ToastOrFields(Snackbar, "Unable to update property", AssignServerError, "Property updated.");
            StateHasChanged();
            return ok;
        }

        var created = await Properties.CreateAsync(request);
        var outcome = created.IsSuccess
            ? ApiResult.Success(created.Status)
            : ApiResult.Failure(created.Status, created.Problem!);
        if (!outcome.ToastOrFields(Snackbar, "Unable to create property", AssignServerError, "Property created."))
        {
            StateHasChanged();
            return false;
        }

        if (created.Value is { } property)
            await OnCreated.InvokeAsync(property);

        return true;
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
            "name", "description", "currencyCode", "disposedDate", "countryCode", "buildYear",
            "livingAreaSqm", "plotAreaSqm", "modelYear", "registrationNumber", "vin",
        ];

        var key = known.FirstOrDefault(k => string.Equals(k, leaf, StringComparison.OrdinalIgnoreCase));
        if (key is null)
            return false;

        _errors[key] = message;
        return true;
    }
}
