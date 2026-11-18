using Microsoft.EntityFrameworkCore;
using Odyssey.Context;
using Odyssey.Context.Secrets;
using Odyssey.Core.Weather;
using Odyssey.Dtos.Journal;
using Xunit;

namespace Odyssey.Core.Tests;

/// <summary>
/// The fetcher's window behaviour beyond the happy path (issue #436 §3.3–§3.5, AC 12, 13a/b, 15, 16, 22): the fence
/// race with a window edit, retry after back-off, the clipped recent re-check, the next wake-up, a station gain on a
/// moved point, the tracked history purge's markers, and a 2 000-window replace through the point service.
/// </summary>
public class WeatherHistoryWindowFetchTests
{
    // 09:00 UTC is 10:00 in Oslo: past the morning window, so the last complete day is 6 November.
    private static readonly DateTimeOffset Morning = new(2026, 11, 7, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly LastComplete = new(2026, 11, 6);

    private static WeatherHistoryWindowDto W(string from, string? to = null) =>
        new() { From = DateOnly.Parse(from), To = to is null ? null : DateOnly.Parse(to) };

    private static DateOnly D(string date) => DateOnly.Parse(date);

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

    /// <summary>Seeds the re-check marker and current-site coverage, as an earlier run would have left them.</summary>
    private static async Task SeedAsync(WeatherTestHarness h, Guid pointId, DateOnly? checkedThrough, params (string From, string To)[] covered)
    {
        await using var context = h.NewContext();
        (await context.WeatherPointSchedules.SingleAsync(s => s.WeatherPointId == pointId)).ObservationsCheckedThroughDate = checkedThrough;
        foreach (var (from, to) in covered)
        {
            context.WeatherObservationCoverages.Add(new WeatherObservationCoverage
            {
                WeatherPointId = pointId, SiteVersion = 1, FromDate = D(from), ToDate = D(to),
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

    private static Task<WeatherPointSchedule> ScheduleAsync(WeatherTestHarness h) =>
        h.NewContext().WeatherPointSchedules.AsNoTracking().SingleAsync();

    private static async Task<List<(DateOnly From, DateOnly To)>> CoverageAsync(WeatherTestHarness h, int siteVersion = 1) =>
        (await h.NewContext().WeatherObservationCoverages.AsNoTracking()
            .Where(c => c.SiteVersion == siteVersion).OrderBy(c => c.FromDate).ToListAsync())
        .Select(c => (c.FromDate, c.ToDate)).ToList();

    private static async Task ReplaceWindowsAsync(WeatherTestHarness h, Guid pointId, OdysseyContext context, params WeatherHistoryWindowDto[] windows)
    {
        var admin = h.Admin(context);
        var current = (await admin.GetAsync(pointId, true, CancellationToken.None))!;
        var update = WeatherPointServiceTestExtensions.Update(current);
        update.HistoryWindows = [.. windows];
        Assert.NotNull(await admin.UpdateAsync(pointId, update, WeatherTestHarness.AdminId, true, CancellationToken.None));
    }

    // ── AC 13a: the fence race ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_window_edit_while_a_fetch_is_out_fences_it_off_and_the_next_tick_plans_from_the_new_set()
    {
        using var h = new WeatherTestHarness(Morning);
        h.Secrets.Frost = SecretResult.Found("client-id");
        var id = await CreateAsync(h, true, W("2026-02-10", "2026-02-17"));
        await ResolveStationsAsync(h, id);
        h.Observations.Daily = EveryDay;
        Guid versionAfterEdit = default;
        h.Observations.DuringDaily = async () =>
        {
            h.Observations.DuringDaily = null;
            // The Admin replaces the trip while the request for the old one is out, from another request's context.
            await ReplaceWindowsAsync(h, id, h.NewContext(), W("2026-03-01", "2026-03-05"));
            versionAfterEdit = (await ScheduleAsync(h)).Version;
        };

        await RunAsync(h, id);

        // The request for the old window was sent, but its save was fenced: the wake-up the edit set ("now") was not
        // overwritten by the fetch's own plan (which would be +24 h: no window left). That nothing was STORED is
        // asserted on MariaDB (WeatherHistoryWindowsRelationalTests): EF InMemory is not atomic, so the observation and
        // coverage inserts staged ahead of the refused schedule update land anyway on this provider.
        Assert.Equal((D("2026-02-10"), D("2026-02-17")), Assert.Single(h.Observations.DailyCalls));
        var schedule = await ScheduleAsync(h);
        Assert.Equal(versionAfterEdit, schedule.Version);
        Assert.Equal(Morning.UtcDateTime, schedule.NextObservationFetchAfterUtc);
        Assert.Null(schedule.LeaseUntilUtc);

        await RunAsync(h, id);

        Assert.Equal((D("2026-03-01"), D("2026-03-05")), h.Observations.DailyCalls[^1]);
        Assert.Contains((D("2026-03-01"), D("2026-03-05")), await CoverageAsync(h));
        Assert.Equal(5, await h.NewContext().WeatherObservations.CountAsync(o => o.LocalDate >= D("2026-03-01")));
    }

    // ── AC 12: retry after back-off ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_failed_chunk_waits_out_its_back_off_and_is_then_requested_again_and_covered()
    {
        using var h = new WeatherTestHarness(Morning);
        h.Secrets.Frost = SecretResult.Found("client-id");
        var id = await CreateAsync(h, true, W("2026-02-10", "2026-02-17"));
        await ResolveStationsAsync(h, id);
        var fail = true;
        h.Observations.Daily = (from, to) => fail
            ? new ObservationFetchResult(false, [], WeatherFetchFailureKind.ProviderError)
            : EveryDay(from, to);

        await RunAsync(h, id);
        var failed = await ScheduleAsync(h);
        Assert.Equal(WeatherFetchFailureKind.ProviderError, failed.ObservationFailureKind);
        Assert.True(failed.NextObservationFetchAfterUtc > Morning.UtcDateTime);
        Assert.Empty(await CoverageAsync(h));

        // Inside the back-off: nothing is sent.
        fail = false;
        await RunAsync(h, id);
        Assert.Single(h.Observations.DailyCalls);

        // A window edit during the back-off does not cut it short (§3.5).
        await ReplaceWindowsAsync(h, id, h.NewContext(), W("2026-02-10", "2026-02-18"));
        Assert.Equal(failed.NextObservationFetchAfterUtc, (await ScheduleAsync(h)).NextObservationFetchAfterUtc);

        h.Time.Now = new DateTimeOffset(failed.NextObservationFetchAfterUtc!.Value, TimeSpan.Zero).AddSeconds(1);
        await RunAsync(h, id);

        Assert.Equal(2, h.Observations.DailyCalls.Count);
        Assert.Equal((D("2026-02-10"), D("2026-02-18")), h.Observations.DailyCalls[^1]);
        Assert.Equal([(D("2026-02-10"), D("2026-02-18"))], await CoverageAsync(h));
        var recovered = await ScheduleAsync(h);
        Assert.Null(recovered.ObservationFailureKind);
        Assert.Equal(0, recovered.ObservationConsecutiveFailures);
    }

    // ── §3.3 step 3: the recent re-check is clipped to the windows ──────────────────────────────

    [Fact]
    public async Task A_re_check_with_no_window_in_the_last_week_sends_nothing_and_advances_the_marker()
    {
        using var h = new WeatherTestHarness(Morning);
        h.Secrets.Frost = SecretResult.Found("client-id");
        var id = await CreateAsync(h, true, W("2026-02-10", "2026-02-17"));
        await ResolveStationsAsync(h, id);
        await SeedAsync(h, id, checkedThrough: D("2026-11-01"), ("2026-02-10", "2026-02-17"));

        await RunAsync(h, id);

        Assert.Empty(h.Observations.DailyCalls);
        var schedule = await ScheduleAsync(h);
        Assert.Equal(LastComplete, schedule.ObservationsCheckedThroughDate);
        // Every window has ended and is covered: a database-only look in 24 h (§3.3 step 6).
        Assert.Equal(Morning.UtcDateTime.AddHours(24), schedule.NextObservationFetchAfterUtc);
    }

    [Fact]
    public async Task A_re_check_asks_only_for_the_window_days_of_the_last_week()
    {
        using var h = new WeatherTestHarness(Morning);
        h.Secrets.Frost = SecretResult.Found("client-id");
        var id = await CreateAsync(h, true, W("2026-11-03", "2026-11-04"));
        await ResolveStationsAsync(h, id);
        await SeedAsync(h, id, checkedThrough: D("2026-11-05"), ("2026-11-03", "2026-11-04"));
        h.Observations.Daily = EveryDay;

        await RunAsync(h, id);

        Assert.Equal((D("2026-11-03"), D("2026-11-04")), Assert.Single(h.Observations.DailyCalls));
        var schedule = await ScheduleAsync(h);
        Assert.Equal(LastComplete, schedule.ObservationsCheckedThroughDate);
        Assert.Equal(Morning.UtcDateTime.AddHours(24), schedule.NextObservationFetchAfterUtc);
    }

    [Fact]
    public async Task A_re_check_over_two_window_pieces_sends_their_hull_and_stores_only_window_days()
    {
        using var h = new WeatherTestHarness(Morning);
        h.Secrets.Frost = SecretResult.Found("client-id");
        var id = await CreateAsync(h, true, W("2026-11-01", "2026-11-02"), W("2026-11-05"));
        await ResolveStationsAsync(h, id);
        await SeedAsync(h, id, checkedThrough: D("2026-11-05"), ("2026-11-01", "2026-11-02"), ("2026-11-05", "2026-11-05"));
        h.Observations.Daily = EveryDay;

        await RunAsync(h, id);

        Assert.Equal((D("2026-11-01"), D("2026-11-06")), Assert.Single(h.Observations.DailyCalls));
        var stored = await h.NewContext().WeatherObservations.OrderBy(o => o.LocalDate).Select(o => o.LocalDate).ToListAsync();
        Assert.Equal([D("2026-11-01"), D("2026-11-02"), D("2026-11-05"), D("2026-11-06")], stored);
        // The days between the pieces were never asked for on their own account: they are not covered.
        Assert.Equal([(D("2026-11-01"), D("2026-11-02")), (D("2026-11-05"), D("2026-11-06"))], await CoverageAsync(h));
        // An ongoing window: the next morning window, 08:00 Oslo (UTC+1) tomorrow.
        Assert.Equal(new DateTime(2026, 11, 8, 7, 0, 0, DateTimeKind.Utc), (await ScheduleAsync(h)).NextObservationFetchAfterUtc);
    }

    // ── §3.3 step 6: the next wake-up ────────────────────────────────────────────────────────────

    [Fact]
    public async Task An_idle_point_with_a_current_window_wakes_the_next_morning()
    {
        using var h = new WeatherTestHarness(Morning);
        h.Secrets.Frost = SecretResult.Found("client-id");
        var id = await CreateAsync(h, true, W("2026-11-01"));
        await ResolveStationsAsync(h, id);
        await SeedAsync(h, id, checkedThrough: LastComplete, ("2026-11-01", "2026-11-06"));

        await RunAsync(h, id);

        Assert.Empty(h.Observations.DailyCalls);
        Assert.Equal(new DateTime(2026, 11, 8, 7, 0, 0, DateTimeKind.Utc), (await ScheduleAsync(h)).NextObservationFetchAfterUtc);
    }

    [Fact]
    public async Task A_point_with_only_upcoming_windows_wakes_the_morning_after_the_earliest_first_day()
    {
        using var h = new WeatherTestHarness(Morning);
        h.Secrets.Frost = SecretResult.Found("client-id");
        var id = await CreateAsync(h, true, W("2027-03-01", "2027-03-07"), W("2026-12-27", "2027-01-03"));
        await ResolveStationsAsync(h, id);

        await RunAsync(h, id);

        Assert.Empty(h.Observations.DailyCalls);
        var schedule = await ScheduleAsync(h);
        // 27 December completes at 08:00 Oslo on the 28th — 07:00 UTC in winter.
        Assert.Equal(new DateTime(2026, 12, 28, 7, 0, 0, DateTimeKind.Utc), schedule.NextObservationFetchAfterUtc);
        Assert.Equal(LastComplete, schedule.ObservationsCheckedThroughDate);
        Assert.Empty(await CoverageAsync(h));
    }

    // ── AC 13b: a shrink or a removal keeps stored observations and days ─────────────────────────

    [Theory]
    [InlineData("shrink")]
    [InlineData("remove")]
    [InlineData("history-off-empty")]
    public async Task Shrinking_or_removing_a_window_leaves_observations_and_days_untouched(string edit)
    {
        using var h = new WeatherTestHarness(Morning);
        h.Secrets.Frost = SecretResult.Found("client-id");
        var id = await CreateAsync(h, true, W("2026-02-10", "2026-02-17"));
        await ResolveStationsAsync(h, id);
        h.Observations.Daily = EveryDay;
        await RunAsync(h, id);

        var observationsBefore = await ObservationsAsync(h);
        var daysBefore = await DaysAsync(h);
        Assert.Equal(8, observationsBefore.Count);
        Assert.Equal(8, daysBefore.Count(d => d.ObservedFetchedAtUtc is not null));

        h.Time.Advance(TimeSpan.FromHours(1));
        switch (edit)
        {
            case "shrink":
                await ReplaceWindowsAsync(h, id, h.NewContext(), W("2026-02-10", "2026-02-11"));
                break;
            case "remove":
                await ReplaceWindowsAsync(h, id, h.NewContext(), W("2026-06-01", "2026-06-07"));
                break;
            default:
                var admin = h.Admin(h.NewContext());
                var current = (await admin.GetAsync(id, true, CancellationToken.None))!;
                var update = WeatherPointServiceTestExtensions.Update(current, history: false);
                update.HistoryWindows = [];
                await admin.UpdateAsync(id, update, WeatherTestHarness.AdminId, true, CancellationToken.None);
                break;
        }

        Assert.Equal(observationsBefore, await ObservationsAsync(h));
        Assert.Equal(daysBefore, await DaysAsync(h));
    }

    private static async Task<List<(Guid, DateOnly, decimal?, DateTime)>> ObservationsAsync(WeatherTestHarness h) =>
        (await h.NewContext().WeatherObservations.AsNoTracking().OrderBy(o => o.LocalDate).ToListAsync())
        .Select(o => (o.WeatherObservationId, o.LocalDate, o.MeanTempC, o.FetchedAtUtc)).ToList();

    private static async Task<List<WeatherDaySnapshot>> DaysAsync(WeatherTestHarness h) =>
        (await h.NewContext().WeatherDays.AsNoTracking().OrderBy(d => d.LocalDate).ToListAsync())
        .Select(d => new WeatherDaySnapshot(d.LocalDate, d.ObservedMinTempC, d.ObservedMaxTempC, d.ObservedMeanTempC,
            d.ObservedPrecipitationMm, d.ObservedFetchedAtUtc, d.ObservedSiteVersion, d.UpdatedAtUtc))
        .ToList();

    private sealed record WeatherDaySnapshot(
        DateOnly LocalDate, decimal? Min, decimal? Max, decimal? Mean, decimal? Precipitation, DateTime? ObservedFetchedAtUtc,
        int? ObservedSiteVersion, DateTime UpdatedAtUtc);

    // ── AC 15: a gained station clears only the current site's coverage ─────────────────────────

    [Fact]
    public async Task A_station_gained_on_a_moved_point_clears_the_current_sites_coverage_and_keeps_the_older_sites()
    {
        var created = new DateTimeOffset(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);
        using var h = new WeatherTestHarness(created);
        h.Secrets.Frost = SecretResult.Found("client-id");
        var id = await CreateAsync(h, true, W("2026-09-01"));
        Assert.NotNull(await h.Admin().SetAsync(id, forecast: false, history: true, tracked: false, latitude: 60.393m));
        h.Context.ChangeTracker.Clear();
        Assert.Equal(2, (await h.NewContext().WeatherPoints.SingleAsync()).CurrentSiteVersion);

        h.Time.Now = Morning;
        var old = Morning.UtcDateTime.AddDays(-2);
        await using (var context = h.NewContext())
        {
            (await context.WeatherPoints.SingleAsync()).StationResolvedAtUtc = old;
            context.WeatherStations.Add(new WeatherStation { WeatherPointId = id, Group = WeatherMeasureGroup.Temperature, StationId = "SN50540", ResolvedAtUtc = old });
            context.WeatherStations.Add(new WeatherStation { WeatherPointId = id, Group = WeatherMeasureGroup.Precipitation, StationId = "SN50540", ResolvedAtUtc = old });
            context.WeatherStations.Add(new WeatherStation { WeatherPointId = id, Group = WeatherMeasureGroup.Wind, StationId = null, ResolvedAtUtc = old });
            context.WeatherObservationCoverages.Add(new WeatherObservationCoverage
            {
                WeatherPointId = id, SiteVersion = 1, FromDate = D("2026-09-01"), ToDate = D("2026-09-30"),
            });
            // Site 2 is partly checked, so the run has backfill work and looks the due group up first.
            context.WeatherObservationCoverages.Add(new WeatherObservationCoverage
            {
                WeatherPointId = id, SiteVersion = 2, FromDate = D("2026-10-01"), ToDate = D("2026-10-20"),
            });
            (await context.WeatherPointSchedules.SingleAsync()).ObservationsCheckedThroughDate = LastComplete;
            (await context.WeatherPointSchedules.SingleAsync()).NextObservationFetchAfterUtc = null;
            await context.SaveChangesAsync();
        }

        await RunAsync(h, id);

        Assert.Equal(WeatherMeasureGroup.Wind, Assert.Single(h.Observations.LookedUp));
        Assert.Empty(h.Observations.DailyCalls);
        Assert.Empty(await CoverageAsync(h, siteVersion: 2));
        Assert.Equal([(D("2026-09-01"), D("2026-09-30"))], await CoverageAsync(h, siteVersion: 1));
        Assert.Null((await ScheduleAsync(h)).ObservationsCheckedThroughDate);
    }

    // ── AC 16: the tracked history purge ─────────────────────────────────────────────────────────

    [Fact]
    public async Task The_tracked_history_purge_resets_the_marker_and_removes_every_sites_coverage_but_keeps_the_windows()
    {
        using var h = new WeatherTestHarness(Morning);
        h.Secrets.Frost = SecretResult.Found("client-id");
        var id = await CreateAsync(h, true, W("2026-02-10", "2026-02-17"), W("2026-11-01"));
        await ResolveStationsAsync(h, id);
        h.Observations.Daily = EveryDay;
        await RunAsync(h, id);
        await RunAsync(h, id);
        Assert.NotNull((await ScheduleAsync(h)).ObservationsCheckedThroughDate);
        await using (var context = h.NewContext())
        {
            // A former site's checked range, which the purge removes too (§3.4: all site versions).
            context.WeatherObservationCoverages.Add(new WeatherObservationCoverage
            {
                WeatherPointId = id, SiteVersion = 7, FromDate = D("2025-01-01"), ToDate = D("2025-01-31"),
            });
            await context.SaveChangesAsync();
        }

        await h.Admin().SetAsync(id, forecast: false, history: false, tracked: false);
        h.Context.ChangeTracker.Clear();
        Assert.True(await h.Admin().PurgeHistoryAsync(id, WeatherTestHarness.AdminId, CancellationToken.None));

        var check = h.NewContext();
        Assert.Empty(await check.WeatherObservationCoverages.ToListAsync());
        Assert.Empty(await check.WeatherObservations.ToListAsync());
        var schedule = await check.WeatherPointSchedules.SingleAsync();
        Assert.Null(schedule.ObservationsCheckedThroughDate);
        Assert.Null(schedule.LastObservationSuccessUtc);
        Assert.Equal([D("2026-02-10"), D("2026-11-01")],
            await check.WeatherPointHistoryWindows.OrderBy(w => w.FromDate).Select(w => w.FromDate).ToListAsync());
    }

    // ── AC 22: a 2 000-window replace through the point service ──────────────────────────────────

    [Fact]
    public async Task Two_thousand_windows_are_stored_shifted_and_read_back_through_the_point_service()
    {
        using var h = new WeatherTestHarness(Morning);
        var id = await CreateAsync(h, true, W("2026-02-10", "2026-02-17"));

        // One-day windows every other day from 2000-01-01; the second set shifts each by a day, so every start changes.
        static WeatherHistoryWindowDto[] Set(int shift) => Enumerable.Range(0, 2000)
            .Select(i => new DateOnly(2000, 1, 1).AddDays(2 * i + shift))
            .Select(d => new WeatherHistoryWindowDto { From = d, To = d })
            .Reverse() // request order is not start order
            .ToArray();

        await ReplaceWindowsAsync(h, id, h.NewContext(), Set(0));
        var first = await h.NewContext().WeatherPointHistoryWindows.AsNoTracking().Select(w => w.WeatherPointHistoryWindowId).ToListAsync();
        Assert.Equal(2000, first.Count);

        await ReplaceWindowsAsync(h, id, h.NewContext(), Set(1));

        var read = (await h.Admin(h.NewContext()).GetAsync(id, true, CancellationToken.None))!;
        Assert.Equal(2000, read.HistoryWindows!.Count);
        Assert.Equal(Set(1).Select(w => w.From!.Value).Order(), read.HistoryWindows.Select(w => w.From));
        Assert.Equal(2000, read.HistoryWindowSummary.Count);
        var second = await h.NewContext().WeatherPointHistoryWindows.AsNoTracking().Select(w => w.WeatherPointHistoryWindowId).ToListAsync();
        Assert.Equal(2000, second.Count);
        Assert.Empty(second.Intersect(first));
    }
}
