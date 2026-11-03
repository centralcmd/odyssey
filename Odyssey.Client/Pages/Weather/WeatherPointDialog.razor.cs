using System.Net;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using Odyssey.ApiClient;
using Odyssey.ApiClient.Resources;
using Odyssey.Client.Components;
using Odyssey.Client.Pages.Journal;
using Odyssey.Dtos;
using Odyssey.Dtos.Journal;

namespace Odyssey.Client.Pages.Weather;

public partial class WeatherPointDialog
{
    [Inject] private IWeatherApiClient Weather { get; set; } = default!;
    [Inject] private ILocationsApiClient Locations { get; set; } = default!;
    [Inject] private IJSRuntime Js { get; set; } = default!;

    [Parameter] public bool Open { get; set; }
    [Parameter] public EventCallback<bool> OpenChanged { get; set; }

    /// <summary>The point being edited; null creates one.</summary>
    [Parameter] public ExistingWeatherPoint? Point { get; set; }

    /// <summary>Points on a schedule now, this one included when it is.</summary>
    [Parameter] public int TrackedCount { get; set; }

    /// <summary><c>WeatherMaxTrackedPoints</c> as served by settings — never a client constant. Null when unreadable.</summary>
    [Parameter] public int? Cap { get; set; }

    /// <summary><c>locations.read</c>: the <i>From a location</i> source. Without it that source is not offered.</summary>
    [Parameter] public bool CanReadLocations { get; set; }

    /// <summary>
    /// <c>locations.create</c>, which <c>POST /api/locations/search</c> requires: the <i>Search address</i> source.
    /// Create only — the settings dialog edits the coordinates in place.
    /// </summary>
    [Parameter] public bool CanSearchLocations { get; set; }

    [Parameter] public string? ReturnFocusToId { get; set; }

    /// <summary>A created point (<c>201</c>); <c>FromLocation</c> says whether it copied a place.</summary>
    [Parameter] public EventCallback<(ExistingWeatherPoint Point, bool FromLocation)> OnCreated { get; set; }

    /// <summary>A saved point (<c>200</c>), its advisories, and whether the position moved.</summary>
    [Parameter] public EventCallback<(WeatherPointUpdateResult Result, bool Moved, bool WasOn)> OnUpdated { get; set; }

    /// <summary><c>409 purge-in-progress</c>'s way out: the page opens the delete dialog.</summary>
    [Parameter] public EventCallback OnContinueDeleting { get; set; }

    /// <summary>Every status the dialog's own point calls answered — the page hands a 403 to the claims refresher.</summary>
    [Parameter] public EventCallback<HttpStatusCode> OnStatus { get; set; }

    /// <summary>The source picker's debounce; a test passes zero.</summary>
    [Parameter] public int LocationDebounceMs { get; set; } = 250;

    /// <summary>The address search's debounce — the location dialog's 350 ms; a test passes zero.</summary>
    [Parameter] public int SearchDebounceMs { get; set; } = 350;

    internal const string FromLocationMode = "location";
    internal const string CoordinatesMode = "coords";
    internal const string SearchMode = "search";

    private static readonly DateTime HistoryFloor = WeatherRules.HistoryFloor.ToDateTime(TimeOnly.MinValue);

    private static readonly OdsCardSelectOption FromLocationOption =
        new() { Value = FromLocationMode, Icon = "place", Label = "From a location", Sub = "Copy a saved place’s position" };

    private static readonly OdsCardSelectOption CoordinatesOption =
        new() { Value = CoordinatesMode, Icon = "my_location", Label = "Enter coordinates", Sub = "Type latitude and longitude" };

    private static readonly OdsCardSelectOption SearchOption =
        new() { Value = SearchMode, Icon = "search", Label = "Search address", Sub = "Find a place with Kartverket" };

    /// <summary>Each source shows only to a caller who can use it; with coordinates alone, no picker is shown.</summary>
    private IReadOnlyList<OdsCardSelectOption> ModeOptions =>
    [
        .. CanReadLocations ? new[] { FromLocationOption } : [],
        CoordinatesOption,
        .. CanSearchLocations ? new[] { SearchOption } : [],
    ];

    private readonly string _uid = Guid.NewGuid().ToString("N")[..8];
    private readonly Dictionary<string, string> _errors = new(StringComparer.OrdinalIgnoreCase);
    private string _mode = FromLocationMode;
    private LocationPick? _pick;
    private ExistingLocation? _source;
    private LocationSearchRegion _region = LocationSearchRegion.Norway;
    private LocationCandidatePick? _candidate;
    private bool _noEligible;
    private string _name = "";
    private bool _nameTouched;
    private decimal? _lat;
    private decimal? _lng;
    private decimal? _alt;
    private string? _tz;
    private bool _forecast;
    private bool _history;
    private bool _tracked;
    private DateTime? _historyFrom;
    private bool _saving;
    private bool _reloading;
    private bool _stale;
    private bool _purgeBlocked;
    private bool _submitted;
    private string? _conflict;
    private string? _trackedError;
    private string? _note;
    private string _announce = "";

    private bool Creating => Point is null;

    private bool FromLocation => Creating && CanReadLocations && _mode == FromLocationMode;

    private bool Searching => Creating && CanSearchLocations && _mode == SearchMode;

    private string SummaryId => $"wp-sum-{_uid}";
    private string NameId => $"wp-name-{_uid}";
    private string FromId => $"wp-from-{_uid}";
    private string SearchId => $"wp-search-{_uid}";
    private string PickedId => $"wp-picked-{_uid}";
    private string CoordsId => $"wp-coords-{_uid}";
    private string AltId => $"wp-alt-{_uid}";
    private string TimeZoneId => $"wp-tz-{_uid}";

    private IReadOnlyDictionary<string, string> ErrorLinks => new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["name"] = NameId,
        ["fromLocationId"] = FromId,
        ["search"] = SearchId,
        ["latitude"] = CoordsId,
        ["altitudeMeters"] = AltId,
        ["timeZoneId"] = TimeZoneId,
    };

    private static readonly string[] FieldOrder = ["fromLocationId", "search", "latitude", "altitudeMeters", "timeZoneId", "name", "historyFrom"];

    /// <summary>Errors render after the first submit attempt, in field order.</summary>
    private IReadOnlyList<KeyValuePair<string, string>> SummaryErrors => !_submitted ? [] :
        [.. _errors.OrderBy(e => Array.IndexOf(FieldOrder, e.Key) is var i && i < 0 ? int.MaxValue : i)];

    private string? Err(string key) => _submitted && _errors.TryGetValue(key, out var message) ? message : null;

    private int? Altitude => _alt is { } a ? (int)Math.Round(a) : null;

    private (bool Coordinates, bool Altitude, bool TimeZone) Changes =>
        Point is null ? default : WeatherVisuals.Changes(Point.Site, _lat, _lng, Altitude, _tz);

    private bool Moved => Point is not null && _lat is not null && _lng is not null && !string.IsNullOrEmpty(_tz)
        && (Changes.Coordinates || Changes.Altitude || Changes.TimeZone);

    private DateOnly DefaultFrom => DateOnly.FromDateTime(DateTime.Today).AddYears(-1);

    private int UsedElsewhere => TrackedCount - (Point is { IsTracked: true, ForecastEnabled: true } ? 1 : 0);

    private bool AtCap => Cap is { } cap && UsedElsewhere >= cap;

    private bool NothingToTurnOn => Creating && !_forecast && !_history;

    private IReadOnlyList<OdsOption> TimeZoneOptions
    {
        get
        {
            var now = DateTime.UtcNow;
            var options = LocationVisuals.TimeZoneIds
                .Select(id => new OdsOption(id, id) { Note = LocationVisuals.UtcOffset(id, now) })
                .ToList();
            if (!string.IsNullOrEmpty(_tz) && !options.Any(o => o.Value == _tz))
                options.Insert(0, new OdsOption(_tz, _tz) { Note = "not recognised here" });
            return options;
        }
    }

    /// <summary>A picked candidate with no time zone leaves the field empty, as Enter coordinates does, and asks for one.</summary>
    private string TimeZoneHelp => Searching && _candidate is { Candidate.TimeZoneId: null } picked && string.IsNullOrEmpty(_tz)
        ? $"{LocationVisuals.SourceOf(picked.Candidate.Source).Label} didn’t return a time zone Odyssey recognises. Choose the one this place keeps."
        : "Weather days are dated in this zone.";

    private static string PickedMeta(LocationCandidate candidate) =>
        string.Join(" · ", new[] { candidate.Description, LocationVisuals.KindOf(candidate.Kind).Label }.Where(s => !string.IsNullOrEmpty(s)));

    private string ForecastHelp =>
        Point is { IsHouseholdDefault: true }
            ? "This is the household’s default weather point, so its forecast can’t be turned off. Choose another default in System settings → Weather first."
        : _forecast
            ? "MET Norway’s forecast is fetched for this point; it can be picked on the dashboard, and linked places show it."
            : "No forecast is fetched, the point can’t be picked on the dashboard, and linked places show only stored history. Stored forecasts are kept.";

    private string HistoryToggleHelp => _history
        ? "Measured daily weather is fetched from MET’s Frost service and shown in the point’s Weather history and on linked places."
        : "No measured data is fetched. Everything already stored is kept until you delete it.";

    private string HistoryHelp => Creating
        ? $"Empty starts one year back, on {WeatherVisuals.Date(DefaultFrom)}. Earlier days are filled in from MET’s Frost service a year at a time."
        : "Measured days from MET’s Frost service go back to this date. Moving it earlier fills in more; moving it later deletes nothing.";

    protected override async Task OnInitializedAsync()
    {
        if (Point is { } p)
        {
            ApplyPoint(p, keepEdits: false);
        }
        else
        {
            _mode = CanReadLocations ? FromLocationMode : CoordinatesMode;
            _forecast = true;
            _history = true;
            _tracked = !(Cap is { } cap && TrackedCount >= cap);
            if (CanReadLocations)
            {
                // With no eligible place at all, the source field says so up front (issue #388 AC 30).
                var any = await Locations.ListAsync(1, 1, statuses: [nameof(ArchivalStatus.Active)], hasCoordinates: true);
                _noEligible = any.IsSuccess && any.Value is { TotalCount: 0 };
            }
        }
    }

    private void ApplyPoint(ExistingWeatherPoint p, bool keepEdits)
    {
        if (keepEdits)
            return;
        _name = p.Name;
        _lat = p.Site.Latitude;
        _lng = p.Site.Longitude;
        _alt = p.Site.AltitudeMeters;
        _tz = p.Site.TimeZoneId;
        _forecast = p.ForecastEnabled;
        _history = p.HistoryEnabled;
        _tracked = p.IsTracked;
        _historyFrom = p.HistoryFrom.ToDateTime(TimeOnly.MinValue);
    }

    private void OnModeChanged(string mode)
    {
        _mode = mode;
        _errors.Clear();
        _conflict = null;
    }

    private void OnNameChanged(string? value)
    {
        _name = value ?? "";
        _nameTouched = true;
        _errors.Remove("name");
    }

    private void OnTrackedChanged(bool value)
    {
        _tracked = value;
        _trackedError = null;
    }

    private async Task OnPickedAsync(LocationPick? pick)
    {
        _pick = pick;
        _errors.Remove("fromLocationId");
        _conflict = null;
        _source = null;
        if (pick?.Id is not { } id)
            return;
        var result = await Locations.GetAsync(id);
        if (result.Value is not { } place || _pick?.Id != id)
            return;
        _source = place;
        if (!_nameTouched)
            _name = place.Name;
        if (!WeatherVisuals.HasPosition(place))
        {
            _submitted = true;
            _errors["fromLocationId"] = FromLocationUnusable;
        }
    }

    /// <summary>
    /// A search result fills the coordinates fields — every one stays editable — and the create is the coordinates
    /// one. Name follows the pick until the Admin types their own.
    /// </summary>
    private async Task OnCandidatePickedAsync(LocationCandidatePick picked)
    {
        var c = picked.Candidate;
        _candidate = picked;
        if (!_nameTouched)
            _name = c.Name.Length > WeatherRules.PointNameMaxLength ? c.Name[..WeatherRules.PointNameMaxLength] : c.Name;
        _lat = WeatherRules.RoundCoordinate(c.Latitude);
        _lng = WeatherRules.RoundCoordinate(c.Longitude);
        _alt = c.AltitudeMeters;
        _tz = c.TimeZoneId;
        _errors.Clear();
        _announce = $"{c.Name} picked. Check the position, then create the weather point.";
        await FocusAsync(PickedId);
    }

    /// <summary>Back to the search, which takes focus again; the prefilled fields wait until the next pick.</summary>
    private void SearchAgain()
    {
        _candidate = null;
        _errors.Remove("search");
    }

    internal const string FromLocationUnusable = "This place has no coordinates or time zone. Add them on the Locations page first.";

    /// <summary>Client checks mirror the server's (issue #388 §8); every bound is the shared constant, never a literal.</summary>
    private void Validate()
    {
        _errors.Clear();
        var name = _name.Trim();
        if (name.Length == 0 && !FromLocation)
            _errors["name"] = "Enter a name for the weather point.";
        else if (name.Length > WeatherRules.PointNameMaxLength)
            _errors["name"] = $"Use at most {WeatherRules.PointNameMaxLength} characters.";

        if (FromLocation)
        {
            if (_source is null)
                _errors["fromLocationId"] = "Choose the place to copy the position from.";
            else if (!WeatherVisuals.HasPosition(_source))
                _errors["fromLocationId"] = FromLocationUnusable;
        }
        else if (Searching && _candidate is null)
        {
            _errors["search"] = SearchNotPicked;
        }
        else
        {
            if (_lat is null || _lng is null)
                _errors["latitude"] = "Enter both latitude and longitude.";
            else if (_lat < (decimal)WeatherRules.LatitudeMin || _lat > (decimal)WeatherRules.LatitudeMax
                || _lng < (decimal)WeatherRules.LongitudeMin || _lng > (decimal)WeatherRules.LongitudeMax)
                _errors["latitude"] = $"Latitude is {WeatherRules.LatitudeMin}…{WeatherRules.LatitudeMax} and longitude {WeatherRules.LongitudeMin}…{WeatherRules.LongitudeMax}.";
            if (_alt is { } alt && (alt < LocationRules.AltitudeMin || alt > LocationRules.AltitudeMax))
                _errors["altitudeMeters"] = $"Altitude must be between {LocationRules.AltitudeMin} and {LocationRules.AltitudeMax:N0} m.";
            if (string.IsNullOrWhiteSpace(_tz))
                _errors["timeZoneId"] = "Choose the time zone.";
        }
    }

    internal const string SearchNotPicked = "Search for the place, then pick a result.";

    private async Task SaveAsync()
    {
        if (_saving || NothingToTurnOn || _stale || _purgeBlocked)
            return;

        _submitted = true;
        _conflict = null;
        _trackedError = null;
        _note = null;
        Validate();
        if (_errors.Count > 0)
        {
            await FocusAsync(SummaryId);
            return;
        }

        _saving = true;
        StateHasChanged();
        try
        {
            if (Creating)
                await CreateAsync();
            else
                await UpdateAsync();
        }
        finally
        {
            _saving = false;
        }
    }

    private DateOnly? HistoryFromValue => _historyFrom is { } from ? DateOnly.FromDateTime(from) : null;

    private async Task CreateAsync()
    {
        var fromLocation = FromLocation;
        var request = fromLocation
            ? new NewWeatherPoint
            {
                Name = string.IsNullOrWhiteSpace(_name) ? null : _name.Trim(),
                FromLocationId = _source!.LocationId,
                ExpectedLocationFingerprint = _source.WeatherFingerprint ?? WeatherVisuals.Fingerprint(_source),
            }
            : new NewWeatherPoint
            {
                Name = _name.Trim(),
                Latitude = _lat,
                Longitude = _lng,
                AltitudeMeters = Altitude,
                TimeZoneId = _tz,
            };
        request.ForecastEnabled = _forecast;
        request.HistoryEnabled = _history;
        request.IsTracked = _forecast && _tracked;
        request.HistoryFrom = _history ? HistoryFromValue : null;

        var result = await Weather.CreatePointAsync(request);
        await OnStatus.InvokeAsync(result.Status);
        if (result.IsSuccess && result.Value is { } created)
        {
            await OpenChanged.InvokeAsync(false);
            await OnCreated.InvokeAsync((created, fromLocation));
            return;
        }

        if (result.Status == HttpStatusCode.Conflict && result.Problem?.Code == "location-changed" && _source is not null)
        {
            // Re-read so the card shows the position the server compared against; the next save sends it.
            if ((await Locations.GetAsync(_source.LocationId)).Value is { } fresh)
                _source = fresh;
            _conflict = "The location’s position changed while this dialog was open. Check the position below, then create the weather point again.";
            _announce = _conflict;
            return;
        }

        await ShowProblemAsync(result.Status, result.Problem);
    }

    private async Task UpdateAsync()
    {
        var point = Point!;
        var moved = Moved;
        var wasOn = point.ForecastEnabled || point.HistoryEnabled;
        var result = await Weather.UpdatePointAsync(point.WeatherPointId, new UpdateWeatherPoint
        {
            Name = _name.Trim(),
            Latitude = _lat,
            Longitude = _lng,
            AltitudeMeters = Altitude,
            TimeZoneId = _tz,
            ForecastEnabled = _forecast,
            HistoryEnabled = _history,
            IsTracked = _forecast && _tracked,
            HistoryFrom = _history ? HistoryFromValue : null,
            SettingsVersion = point.SettingsVersion,
        });
        await OnStatus.InvokeAsync(result.Status);
        if (result.IsSuccess && result.Value is { } saved)
        {
            await OpenChanged.InvokeAsync(false);
            await OnUpdated.InvokeAsync((saved, moved, wasOn));
            return;
        }

        if (result.Status == HttpStatusCode.Conflict && result.Problem?.Code == "stale")
        {
            _stale = true;
            _announce = "Someone else changed this weather point. Reload to see their changes.";
            return;
        }

        if (result.Status == HttpStatusCode.Conflict && result.Problem?.Code == "purge-in-progress")
        {
            _purgeBlocked = true;
            _announce = "Deleting this point’s history was interrupted. Finish deleting first.";
            return;
        }

        await ShowProblemAsync(result.Status, result.Problem);
    }

    /// <summary>Server validation lands in the same places as the client's, keyed by the server's field names.</summary>
    private async Task ShowProblemAsync(HttpStatusCode status, ApiProblem? problem)
    {
        if (problem?.ErrorFor("isTracked") is { } tracked)
        {
            _trackedError = Cap is { } cap
                ? $"{cap} of {cap} scheduled points are in use. Turn scheduling off for another point, or raise the limit in System settings → Weather."
                : tracked;
            _announce = _trackedError;
            return;
        }

        if (status == HttpStatusCode.Conflict && problem?.Code == "duplicate-name")
        {
            var name = string.IsNullOrWhiteSpace(_name) ? _source?.Name ?? "" : _name.Trim();
            _errors["name"] = $"Another weather point is already called {name}.";
            await FocusAsync(SummaryId);
            return;
        }

        if (status == HttpStatusCode.UnprocessableEntity && problem?.ErrorFor("fromLocationId") is not null)
        {
            _errors["fromLocationId"] = FromLocationUnusable;
            await FocusAsync(SummaryId);
            return;
        }

        var any = false;
        foreach (var key in new[] { "name", "fromLocationId", "latitude", "longitude", "altitudeMeters", "timeZoneId", "historyFrom", "expectedLocationFingerprint" })
        {
            if (problem?.ErrorFor(key) is { } message)
            {
                _errors[key == "longitude" ? "latitude" : key == "expectedLocationFingerprint" ? "fromLocationId" : key] = message;
                any = true;
            }
        }

        if (any)
        {
            await FocusAsync(SummaryId);
            return;
        }

        _conflict = status == HttpStatusCode.TooManyRequests
            ? "Weather actions have been used many times in the last few minutes. Wait a moment, then try again."
            : problem?.Message ?? "The weather point couldn’t be saved. Try again.";
        _announce = _conflict;
    }

    /// <summary>Reload after <c>409 stale</c>: their changes come in, the user's edits are kept, focus goes to Name.</summary>
    private async Task ReloadAsync()
    {
        if (Point is null || _reloading)
            return;
        _reloading = true;
        var result = await Weather.PointAsync(Point.WeatherPointId);
        await OnStatus.InvokeAsync(result.Status);
        _reloading = false;
        if (result.Value is not { } fresh)
        {
            _conflict = result.Problem?.Message ?? "The weather point couldn’t be reloaded. Try again.";
            return;
        }

        Point = fresh;
        _stale = false;
        _note = "Reloaded. Their changes are in, and your edits are kept — check them, then save again.";
        _announce = _note;
        await FocusAsync(NameId);
    }

    private async Task ContinueDeletingAsync()
    {
        await OpenChanged.InvokeAsync(false);
        await OnContinueDeleting.InvokeAsync();
    }

    private async Task FocusAsync(string id)
    {
        StateHasChanged();
        await Task.Yield();
        try { await Js.InvokeVoidAsync("odsFocusById", id); }
        catch (JSException) { }
        catch (InvalidOperationException) { }
    }

    private Task CancelAsync() => _saving ? Task.CompletedTask : OpenChanged.InvokeAsync(false);

    private Task OnOpenChanged(bool open) => open || !_saving ? OpenChanged.InvokeAsync(open) : Task.CompletedTask;
}
