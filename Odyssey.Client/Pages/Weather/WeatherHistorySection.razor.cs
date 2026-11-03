using System.Net;
using Microsoft.AspNetCore.Components;
using Odyssey.ApiClient.Resources;
using Odyssey.Client.Components;
using Odyssey.Client.Services;
using Odyssey.Dtos.Journal;

namespace Odyssey.Client.Pages.Weather;

/// <summary>
/// The Weather history section of one weather point, for every role (design system · Weather.jsx
/// <c>WeatherHistorySection</c>, components-weather.md → Weather history). It reads
/// <c>GET /api/weather/daily?weatherPointId=…</c> for one weather point (issue #387) and pages
/// through its measured days by the response's <c>history</c> bounds, so every role pages the same way. The
/// caller's <see cref="HistoryOff"/> adds the history-off notice; the caller hides the section during a
/// purge.
/// <para>
/// The window is two dates, the last 365 days by default, and is remembered per user and weather point with the
/// measures (<see cref="StateKey"/>). A window ending on the newest measured day is stored with an open end,
/// so the next visit keeps its From and runs to whatever is newest then.
/// </para>
/// </summary>
public partial class WeatherHistorySection
{
    [Parameter, EditorRequired] public Guid WeatherPointId { get; set; }

    /// <summary>The point's name, for the chart's accessible names and the history-off notice.</summary>
    [Parameter, EditorRequired] public string PointName { get; set; } = "";

    /// <summary>The point's historical data is off: the notice over what was measured before.</summary>
    [Parameter] public bool HistoryOff { get; set; }

    /// <summary>The section meta — on a linked location, where the weather comes from (<i>from Oslo · 2 km</i>).</summary>
    [Parameter] public string? Meta { get; set; }

    /// <summary>weather.points.update — with <see cref="CanOpenSettings"/>, points at the missing setting.</summary>
    [Parameter] public bool CanSetUp { get; set; }

    /// <summary>system-settings.update — the link to System settings is offered only to someone who can use it.</summary>
    [Parameter] public bool CanOpenSettings { get; set; }

    [Inject] private IWeatherApiClient Weather { get; set; } = default!;
    [Inject] private IPageStateService PageState { get; set; } = default!;

    internal enum Phase { Loading, Ready, RateLimited, Error }

    /// <summary>The window a place opens on with nothing remembered: the last year.</summary>
    internal const int DefaultDays = 365;

    /// <summary>The endpoint's cap (<c>WeatherRules.DailyMaxDays</c>): a window spans at most this many days.</summary>
    internal const int MaxDays = WeatherRules.DailyMaxDays;

    /// <summary>The viewer's today, as a seam for bUnit. The first window ends the day before it.</summary>
    internal static Func<DateOnly> Today = static () => DateOnly.FromDateTime(DateTime.Now);

    /// <summary>The UI-state key the window and measures are remembered under, one per weather point.</summary>
    internal static string StateKey(Guid weatherPointId) => $"weather-history-{weatherPointId}";

    /// <summary>
    /// What is remembered for one place. <see cref="To"/> null is an open end: the window runs to the newest
    /// measured day, whichever that is when it is next opened.
    /// </summary>
    internal sealed class SavedWindow
    {
        public DateOnly? From { get; set; }
        public DateOnly? To { get; set; }
        public List<string>? Measures { get; set; }
    }

    private Phase _phase = Phase.Loading;
    private DateOnly _from;
    private DateOnly _to;
    private SavedWindow? _saved;
    private WeatherHistoryBounds? _bounds;
    private WeatherDailyResponse? _response;
    private int _requestSeq;
    private Guid _loadedFor;
    private IReadOnlyList<string> _measures = [WeatherMeasures.Temperature];

    protected override async Task OnParametersSetAsync()
    {
        // A different place starts over on its remembered window; a re-render of the same place keeps it.
        if (_loadedFor == WeatherPointId)
            return;
        _loadedFor = WeatherPointId;
        _bounds = null;
        _response = null;
        _phase = Phase.Loading;
        _saved = await PageState.LoadAsync<SavedWindow>(StateKey(WeatherPointId));
        if (_loadedFor != WeatherPointId)
            return;
        _measures = RestoreMeasures(_saved?.Measures);
        (_from, _to) = Resolve(_saved, Today().AddDays(-1), null);
        await LoadAsync(firstLoad: true);
    }

    /// <summary>
    /// The first request asks for the remembered (or default) window ending no later than yesterday. Once its
    /// <c>history</c> bounds are known the window is resolved against them — an open end runs to
    /// <c>measuredThrough</c>, which for a place with history off is the day it stopped — and asked for once
    /// more if that moved it. Every later request already knows the bounds.
    /// </summary>
    private async Task LoadAsync(bool firstLoad = false)
    {
        var seq = ++_requestSeq;
        _phase = Phase.Loading;
        StateHasChanged();

        var result = await Weather.DailyAsync(_from, _to, WeatherPointId);
        if (seq != _requestSeq)
            return;
        if (!result.IsSuccess || result.Value is not { } response)
        {
            _phase = result.Status == HttpStatusCode.TooManyRequests ? Phase.RateLimited : Phase.Error;
            StateHasChanged();
            return;
        }

        _bounds = response.History ?? new WeatherHistoryBounds();
        _measures = KeepOffered(_measures, WeatherMeasures.Offered(_bounds.Stations));
        if (firstLoad && Latest is { } latest && Resolve(_saved, latest, Earliest) is var resolved && resolved != (_from, _to))
        {
            (_from, _to) = resolved;
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
    /// The window to show from what was remembered: an open or missing end is <paramref name="latest"/>, a
    /// missing start is <see cref="DefaultDays"/> before the end, and the result lies inside the stored history
    /// and the endpoint's cap — a remembered From more than <see cref="MaxDays"/> back moves up to fit.
    /// </summary>
    internal static (DateOnly From, DateOnly To) Resolve(SavedWindow? saved, DateOnly latest, DateOnly? earliest)
    {
        var to = saved?.To is { } end && end < latest ? end : latest;
        if (earliest is { } first && to < first)
            to = latest;
        var from = saved?.From ?? to.AddDays(-(DefaultDays - 1));
        if (from > to)
            from = to.AddDays(-(DefaultDays - 1));
        from = Max(from, to.AddDays(-(MaxDays - 1)));
        if (earliest is { } floor)
            from = Max(from, floor);
        return (from, to);
    }

    /// <summary>
    /// A window of <paramref name="days"/> ending on <paramref name="end"/>, kept whole and inside the stored
    /// history: stepping past either end lands on the first or last full window, never a stub.
    /// </summary>
    internal static (DateOnly From, DateOnly To) Place(DateOnly end, int days, DateOnly earliest, DateOnly latest)
    {
        var to = Max(Min(end, latest), Min(earliest.AddDays(days - 1), latest));
        return (Max(earliest, to.AddDays(-(days - 1))), to);
    }

    private int DaysShown => _to.DayNumber - _from.DayNumber + 1;
    private bool AtStart => Earliest is not { } earliest || _from <= earliest;
    private bool AtEnd => Latest is not { } latest || _to >= latest;
    private string Period => $"{WeatherVisuals.Date(_from)} – {WeatherVisuals.Date(_to)}";
    private string StepLabel => $"{DaysShown} days";

    private Task Earlier() => Move(_from.AddDays(-1));

    private Task Later() => Move(_to.AddDays(DaysShown));

    private Task Move(DateOnly end)
    {
        if (Earliest is not { } earliest || Latest is not { } latest)
            return Task.CompletedTask;
        (_from, _to) = Place(end, DaysShown, earliest, latest);
        return Changed();
    }

    // Each end is bounded by the stored history and by the cap measured from the other end, so no pick can
    // produce a range the endpoint would refuse.
    private DateTime FromMin => Max(Earliest ?? _from, _to.AddDays(-(MaxDays - 1))).ToDateTime(TimeOnly.MinValue);
    private DateTime FromMax => _to.ToDateTime(TimeOnly.MinValue);
    private DateTime ToMin => _from.ToDateTime(TimeOnly.MinValue);
    private DateTime ToMax => Min(Latest ?? _to, _from.AddDays(MaxDays - 1)).ToDateTime(TimeOnly.MinValue);

    private static DateOnly Max(DateOnly a, DateOnly b) => a > b ? a : b;
    private static DateOnly Min(DateOnly a, DateOnly b) => a < b ? a : b;

    /// <summary>The calendar's Clear has nothing to clear here — the window always has two ends. A date equal
    /// to the window's own end is MudDatePicker echoing a value set from code, not a pick.</summary>
    private Task PickFrom(DateTime? value)
    {
        if (value is not { } picked || DateOnly.FromDateTime(picked) is var from && from == _from)
            return Task.CompletedTask;
        _from = from;
        return Changed();
    }

    private Task PickTo(DateTime? value)
    {
        if (value is not { } picked || DateOnly.FromDateTime(picked) is var to && to == _to)
            return Task.CompletedTask;
        _to = to;
        return Changed();
    }

    private Task Changed()
    {
        Remember();
        return LoadAsync();
    }

    /// <summary>
    /// Stores the window and the measures for this user and place. A window ending on the newest measured day
    /// is stored with an open end: choosing "up to now" means up to now, not up to the day it was chosen.
    /// </summary>
    private void Remember()
    {
        _saved = new SavedWindow
        {
            From = _from,
            To = Latest is { } latest && _to >= latest ? null : _to,
            Measures = [.. _measures],
        };
        PageState.QueueSave(StateKey(WeatherPointId), _saved);
    }

    /// <summary>
    /// The selection limited to the measures offered; when none of it is, the first measure offered — a remembered
    /// wind chart at a place whose wind station is gone opens on what the place does measure.
    /// </summary>
    internal static IReadOnlyList<string> KeepOffered(IReadOnlyList<string> selected, IReadOnlyList<WeatherMeasure> offered)
    {
        var kept = selected.Where(k => offered.Any(m => m.Key == k)).ToList();
        return kept.Count > 0 ? kept : [(offered.Count > 0 ? offered[0] : WeatherMeasures.Of(WeatherMeasures.Temperature)).Key];
    }

    internal const string DayNote =
        "A day runs midnight to midnight UTC — precipitation 06:00 to 06:00 UTC, as MET reports it — so it can sit an hour or two off the local date.";

    internal const string NoStationText =
        "No MET Norway station measures this point — none for temperature within 25 km, precipitation within 15 km or wind within 30 km — so there is no weather history. Forecasts are not affected.";

    /// <summary>The tooltip's lead sentence: where the values come from, ending on a colon when station rows follow.</summary>
    internal static string AboutLead(IReadOnlyList<WeatherStationRef> stations) => stations.Count == 0
        ? "Daily values from the MET Norway stations nearest the weather position."
        : "Daily values from MET Norway stations, each measure from the nearest station that reports it:";

    /// <summary>
    /// What the info button says, as one plain string for the button's <c>aria-describedby</c>: the lead, one line per
    /// measure group (where it is measured, or how far was searched), then the UTC day. The tooltip draws the same
    /// three parts as structured rows (design system · Weather.jsx WxHistoryAbout).
    /// </summary>
    internal static string AboutText(IReadOnlyList<WeatherStationRef> stations) => string.Join("\n",
        new[] { AboutLead(stations) }.Concat(stations.Select(WeatherMeasures.StationLine)).Append(DayNote));

    private IReadOnlyList<WeatherStationRef> AboutStations => _bounds?.Stations ?? [];

    private string About => AboutText(AboutStations);

    /// <summary>Remembered measures, kept only while they still name one unit; otherwise temperature.</summary>
    internal static IReadOnlyList<string> RestoreMeasures(IEnumerable<string>? saved)
    {
        var known = (saved ?? []).Where(k => WeatherMeasures.All.Any(m => m.Key == k)).Distinct().ToList();
        if (known.Count == 0)
            return [WeatherMeasures.Temperature];
        var group = WeatherMeasures.Of(known[0]).Group;
        return [.. known.Where(k => WeatherMeasures.Of(k).Group == group)];
    }

    // ── Measures and facts ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The dash means "this is the line", so only a selected row carries the measure's hue. A measure whose group
    /// has no station is not offered; the info button says why.
    /// </summary>
    private IReadOnlyList<OdsOption> MeasureOptions =>
        [.. WeatherMeasures.Offered(_bounds?.Stations ?? []).Select(m => new OdsOption(m.Key, m.Label) { Icon = "remove", IconColor = _measures.Contains(m.Key) ? m.Color : null })];

    private void PickMeasures(IReadOnlyCollection<string> next)
    {
        _measures = WeatherMeasures.NextSelection(_measures, next);
        if (_bounds is not null)
            Remember();
    }

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

}
