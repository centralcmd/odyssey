using System.Net;
using Microsoft.AspNetCore.Components;
using Odyssey.ApiClient.Resources;
using Odyssey.Client.Components;
using Odyssey.Dtos.Journal;

namespace Odyssey.Client.Pages.Weather;

/// <summary>
/// The Weather history section of a location's record, for every role (design system · Weather.jsx
/// <c>LocationWeatherHistorySection</c>, components-weather.md → Weather history). It reads
/// <c>GET /api/weather/daily?locationId=…</c> and pages through the place's measured days by the response's
/// <c>history</c> bounds, never by the Admin row, so every role pages the same way. <see cref="Row"/> is
/// the Admin row or null; it only adds the weather-off notice and hides the section during a purge.
/// </summary>
public partial class LocationWeatherHistorySection
{
    [Parameter, EditorRequired] public ExistingLocation Location { get; set; } = default!;
    [Parameter] public ExistingWeatherLocation? Row { get; set; }

    /// <summary>weather.locations.update — with <see cref="CanOpenSettings"/>, points at the missing setting.</summary>
    [Parameter] public bool CanSetUp { get; set; }

    /// <summary>system-settings.update — the link to System settings is offered only to someone who can use it.</summary>
    [Parameter] public bool CanOpenSettings { get; set; }

    [Inject] private IWeatherApiClient Weather { get; set; } = default!;

    internal enum Phase { Loading, Ready, RateLimited, Error }

    /// <summary>The preset spans. A custom range is whatever two picked dates enclose.</summary>
    internal static readonly int[] Spans = [30, 60, 365];

    /// <summary>The endpoint's cap (<c>WeatherRules.DailyMaxDays</c>): a custom range spans at most this many days.</summary>
    internal const int MaxDays = WeatherRules.DailyMaxDays;

    internal const int DefaultSpan = 60;
    private const string CustomValue = "custom";

    /// <summary>The viewer's today, as a seam for bUnit. The first window ends the day before it.</summary>
    internal static Func<DateOnly> Today = static () => DateOnly.FromDateTime(DateTime.Now);

    private Phase _phase = Phase.Loading;
    private int _span = DefaultSpan;
    private bool _custom;
    private DateOnly _end;
    private WeatherHistoryBounds? _bounds;
    private WeatherDailyResponse? _response;
    private int _requestSeq;
    private Guid _loadedFor;
    private OdsDatePicker? _fromPicker;
    private IReadOnlyList<string> _measures = [WeatherMeasures.Temperature];

    protected override async Task OnParametersSetAsync()
    {
        // A different place starts over on the default window; a re-render of the same place keeps it.
        if (_loadedFor == Location.LocationId)
            return;
        _loadedFor = Location.LocationId;
        _span = DefaultSpan;
        _custom = false;
        _bounds = null;
        _end = Today().AddDays(-1);
        await LoadAsync(firstLoad: true);
    }

    /// <summary>
    /// The first request asks for the window ending yesterday. When its <c>history.measuredThrough</c> is
    /// earlier — a place whose weather is off — the window re-anchors on that day and asks once more. Every
    /// later request already knows the bounds.
    /// </summary>
    private async Task LoadAsync(bool firstLoad = false)
    {
        var seq = ++_requestSeq;
        _phase = Phase.Loading;
        StateHasChanged();

        var result = await Weather.DailyAsync(From, _end, Location.LocationId);
        if (seq != _requestSeq)
            return;
        if (!result.IsSuccess || result.Value is not { } response)
        {
            _phase = result.Status == HttpStatusCode.TooManyRequests ? Phase.RateLimited : Phase.Error;
            StateHasChanged();
            return;
        }

        _bounds = response.History ?? new WeatherHistoryBounds();
        if (firstLoad && ClampEnd(_end, _span) is { } anchored && anchored != _end)
        {
            _end = anchored;
            await LoadAsync();
            return;
        }

        _response = response;
        _phase = Phase.Ready;
        StateHasChanged();
    }

    /// <summary>Awaited, and ignored while a load is already running, so a double press cannot start two.</summary>
    private Task Retry() => _phase == Phase.Loading ? Task.CompletedTask : LoadAsync(firstLoad: _bounds is null);

    // ── The window ──────────────────────────────────────────────────────────────────────────────────

    private DateOnly? Latest => _bounds?.MeasuredThrough;
    private DateOnly? Earliest => _bounds?.MeasuredFrom;

    /// <summary>
    /// A window is always <paramref name="span"/> days long and lies inside the stored history: stepping
    /// back past the start lands on the first full window instead of a stub of a few days.
    /// </summary>
    internal static DateOnly ClampEnd(DateOnly end, int span, DateOnly earliest, DateOnly latest)
    {
        var firstFull = earliest.AddDays(span - 1);
        var lowest = firstFull < latest ? firstFull : latest;
        var capped = end > latest ? latest : end;
        return capped < lowest ? lowest : capped;
    }

    private DateOnly? ClampEnd(DateOnly end, int span) =>
        Earliest is { } earliest && Latest is { } latest ? ClampEnd(end, span, earliest, latest) : null;

    private DateOnly From
    {
        get
        {
            var wanted = _end.AddDays(-(_span - 1));
            return Earliest is { } earliest && wanted < earliest ? earliest : wanted;
        }
    }

    private int DaysShown => _end.DayNumber - From.DayNumber + 1;
    private bool AtStart => Earliest is not { } earliest || From <= earliest;
    private bool AtEnd => Latest is not { } latest || _end >= latest;
    private string Period => $"{WeatherVisuals.Date(From)} – {WeatherVisuals.Date(_end)}";
    private string StepLabel => $"{(_custom ? DaysShown : _span)} days";

    private IReadOnlyList<OdsSegmentedOption> SpanOptions =>
    [
        .. Spans.Select(s => new OdsSegmentedOption { Value = s.ToString(), Label = $"{s} days" }),
        new OdsSegmentedOption { Value = CustomValue, Label = "Custom" },
    ];

    private string SpanValue => _custom ? CustomValue : _span.ToString();

    private async Task OnSpanChanged(string value)
    {
        if (value == CustomValue)
        {
            // Custom keeps the current dates and hands the choice to the From calendar.
            _custom = true;
            if (_fromPicker is not null)
                await _fromPicker.OpenAsync();
            return;
        }

        var span = int.Parse(value);
        _custom = false;
        _span = span;
        _end = ClampEnd(_end, span) ?? _end;
        await LoadAsync();
    }

    private Task Earlier()
    {
        _end = ClampEnd(From.AddDays(-1), DaysShown) ?? _end;
        return LoadAsync();
    }

    private Task Later()
    {
        var shown = DaysShown;
        _end = ClampEnd(_end.AddDays(shown), shown) ?? _end;
        return LoadAsync();
    }

    // Each end is bounded by the stored history and by the cap measured from the other end, so no pick can
    // produce a range the endpoint would refuse.
    private DateTime FromMin => Max(Earliest ?? From, _end.AddDays(-(MaxDays - 1))).ToDateTime(TimeOnly.MinValue);
    private DateTime FromMax => _end.ToDateTime(TimeOnly.MinValue);
    private DateTime ToMin => From.ToDateTime(TimeOnly.MinValue);
    private DateTime ToMax => Min(Latest ?? _end, From.AddDays(MaxDays - 1)).ToDateTime(TimeOnly.MinValue);

    private static DateOnly Max(DateOnly a, DateOnly b) => a > b ? a : b;
    private static DateOnly Min(DateOnly a, DateOnly b) => a < b ? a : b;

    /// <summary>The calendar's Clear has nothing to clear here — the window always has two ends. A date equal
    /// to the window's own end is MudDatePicker echoing a value set from code, not a pick.</summary>
    private Task PickFrom(DateTime? value)
    {
        if (value is not { } picked)
            return Task.CompletedTask;
        var from = DateOnly.FromDateTime(picked);
        // MudDatePicker echoes DateChanged when its value is set from code (a preset, Earlier, Later);
        // only a date that differs from the window's is the user's pick.
        if (from == From)
            return Task.CompletedTask;
        _custom = true;
        _span = _end.DayNumber - from.DayNumber + 1;
        return LoadAsync();
    }

    private Task PickTo(DateTime? value)
    {
        if (value is not { } picked)
            return Task.CompletedTask;
        var to = DateOnly.FromDateTime(picked);
        if (to == _end)
            return Task.CompletedTask;
        var from = From;
        _custom = true;
        _span = to.DayNumber - from.DayNumber + 1;
        _end = to;
        return LoadAsync();
    }

    // ── Measures and facts ──────────────────────────────────────────────────────────────────────────

    /// <summary>The dash means "this is the line", so only a selected row carries the measure's hue.</summary>
    private IReadOnlyList<OdsOption> MeasureOptions =>
        [.. WeatherMeasures.All.Select(m => new OdsOption(m.Key, m.Label) { Icon = "remove", IconColor = _measures.Contains(m.Key) ? m.Color : null })];

    private void PickMeasures(IReadOnlyCollection<string> next) => _measures = WeatherMeasures.NextSelection(_measures, next);

    /// <summary>One fact: its figure in bold, then its day or <see cref="After"/> in plain text.</summary>
    internal sealed record Fact(string Icon, string Label, string Value, DateOnly? Date = null, string? After = null);

    private IReadOnlyList<WeatherDaySummary> Measured =>
        [.. (_response?.Days ?? []).Where(d => d.Observed is not null)];

    /// <summary>The facts row for the unit plotted (design system · Weather.jsx <c>WxHistoryFacts</c>).</summary>
    private IReadOnlyList<Fact> ShownFacts => FactsFor(Measured, DaysShown, WeatherMeasures.Of(_measures[0]).Group);

    internal static IReadOnlyList<Fact> FactsFor(IReadOnlyList<WeatherDaySummary> measured, int daysShown, string group)
    {
        static string One(decimal v) => v.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
        WeatherDaySummary? MaxBy(Func<WeatherDayObserved, decimal?> pick) =>
            measured.Where(d => pick(d.Observed!) is not null).MaxBy(d => pick(d.Observed!));
        decimal? Mean(Func<WeatherDayObserved, decimal?> pick)
        {
            var values = measured.Select(d => pick(d.Observed!)).Where(v => v is not null).Select(v => v!.Value).ToList();
            return values.Count > 0 ? values.Average() : null;
        }
        Fact DaysFact(Func<WeatherDayObserved, bool> has) =>
            new("event_available", "Days measured", $"{measured.Count(d => has(d.Observed!))}", After: $" of {daysShown}");

        var facts = new List<Fact?>();
        if (group == "mm")
        {
            var wettest = MaxBy(o => o.PrecipitationMm);
            facts.Add(new("water_drop", "Total", $"{One(measured.Sum(d => d.Observed!.PrecipitationMm ?? 0))} mm"));
            facts.Add(wettest is null ? null : new("north", "Wettest day", $"{One(wettest.Observed!.PrecipitationMm!.Value)} mm", wettest.Date));
            // 0.1 mm is the smallest amount a gauge reports; below it is a dry day.
            facts.Add(new("umbrella", "Days with precipitation", $"{measured.Count(d => d.Observed!.PrecipitationMm >= 0.1m)}"));
            facts.Add(DaysFact(o => o.PrecipitationMm is not null));
        }
        else if (group == "ms")
        {
            var windiest = MaxBy(o => o.MeanWindSpeedMps);
            var gust = MaxBy(o => o.MaxWindGustMps);
            facts.Add(Mean(o => o.MeanWindSpeedMps) is { } avg ? new("air", "Average wind", $"{One(avg)} m/s") : null);
            facts.Add(windiest is null ? null : new("north", "Windiest day", $"{One(windiest.Observed!.MeanWindSpeedMps!.Value)} m/s", windiest.Date));
            facts.Add(gust is null ? null : new("storm", "Strongest gust", $"{One(gust.Observed!.MaxWindGustMps!.Value)} m/s", gust.Date));
            facts.Add(DaysFact(o => o.MeanWindSpeedMps is not null || o.MaxWindGustMps is not null));
        }
        else
        {
            var band = measured.Where(d => d.Observed is { MinTempC: not null, MaxTempC: not null }).ToList();
            var low = band.MinBy(d => d.Observed!.MinTempC);
            var high = band.MaxBy(d => d.Observed!.MaxTempC);
            facts.Add(low is null ? null : new("south", "Lowest", WeatherVisuals.Temp(low.Observed!.MinTempC), low.Date));
            facts.Add(high is null ? null : new("north", "Highest", WeatherVisuals.Temp(high.Observed!.MaxTempC), high.Date));
            facts.Add(Mean(o => o.MeanTempC) is { } avg ? new("thermostat", "Average of daily means", WeatherVisuals.Temp(avg)) : null);
            facts.Add(DaysFact(o => o.MeanTempC is not null || o.MaxTempC is not null));
        }

        return [.. facts.OfType<Fact>()];
    }

    private static string ShortDate(DateOnly date) => date.ToString("d MMM", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The Admin row says historical data is off: nothing new is measured for this place.</summary>
    private bool HistoryOff => Row is { HistoryEnabled: false };
}
