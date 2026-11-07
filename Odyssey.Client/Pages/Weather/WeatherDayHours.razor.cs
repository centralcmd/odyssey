using System.Globalization;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using Odyssey.Dtos.Journal;

namespace Odyssey.Client.Pages.Weather;

/// <summary>What the hours panel is showing (issue #415 §3 States).</summary>
public enum WeatherHoursPhase
{
    Loading,
    Ready,
    RateLimited,
    Forbidden,
    NotFound,
    Error,
}

public partial class WeatherDayHours
{
    [Inject] private IJSRuntime JS { get; set; } = default!;

    /// <summary>The day the panel is for — the card's own date, used for the heading until the response names it.</summary>
    [Parameter, EditorRequired] public DateOnly Date { get; set; }

    [Parameter] public WeatherHourlyResponse? Response { get; set; }

    [Parameter] public WeatherHoursPhase Phase { get; set; }

    [Parameter, EditorRequired] public string PointName { get; set; } = string.Empty;

    [Parameter, EditorRequired] public string HeadingId { get; set; } = string.Empty;

    /// <summary>The panel heading's level: one below the host's nearest heading.</summary>
    [Parameter] public int HeadingLevel { get; set; } = 3;

    /// <summary>False while a request is in flight or a 429 cool-down runs; Try again stays focusable, <c>aria-disabled</c>.</summary>
    [Parameter] public bool RetryEnabled { get; set; } = true;

    /// <summary>True when the body's foot already says the forecast is not up to date, so the panel does not repeat it.</summary>
    [Parameter] public bool BodyStale { get; set; }

    [Parameter] public EventCallback OnRetry { get; set; }

    /// <summary>The id of the panel itself — the focus destination, since the heading is visually hidden.</summary>
    internal static string FocusIdFor(string headingId) => $"{headingId}-panel";

    private ElementReference _wrapper;
    private ElementReference? _observed;
    private bool _overflows;
    private IJSObjectReference? _module;
    private DotNetObjectReference<WeatherDayHours>? _self;

    private string Title
    {
        get
        {
            var day = Response?.Day;
            var title = WeatherVisuals.DayLong(day?.Date ?? Date);
            if (day is { IsToday: true })
                title += " — rest of today";
            if (Response?.Steps is { Count: > 0 } steps && OffsetDiffersFromViewer(steps))
                title += $" · times at {PointName}";
            return title;
        }
    }

    private bool ShowsTable => Phase == WeatherHoursPhase.Ready && Response is { Status: WeatherStatus.Ok, Steps.Count: > 0 };

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        // The wrapper exists only while a table is shown; observe each new one, and let go of a removed one.
        if (!ShowsTable)
        {
            if (_observed is not null)
                await UnobserveAsync();
            return;
        }

        if (_observed is { } observed && observed.Id == _wrapper.Id)
            return;
        if (_observed is not null)
            await UnobserveAsync();

        try
        {
            _module ??= await JS.InvokeAsync<IJSObjectReference>("import", "./js/weather-hours.js");
            _self ??= DotNetObjectReference.Create(this);
            await _module.InvokeVoidAsync("observeOverflow", _wrapper, FocusIdFor(HeadingId), _self);
            _observed = _wrapper;
        }
        catch (JSDisconnectedException)
        {
        }
        catch (InvalidOperationException)
        {
            // Prerendering or a test host without the module: the wrapper stays a plain div.
        }
    }

    [JSInvokable]
    public Task OnOverflowChanged(bool overflows)
    {
        if (_overflows == overflows)
            return Task.CompletedTask;
        _overflows = overflows;
        return InvokeAsync(StateHasChanged);
    }

    private Task RetryAsync() => RetryEnabled ? OnRetry.InvokeAsync() : Task.CompletedTask;

    private async Task UnobserveAsync()
    {
        var observed = _observed;
        _observed = null;
        _overflows = false;
        if (_module is null || observed is null)
            return;
        try
        {
            await _module.InvokeVoidAsync("unobserveOverflow", observed.Value);
        }
        catch (JSDisconnectedException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_module is not null)
        {
            try
            {
                if (_observed is { } observed)
                    await _module.InvokeVoidAsync("unobserveOverflow", observed);
                await _module.DisposeAsync();
            }
            catch (JSDisconnectedException)
            {
            }
        }

        _self?.Dispose();
    }

    // ── Rows ──────────────────────────────────────────────────────────────────────────────────────

    /// <summary>One table row: a forecast step, or a gap with its words.</summary>
    internal sealed record Row(WeatherStep? Step, string? Gap, bool First);

    /// <summary>
    /// The steps in order, with a full-width row for a leading gap (a later day whose first row starts after its
    /// midnight) and for every inner gap (a run join). The tail is not a row: it is the "Forecast ends" line.
    /// </summary>
    internal static IEnumerable<Row> Rows(WeatherHourlyResponse response)
    {
        var steps = response.Steps;
        var day = response.Day;
        if (day is { IsToday: false, FromUtc: { } dayStart } && steps.Count > 0 && Utc(steps[0].TimeUtc) > Utc(dayStart))
            yield return new Row(null, $"No forecast before {Hour(Start(steps[0]))}", false);

        for (var i = 0; i < steps.Count; i++)
        {
            if (i > 0 && Utc(steps[i].TimeUtc) > Utc(End(steps[i - 1]).UtcDateTime))
                yield return new Row(null, $"No forecast for {Hour(End(steps[i - 1]))}–{Hour(Start(steps[i]))}", false);
            yield return new Row(steps[i], null, i == 0);
        }
    }

    /// <summary>"Forecast ends at HH:mm" when the last row stops before the day does; null otherwise.</summary>
    internal static string? EndsAt(WeatherHourlyResponse response) =>
        response is { Day.ToUtc: { } dayEnd, Steps: [.., var last] } && End(last).UtcDateTime < Utc(dayEnd)
            ? Hour(End(last))
            : null;

    // ── A row's span within the day ───────────────────────────────────────────────────────────────
    // A row never shows time outside the day it is listed under: a block that starts before the day's midnight is
    // shown from 00, one that runs past the next midnight is shown to 24, both in the point's own clock. Only the
    // label is cut; the row's values stay MET's, and a cut block's precipitation is its whole-block total.

    internal static bool CutsStart(WeatherStep step, WeatherHourlyDay? day) =>
        day?.FromUtc is { } dayStart && Utc(step.TimeUtc) < Utc(dayStart);

    internal static bool CutsEnd(WeatherStep step, WeatherHourlyDay? day) =>
        day?.ToUtc is { } dayEnd && Utc(step.TimeUtc).AddHours(step.PeriodHours) > Utc(dayEnd);

    internal static bool IsCut(WeatherStep step, WeatherHourlyDay? day) => CutsStart(step, day) || CutsEnd(step, day);

    private static string StartLabel(WeatherStep step, WeatherHourlyDay? day) => CutsStart(step, day) ? "00:00" : Hour(Start(step));

    private static string EndLabel(WeatherStep step, WeatherHourlyDay? day) => CutsEnd(step, day) ? "24:00" : Hour(End(step));

    /// <summary>"14:00" for a whole hour; "19–24" for a block or a cut row; minutes kept where the zone has them.</summary>
    internal static string VisibleTime(WeatherStep step, WeatherHourlyDay? day) =>
        step.PeriodHours > 1 || IsCut(step, day)
            ? $"{Short(StartLabel(step, day))}–{Short(EndLabel(step, day))}"
            : Hour(Start(step));

    internal static string SpokenTime(WeatherStep step, WeatherHourlyDay? day, bool now) =>
        $"{(now ? "Now, " : "")}{StartLabel(step, day)} to {EndLabel(step, day)}";

    /// <summary>The whole block a cut row came from, in the point's clock: "19–01".</summary>
    internal static string BlockSpan(WeatherStep step) => $"{Short(Hour(Start(step)))}–{Short(Hour(End(step)))}";

    internal static string SpokenPrecipitation(WeatherStep step, WeatherHourlyDay? day) =>
        $"{(step.PrecipitationMm is null ? "precipitation not available" : WeatherVisuals.Mm(step.PrecipitationMm))}"
        + $"{(IsCut(step, day) && step.PrecipitationMm is not null ? $" over {Hour(Start(step))} to {Hour(End(step))}" : "")}, "
        + $"{(step.PrecipitationProbabilityPct is null ? "chance not available" : $"{WeatherVisuals.Pct(step.PrecipitationProbabilityPct)} chance")}";

    /// <summary>"19:00" → "19"; "05:30" stays "05:30" in a half-hour zone.</summary>
    private static string Short(string hhmm) => hhmm.EndsWith(":00", StringComparison.Ordinal) ? hhmm[..2] : hhmm;

    /// <summary>
    /// True when any row's own offset (the point's clock) differs from the viewer's at that instant. Compared per row,
    /// so a DST change inside the day is handled. A row without a local time — which day mode never sends — is shown
    /// in the viewer's clock and so never differs.
    /// </summary>
    internal static bool OffsetDiffersFromViewer(IEnumerable<WeatherStep> steps) =>
        steps.Any(s => s.LocalStart is { } start && start.Offset != ViewerOffset(start.UtcDateTime));

    /// <summary>The viewer's UTC offset at an instant, as a seam for bUnit (the browser zone is the process zone in WASM).</summary>
    internal static Func<DateTime, TimeSpan> ViewerOffset = static utc => TimeZoneInfo.Local.GetUtcOffset(utc);

    // A row's local start and end; a missing one falls back to the viewer's clock (#415 §3 N3).
    private static DateTimeOffset Start(WeatherStep step) =>
        step.LocalStart ?? new DateTimeOffset(Utc(step.TimeUtc)).ToOffset(ViewerOffset(Utc(step.TimeUtc)));

    private static DateTimeOffset End(WeatherStep step) =>
        step.LocalEnd ?? new DateTimeOffset(Utc(step.TimeUtc).AddHours(step.PeriodHours)).ToOffset(ViewerOffset(Utc(step.TimeUtc).AddHours(step.PeriodHours)));

    private static string Hour(DateTimeOffset local) => local.ToString("HH:mm", CultureInfo.InvariantCulture);

    private static DateTime Utc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };
}
