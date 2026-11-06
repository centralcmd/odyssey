using System.Net;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Odyssey.ApiClient;
using Odyssey.ApiClient.Resources;
using Odyssey.Client.Authorization;
using Odyssey.Client.Components;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos.Journal;

namespace Odyssey.Client.Pages.Weather;

public partial class WeatherWidget
{
    [Inject] private IWeatherApiClient Weather { get; set; } = default!;
    [Inject] private AuthenticationStateProvider Auth { get; set; } = default!;
    [Inject] private NavigationManager Nav { get; set; } = default!;
    [Inject] private Odyssey.Client.Services.IWeatherPointOptionsCache OptionsCache { get; set; } = default!;
    [Inject] private IServiceProvider Services { get; set; } = default!;

    /// <summary>The registered clock, if any: the weather reads fall back to the system clock in a host that registers none.</summary>
    private TimeProvider? ClockOrNull => Services.GetService(typeof(TimeProvider)) as TimeProvider;

    /// <summary>The card's own cache for the hours of a day (issue #415); the card reads current and daily itself.</summary>
    private WeatherPointReads _hoursReads = default!;

    internal enum Phase { Loading, Ready, RateLimited, Error }

    /// <summary>Up to nine days from today, as the design shows.</summary>
    internal const int DaysShown = WeatherForecastBody.DaysShown;

    private Phase _phase = Phase.Loading;
    private WeatherCurrentResponse? _current;
    private IReadOnlyList<WeatherDaySummary> _days = [];
    private WeatherPointOptions _options = new();
    private WeatherPreferences _preferences = new();
    private WeatherPointRef? _resolved;
    private bool _saving;
    private string? _prefError;
    private string _announce = "";
    private bool _canSetUp;
    private bool _canOpenSettings;
    private bool _canChoose;

    private static DateOnly ViewerToday => DateOnly.FromDateTime(LocalNow());

    /// <summary>The viewer's clock, as a seam for bUnit, so a run that straddles midnight cannot disagree with itself.</summary>
    internal static Func<DateTime> LocalNow = static () => DateTime.Now;

    /// <summary>The interactivity check, as a seam for bUnit (the <c>Settings.InteractiveCheck</c> pattern).</summary>
    internal static Func<bool> InteractiveCheck = OperatingSystem.IsBrowser;

    protected override async Task OnInitializedAsync()
    {
        _hoursReads = new WeatherPointReads(Weather, ClockOrNull);
        if (!InteractiveCheck())
            return;

        var user = await Auth.GetUserAsync();
        // The set-up link opens /weather-points, which needs read as well as update (issue #388 §5).
        _canSetUp = user.HasPermission(PermissionClaims.WeatherPointsRead) && user.HasPermission(PermissionClaims.WeatherPointsUpdate);
        _canOpenSettings = user.HasPermission(PermissionClaims.SystemSettingsUpdate);
        _canChoose = user.HasPermission(PermissionClaims.WeatherPreferencesUpdate);
        await LoadAsync();
    }

    /// <param name="quiet">
    /// Reload in place, keeping the current body and the picker mounted — used after the picker saves, so
    /// the control the viewer is on is not unmounted under their focus (WCAG 2.4.3).
    /// </param>
    private async Task LoadAsync(bool quiet = false)
    {
        if (!quiet)
        {
            _phase = Phase.Loading;
            _announce = "Loading weather.";
        }

        StateHasChanged();

        // The options and the caller's own place feed only the picker: someone who cannot choose never needs them.
        var options = _canChoose ? OptionsCache.GetAsync() : Task.FromResult(ApiResult<WeatherPointOptions>.Success(new WeatherPointOptions(), HttpStatusCode.OK));
        var preferences = _canChoose ? Weather.PreferencesAsync() : Task.FromResult(ApiResult<WeatherPreferences>.Success(new WeatherPreferences(), HttpStatusCode.OK));
        var current = Weather.CurrentAsync();
        // Yesterday for the measured line, then the days shown. The dates are the place's own local dates;
        // the viewer's today is close enough to choose the window, and the server omits days it lacks.
        // One day of slack each side, because the place's date may differ from the viewer's by one.
        var daily = Weather.DailyAsync(ViewerToday.AddDays(-2), ViewerToday.AddDays(DaysShown));
        await Task.WhenAll(options, preferences, current, daily);

        var currentResult = await current;
        if ((!currentResult.IsSuccess || currentResult.Value is null) && quiet && _phase == Phase.Ready)
        {
            // A quiet reload after a picker save keeps the body and the picker the viewer is on (WCAG 2.4.3):
            // the choice is already saved, so the failure is reported beside the picker instead of replacing it.
            _prefError = "Your choice is saved, but the weather couldn’t be reloaded. It shows next time the page loads.";
            StateHasChanged();
            return;
        }

        if (!currentResult.IsSuccess || currentResult.Value is null)
        {
            _phase = currentResult.Status == HttpStatusCode.TooManyRequests ? Phase.RateLimited : Phase.Error;
            _announce = _phase == Phase.RateLimited ? "Weather is resting for a moment." : "Weather couldn't be loaded.";
            StateHasChanged();
            return;
        }

        _current = currentResult.Value;
        _resolved = _current.Point;
        _options = (await options).Value ?? new WeatherPointOptions();
        _preferences = (await preferences).Value ?? new WeatherPreferences();
        var dailyResult = await daily;
        _days = dailyResult.IsSuccess && dailyResult.Value is { } d && d.Point?.WeatherPointId == _resolved?.WeatherPointId ? d.Days : [];
        _phase = Phase.Ready;
        _announce = _current.Status == WeatherStatus.Ok && _resolved is { } place
            ? _current.Stale ? $"Weather for {place.Name} is not up to date: MET Norway didn't answer the last refresh." : $"Weather for {place.Name} loaded."
            : "";
        StateHasChanged();
    }

    // ── Picker ─────────────────────────────────────────────────────────────────────────────────────

    private WeatherPointOption? HouseholdDefault => WeatherVisuals.HouseholdDefault(_options);

    /// <summary>Two or more selectable places, and a state with a forecast to show — otherwise the name rides in the title.</summary>
    private bool ShowPicker =>
        _canChoose && _phase == Phase.Ready && WeatherVisuals.Selectable(_options).Count() > 1
        && _current?.Status is WeatherStatus.Ok or WeatherStatus.NoData;

    /// <summary>With no picker, the place follows the title: "Weather · Home".</summary>
    private string? TitlePlace => _resolved is { } place && !ShowPicker ? $" · {place.Name}" : null;

    private string PickerValue => WeatherVisuals.PickerValue(_options, _preferences.DefaultPoint);

    private IReadOnlyList<OdsOption> PickerOptions => WeatherVisuals.PickerOptions(_options);

    /// <summary>Saves the caller's own default; the empty choice follows the household default.</summary>
    private async Task ChooseAsync(string value)
    {
        if (_saving)
            return;

        Guid? next = Guid.TryParse(value, out var id) ? id : null;
        _saving = true;
        _prefError = null;
        StateHasChanged();

        var result = await Weather.SetPreferenceAsync(next);
        if (!result.IsSuccess)
        {
            _saving = false;
            _prefError = result.Status == HttpStatusCode.BadRequest
                ? "That weather point no longer has a forecast. Choose another one."
                : "Your choice couldn't be saved. Try again.";
            StateHasChanged();
            return;
        }

        var name = next is { } chosen ? WeatherVisuals.Selectable(_options).FirstOrDefault(p => p.WeatherPointId == chosen)?.Name : HouseholdDefault?.Name;
        await LoadAsync(quiet: true);
        _saving = false;
        // A failed reload has already said the choice is saved but not shown; announcing the new place as
        // shown would contradict it.
        _announce = _prefError is not null ? _prefError
            : next is not null
                ? $"Weather now shows {name}. This is saved for you only."
                : $"Weather follows the household default{(name is not null ? $", {name}" : "")}.";
        StateHasChanged();
    }

    /// <summary>Awaited, and ignored while a load is already running, so a double press cannot start two.</summary>
    private Task Retry() => _phase == Phase.Loading ? Task.CompletedTask : LoadAsync();

    private void GoTo(string path) => Nav.NavigateTo(path);

    // ── Body ───────────────────────────────────────────────────────────────────────────────────────

    private RenderFragment Body => _phase switch
    {
        Phase.Loading => Skeleton,
        Phase.Error => Empty("cloud_off", "Weather couldn’t be loaded",
            "Something went wrong reaching Odyssey. Your other data is unaffected.", RetryButton),
        Phase.RateLimited => Empty("hourglass_bottom", "Weather is resting for a moment",
            "Weather has been loaded many times in the last minute. Wait a moment, then try again.", RetryButton),
        _ => ReadyBody,
    };

    private RenderFragment ReadyBody => _current!.Status switch
    {
        WeatherStatus.NotConfigured => _canOpenSettings
            ? Empty("settings", "Weather isn’t fetched yet",
                "MET Norway asks every client for a contact address. Add one for this instance in System settings → Weather, and forecasts start within a few minutes.",
                ActionButton("settings", "Open System settings", "/settings"))
            : Empty("settings", "Weather isn’t set up yet", "An administrator needs to finish setting up weather for this household.", null),
        WeatherStatus.NoPlace => _canSetUp
            ? Empty("add_location_alt", "No weather point has a forecast yet",
                "Create a weather point for the household — home, the cabin — with its forecast on, and it shows here.",
                ActionButton("wb_sunny", "Go to Weather points", "/weather-points"))
            : Empty("add_location_alt", "No weather point has a forecast yet", "An administrator can create a weather point for the household.", null),
        WeatherStatus.Ok when _current.Step is not null => Forecast,
        _ => Empty("cloud_queue", $"No forecast for {_resolved?.Name ?? "this point"} yet",
            "MET Norway hasn’t answered for this point yet. Odyssey keeps trying on its own — check back in a little while.", null),
    };

    private RenderFragment RetryButton => builder =>
    {
        builder.OpenComponent<OdsButton>(0);
        builder.AddComponentParameter(1, nameof(OdsButton.Variant), OdsButtonVariant.Outlined);
        builder.AddComponentParameter(2, nameof(OdsButton.Icon), "refresh");
        builder.AddComponentParameter(3, nameof(OdsButton.OnClick), EventCallback.Factory.Create<Microsoft.AspNetCore.Components.Web.MouseEventArgs>(this, Retry));
        builder.AddComponentParameter(4, nameof(OdsButton.ChildContent), (RenderFragment)(b => b.AddContent(0, "Try again")));
        builder.CloseComponent();
    };

    private RenderFragment ActionButton(string icon, string label, string path) => builder =>
    {
        builder.OpenComponent<OdsButton>(0);
        builder.AddComponentParameter(1, nameof(OdsButton.Variant), OdsButtonVariant.Outlined);
        builder.AddComponentParameter(2, nameof(OdsButton.Icon), icon);
        builder.AddComponentParameter(3, nameof(OdsButton.OnClick), EventCallback.Factory.Create<Microsoft.AspNetCore.Components.Web.MouseEventArgs>(this, () => GoTo(path)));
        builder.AddComponentParameter(4, nameof(OdsButton.ChildContent), (RenderFragment)(b => b.AddContent(0, label)));
        builder.CloseComponent();
    };

    // ── Forecast data ──────────────────────────────────────────────────────────────────────────────

    internal static string DaySentence(WeatherDaySummary day) => WeatherForecastBody.DaySentence(day);
}
