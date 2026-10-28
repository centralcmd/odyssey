using System.Net;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MudBlazor.Services;
using Odyssey.ApiClient;
using Odyssey.ApiClient.Resources;
using Odyssey.Client.Components;
using Odyssey.Client.Pages.Weather;
using Odyssey.Dtos.Journal;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// Weather history in a location's record (design system · Weather.jsx LocationWeatherHistorySection
/// and WeatherHistoryChart, components-weather.md → Weather history): the window, its bounds from
/// the daily response, the custom range, the measures and the states.
/// </summary>
[Collection(nameof(WeatherWidgetSeam))]
public class LocationWeatherHistoryTests : IDisposable
{
    private static readonly DateOnly Today = new(2026, 10, 28);
    private static readonly DateOnly Yesterday = Today.AddDays(-1);

    private readonly Mock<IWeatherApiClient> weather = new();
    private readonly List<(DateOnly From, DateOnly To)> requests = [];

    public LocationWeatherHistoryTests() => LocationWeatherHistorySection.Today = static () => Today;

    public void Dispose() => LocationWeatherHistorySection.Today = static () => DateOnly.FromDateTime(DateTime.Now);

    // ── The window ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_first_window_is_sixty_days_ending_yesterday_with_facts_chart_and_attribution()
    {
        Serve(measuredFrom: new DateOnly(2025, 3, 2), measuredThrough: Yesterday);
        await using var ctx = Context();

        var section = Render(ctx);

        section.WaitForAssertion(() => Assert.Contains("Days measured", section.Markup));
        Assert.Equal((Yesterday.AddDays(-59), Yesterday), Assert.Single(requests));
        Assert.Contains("60</b> of 60", section.Markup);
        Assert.Single(section.FindComponents<WeatherHistoryChart>());
        Assert.Contains("Weather data from MET Norway", section.Markup);
        Assert.Equal("60", section.FindComponent<OdsSegmentedControl>().Instance.Value);
        // OdsIconButton draws an SVG icon constant; a Material ligature would render an empty button.
        Assert.All(section.FindComponents<OdsIconButton>(), b => Assert.StartsWith("<", b.Instance.Icon, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_history_off_place_re_anchors_on_its_last_measured_day_and_says_so()
    {
        var stopped = new DateOnly(2026, 8, 30);
        Serve(measuredFrom: new DateOnly(2026, 1, 1), measuredThrough: stopped);
        await using var ctx = Context();

        var section = Render(ctx, Row(enabled: false));

        section.WaitForAssertion(() => Assert.Equal(2, requests.Count));
        Assert.Equal((stopped.AddDays(-59), stopped), requests[1]);
        section.WaitForAssertion(() => Assert.Contains("Historical data is turned off for this place.", section.Markup));
    }

    [Fact]
    public async Task Choosing_365_days_asks_for_a_year_in_one_request()
    {
        Serve(measuredFrom: new DateOnly(2024, 1, 1), measuredThrough: Yesterday);
        await using var ctx = Context();
        var section = Render(ctx);
        section.WaitForAssertion(() => Assert.Contains("Days measured", section.Markup));

        await section.InvokeAsync(() => section.FindComponent<OdsSegmentedControl>().Instance.ValueChanged.InvokeAsync("365"));

        section.WaitForAssertion(() => Assert.Equal((Yesterday.AddDays(-364), Yesterday), requests[^1]));
        Assert.Equal("365", section.FindComponent<OdsSegmentedControl>().Instance.Value);
    }

    [Fact]
    public async Task A_picker_echoing_the_windows_own_date_is_not_a_custom_pick()
    {
        // MudDatePicker raises DateChanged when its value is set from code; that must not make the window custom.
        Serve(measuredFrom: new DateOnly(2025, 1, 1), measuredThrough: Yesterday);
        await using var ctx = Context();
        var section = Render(ctx);
        section.WaitForAssertion(() => Assert.Contains("Days measured", section.Markup));

        await section.InvokeAsync(() => Picker(section, "From").Instance.ValueChanged.InvokeAsync(Yesterday.AddDays(-59).ToDateTime(TimeOnly.MinValue)));
        await section.InvokeAsync(() => Picker(section, "To").Instance.ValueChanged.InvokeAsync(Yesterday.ToDateTime(TimeOnly.MinValue)));

        Assert.Single(requests);
        Assert.Equal("60", section.FindComponent<OdsSegmentedControl>().Instance.Value);
    }

    [Fact]
    public async Task Earlier_steps_back_a_whole_window_and_stops_at_the_first_full_one()
    {
        var start = Yesterday.AddDays(-89);
        Serve(measuredFrom: start, measuredThrough: Yesterday);
        await using var ctx = Context();
        var section = Render(ctx);
        section.WaitForAssertion(() => Assert.Contains("Days measured", section.Markup));

        await section.InvokeAsync(() => Nav(section, "Earlier").Instance.OnClick.InvokeAsync());

        // 90 days of history hold one and a half windows: Earlier lands on the first full one, not a stub.
        section.WaitForAssertion(() => Assert.Equal((start, start.AddDays(59)), requests[^1]));
        section.WaitForAssertion(() => Assert.True(Nav(section, "Earlier").Instance.Disabled));
    }

    [Fact]
    public async Task Picking_a_from_date_makes_the_window_a_custom_range()
    {
        Serve(measuredFrom: new DateOnly(2025, 1, 1), measuredThrough: Yesterday);
        await using var ctx = Context();
        var section = Render(ctx);
        section.WaitForAssertion(() => Assert.Contains("Days measured", section.Markup));
        var from = new DateOnly(2026, 6, 1);

        await section.InvokeAsync(() => Picker(section, "From").Instance.ValueChanged.InvokeAsync(from.ToDateTime(TimeOnly.MinValue)));

        section.WaitForAssertion(() => Assert.Equal((from, Yesterday), requests[^1]));
        Assert.Equal("custom", section.FindComponent<OdsSegmentedControl>().Instance.Value);
        // Each end is bounded by the cap measured from the other, so no pick can exceed it.
        Assert.Equal(Yesterday.AddDays(-(WeatherRules.DailyMaxDays - 1)).ToDateTime(TimeOnly.MinValue), Picker(section, "From").Instance.Min);
        Assert.Equal(Yesterday.ToDateTime(TimeOnly.MinValue), Picker(section, "To").Instance.Max);
    }

    [Fact]
    public async Task Clearing_a_date_changes_nothing_because_the_window_always_has_two_ends()
    {
        Serve(measuredFrom: new DateOnly(2025, 1, 1), measuredThrough: Yesterday);
        await using var ctx = Context();
        var section = Render(ctx);
        section.WaitForAssertion(() => Assert.Contains("Days measured", section.Markup));

        await section.InvokeAsync(() => Picker(section, "To").Instance.ValueChanged.InvokeAsync(null));

        Assert.Single(requests);
    }

    // ── States ──────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task No_station_says_there_is_no_history_and_forecasts_are_unaffected()
    {
        Serve(measuredFrom: null, measuredThrough: null, station: false);
        await using var ctx = Context();

        var section = Render(ctx);

        section.WaitForAssertion(() => Assert.Contains("No MET Norway station measures within 25 km", section.Markup));
        Assert.Empty(section.FindComponents<OdsSegmentedControl>());
    }

    [Fact]
    public async Task Without_a_frost_client_id_an_admin_is_pointed_at_system_settings()
    {
        Serve(measuredFrom: null, measuredThrough: null, configured: false);
        await using var ctx = Context();

        var section = Render(ctx, Row(enabled: true), canSetUp: true, canOpenSettings: true);

        section.WaitForAssertion(() => Assert.Contains("Historical weather service is not configured. See", section.Markup));
        var link = section.Find("a.wx-link");
        Assert.Equal("/settings", link.GetAttribute("href"));
        Assert.Equal("system settings", link.TextContent);
        Assert.DoesNotContain("No MET Norway station", section.Markup);
        Assert.DoesNotContain("check back later", section.Markup);
    }

    [Fact]
    public async Task Without_a_frost_client_id_everyone_else_gets_the_message_without_the_link()
    {
        Serve(measuredFrom: null, measuredThrough: null, configured: false);
        await using var ctx = Context();

        var section = Render(ctx);

        section.WaitForAssertion(() => Assert.Contains("Historical weather service is not configured.", section.Markup));
        Assert.Empty(section.FindAll("a.wx-link"));
    }

    [Fact]
    public async Task History_turned_off_outranks_an_unconfigured_frost()
    {
        // Nothing would be fetched anyway: the place's own switch is the answer, not the instance's setup.
        Serve(measuredFrom: null, measuredThrough: null, configured: false);
        await using var ctx = Context();

        var section = Render(ctx, Row(enabled: false), canSetUp: true, canOpenSettings: true);

        section.WaitForAssertion(() => Assert.Contains("Historical data is turned off for this place.", section.Markup));
        Assert.DoesNotContain("not configured", section.Markup);
    }

    [Fact]
    public async Task History_stored_before_frost_was_unset_still_shows()
    {
        Serve(measuredFrom: new DateOnly(2025, 1, 1), measuredThrough: Yesterday, configured: false);
        await using var ctx = Context();

        var section = Render(ctx, canSetUp: true);

        section.WaitForAssertion(() => Assert.Contains("Days measured", section.Markup));
        Assert.DoesNotContain("not configured", section.Markup);
    }

    [Theory]
    [InlineData(true, "No measured weather yet.")]
    [InlineData(false, "Historical data is turned off for this place.")]
    public async Task Nothing_stored_names_the_reason(bool enabled, string expected)
    {
        Serve(measuredFrom: null, measuredThrough: null);
        await using var ctx = Context();

        var section = Render(ctx, Row(enabled));

        section.WaitForAssertion(() => Assert.Contains(expected, section.Markup));
    }

    [Fact]
    public async Task A_429_rests_with_try_again_and_no_attribution()
    {
        weather.Setup(w => w.DailyAsync(It.IsAny<DateOnly>(), It.IsAny<DateOnly>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResult<WeatherDailyResponse>.Failure(HttpStatusCode.TooManyRequests, new ApiProblem { Status = 429 }));
        await using var ctx = Context();

        var section = Render(ctx);

        section.WaitForAssertion(() => Assert.Contains("Weather is resting for a moment", section.Markup));
        Assert.Contains("Try again", section.Markup);
        Assert.DoesNotContain("Weather data from MET Norway", section.Markup);
    }

    [Fact]
    public async Task A_purge_in_progress_renders_nothing()
    {
        Serve(measuredFrom: new DateOnly(2025, 1, 1), measuredThrough: Yesterday);
        await using var ctx = Context();

        var section = Render(ctx, Row(enabled: false) with { PurgeInProgress = true });

        Assert.DoesNotContain("Weather history", section.Markup);
    }

    // ── The chart ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_missing_day_breaks_the_band_and_a_lone_day_is_a_bar_with_a_dot()
    {
        var from = new DateOnly(2026, 10, 1);
        // Days 0–2 measured, 3–4 missing, 5 alone, 6 missing, 7–9 measured.
        int[] measured = [0, 1, 2, 5, 7, 8, 9];
        await using var ctx = Context();

        var chart = ctx.Render<WeatherHistoryChart>(p => p
            .Add(c => c.Days, [.. measured.Select(i => Day(from.AddDays(i), 2 + i))])
            .Add(c => c.From, from).Add(c => c.To, from.AddDays(9)).Add(c => c.LocationName, "Cabin"));

        Assert.Equal(2, chart.FindAll(".odc-line-svg path.wx-hist-band").Count);
        Assert.Single(chart.FindAll(".odc-line-svg rect.wx-hist-band"));
        Assert.Equal(2, chart.FindAll(".odc-line-svg polyline.wx-hist-mean").Count);
        Assert.Single(chart.FindAll(".odc-line-svg circle.wx-hist-dot"));
        Assert.Contains("3 of 10 days were not measured", chart.Markup);
        Assert.Contains("Break: not measured", chart.Markup);
        Assert.Equal(3, chart.FindAll("td[colspan='3']").Count);
    }

    [Fact]
    public async Task Precipitation_draws_a_bar_per_measured_day_and_shades_the_missing_ones()
    {
        var from = new DateOnly(2026, 10, 1);
        int[] measured = [0, 1, 2, 5, 7, 8, 9];
        await using var ctx = Context();

        var chart = ctx.Render<WeatherHistoryChart>(p => p
            .Add(c => c.Days, [.. measured.Select(i => Day(from.AddDays(i), 2 + i))])
            .Add(c => c.From, from).Add(c => c.To, from.AddDays(9)).Add(c => c.LocationName, "Cabin")
            .Add(c => c.Measures, [WeatherMeasures.Precipitation]));

        Assert.Equal(7, chart.FindAll(".odc-line-svg rect.wx-hist-bar-mark").Count);
        // A missing bar would read as a dry day, so the day is shaded instead.
        Assert.Equal(3, chart.FindAll(".odc-line-svg rect.wx-hist-nodata").Count);
        Assert.Empty(chart.FindAll(".odc-line-svg .wx-hist-band"));
        Assert.Contains("Shaded: not measured", chart.Markup);
        // (1+1.5+2+3.5+4.5+5+5.5) = 23 mm over the window; the amount axis names its unit once.
        Assert.Contains("23.0 mm total", chart.Find(".wx-hist-legend").TextContent);
        Assert.Contains(">mm</text>", chart.Markup);
        Assert.Equal(["Date", "Precipitation"], chart.FindAll("thead th").Select(th => th.TextContent));
    }

    [Fact]
    public async Task Wind_and_gusts_share_the_metres_per_second_axis_told_apart_by_shape()
    {
        var from = new DateOnly(2026, 10, 1);
        await using var ctx = Context();

        var chart = ctx.Render<WeatherHistoryChart>(p => p
            .Add(c => c.Days, [.. Enumerable.Range(0, 10).Select(i => Day(from.AddDays(i), 5))])
            .Add(c => c.From, from).Add(c => c.To, from.AddDays(9)).Add(c => c.LocationName, "Cabin")
            .Add(c => c.Measures, [WeatherMeasures.Wind, WeatherMeasures.Gust]));

        Assert.Single(chart.FindAll(".odc-line-svg polyline.wx-hist-series:not(.thin)"));
        Assert.Single(chart.FindAll(".odc-line-svg polyline.wx-hist-series.thin"));
        // Gusts carry a dot per day up to 90 days; mean wind is the plain line.
        Assert.Equal(10, chart.FindAll(".odc-line-svg circle").Count);
        var ledger = chart.Find(".wx-hist-legend").TextContent;
        Assert.Contains("3.0 m/s avg", ledger);
        Assert.Contains("8.0 m/s max", ledger);
        Assert.DoesNotContain("not measured", ledger);
        Assert.Contains(">m/s</text>", chart.Markup);
        Assert.Contains("mean wind and gusts", chart.Find("svg").GetAttribute("aria-label"));
    }

    [Fact]
    public async Task Choosing_a_measure_in_another_unit_replaces_the_selection_and_its_facts()
    {
        Serve(measuredFrom: new DateOnly(2025, 3, 2), measuredThrough: Yesterday);
        await using var ctx = Context();
        var section = Render(ctx);
        section.WaitForAssertion(() => Assert.Contains("Days measured", section.Markup));
        var picker = section.FindComponent<OdsMultiSelect>();
        Assert.Equal("Measures", picker.Instance.Label);
        // Only a selected row carries its hue.
        Assert.Equal(["var(--brand-text)", null, null, null], picker.Instance.Options.Select(o => o.IconColor));

        await section.InvokeAsync(() => picker.Instance.ValuesChanged.InvokeAsync([WeatherMeasures.Temperature, WeatherMeasures.Precipitation]));

        Assert.Equal([WeatherMeasures.Precipitation], section.FindComponent<WeatherHistoryChart>().Instance.Measures);
        Assert.Contains("Wettest day", section.Markup);
        Assert.Contains("Days with precipitation", section.Markup);
        Assert.DoesNotContain("Average of daily means", section.Markup);
        // A change of measure is client-side: one response carries every measure.
        Assert.Single(requests);
    }

    [Theory]
    [InlineData(new[] { "temp" }, new[] { "temp", "precip" }, new[] { "precip" })]
    [InlineData(new[] { "wind" }, new[] { "wind", "gust" }, new[] { "wind", "gust" })]
    [InlineData(new[] { "wind", "gust" }, new[] { "gust" }, new[] { "gust" })]
    [InlineData(new[] { "precip" }, new string[0], new[] { "precip" })]
    [InlineData(new[] { "wind", "gust" }, new[] { "wind", "gust", "temp" }, new[] { "temp" })]
    public void The_selection_never_empties_and_never_mixes_units(string[] current, string[] next, string[] expected) =>
        Assert.Equal(expected, WeatherMeasures.NextSelection(current, next));

    [Fact]
    public void The_window_facts_follow_the_unit_plotted()
    {
        var from = new DateOnly(2026, 10, 1);
        var days = Enumerable.Range(0, 4).Select(i => Day(from.AddDays(i), i * 2)).ToList();

        var mm = LocationWeatherHistorySection.FactsFor(days, 5, "mm");
        Assert.Equal(["Total", "Wettest day", "Days with precipitation", "Days measured"], mm.Select(f => f.Label));
        Assert.Equal("6.0 mm", mm[0].Value);
        Assert.Equal(from.AddDays(3), mm[1].Date);
        // Day 0 has 0 mm, below the 0.1 mm a gauge reports.
        Assert.Equal("3", mm[2].Value);
        Assert.Equal((" of 5"), mm[3].After);

        var ms = LocationWeatherHistorySection.FactsFor(days, 5, "ms");
        Assert.Equal(["Average wind", "Windiest day", "Strongest gust", "Days measured"], ms.Select(f => f.Label));
        Assert.Equal("8.0 m/s", ms[2].Value);
    }

    [Fact]
    public void A_year_is_labelled_by_month_with_the_year_on_january_and_the_first_label()
    {
        var labels = WeatherHistoryChart.DateLabels(new DateOnly(2025, 10, 28), 365, 1100);

        Assert.Equal("Nov 2025", labels[0].Text);
        Assert.Contains(labels, l => l.Text == "Jan 2026");
        Assert.Contains(labels, l => l.Text == "Feb");
        Assert.All(labels, l => Assert.True(l.Start));
    }

    [Fact]
    public void Up_to_ninety_days_is_labelled_from_the_newest_day_back()
    {
        var labels = WeatherHistoryChart.DateLabels(new DateOnly(2026, 9, 1), 60, 1100);

        Assert.Equal(59, labels[^1].Index);
        Assert.All(labels, l => Assert.False(l.Start));
    }

    [Theory]
    [InlineData("2026-10-27", 60, "2026-10-27")]
    [InlineData("2026-12-01", 60, "2026-10-27")]
    [InlineData("2026-03-01", 60, "2026-04-30")]
    [InlineData("2026-03-01", 365, "2026-10-27")]
    public void A_window_stays_whole_and_inside_the_stored_history(string end, int span, string expected) =>
        Assert.Equal(DateOnly.Parse(expected), LocationWeatherHistorySection.ClampEnd(
            DateOnly.Parse(end), span, new DateOnly(2026, 3, 2), new DateOnly(2026, 10, 27)));

    // ── Harness ─────────────────────────────────────────────────────────────────────────────────────

    private static readonly ExistingLocation Cabin = new()
    {
        LocationId = Guid.Parse("22222222-2222-2222-2222-222222222222"),
        Name = "Cabin",
        Latitude = 61.52914m,
        Longitude = 10.13802m,
        TimeZoneId = "Europe/Oslo",
    };

    /// <summary>The Admin row; <paramref name="enabled"/> is historical data — the half this section follows.</summary>
    private static ExistingWeatherLocation Row(bool enabled) => new()
    {
        LocationId = Cabin.LocationId,
        Name = Cabin.Name,
        ForecastEnabled = true,
        HistoryEnabled = enabled,
        Site = new WeatherLocationSiteDto { Version = 1, TimeZoneId = "Europe/Oslo" },
    };

    private static WeatherDaySummary Day(DateOnly date, decimal mean) => new()
    {
        Date = date,
        Observed = new WeatherDayObserved
        {
            MinTempC = mean - 3, MaxTempC = mean + 3, MeanTempC = mean,
            PrecipitationMm = mean / 2, MeanWindSpeedMps = 3, MaxWindGustMps = 8,
        },
    };

    /// <summary>Answers every daily request with each measured day of the range, and the given bounds.</summary>
    private void Serve(DateOnly? measuredFrom, DateOnly? measuredThrough, bool station = true, bool configured = true) =>
        weather.Setup(w => w.DailyAsync(It.IsAny<DateOnly>(), It.IsAny<DateOnly>(), Cabin.LocationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((DateOnly from, DateOnly to, Guid? _, CancellationToken _) =>
            {
                requests.Add((from, to));
                var days = measuredFrom is { } first && measuredThrough is { } last
                    ? Enumerable.Range(0, to.DayNumber - from.DayNumber + 1).Select(from.AddDays)
                        .Where(d => d >= first && d <= last).Select(d => Day(d, 5)).ToList()
                    : [];
                return ApiResult<WeatherDailyResponse>.Success(new WeatherDailyResponse
                {
                    Location = new WeatherLocationRef { LocationId = Cabin.LocationId, Name = Cabin.Name },
                    Status = days.Count > 0 ? WeatherStatus.Ok : WeatherStatus.NoData,
                    Days = days,
                    History = new WeatherHistoryBounds { MeasuredFrom = measuredFrom, MeasuredThrough = measuredThrough, HasObservationStation = station, ObservationsConfigured = configured },
                }, HttpStatusCode.OK);
            });

    private IRenderedComponent<LocationWeatherHistorySection> Render(
        BunitContext ctx, ExistingWeatherLocation? row = null, bool canSetUp = false, bool canOpenSettings = false) =>
        ctx.Render<LocationWeatherHistorySection>(p => p.Add(c => c.Location, Cabin).Add(c => c.Row, row)
            .Add(c => c.CanSetUp, canSetUp).Add(c => c.CanOpenSettings, canOpenSettings));

    private static IRenderedComponent<OdsIconButton> Nav(IRenderedComponent<LocationWeatherHistorySection> section, string direction) =>
        section.FindComponents<OdsIconButton>().Single(b => b.Instance.AriaLabel.StartsWith(direction, StringComparison.Ordinal));

    private static IRenderedComponent<OdsDatePicker> Picker(IRenderedComponent<LocationWeatherHistorySection> section, string end) =>
        section.FindComponents<OdsDatePicker>().Single(p => p.Instance.AriaLabel == end);

    private BunitContext Context()
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        ctx.Services.AddSingleton(weather.Object);
        return ctx;
    }
}
