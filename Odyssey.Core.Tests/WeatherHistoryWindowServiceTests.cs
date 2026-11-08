using Microsoft.EntityFrameworkCore;
using Odyssey.Context;
using Odyssey.Context.Secrets;
using Odyssey.Core.Weather;
using Odyssey.Dtos.Journal;
using Xunit;

namespace Odyssey.Core.Tests;

/// <summary>History windows through the point service and the fetcher (issue #436), on EF InMemory.</summary>
public class WeatherHistoryWindowServiceTests
{
    // 09:00 UTC is 10:00 in Oslo: past the morning window, so the last complete day is 6 November.
    private static readonly DateTimeOffset Morning = new(2026, 11, 7, 9, 0, 0, TimeSpan.Zero);

    private static WeatherHistoryWindowDto W(string from, string? to = null) =>
        new() { From = DateOnly.Parse(from), To = to is null ? null : DateOnly.Parse(to) };

    private static async Task<Guid> CreateAsync(WeatherTestHarness h, bool history = true, params WeatherHistoryWindowDto[] windows)
    {
        var point = await h.Admin().CreateAsync(new NewWeatherPoint
        {
            Name = "Cabin", Latitude = 61.517m, Longitude = 10.165m, TimeZoneId = "Europe/Oslo",
            ForecastEnabled = false, HistoryEnabled = history, IsTracked = false,
            HistoryWindows = windows.Length == 0 ? null : [.. windows],
        }, WeatherTestHarness.AdminId, CancellationToken.None);
        h.Context.ChangeTracker.Clear();
        return point.WeatherPointId;
    }

    /// <summary>Every group already has its station, so a run's one request is a daily fetch.</summary>
    private static async Task ResolveStationsAsync(WeatherTestHarness h, Guid pointId)
    {
        await using var context = h.NewContext();
        (await context.WeatherPoints.SingleAsync(p => p.WeatherPointId == pointId)).StationResolvedAtUtc = h.Time.GetUtcNow().UtcDateTime;
        foreach (var group in new[] { WeatherMeasureGroup.Temperature, WeatherMeasureGroup.Precipitation, WeatherMeasureGroup.Wind })
        {
            context.WeatherStations.Add(new WeatherStation
            {
                WeatherPointId = pointId, Group = group, StationId = "SN18700", ResolvedAtUtc = h.Time.GetUtcNow().UtcDateTime,
            });
        }

        await context.SaveChangesAsync();
    }

    private static ObservationFetchResult EveryDay(DateOnly from, DateOnly to)
    {
        var days = new List<ObservationDayData>();
        for (var d = from; d <= to; d = d.AddDays(1))
        {
            days.Add(new ObservationDayData(d, 1, 0, 2, 3, 5, 0.4m));
        }

        return new ObservationFetchResult(true, days);
    }

    private static Task RunAsync(WeatherTestHarness h, Guid pointId) =>
        h.Fetch(h.NewContext()).FetchObservationsAsync(pointId, CancellationToken.None);

    [Fact]
    public async Task Create_without_windows_stores_one_ongoing_window_from_a_year_back_even_with_history_off()
    {
        using var h = new WeatherTestHarness(Morning);
        var id = await CreateAsync(h, history: false);

        var window = await h.NewContext().WeatherPointHistoryWindows.SingleAsync();
        Assert.Equal((id, new DateOnly(2025, 11, 7), (DateOnly?)null), (window.WeatherPointId, window.FromDate, window.ToDate));
    }

    [Fact]
    public async Task Two_past_trips_are_fetched_in_one_request_and_only_their_days_are_stored()
    {
        using var h = new WeatherTestHarness(Morning);
        h.Secrets.Frost = SecretResult.Found("client-id");
        var id = await CreateAsync(h, true, W("2026-02-10", "2026-02-17"), W("2025-07-01", "2025-07-07"));
        await ResolveStationsAsync(h, id);
        h.Observations.Daily = EveryDay;

        await RunAsync(h, id);
        await RunAsync(h, id);

        // One request whose hull spans both trips (232 days fits one 365-day chunk); nothing on the second run.
        var call = Assert.Single(h.Observations.DailyCalls);
        Assert.Equal((new DateOnly(2025, 7, 1), new DateOnly(2026, 2, 17)), (call.From, call.To));
        var stored = await h.NewContext().WeatherObservations.Select(o => o.LocalDate).ToListAsync();
        Assert.Equal(15, stored.Count);
        Assert.All(stored, d => Assert.True(
            (d >= new DateOnly(2025, 7, 1) && d <= new DateOnly(2025, 7, 7)) || (d >= new DateOnly(2026, 2, 10) && d <= new DateOnly(2026, 2, 17))));

        var point = await h.Admin().GetAsync(id, canReadLocations: true, CancellationToken.None);
        Assert.All(point!.HistoryWindows!, w => Assert.Equal(WeatherHistoryWindowStatus.Complete, w.Status));
    }

    [Fact]
    public async Task An_empty_frost_answer_is_recorded_as_checked_and_not_asked_again()
    {
        using var h = new WeatherTestHarness(Morning);
        h.Secrets.Frost = SecretResult.Found("client-id");
        var id = await CreateAsync(h, true, W("2026-02-10", "2026-02-17"));
        await ResolveStationsAsync(h, id);

        await RunAsync(h, id);
        await RunAsync(h, id);

        Assert.Single(h.Observations.DailyCalls);
        var coverage = await h.NewContext().WeatherObservationCoverages.SingleAsync();
        Assert.Equal((new DateOnly(2026, 2, 10), new DateOnly(2026, 2, 17)), (coverage.FromDate, coverage.ToDate));
    }

    [Fact]
    public async Task A_failed_request_is_not_recorded_as_checked()
    {
        using var h = new WeatherTestHarness(Morning);
        h.Secrets.Frost = SecretResult.Found("client-id");
        var id = await CreateAsync(h, true, W("2026-02-10", "2026-02-17"));
        await ResolveStationsAsync(h, id);
        h.Observations.Daily = (_, _) => new ObservationFetchResult(false, [], WeatherFetchFailureKind.ProviderError);

        await RunAsync(h, id);

        Assert.Empty(await h.NewContext().WeatherObservationCoverages.ToListAsync());
    }

    [Fact]
    public async Task A_window_added_after_the_morning_check_is_fetched_on_the_next_tick()
    {
        using var h = new WeatherTestHarness(Morning);
        h.Secrets.Frost = SecretResult.Found("client-id");
        var id = await CreateAsync(h, true, W("2026-02-10", "2026-02-17"));
        await ResolveStationsAsync(h, id);
        await RunAsync(h, id); // the morning: the trip, then the re-check is satisfied with no window in the last week

        var admin = h.Admin();
        var current = (await admin.GetAsync(id, true, CancellationToken.None))!;
        var update = WeatherPointServiceTestExtensions.Update(current);
        update.HistoryWindows = [W("2026-02-10", "2026-02-17"), W("2026-11-01", "2026-11-08")];
        await admin.UpdateAsync(id, update, WeatherTestHarness.AdminId, true, CancellationToken.None);
        h.Context.ChangeTracker.Clear();

        var schedule = await h.NewContext().WeatherPointSchedules.SingleAsync();
        Assert.Equal(h.Time.GetUtcNow().UtcDateTime, schedule.NextObservationFetchAfterUtc);
        var status = (await h.Admin().GetAsync(id, true, CancellationToken.None))!.HistoryWindows![1].Status;
        Assert.Equal(WeatherHistoryWindowStatus.Backfilling, status);

        await RunAsync(h, id);
        Assert.Equal((new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 6)), h.Observations.DailyCalls[^1]);
    }

    [Fact]
    public async Task A_window_edit_rotates_the_fetch_fence_and_an_identical_set_changes_nothing()
    {
        using var h = new WeatherTestHarness(Morning);
        var id = await CreateAsync(h, true, W("2026-02-10", "2026-02-17"));
        var before = (await h.NewContext().WeatherPointSchedules.SingleAsync()).Version;

        var current = (await h.Admin().GetAsync(id, true, CancellationToken.None))!;
        var same = WeatherPointServiceTestExtensions.Update(current);
        same.HistoryWindows = [W("2026-02-10", "2026-02-17")];
        await h.Admin().UpdateAsync(id, same, WeatherTestHarness.AdminId, true, CancellationToken.None);
        Assert.Equal(before, (await h.NewContext().WeatherPointSchedules.SingleAsync()).Version);

        current = (await h.Admin().GetAsync(id, true, CancellationToken.None))!;
        var widened = WeatherPointServiceTestExtensions.Update(current);
        widened.HistoryWindows = [W("2026-02-10", "2026-02-20")];
        await h.Admin().UpdateAsync(id, widened, WeatherTestHarness.AdminId, true, CancellationToken.None);
        Assert.NotEqual(before, (await h.NewContext().WeatherPointSchedules.SingleAsync()).Version);
    }

    [Fact]
    public async Task Changing_an_end_keeps_the_row_and_a_changed_start_replaces_it()
    {
        using var h = new WeatherTestHarness(Morning);
        var id = await CreateAsync(h, true, W("2026-02-10", "2026-02-17"), W("2026-06-01", "2026-06-07"));
        var rows = await h.NewContext().WeatherPointHistoryWindows.ToDictionaryAsync(w => w.FromDate, w => w.WeatherPointHistoryWindowId);

        var current = (await h.Admin().GetAsync(id, true, CancellationToken.None))!;
        var update = WeatherPointServiceTestExtensions.Update(current);
        update.HistoryWindows = [W("2026-02-10", "2026-02-20"), W("2026-06-02", "2026-06-07")];
        await h.Admin().UpdateAsync(id, update, WeatherTestHarness.AdminId, true, CancellationToken.None);

        var after = await h.NewContext().WeatherPointHistoryWindows.OrderBy(w => w.FromDate).ToListAsync();
        Assert.Equal(rows[new DateOnly(2026, 2, 10)], after[0].WeatherPointHistoryWindowId);
        Assert.Equal(new DateOnly(2026, 2, 20), after[0].ToDate);
        Assert.Equal(new DateOnly(2026, 6, 2), after[1].FromDate);
        Assert.DoesNotContain(after[1].WeatherPointHistoryWindowId, rows.Values);
    }

    [Fact]
    public async Task Shrinking_a_window_keeps_what_was_stored()
    {
        using var h = new WeatherTestHarness(Morning);
        h.Secrets.Frost = SecretResult.Found("client-id");
        var id = await CreateAsync(h, true, W("2026-02-10", "2026-02-17"));
        await ResolveStationsAsync(h, id);
        h.Observations.Daily = EveryDay;
        await RunAsync(h, id);

        var current = (await h.Admin().GetAsync(id, true, CancellationToken.None))!;
        var update = WeatherPointServiceTestExtensions.Update(current);
        update.HistoryWindows = [W("2026-02-10", "2026-02-11")];
        await h.Admin().UpdateAsync(id, update, WeatherTestHarness.AdminId, true, CancellationToken.None);

        Assert.Equal(8, await h.NewContext().WeatherObservations.CountAsync());
    }

    [Fact]
    public async Task Turning_history_on_over_no_windows_stores_the_default_window()
    {
        using var h = new WeatherTestHarness(Morning);
        var id = await CreateAsync(h, false, W("2026-02-10", "2026-02-17"));
        var current = (await h.Admin().GetAsync(id, true, CancellationToken.None))!;
        var empty = WeatherPointServiceTestExtensions.Update(current);
        empty.HistoryWindows = [];
        await h.Admin().UpdateAsync(id, empty, WeatherTestHarness.AdminId, true, CancellationToken.None);
        Assert.Empty(await h.NewContext().WeatherPointHistoryWindows.ToListAsync());

        current = (await h.Admin().GetAsync(id, true, CancellationToken.None))!;
        var on = WeatherPointServiceTestExtensions.Update(current, history: true);
        await h.Admin().UpdateAsync(id, on, WeatherTestHarness.AdminId, true, CancellationToken.None);

        var window = await h.NewContext().WeatherPointHistoryWindows.SingleAsync();
        Assert.Equal((new DateOnly(2025, 11, 7), (DateOnly?)null), (window.FromDate, window.ToDate));
    }

    [Fact]
    public async Task The_list_carries_the_summary_and_only_the_single_read_carries_the_windows()
    {
        using var h = new WeatherTestHarness(Morning);
        var id = await CreateAsync(h, true, W("2026-02-10", "2026-02-17"), W("2026-12-27", "2027-01-03"));

        var page = await h.Admin().ListAsync(new WeatherPointsQueryParams(), canReadLocations: true, CancellationToken.None);
        var row = Assert.Single(page.Items);
        Assert.Null(row.HistoryWindows);
        Assert.Equal((2, new DateOnly(2026, 2, 10), (DateOnly?)new DateOnly(2027, 1, 3), false),
            (row.HistoryWindowSummary.Count, row.HistoryWindowSummary.EarliestFrom, row.HistoryWindowSummary.LatestTo, row.HistoryWindowSummary.HasOpenEnd));

        var single = (await h.Admin().GetAsync(id, true, CancellationToken.None))!;
        Assert.Equal([WeatherHistoryWindowStatus.Backfilling, WeatherHistoryWindowStatus.Upcoming], single.HistoryWindows!.Select(w => w.Status));
    }

    [Fact]
    public async Task The_history_purge_removes_coverage_and_keeps_the_windows()
    {
        using var h = new WeatherTestHarness(Morning);
        h.Secrets.Frost = SecretResult.Found("client-id");
        var id = await CreateAsync(h, true, W("2026-02-10", "2026-02-17"));
        await ResolveStationsAsync(h, id);
        h.Observations.Daily = EveryDay;
        await RunAsync(h, id);
        await h.Admin().SetAsync(id, forecast: false, history: false, tracked: false);

        Assert.True(await h.Admin().PurgeHistoryAsync(id, WeatherTestHarness.AdminId, CancellationToken.None));

        var check = h.NewContext();
        Assert.Empty(await check.WeatherObservationCoverages.ToListAsync());
        Assert.Single(await check.WeatherPointHistoryWindows.ToListAsync());
    }

    [Fact]
    public async Task Deleting_the_point_removes_its_windows_and_coverage()
    {
        using var h = new WeatherTestHarness(Morning);
        var id = await CreateAsync(h, false, W("2026-02-10", "2026-02-17"));
        await using (var context = h.NewContext())
        {
            context.WeatherObservationCoverages.Add(new WeatherObservationCoverage
            {
                WeatherPointId = id, SiteVersion = 1, FromDate = new DateOnly(2026, 2, 10), ToDate = new DateOnly(2026, 2, 17),
            });
            await context.SaveChangesAsync();
        }

        Assert.True(await h.Admin().DeleteAsync(id, WeatherTestHarness.AdminId, CancellationToken.None));

        var check = h.NewContext();
        Assert.Empty(await check.WeatherPointHistoryWindows.ToListAsync());
        Assert.Empty(await check.WeatherObservationCoverages.ToListAsync());
    }

    [Fact]
    public async Task A_station_gained_for_a_group_clears_the_current_sites_coverage()
    {
        using var h = new WeatherTestHarness(Morning);
        h.Secrets.Frost = SecretResult.Found("client-id");
        var id = await CreateAsync(h, true, W("2026-02-10", "2026-02-17"));
        var old = h.Time.GetUtcNow().UtcDateTime.AddDays(-2);
        await using (var context = h.NewContext())
        {
            (await context.WeatherPoints.SingleAsync()).StationResolvedAtUtc = old;
            context.WeatherStations.Add(new WeatherStation { WeatherPointId = id, Group = WeatherMeasureGroup.Temperature, StationId = "SN18700", ResolvedAtUtc = old });
            context.WeatherStations.Add(new WeatherStation { WeatherPointId = id, Group = WeatherMeasureGroup.Precipitation, StationId = "SN18700", ResolvedAtUtc = old });
            // Wind found nothing two days ago, so it is due for another lookup — which now finds a station.
            context.WeatherStations.Add(new WeatherStation { WeatherPointId = id, Group = WeatherMeasureGroup.Wind, StationId = null, ResolvedAtUtc = old });
            // Part of the trip is still unchecked, so the run has work and looks the due group up first.
            context.WeatherObservationCoverages.Add(new WeatherObservationCoverage
            {
                WeatherPointId = id, SiteVersion = 1, FromDate = new DateOnly(2026, 2, 10), ToDate = new DateOnly(2026, 2, 14),
            });
            (await context.WeatherPointSchedules.SingleAsync()).ObservationsCheckedThroughDate = new DateOnly(2026, 11, 6);
            await context.SaveChangesAsync();
        }

        await RunAsync(h, id);

        var check = h.NewContext();
        Assert.Equal(WeatherMeasureGroup.Wind, Assert.Single(h.Observations.LookedUp));
        Assert.Empty(await check.WeatherObservationCoverages.ToListAsync());
        Assert.Null((await check.WeatherPointSchedules.SingleAsync()).ObservationsCheckedThroughDate);
    }

    [Fact]
    public async Task Window_writes_and_fetches_log_no_window_dates()
    {
        using var h = new WeatherTestHarness(Morning);
        var log = new RecordingLogger<WeatherPointService>();
        var admin = new WeatherPointService(h.Context, h.Fetch(), h.Settings, new TrackedWeatherHistoryPurger(h.Context), h.Time, log);
        var point = await admin.CreateAsync(new NewWeatherPoint
        {
            Name = "Cabin", Latitude = 61.5m, Longitude = 10.1m, TimeZoneId = "Europe/Oslo",
            ForecastEnabled = false, HistoryEnabled = true, IsTracked = false,
            HistoryWindows = [W("2026-02-10", "2026-02-17")],
        }, WeatherTestHarness.AdminId, CancellationToken.None);
        var update = WeatherPointServiceTestExtensions.Update(point);
        update.HistoryWindows = [W("2026-03-10", "2026-03-17")];
        await new WeatherPointService(h.NewContext(), h.Fetch(), h.Settings, new TrackedWeatherHistoryPurger(h.NewContext()), h.Time, log)
            .UpdateAsync(point.WeatherPointId, update, WeatherTestHarness.AdminId, true, CancellationToken.None);

        Assert.Contains(log.Lines, l => l.Contains("HistoryWindows", StringComparison.Ordinal) && l.Contains("1 -> 1", StringComparison.Ordinal));
        Assert.All(log.Lines, l =>
        {
            Assert.DoesNotContain("2026-02", l, StringComparison.Ordinal);
            Assert.DoesNotContain("2026-03", l, StringComparison.Ordinal);
            Assert.DoesNotContain("10.02.2026", l, StringComparison.Ordinal);
        });
    }

    private sealed class RecordingLogger<T> : Microsoft.Extensions.Logging.ILogger<T>
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(
            Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Lines.Add(formatter(state, exception));
    }
}
