using System.Net;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using Odyssey.ApiClient.Resources;
using Odyssey.Client.Authorization;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos.Journal;

namespace Odyssey.Client.Pages.Weather;

public partial class CalendarEventWeatherSection : IAsyncDisposable
{
    [Inject] private IWeatherApiClient WeatherApi { get; set; } = default!;
    [Inject] private AuthenticationStateProvider Auth { get; set; } = default!;
    [Inject] private IJSRuntime JS { get; set; } = default!;
    [Inject] private ILogger<CalendarEventWeatherSection> Log { get; set; } = default!;

    [Parameter, EditorRequired] public Guid EventId { get; set; }

    /// <summary>The event's title as loaded, for the table caption — rendered as encoded text only.</summary>
    [Parameter] public string EventTitle { get; set; } = string.Empty;

    /// <summary>The LOADED event's start and end, never the form's draft: the weather is for what is saved.</summary>
    [Parameter] public DateTime Start { get; set; }

    [Parameter] public DateTime End { get; set; }

    /// <summary>The date, time, all-day or place fields differ from the loaded event (issue #363 §3.4).</summary>
    [Parameter] public bool Dirty { get; set; }

    /// <summary>A plain activation of an action link: the host closes the dialog; navigation proceeds.</summary>
    [Parameter] public EventCallback OnNavigate { get; set; }

    /// <summary>For a test: the instant "past" is measured against.</summary>
    [Parameter] public DateTime? UtcNow { get; set; }

    internal enum Phase { Loading, Ready, RateLimited, Forbidden, NotFound, Error }

    private static int _instances;
    private readonly string _id = $"wxev{Interlocked.Increment(ref _instances)}";
    private Phase _phase = Phase.Loading;
    private CalendarEventWeatherResponse? _response;
    private Guid? _loadedFor;
    private CancellationTokenSource? _load;
    private CancellationTokenSource? _coolDown;
    private bool _inFlight;
    private bool _canRead;
    private bool _canReadPoints;
    private bool _canUpdatePoints;
    private bool _canReadSettings;
    private bool _subscribed;
    private IJSObjectReference? _module;

    private string LabelId => $"{_id}-label";

    private bool RetryEnabled => !_inFlight && _coolDown is null;

    private bool Past => WeatherEventText.IsPast(End, UtcNow ?? DateTime.UtcNow);

    private int SpanDays => Math.Max(1, DateOnly.FromDateTime(End).DayNumber - DateOnly.FromDateTime(Start).DayNumber);

    protected override async Task OnParametersSetAsync()
    {
        if (!_subscribed)
        {
            _subscribed = true;
            Auth.AuthenticationStateChanged += OnAuthenticationStateChanged;
            ReadClaims(await Auth.GetAuthenticationStateAsync());
        }

        if (_canRead && _loadedFor != EventId)
        {
            _loadedFor = EventId;
            _ = LoadAsync();
        }
    }

    private void ReadClaims(AuthenticationState state)
    {
        var user = state.User;
        _canRead = user.HasPermission(PermissionClaims.WeatherRead);
        _canReadPoints = user.HasPermission(PermissionClaims.WeatherPointsRead);
        _canUpdatePoints = user.HasPermission(PermissionClaims.WeatherPointsUpdate);
        _canReadSettings = user.HasPermission(PermissionClaims.SystemSettingsRead);
    }

    // A changed principal: re-read the claims, and without weather.read drop everything rendered.
    private async void OnAuthenticationStateChanged(Task<AuthenticationState> state)
    {
        try
        {
            ReadClaims(await state);
            if (!_canRead)
            {
                Cancel(ref _load);
                _response = null;
                _loadedFor = null;
            }
            else if (_loadedFor != EventId)
            {
                _loadedFor = EventId;
                _ = LoadAsync();
            }

            await InvokeAsync(StateHasChanged);
        }
        catch (Exception)
        {
            // The dialog may already be gone.
        }
    }

    private async Task LoadAsync()
    {
        Cancel(ref _load);
        var source = _load = new CancellationTokenSource();
        _phase = Phase.Loading;
        _inFlight = true;
        await InvokeAsync(StateHasChanged);

        try
        {
            var result = await WeatherApi.CalendarEventAsync(EventId, ct: source.Token);
            source.Token.ThrowIfCancellationRequested();
            _response = result.IsSuccess ? result.Value : null;
            _phase = result switch
            {
                { IsSuccess: true, Value: not null } => Phase.Ready,
                { Status: HttpStatusCode.TooManyRequests } => Phase.RateLimited,
                { Status: HttpStatusCode.Forbidden } => Phase.Forbidden,
                { Status: HttpStatusCode.NotFound } => Phase.NotFound,
                _ => Phase.Error,
            };
            if (_phase != Phase.Ready)
                Log.LogWarning("Weather for {Surface} failed: {FailureClass}.", "event dialog", _phase);
            if (_phase == Phase.RateLimited)
                _ = CoolDownAsync();
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            Log.LogWarning("Weather for {Surface} failed: {FailureClass}.", "event dialog", ex.GetType().Name);
            _phase = Phase.Error;
        }
        finally
        {
            if (ReferenceEquals(_load, source))
                _inFlight = false;
        }

        await InvokeAsync(StateHasChanged);
    }

    /// <summary>
    /// Try again: the skeleton replaces the button, so focus goes to the section's label first and stays there when
    /// the result arrives — the hours panel's pattern. Refused (but still focusable) during the cool-down.
    /// </summary>
    private async Task RetryAsync()
    {
        if (!RetryEnabled)
            return;

        try
        {
            _module ??= await JS.InvokeAsync<IJSObjectReference>("import", "./js/weather-hours.js");
            await _module.InvokeVoidAsync("focusById", LabelId);
        }
        catch (Exception)
        {
            // Best-effort: the result still renders.
        }

        await LoadAsync();
    }

    private async Task CoolDownAsync()
    {
        Cancel(ref _coolDown);
        var source = _coolDown = new CancellationTokenSource();
        try
        {
            await Task.Delay(WeatherRetry.CoolDown, source.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (ReferenceEquals(_coolDown, source))
        {
            _coolDown = null;
            source.Dispose();
            await InvokeAsync(StateHasChanged);
        }
    }

    /// <summary>Only a plain primary click closes the dialog; Ctrl/Cmd/Shift-click opens elsewhere and leaves it open.</summary>
    private Task ActivatedAsync(MouseEventArgs e) =>
        e is { Button: 0, CtrlKey: false, MetaKey: false, ShiftKey: false, AltKey: false } ? OnNavigate.InvokeAsync() : Task.CompletedTask;

    private static string PointHref(WeatherPointRef point) => $"/weather-points?open={point.WeatherPointId:D}";

    /// <summary>"Open in Weather points" → "Weather points", for the dirty-form text.</summary>
    private static string Destination(string label) =>
        label.StartsWith("Go to ", StringComparison.Ordinal) ? label["Go to ".Length..]
        : label.StartsWith("Open in ", StringComparison.Ordinal) ? label["Open in ".Length..]
        : label.StartsWith("Open ", StringComparison.Ordinal) ? label["Open ".Length..]
        : label;

    private string IssuedLine(CalendarEventWeatherResponse r)
    {
        var issued = r.Steps.Count == 0 ? (DateTime?)null : r.Steps.Max(s => s.IssuedAtUtc);
        return Past
            ? $"As forecast, issued {WeatherVisuals.Stamp(issued)} (your time). Hourly weather is never measured."
            : $"Forecast issued {WeatherVisuals.Clock(issued)} (your time)";
    }

    private static void Cancel(ref CancellationTokenSource? source)
    {
        source?.Cancel();
        source?.Dispose();
        source = null;
    }

    public async ValueTask DisposeAsync()
    {
        if (_subscribed)
            Auth.AuthenticationStateChanged -= OnAuthenticationStateChanged;
        Cancel(ref _load);
        Cancel(ref _coolDown);
        if (_module is not null)
        {
            try
            {
                await _module.DisposeAsync();
            }
            catch (JSDisconnectedException)
            {
            }
        }
    }
}
