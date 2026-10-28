using System.Net;
using Microsoft.AspNetCore.Components;
using Odyssey.ApiClient.Resources;
using Odyssey.Dtos.Journal;

namespace Odyssey.Client.Pages.Weather;

public partial class WeatherSettingsDialog
{
    [Inject] private IWeatherApiClient Weather { get; set; } = default!;
    [Inject] private ILocationsApiClient Locations { get; set; } = default!;

    [Parameter] public bool Open { get; set; }
    [Parameter] public EventCallback<bool> OpenChanged { get; set; }

    [Parameter, EditorRequired] public ExistingLocation Location { get; set; } = default!;

    /// <summary>The existing weather row; null turns weather on.</summary>
    [Parameter] public ExistingWeatherLocation? Row { get; set; }

    /// <summary>Places on a schedule now, this one included when it is.</summary>
    [Parameter] public int TrackedCount { get; set; }

    /// <summary><c>WeatherMaxTrackedLocations</c> as served by settings — never a client constant. Null when unreadable.</summary>
    [Parameter] public int? Cap { get; set; }

    [Parameter] public EventCallback<ExistingWeatherLocation> OnSaved { get; set; }

    private static readonly DateTime HistoryFloor = WeatherRules.HistoryFloor.ToDateTime(TimeOnly.MinValue);

    private bool _forecast;
    private bool _history;
    private bool _tracked;
    private DateTime? _historyFrom;
    private bool _saving;
    private string? _conflict;
    private string? _trackedError;
    private string? _historyError;

    /// <summary>The position shown and compared: the location as this dialog last read it.</summary>
    private ExistingLocation _preview = default!;

    private bool Creating => Row is null;

    private DateOnly DefaultFrom => DateOnly.FromDateTime(DateTime.Today).AddYears(-1);

    private int UsedElsewhere => TrackedCount - (Row is { IsTracked: true, ForecastEnabled: true } ? 1 : 0);

    private bool AtCap => Cap is { } cap && UsedElsewhere >= cap;

    private bool Archived => Location.Archived is not null;

    /// <summary>The household default's forecast cannot be turned off; an archived place's off half cannot be turned on.</summary>
    private bool ForecastLocked => Row is { IsHouseholdDefault: true } || (Archived && Row is { ForecastEnabled: false });

    private bool HistoryLocked => Archived && Row is { HistoryEnabled: false };

    /// <summary>Turning weather on with both halves off would create a row that fetches nothing.</summary>
    private bool NothingToTurnOn => Creating && !_forecast && !_history;

    private bool ReEnablingMoved =>
        Row is { ForecastEnabled: false, HistoryEnabled: false } row && (_forecast || _history) && WeatherVisuals.Moved(row, Location);

    private string ForecastHelp =>
        Row is { IsHouseholdDefault: true }
            ? "This is the household’s default weather location, so its forecast can’t be turned off. Choose another default in System settings → Weather first."
        : Archived && Row is { ForecastEnabled: false }
            ? "The location is archived. Restore it before turning the forecast on."
        : _forecast
            ? "MET Norway’s forecast is fetched for this place, and it can be picked on the dashboard."
            : "No forecast is fetched, and the place can’t be picked on the dashboard. Stored forecasts are kept.";

    private string HistoryToggleHelp =>
        Archived && Row is { HistoryEnabled: false }
            ? "The location is archived. Restore it before turning historical data on."
        : _history
            ? "Measured daily weather is fetched from MET’s Frost service and shown in the location’s Temperature history."
            : "No measured data is fetched. Everything already stored is kept until you delete it.";

    private string HistoryHelp => Creating
        ? $"Empty starts one year back, on {WeatherVisuals.Date(DefaultFrom)}. Earlier days are filled in from MET’s Frost service a year at a time."
        : "Measured days from MET’s Frost service go back to this date. Moving it earlier fills in more; moving it later deletes nothing.";

    protected override void OnInitialized()
    {
        _preview = Location;
        _forecast = Row?.ForecastEnabled ?? true;
        _history = Row?.HistoryEnabled ?? true;
        _tracked = Row?.IsTracked ?? !(Cap is { } cap && TrackedCount >= cap);
        _historyFrom = Row?.HistoryFrom.ToDateTime(TimeOnly.MinValue);
    }

    private void OnTrackedChanged(bool value)
    {
        _tracked = value;
        _trackedError = null;
    }

    // The dialog body: focus moves here before a save disables Save and Cancel, so keyboard focus stays inside
    // the dialog instead of dropping to <body> (WCAG 2.4.3) — the WeatherPurgeDialog pattern.
    private ElementReference _body;

    private async Task SaveAsync()
    {
        if (_saving || NothingToTurnOn)
            return;
        try { await _body.FocusAsync(); }
        catch (InvalidOperationException) { /* not rendered (prerender / teardown) */ }
        catch (Microsoft.JSInterop.JSException) { /* the element went away first */ }

        _saving = true;
        _conflict = null;
        _trackedError = null;
        _historyError = null;

        var result = await Weather.UpsertLocationAsync(Location.LocationId, new UpsertWeatherLocation
        {
            ForecastEnabled = _forecast,
            HistoryEnabled = _history,
            IsTracked = _tracked,
            HistoryFrom = _historyFrom is { } from ? DateOnly.FromDateTime(from) : null,
            ExpectedLocationFingerprint = Creating ? WeatherVisuals.Fingerprint(_preview) : null,
        });
        if (result.IsSuccess && result.Value is { } saved)
        {
            // Still "saving" until the dialog has closed, so a second click cannot submit again.
            await CloseAsync();
            await OnSaved.InvokeAsync(saved);
            return;
        }

        _saving = false;

        var problem = result.Problem;
        if (result.Status == HttpStatusCode.Conflict && problem?.Code == "location-changed")
        {
            // Re-read so the card shows the position the server compared against; the next save sends it.
            if ((await Locations.GetAsync(Location.LocationId)).Value is { } fresh)
                _preview = fresh;
            _conflict = "The location’s position changed while this dialog was open. Check the position below, then turn weather on again.";
        }
        else if (problem?.ErrorFor("isTracked") is { } tracked)
        {
            _trackedError = tracked;
        }
        else if (problem?.ErrorFor("historyFrom") is { } history)
        {
            _historyError = history;
        }
        else
        {
            _conflict = problem?.Message ?? "Weather settings couldn’t be saved. Try again.";
        }
    }

    private Task CloseAsync() => OpenChanged.InvokeAsync(false);

    /// <summary>The user's way out; refused while a save is in flight.</summary>
    private Task CancelAsync() => _saving ? Task.CompletedTask : CloseAsync();

    private Task OnOpenChanged(bool open) => open || !_saving ? OpenChanged.InvokeAsync(open) : Task.CompletedTask;
}
