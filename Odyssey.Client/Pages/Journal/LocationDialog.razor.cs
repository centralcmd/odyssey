using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using MudBlazor;
using Odyssey.ApiClient;
using Odyssey.ApiClient.Resources;
using Odyssey.Client.Components;
using Odyssey.Client.Services;
using Odyssey.Dtos.Journal;

namespace Odyssey.Client.Pages.Journal;

/// <summary>
/// The New / Edit location dialog. Parameters, state and the save path; the markup is in
/// <c>LocationDialog.razor</c>.
/// </summary>
public partial class LocationDialog
{
    internal enum Step { Search, Details }

    private enum ZoneOrigin { Provider, Missing, Browser }

    /// <summary>
    /// The coordinates the provider stays the source for — the picked candidate's, or a stored row's. A
    /// stored Manual row has none.
    /// </summary>
    private sealed record Anchor(
        LocationSource Source, string? SourceReference, decimal Latitude, decimal Longitude,
        LocationCandidateKind? Kind, string Name, string? Description, LocationAttribution? Attribution);

    [Inject] private ILocationsApiClient Locations { get; set; } = default!;
    [Inject] private ISnackbar Snackbar { get; set; } = default!;
    [Inject] private IJSRuntime Js { get; set; } = default!;

    [Parameter] public bool Open { get; set; }
    [Parameter] public EventCallback<bool> OpenChanged { get; set; }

    /// <summary>When set, the dialog edits this location. Null = create mode.</summary>
    [Parameter] public ExistingLocation? Location { get; set; }

    /// <summary><c>locations.create</c> — the geocoding search, in both modes.</summary>
    [Parameter] public bool CanSearch { get; set; } = true;

    /// <summary>Raised with the stored row after a successful create or update.</summary>
    [Parameter] public EventCallback<ExistingLocation> OnSaved { get; set; }

    // The region is remembered for the browser session (DS: locations-page.region) — the WASM runtime
    // lives exactly as long as the tab, which is what a session-scoped value means here.
    private static LocationSearchRegion RememberedRegion = LocationSearchRegion.Norway;

    private LocationSearchRegion Region { get; set; } = RememberedRegion;

    private Step _step;
    private bool _researching;
    private bool _replacedNote;
    private bool _saving;

    private string? _name;
    private string? _description;
    private string? _address;
    private decimal? _lat;
    private decimal? _lng;
    private decimal? _altitude;
    private string? _timeZone;
    private Anchor? _pick;
    private ZoneOrigin _zoneOrigin = ZoneOrigin.Provider;

    private readonly string _uid = Guid.NewGuid().ToString("N")[..8];

    // A step change removes the control that had focus (a result row, Enter by hand, Search again), so
    // focus is moved on deliberately after the render rather than left to drop to the page (WCAG 2.4.3).
    private bool _focusPicked;
    private string _announce = "";

    private string PickedId => $"loc-picked-{_uid}";

    // Keyed by the NewLocation JSON property name, so a server 400/409 lands on the control a client
    // pre-check would have marked. Coordinates share one key — the field shows one error line.
    private readonly Dictionary<string, string> _errors = new(StringComparer.OrdinalIgnoreCase);

    private bool IsEdit => Location is not null;

    private string TimeZoneInputId => $"loc-tz-{_uid}";

    private static DateTime Now => DateTime.UtcNow;

    private decimal? LatRounded => LocationVisuals.Round(_lat);
    private decimal? LngRounded => LocationVisuals.Round(_lng);

    /// <summary>The provider stays the source only while both coordinates equal its own after rounding.</summary>
    private bool Anchored => _pick is { } p && LatRounded == p.Latitude && LngRounded == p.Longitude;

    private bool Diverged => _pick is not null && !Anchored;

    internal LocationSource Source => Anchored ? _pick!.Source : LocationSource.Manual;

    private string PickLabel => _pick is { } p ? LocationVisuals.SourceOf(p.Source).Label : "search";

    private string SubtitleText => IsEdit
        ? "Update the details. Every household member sees the same location."
        : _step == Step.Search
            ? "Search an address or place name, then check the details before saving."
            : "Check the details. Every household member sees the same location.";

    private string PickedGlyph => _pick is { Kind: { } kind }
        ? LocationVisuals.KindOf(kind).Icon
        : LocationVisuals.SourceOf(Source).Icon;

    private static string PickedMeta(Anchor pick) =>
        string.Join(" · ", new[] { pick.Description, pick.Kind is { } k ? LocationVisuals.KindOf(k).Label : null }
            .Where(s => !string.IsNullOrEmpty(s)));

    private string ProvenanceTitle => Diverged ? $"Was from {PickLabel}"
        : Source == LocationSource.Manual ? "Entered by hand"
        : $"From {LocationVisuals.SourceOf(Source).Label}";

    private string AddressHelp => (_address?.Length ?? 0) > LocationRules.AddressTextMaxLength - 30
        ? $"{_address!.Length}/{LocationRules.AddressTextMaxLength}"
        : "One line, as you’d write it on an envelope.";

    private string CoordinatesHelp =>
        (LocationVisuals.WillRound(_lat) || LocationVisuals.WillRound(_lng)) && LatRounded is { } lat && LngRounded is { } lng
            ? $"Saved as {LocationVisuals.FormatPair(lat, lng)} — 5 decimals, about 1 m."
            : "Decimal degrees (WGS 84). Negative is south or west.";

    private string? CoordinatesError => Err("coordinates") ?? Err("latitude") ?? Err("longitude");

    private string? TimeZoneHelp => _zoneOrigin == ZoneOrigin.Missing && string.IsNullOrEmpty(_timeZone)
        ? $"{(_pick is null ? "The provider" : PickLabel)} didn’t return a time zone Odyssey recognises. Choose the one this place keeps."
        : _zoneOrigin == ZoneOrigin.Browser && _timeZone is not null && _timeZone == LocationVisuals.BrowserZone()
            ? "Prefilled with your browser’s time zone — change it if the place is elsewhere."
            : LocationVisuals.NowThere(_timeZone, Now);

    private IReadOnlyList<OdsOption> TimeZoneOptions
    {
        get
        {
            var now = Now;
            var options = LocationVisuals.TimeZoneIds
                .Select(id => new OdsOption(id, id) { Note = LocationVisuals.UtcOffset(id, now) })
                .ToList();
            // A stored id this browser does not know still displays, and says so.
            if (!string.IsNullOrEmpty(_timeZone) && !options.Any(o => o.Value == _timeZone))
                options.Insert(0, new OdsOption(_timeZone, _timeZone) { Note = "not recognised here" });
            return options;
        }
    }

    private string? Err(string key) => _errors.TryGetValue(key, out var message) ? message : null;

    protected override void OnInitialized()
    {
        if (Location is not { } l)
        {
            _step = Step.Search;
            return;
        }

        _step = Step.Details;
        _name = l.Name;
        _description = l.Description;
        _address = l.AddressText;
        _lat = l.Latitude;
        _lng = l.Longitude;
        _altitude = l.AltitudeMeters;
        _timeZone = l.TimeZoneId;
        _pick = l.Source == LocationSource.Manual
            ? null
            : new Anchor(l.Source, l.SourceReference, l.Latitude, l.Longitude, null, l.Name, null, l.Attribution);
    }

    private void Set(ref string? field, string? value, string key)
    {
        field = value;
        _errors.Remove(key);
    }

    private void OnLatChanged(decimal? value)
    {
        _lat = value;
        ClearCoordinateErrors();
    }

    private void OnLngChanged(decimal? value)
    {
        _lng = value;
        ClearCoordinateErrors();
    }

    private void ClearCoordinateErrors()
    {
        _errors.Remove("coordinates");
        _errors.Remove("latitude");
        _errors.Remove("longitude");
    }

    private void OnTimeZoneChanged(string? value)
    {
        _timeZone = string.IsNullOrEmpty(value) ? null : value;
        _errors.Remove("timeZoneId");
        if (_timeZone is not null)
            _zoneOrigin = ZoneOrigin.Provider;
    }

    private void OnRegionChanged(LocationSearchRegion region)
    {
        Region = region;
        RememberedRegion = region;
    }

    private void ApplyPick(LocationPick picked)
    {
        var c = picked.Candidate;
        // Create: the candidate names the place unless the user already typed their own name. Edit: the
        // name — and the description, always — are untouched.
        if (!IsEdit && (string.IsNullOrWhiteSpace(_name) || (_pick is not null && _name == _pick.Name)))
            _name = c.Name;
        _address = c.AddressText;
        _lat = c.Latitude;
        _lng = c.Longitude;
        _altitude = c.AltitudeMeters;
        _timeZone = c.TimeZoneId;
        _pick = new Anchor(c.Source, c.SourceReference, c.Latitude, c.Longitude, c.Kind, c.Name, c.Description, picked.Attribution);
        _zoneOrigin = c.TimeZoneId is null ? ZoneOrigin.Missing : ZoneOrigin.Provider;
        _errors.Clear();
        _step = Step.Details;
        _replacedNote = IsEdit;
        _researching = false;
        MoveToDetails(IsEdit
            ? $"Address replaced from {LocationVisuals.SourceOf(c.Source).Label}."
            : $"{c.Name} picked. Check the details before saving.");
    }

    private void MoveToDetails(string announcement)
    {
        _focusPicked = true;
        _announce = announcement;
    }

    private void KeepCurrentAddress()
    {
        _researching = false;
        MoveToDetails("Search closed. The address is unchanged.");
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!_focusPicked)
            return;
        _focusPicked = false;
        try
        { await Js.InvokeVoidAsync("odsFocusById", PickedId); }
        catch (JSException) { }
    }

    private void EnterByHand()
    {
        _pick = null;
        if (string.IsNullOrEmpty(_timeZone))
        {
            _timeZone = LocationVisuals.BrowserZone();
            _zoneOrigin = ZoneOrigin.Browser;
        }
        _step = Step.Details;
        MoveToDetails("Entering the place by hand.");
    }

    private void SearchAgain()
    {
        if (IsEdit)
            _researching = true;
        else
            _step = Step.Search;
    }

    private void RestoreProviderCoordinates()
    {
        if (_pick is null)
            return;
        _lat = _pick.Latitude;
        _lng = _pick.Longitude;
        ClearCoordinateErrors();
        MoveToDetails($"{PickLabel}’s coordinates restored.");
    }

    private Task CloseAsync() => OpenChanged.InvokeAsync(false);

    private static string? Blank(string? value)
    {
        var trimmed = value?.Normalize(NormalizationForm.FormC).Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    /// <summary>The client pre-checks — the same limits the server's attributes hold, named once.</summary>
    internal void Validate()
    {
        _errors.Clear();
        var name = Blank(_name);
        if (name is null)
            _errors["name"] = "Give the place a name.";
        else if (name.Length > LocationRules.NameMaxLength)
            _errors["name"] = $"Keep the name to {LocationRules.NameMaxLength} characters or fewer.";
        if ((_address?.Length ?? 0) > LocationRules.AddressTextMaxLength)
            _errors["addressText"] = $"Keep the address to {LocationRules.AddressTextMaxLength} characters or fewer.";
        if ((_description?.Length ?? 0) > LocationRules.DescriptionMaxLength)
            _errors["description"] = $"Keep the description to {LocationRules.DescriptionMaxLength} characters or fewer.";
        if (LatRounded is not { } lat || LngRounded is not { } lng)
            _errors["coordinates"] = "Enter both latitude and longitude.";
        else if (lat is < -90 or > 90 || lng is < -180 or > 180)
            _errors["coordinates"] = "Latitude is −90 to 90 and longitude −180 to 180.";
        if (_altitude is { } alt && (alt < LocationRules.AltitudeMin || alt > LocationRules.AltitudeMax || alt != decimal.Truncate(alt)))
            _errors["altitudeMeters"] = string.Create(CultureInfo.InvariantCulture,
                $"A whole number between {LocationRules.AltitudeMin} and {LocationRules.AltitudeMax:N0} m.");
        if (string.IsNullOrEmpty(_timeZone))
            _errors["timeZoneId"] = _zoneOrigin == ZoneOrigin.Missing ? "Choose the time zone this place keeps." : "Choose a time zone.";
    }

    private async Task SubmitAsync()
    {
        if (_saving)
            return;
        Validate();
        if (_errors.Count > 0)
            return;

        _saving = true;
        try
        {
            var anchored = Anchored;
            var reference = anchored ? _pick!.SourceReference : null;
            var name = Blank(_name)!;
            var altitude = _altitude is { } a ? (int?)decimal.ToInt32(a) : null;

            ApiResult<ExistingLocation> result = IsEdit
                ? await Locations.UpdateAsync(Location!.LocationId, new UpdateLocation
                {
                    Name = name, Description = Blank(_description), AddressText = Blank(_address),
                    Latitude = LatRounded, Longitude = LngRounded, AltitudeMeters = altitude,
                    TimeZoneId = _timeZone, Source = Source, SourceReference = reference,
                    Archived = Location.Archived is not null,
                })
                : await Locations.CreateAsync(new NewLocation
                {
                    Name = name, Description = Blank(_description), AddressText = Blank(_address),
                    Latitude = LatRounded, Longitude = LngRounded, AltitudeMeters = altitude,
                    TimeZoneId = _timeZone, Source = Source, SourceReference = reference,
                });

            var outcome = result.IsSuccess ? ApiResult.Success(result.Status) : ApiResult.Failure(result.Status, result.Problem!);
            if (!outcome.ToastOrFields(Snackbar, IsEdit ? "Unable to save location" : "Unable to create location", AssignServerError))
                return;

            // Close first: the host's follow-up (a list reload, the new row's highlight) should not hold the
            // dialog open.
            await OpenChanged.InvokeAsync(false);
            if (result.Value is { } saved)
                await OnSaved.InvokeAsync(saved);
        }
        finally
        {
            _saving = false;
        }
    }

    /// <summary>Places a server field error on its control; the 409 is keyed <c>name</c>.</summary>
    private bool AssignServerError(string field, string message)
    {
        var leaf = field[(field.LastIndexOf('.') + 1)..];
        string[] known = ["name", "description", "addressText", "latitude", "longitude", "altitudeMeters", "timeZoneId"];
        var key = known.FirstOrDefault(k => string.Equals(k, leaf, StringComparison.OrdinalIgnoreCase));
        if (key is null)
            return false;
        _errors[key] = message;
        return true;
    }
}
