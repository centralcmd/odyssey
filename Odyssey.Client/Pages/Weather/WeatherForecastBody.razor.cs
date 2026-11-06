using System.Net;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using Odyssey.Client.Components;
using Odyssey.Dtos.Journal;

namespace Odyssey.Client.Pages.Weather;

public partial class WeatherForecastBody : IAsyncDisposable
{
    [Inject] private IJSRuntime JS { get; set; } = default!;
    [Inject] private ILogger<WeatherForecastBody> Log { get; set; } = default!;

    /// <summary>An Ok current read with its step and point.</summary>
    [Parameter, EditorRequired] public WeatherCurrentResponse Current { get; set; } = default!;

    /// <summary>The daily read for the same point; days it lacks are simply not shown.</summary>
    [Parameter] public IReadOnlyList<WeatherDaySummary> Days { get; set; } = [];

    /// <summary>The page's shared reads: a day's hours are one request per (point, date), however many records show it.</summary>
    [Parameter, EditorRequired] public WeatherPointReads Reads { get; set; } = default!;

    /// <summary>The hours panel's heading level: one below the host's nearest heading (issue #415 §6.4).</summary>
    [Parameter] public int HeadingLevel { get; set; } = 3;

    /// <summary>Up to nine days from today, as the design shows.</summary>
    internal const int DaysShown = 9;

    /// <summary>How long Try again stays unavailable after a 429. A pause for the person, not a copy of a server limit.</summary>
    internal static TimeSpan RetryCoolDown = TimeSpan.FromSeconds(10);

    private static int _instances;

    private readonly string _instanceId = $"wx{Interlocked.Increment(ref _instances)}";
    private readonly Dictionary<DateOnly, ElementReference> _cards = [];
    private ElementReference _panel;
    private OdsLiveAnnouncer? _announcer;
    private IJSObjectReference? _module;
    private DotNetObjectReference<WeatherForecastBody>? _self;
    private bool _escapeAttached;

    private DateOnly? _selected;
    private WeatherHoursPhase _phase;
    private WeatherHourlyResponse? _response;
    private int _loadToken;
    private bool _inFlight;
    private CancellationTokenSource? _coolDown;
    private Guid? _pointId;

    /// <summary>Where focus goes after the next render: the panel heading, a card, or nowhere.</summary>
    private enum FocusTarget { None, Heading, SelectedCard, FirstCard }

    private FocusTarget _focusAfterRender;
    private DateOnly? _focusCard;

    private string PanelId => $"{_instanceId}-hours";

    private string HeadingId => $"{_instanceId}-hours-h";

    private bool RetryEnabled => !_inFlight && _coolDown is null;

    /// <summary>The POINT's today when the response carries it — its local date, which the server computes — else the viewer's.</summary>
    private DateOnly Today => Current.Today?.Date ?? DateOnly.FromDateTime(WeatherWidget.LocalNow());

    private WeatherDaySummary? TodaySummary =>
        Current.Today ?? Days.FirstOrDefault(d => d.Date == Today && d.Forecast is not null);

    private IReadOnlyList<WeatherDaySummary> Coming =>
        Days.Where(d => d.Forecast is not null && d.Date >= Today).Take(DaysShown).ToList();

    private WeatherDaySummary? Yesterday =>
        Days.FirstOrDefault(d => d.Date == Today.AddDays(-1) && d.Observed is not null);

    internal static string DaySentence(WeatherDaySummary day) =>
        $"{WeatherVisuals.SymbolOf(day.Forecast?.SymbolCode)?.Label ?? "Unknown conditions"}, high {WeatherVisuals.Temp(day.Forecast?.MaxTempC)}, low {WeatherVisuals.Temp(day.Forecast?.MinTempC)}";

    protected override async Task OnParametersSetAsync()
    {
        var point = Current.Point?.WeatherPointId;
        if (_pointId != point)
        {
            // Another point (the dashboard picker): a selection made for the old one means nothing here.
            _pointId = point;
            await CloseAsync(FocusTarget.None);
            return;
        }

        // A re-render that no longer shows the selected day — a quiet reload after the picker saves, midnight, a
        // section refresh — closes the panel rather than leaving it open under no card (issue #415 B3). Blazor cannot
        // see focus, so the module is asked BEFORE this render removes the elements.
        if (_selected is { } open && Coming.All(d => d.Date != open))
        {
            var hadFocus = await ContainsFocusAsync(open);
            await CloseAsync(hadFocus ? FocusTarget.FirstCard : FocusTarget.None);
        }
    }

    private async Task ToggleAsync(DateOnly date)
    {
        if (_selected == date)
        {
            await CloseAsync(FocusTarget.None);
            return;
        }

        StopCoolDown();
        _selected = date;
        await LoadAsync(date);
    }

    private async Task RetryAsync()
    {
        if (_selected is not { } date || !RetryEnabled || _pointId is not { } point)
            return;

        Reads.ForgetDay(point, date);
        _focusAfterRender = FocusTarget.Heading;
        await LoadAsync(date);
    }

    private async Task LoadAsync(DateOnly date)
    {
        if (_pointId is not { } point)
            return;

        var token = ++_loadToken;
        _phase = WeatherHoursPhase.Loading;
        _response = null;
        _inFlight = true;
        StateHasChanged();

        var result = await Reads.HourlyForDayAsync(point, date);

        // A newer selection, a close, or another point while this was in flight: its answer is not for this panel.
        if (token != _loadToken || _selected != date || _pointId != point)
            return;

        _inFlight = false;
        if (result is { IsSuccess: true, Value: { } value })
        {
            _response = value;
            _phase = WeatherHoursPhase.Ready;
            await AnnounceAsync(SuccessMessage(date, value));
        }
        else
        {
            _phase = result.Status switch
            {
                HttpStatusCode.TooManyRequests => WeatherHoursPhase.RateLimited,
                HttpStatusCode.Forbidden => WeatherHoursPhase.Forbidden,
                HttpStatusCode.NotFound => WeatherHoursPhase.NotFound,
                _ => WeatherHoursPhase.Error,
            };
            // Status and failure class only: never the point's name, the date or a response body (issue #415 S5).
            Log.LogWarning("The hours read failed with {Status} ({Phase}).", (int)result.Status, _phase);
            if (_phase == WeatherHoursPhase.RateLimited)
                StartCoolDown();
        }

        StateHasChanged();
    }

    private string SuccessMessage(DateOnly date, WeatherHourlyResponse value)
    {
        var name = WeatherVisuals.DayShort(date, Today);
        return value.Status switch
        {
            WeatherStatus.Ok when value.Steps.Count > 0 => $"{name}: {value.Steps.Count} {(value.Steps.Count == 1 ? "row" : "rows")} below the day list.",
            WeatherStatus.Ok => "No hourly forecast for this day yet.",
            WeatherStatus.OutOfRange => "This day is outside the forecast window.",
            _ => "MET Norway hasn’t answered for this point yet.",
        };
    }

    private void StartCoolDown()
    {
        StopCoolDown();
        var coolDown = new CancellationTokenSource();
        _coolDown = coolDown;
        _ = EndCoolDownAsync(coolDown);
    }

    private async Task EndCoolDownAsync(CancellationTokenSource coolDown)
    {
        try
        {
            await Task.Delay(RetryCoolDown, coolDown.Token);
        }
        catch (TaskCanceledException)
        {
            // Stopped (a close, a day switch, a new 429): StopCoolDown disposed it.
            return;
        }

        if (ReferenceEquals(_coolDown, coolDown))
        {
            _coolDown = null;
            coolDown.Dispose();
            await InvokeAsync(StateHasChanged);
        }
    }

    private void StopCoolDown()
    {
        var coolDown = _coolDown;
        _coolDown = null;
        if (coolDown is null)
            return;
        coolDown.Cancel();
        coolDown.Dispose();
    }

    private async Task CloseAsync(FocusTarget focus)
    {
        var wasOpen = _selected;
        _loadToken++;
        _selected = null;
        _response = null;
        _inFlight = false;
        StopCoolDown();
        if (wasOpen is not null)
        {
            _focusAfterRender = focus;
            _focusCard = wasOpen;
        }

        await DetachEscapeAsync();
        StateHasChanged();
    }

    /// <summary>Escape inside the panel: close it and return focus to the card that opened it.</summary>
    [JSInvokable]
    public Task CloseFromEscape() => InvokeAsync(() => CloseAsync(FocusTarget.SelectedCard));

    private Task AnnounceAsync(string message) => _announcer?.Announce(message) ?? Task.CompletedTask;

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (_selected is not null && !_escapeAttached)
            await AttachEscapeAsync();

        var target = _focusAfterRender;
        _focusAfterRender = FocusTarget.None;
        ElementReference? element = target switch
        {
            FocusTarget.Heading => null,
            FocusTarget.SelectedCard when _focusCard is { } d && _cards.TryGetValue(d, out var card) => card,
            FocusTarget.FirstCard when Coming.Count > 0 && _cards.TryGetValue(Coming[0].Date, out var first) => first,
            _ => null,
        };

        try
        {
            if (target == FocusTarget.Heading && await ModuleAsync() is { } module)
                await module.InvokeVoidAsync("focusById", HeadingId);
            else if (element is { } el)
                await el.FocusAsync();
        }
        catch (JSException)
        {
        }
        catch (JSDisconnectedException)
        {
        }
    }

    private async Task<IJSObjectReference?> ModuleAsync()
    {
        try
        {
            return _module ??= await JS.InvokeAsync<IJSObjectReference>("import", "./js/weather-hours.js");
        }
        catch (JSDisconnectedException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private async Task<bool> ContainsFocusAsync(DateOnly date)
    {
        if (await ModuleAsync() is not { } module)
            return false;
        try
        {
            var card = _cards.TryGetValue(date, out var c) ? (object)c : null;
            return await module.InvokeAsync<bool>("containsFocus", _panel, card);
        }
        catch (JSException)
        {
            return false;
        }
        catch (JSDisconnectedException)
        {
            return false;
        }
    }

    private async Task AttachEscapeAsync()
    {
        if (await ModuleAsync() is not { } module)
            return;
        try
        {
            _self ??= DotNetObjectReference.Create(this);
            await module.InvokeVoidAsync("attachEscape", _panel, _self);
            _escapeAttached = true;
        }
        catch (JSException)
        {
        }
        catch (JSDisconnectedException)
        {
        }
    }

    private async Task DetachEscapeAsync()
    {
        if (!_escapeAttached || _module is null)
            return;
        _escapeAttached = false;
        try
        {
            await _module.InvokeVoidAsync("detachEscape", _panel);
        }
        catch (JSException)
        {
        }
        catch (JSDisconnectedException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        StopCoolDown();
        await DetachEscapeAsync();
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

        _self?.Dispose();
    }
}
