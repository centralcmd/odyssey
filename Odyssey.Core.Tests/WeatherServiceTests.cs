using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Odyssey.Context.Secrets;
using Odyssey.Core.Preferences;
using Odyssey.Core.Weather;
using Odyssey.Dtos.Journal;
using Xunit;

namespace Odyssey.Core.Tests;

/// <summary>
/// The weather services on the EF InMemory provider (issue #348): fetch, claiming and write fencing, the
/// Admin's enable/apply/purge preconditions, and read resolution. The InMemory provider honours the
/// <c>Version</c> concurrency token, which is what makes fencing testable here; the RESTRICT keys and the
/// relational purge are covered against real MariaDB in <c>Odyssey.IntegrationTests</c>.
/// </summary>
public class WeatherServiceTests
{
    private static readonly DateTime Noon = new(2026, 10, 23, 12, 0, 0, DateTimeKind.Utc);

    // ── Fetch ─────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_fetch_stores_one_run_for_the_frozen_site_and_builds_the_days()
    {
        using var h = new WeatherTestHarness();
        var location = await h.EnableAsync();
        h.Forecasts.Respond = _ => Task.FromResult(WeatherTestHarness.Run(Noon.AddMinutes(-30), Noon, 36, 7));

        var attempt = await h.Fetch().FetchForecastAsync(location.Id, WeatherFetchMode.Scheduled, CancellationToken.None);

        Assert.Equal(WeatherAttemptStatus.Completed, attempt.Status);
        Assert.True(attempt.StoredNewRun);
        Assert.Equal(new WeatherSiteQuery(59.913m, 10.750m, 12), h.Forecasts.LastSite);
        Assert.Equal(1, await h.Context.WeatherForecastRuns.CountAsync());
        Assert.Equal(36, await h.Context.WeatherForecastSteps.CountAsync());
        Assert.True(await h.Context.WeatherDays.AnyAsync(d => d.LocalDate == new DateOnly(2026, 10, 23) && d.ForecastMaxTempC == 7));
        var schedule = await h.Context.WeatherPointSchedules.AsNoTracking().SingleAsync();
        Assert.Null(schedule.LeaseUntilUtc is { } lease && lease > Noon ? schedule.LeaseUntilUtc : null);
        Assert.True(schedule.NextForecastFetchAfterUtc >= Noon.AddMinutes(60));
    }

    [Fact]
    public async Task The_same_issue_time_stores_nothing_new()
    {
        using var h = new WeatherTestHarness();
        var location = await h.EnableAsync();
        h.Forecasts.Respond = _ => Task.FromResult(WeatherTestHarness.Run(Noon.AddMinutes(-30), Noon, 6, 7));
        await h.Fetch().FetchForecastAsync(location.Id, WeatherFetchMode.Scheduled, CancellationToken.None);

        h.Time.Advance(TimeSpan.FromHours(2));
        var second = await h.Fetch(h.NewContext()).FetchForecastAsync(location.Id, WeatherFetchMode.Scheduled, CancellationToken.None);

        Assert.Equal(WeatherAttemptStatus.Completed, second.Status);
        Assert.False(second.StoredNewRun);
        Assert.Equal(1, await h.NewContext().WeatherForecastRuns.CountAsync());
    }

    [Fact]
    public async Task A_fetch_is_not_sent_before_it_is_due()
    {
        using var h = new WeatherTestHarness();
        var location = await h.EnableAsync();
        await h.Fetch().FetchForecastAsync(location.Id, WeatherFetchMode.Scheduled, CancellationToken.None);

        h.Time.Advance(TimeSpan.FromMinutes(10));
        var again = await h.Fetch(h.NewContext()).FetchForecastAsync(location.Id, WeatherFetchMode.Scheduled, CancellationToken.None);

        Assert.Equal(WeatherAttemptStatus.Skipped, again.Status);
        Assert.Equal(1, h.Forecasts.Calls);
    }

    [Fact]
    public async Task No_contact_means_nothing_is_sent()
    {
        using var h = new WeatherTestHarness();
        var location = await h.EnableAsync();
        h.Settings.Value = h.Settings.Value with { Contact = null };

        var attempt = await h.Fetch().FetchForecastAsync(location.Id, WeatherFetchMode.Scheduled, CancellationToken.None);

        Assert.Equal(WeatherAttemptStatus.NotConfigured, attempt.Status);
        Assert.Equal(0, h.Forecasts.Calls);
    }

    [Fact]
    public async Task A_403_pauses_six_hours_and_a_refresh_cannot_shorten_the_pause()
    {
        using var h = new WeatherTestHarness();
        var location = await h.EnableAsync();
        h.Forecasts.Respond = _ => Task.FromResult(new ForecastFetchResult(WeatherFetchOutcome.Failed, FailureKind: WeatherFetchFailureKind.ProviderRejected));

        var failed = await h.Fetch().FetchForecastAsync(location.Id, WeatherFetchMode.Scheduled, CancellationToken.None);
        h.Time.Advance(TimeSpan.FromMinutes(5));
        var refresh = await h.Fetch(h.NewContext()).FetchForecastAsync(location.Id, WeatherFetchMode.Refresh, CancellationToken.None);

        Assert.Equal(WeatherAttemptStatus.Failed, failed.Status);
        Assert.Equal(WeatherAttemptStatus.Skipped, refresh.Status);
        Assert.Equal(1, h.Forecasts.Calls);
        var schedule = await h.NewContext().WeatherPointSchedules.SingleAsync();
        Assert.Equal(WeatherFetchFailureKind.ProviderRejected, schedule.ForecastFailureKind);
        Assert.Equal(Noon.AddHours(6), schedule.NextForecastFetchAfterUtc);
    }

    [Fact]
    public async Task A_scheduled_fetch_retries_a_5xx_but_never_a_429()
    {
        using var h = new WeatherTestHarness();
        var location = await h.EnableAsync();
        h.Forecasts.Respond = _ => Task.FromResult(new ForecastFetchResult(WeatherFetchOutcome.Failed, FailureKind: WeatherFetchFailureKind.ProviderError));
        await h.Fetch().FetchForecastAsync(location.Id, WeatherFetchMode.Scheduled, CancellationToken.None);
        Assert.Equal(3, h.Forecasts.Calls);

        using var h2 = new WeatherTestHarness();
        var other = await h2.EnableAsync();
        h2.Forecasts.Respond = _ => Task.FromResult(new ForecastFetchResult(WeatherFetchOutcome.Failed, FailureKind: WeatherFetchFailureKind.RateLimited));
        await h2.Fetch().FetchForecastAsync(other.Id, WeatherFetchMode.Scheduled, CancellationToken.None);
        Assert.Equal(1, h2.Forecasts.Calls);
    }

    [Fact]
    public async Task A_refresh_bypasses_the_interval_but_honours_expires()
    {
        using var h = new WeatherTestHarness();
        var location = await h.EnableAsync();
        h.Forecasts.Respond = _ => Task.FromResult(WeatherTestHarness.Run(Noon.AddMinutes(-30), Noon, 6, 7));
        await h.Fetch().FetchForecastAsync(location.Id, WeatherFetchMode.Scheduled, CancellationToken.None);

        // Run() expires 30 min after issue, i.e. at Noon. Ten minutes later the interval is still pending.
        h.Time.Advance(TimeSpan.FromMinutes(10));
        var refresh = await h.Fetch(h.NewContext()).FetchForecastAsync(location.Id, WeatherFetchMode.Refresh, CancellationToken.None);

        Assert.Equal(WeatherAttemptStatus.Completed, refresh.Status);
        Assert.Equal(2, h.Forecasts.Calls);
    }

    // ── Fencing (AC 43) ───────────────────────────────────────────────────────────────────────────

    public static TheoryData<string> FencingActions => new() { "disable", "move", "purge-start" };

    [Theory]
    [MemberData(nameof(FencingActions))]
    public async Task A_fetch_in_flight_when_the_admin_acts_stores_nothing(string action)
    {
        using var h = new WeatherTestHarness();
        var location = await h.EnableAsync();
        h.Forecasts.Respond = async _ =>
        {
            // The Admin acts while the request is out, from another request's context.
            var other = h.NewContext();
            switch (action)
            {
                case "disable":
                    await h.Admin(other).SetAsync(location.Id, false, false, true);
                    break;
                case "move":
                    await h.Admin(other).SetAsync(location.Id, true, true, true, latitude: 60.39299m);
                    break;
                case "purge-start":
                    var weather = await other.WeatherPoints.SingleAsync();
                    var schedule = await other.WeatherPointSchedules.SingleAsync();
                    weather.PurgeStartedAt = Noon;
                    weather.PurgeMode = WeatherPurgeMode.Full;
                    schedule.LeaseUntilUtc = DateTime.MaxValue;
                    schedule.Version = Guid.NewGuid();
                    await other.SaveChangesAsync();
                    break;
            }

            return WeatherTestHarness.Run(Noon.AddMinutes(-30), Noon, 12, 7);
        };

        var attempt = await h.Fetch().FetchForecastAsync(location.Id, WeatherFetchMode.Scheduled, CancellationToken.None);

        Assert.Equal(WeatherAttemptStatus.Discarded, attempt.Status);
        var check = h.NewContext();
        Assert.Equal(0, await check.WeatherForecastRuns.CountAsync());
        Assert.Equal(0, await check.WeatherForecastSteps.CountAsync());
        Assert.Equal(0, await check.WeatherDays.CountAsync());
    }

    [Fact]
    public async Task A_second_replica_cannot_claim_a_job_already_leased()
    {
        using var h = new WeatherTestHarness();
        var location = await h.EnableAsync();
        WeatherAttempt? concurrent = null;
        h.Forecasts.Respond = async _ =>
        {
            if (concurrent is null)
            {
                concurrent = new WeatherAttempt(WeatherAttemptStatus.Completed);
                concurrent = await h.Fetch(h.NewContext()).FetchForecastAsync(location.Id, WeatherFetchMode.Scheduled, CancellationToken.None);
            }

            return WeatherTestHarness.Run(Noon.AddMinutes(-30), Noon, 6, 7);
        };

        var first = await h.Fetch().FetchForecastAsync(location.Id, WeatherFetchMode.Scheduled, CancellationToken.None);

        Assert.Equal(WeatherAttemptStatus.Completed, first.Status);
        Assert.Equal(WeatherAttemptStatus.Skipped, concurrent!.Status);
        Assert.Equal(1, h.Forecasts.Calls);
    }

    [Fact]
    public async Task Reads_pick_one_step_per_hour_in_the_database_however_many_runs_are_stored()
    {
        using var h = new WeatherTestHarness();
        var location = await h.EnableAsync(tracked: false);
        // Twenty stored runs over the same 24 hours, each issued later and one degree warmer.
        for (var i = 0; i < 20; i++)
        {
            var issued = Noon.AddMinutes(-60 + i);
            var run = new Odyssey.Context.WeatherForecastRun
            {
                WeatherForecastRunId = Guid.NewGuid(), WeatherPointId = location.Id, SiteVersion = 1,
                Provider = WeatherProvider.MetNorway, ProviderUpdatedAt = issued, FetchedAtUtc = issued,
            };
            for (var hour = 0; hour < 24; hour++)
            {
                run.Steps.Add(new Odyssey.Context.WeatherForecastStep
                {
                    WeatherForecastRunId = run.WeatherForecastRunId, WeatherPointId = location.Id, SiteVersion = 1,
                    RunProviderUpdatedAt = issued, RunFetchedAtUtc = issued, TimeUtc = Noon.AddHours(hour),
                    PeriodHours = 1, AirTemperatureC = i, SymbolCode = "cloudy",
                });
            }

            h.Context.WeatherForecastRuns.Add(run);
        }

        await h.Context.SaveChangesAsync();
        h.Context.ChangeTracker.Clear();

        var query = WeatherComposer.EffectiveStepsQuery(h.Context.WeatherForecastSteps, location.Id, Noon, Noon.AddHours(24));
        var picked = await query.ToListAsync();

        Assert.Equal(24, picked.Count);
        Assert.All(picked, step => Assert.Equal(19, step.AirTemperatureC));
    }

    [Fact]
    public async Task An_admin_write_that_loses_the_version_race_is_a_409_not_a_500()
    {
        using var h = new WeatherTestHarness();
        var location = await h.EnableAsync();

        // A fetch claims between the Admin's read and its save: simulated by rotating the version from
        // another context while the Admin's own tracked schedule still holds the old one.
        var adminContext = h.NewContext();
        var service = h.Admin(adminContext);
        // Tracked now, so the service's own read resolves to this stale instance.
        await adminContext.WeatherPointSchedules.SingleAsync();
        await using (var other = h.NewContext())
        {
            var schedule = await other.WeatherPointSchedules.SingleAsync();
            schedule.Version = Guid.NewGuid();
            await other.SaveChangesAsync();
        }

        var error = await Assert.ThrowsAsync<WeatherConflictException>(() => service.SetAsync(location.Id, false, false, true));
        Assert.Equal(WeatherConflictException.FetchInProgress, error.Code);
    }

    [Fact]
    public async Task A_forecast_older_than_three_intervals_is_stale_even_with_no_failure()
    {
        using var h = new WeatherTestHarness();
        var cabin = await h.EnableAsync("Cabin", tracked: true);
        h.Forecasts.Respond = _ => Task.FromResult(WeatherTestHarness.Run(Noon.AddMinutes(-30), Noon, 48, 7));
        await h.Fetch().FetchForecastAsync(cabin.Id, WeatherFetchMode.Scheduled, CancellationToken.None);

        Assert.False((await h.Read(h.NewContext()).CurrentAsync(cabin.Id, "u1", CancellationToken.None)).Stale);

        // The scheduler stops (or the place is paused past the cap): no failure is ever recorded.
        h.Time.Advance(TimeSpan.FromHours(3) + TimeSpan.FromMinutes(1));
        Assert.True((await h.Read(h.NewContext()).CurrentAsync(cabin.Id, "u1", CancellationToken.None)).Stale);
    }

    // ── Observations ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Observations_need_the_frost_secret_and_report_an_unreadable_one()
    {
        using var h = new WeatherTestHarness(new DateTimeOffset(2026, 10, 23, 9, 0, 0, TimeSpan.Zero));
        var location = await h.EnableAsync();

        Assert.Equal(SecretReadState.NotSet, await h.Fetch().FetchObservationsAsync(location.Id, CancellationToken.None));
        h.Secrets.Frost = SecretResult.Unreadable;
        Assert.Equal(SecretReadState.Unreadable, await h.Fetch().FetchObservationsAsync(location.Id, CancellationToken.None));
        Assert.Equal(0, h.Observations.StationCalls);
    }

    [Fact]
    public async Task Observations_look_up_a_station_per_measure_then_fetch_one_request_per_call()
    {
        using var h = new WeatherTestHarness(new DateTimeOffset(2026, 10, 23, 9, 0, 0, TimeSpan.Zero));
        h.Secrets.Frost = SecretResult.Found("client-id");
        var location = await h.EnableAsync();
        h.Observations.Daily = (from, _) => new ObservationFetchResult(true,
            [new ObservationDayData(new DateOnly(2026, 10, 22), 6.8m, 3.6m, 10.1m, 4.9m, 15.2m, 7.4m)]);

        // One lookup per call, a group at a time, so the scheduler stays at one request per call.
        for (var i = 1; i <= 3; i++)
        {
            await h.Fetch(h.NewContext()).FetchObservationsAsync(location.Id, CancellationToken.None);
            Assert.Equal(i, h.Observations.StationCalls);
            Assert.Empty(h.Observations.DailyCalls);
        }

        Assert.Equal([WeatherMeasureGroup.Temperature, WeatherMeasureGroup.Precipitation, WeatherMeasureGroup.Wind], h.Observations.LookedUp);
        Assert.NotNull((await h.NewContext().WeatherPoints.SingleAsync()).StationResolvedAtUtc);

        await h.Fetch(h.NewContext()).FetchObservationsAsync(location.Id, CancellationToken.None);

        var call = Assert.Single(h.Observations.DailyCalls);
        Assert.True(call.To <= new DateOnly(2026, 10, 22));
        var check = h.NewContext();
        Assert.Equal(1, await check.WeatherObservations.CountAsync());
        Assert.True(await check.WeatherDays.AnyAsync(d => d.LocalDate == new DateOnly(2026, 10, 22) && d.ObservedMaxTempC == 10.1m));
    }

    [Fact]
    public async Task Each_measure_comes_from_its_own_station_in_one_request()
    {
        using var h = new WeatherTestHarness(new DateTimeOffset(2026, 10, 23, 9, 0, 0, TimeSpan.Zero));
        h.Secrets.Frost = SecretResult.Found("client-id");
        var location = await h.EnableAsync();
        h.Observations.Stations[WeatherMeasureGroup.Temperature] = "SN-T";
        h.Observations.Stations[WeatherMeasureGroup.Precipitation] = "SN-P";
        h.Observations.Stations[WeatherMeasureGroup.Wind] = null;
        // Every station reports every value; only each group's own station may supply it.
        h.Observations.Daily = (_, _) => new ObservationFetchResult(true,
            [new ObservationDayData(new DateOnly(2026, 10, 22), 6.8m, 3.6m, 10.1m, 4.9m, 15.2m, 7.4m)]);
        for (var i = 0; i < 4; i++)
        {
            await h.Fetch(h.NewContext()).FetchObservationsAsync(location.Id, CancellationToken.None);
        }

        Assert.Equal(["SN-T", "SN-P"], Assert.Single(h.Observations.DailyStations));
        var check = h.NewContext();
        var observation = await check.WeatherObservations.SingleAsync();
        Assert.Equal(("SN-T", "SN-P", (string?)null), (observation.TemperatureStationId, observation.PrecipitationStationId, observation.WindStationId));
        var day = await check.WeatherDays.SingleAsync(d => d.LocalDate == new DateOnly(2026, 10, 22));
        Assert.Equal(6.8m, day.ObservedMeanTempC);
        Assert.Equal(7.4m, day.ObservedPrecipitationMm);
        // No wind station: no wind, even though the other stations report it.
        Assert.Null(day.ObservedMeanWindSpeedMps);
        Assert.Null(day.ObservedMaxWindGustMps);

        // The read shapes name each group's station, and the one with none in reach.
        var daily = await h.Read(h.NewContext()).DailyAsync(Daily(location.Id, new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 22)), "u1", CancellationToken.None);
        Assert.True(daily.History!.HasObservationStation);
        Assert.Collection(daily.History.Stations,
            t => Assert.Equal((WeatherMeasureGroup.Temperature, "Station Sn-T", 4, 25), (t.Group, t.Name, t.DistanceKm!.Value, t.MaxDistanceKm)),
            p => Assert.Equal((WeatherMeasureGroup.Precipitation, 15), (p.Group, p.MaxDistanceKm)),
            w => Assert.Equal((WeatherMeasureGroup.Wind, (string?)null, (int?)null, 30), (w.Group, w.Name, w.DistanceKm, w.MaxDistanceKm)));
    }

    [Fact]
    public async Task A_measure_with_no_station_is_asked_again_after_a_day_and_measures_from_the_start_once_found()
    {
        using var h = new WeatherTestHarness(new DateTimeOffset(2026, 10, 23, 9, 0, 0, TimeSpan.Zero));
        h.Secrets.Frost = SecretResult.Found("client-id");
        var location = await h.EnableAsync();
        h.Observations.Stations[WeatherMeasureGroup.Wind] = null;
        for (var i = 0; i < 4; i++)
        {
            await h.Fetch(h.NewContext()).FetchObservationsAsync(location.Id, CancellationToken.None);
        }

        Assert.Equal(3, h.Observations.StationCalls);
        Assert.NotNull((await h.NewContext().WeatherPointSchedules.SingleAsync()).ObservationsCheckedThroughDate);

        // A day later the wind lookup runs again, and a station found now starts its history from the beginning.
        h.Observations.Stations[WeatherMeasureGroup.Wind] = "SN-W";
        h.Time.Advance(TimeSpan.FromHours(25));
        await h.Fetch(h.NewContext()).FetchObservationsAsync(location.Id, CancellationToken.None);

        Assert.Equal(WeatherMeasureGroup.Wind, h.Observations.LookedUp[^1]);
        var check = h.NewContext();
        Assert.Equal("SN-W", (await check.WeatherStations.SingleAsync(st => st.Group == WeatherMeasureGroup.Wind)).StationId);
        Assert.Null((await check.WeatherPointSchedules.SingleAsync()).ObservationsCheckedThroughDate);
    }

    [Fact]
    public async Task A_never_fetched_place_starts_at_once_and_adds_yesterday_at_the_morning_window()
    {
        // 01:00 UTC is 03:00 in Oslo: before Frost has finished yesterday, but the days before it are complete.
        using var h = new WeatherTestHarness(new DateTimeOffset(2026, 10, 23, 1, 0, 0, TimeSpan.Zero));
        h.Secrets.Frost = SecretResult.Found("client-id");
        var location = await h.EnableAsync();
        var weather = await h.Context.WeatherPoints.SingleAsync(w => w.WeatherPointId == location.Id);
        weather.HistoryFrom = new DateOnly(2026, 10, 15);
        await h.Context.SaveChangesAsync();
        h.Context.ChangeTracker.Clear();
        h.Observations.Daily = (_, _) => new ObservationFetchResult(true, []);

        for (var i = 0; i < 3; i++)
        {
            await h.Fetch(h.NewContext()).FetchObservationsAsync(location.Id, CancellationToken.None);
        }

        Assert.Equal(3, h.Observations.StationCalls);

        await h.Fetch(h.NewContext()).FetchObservationsAsync(location.Id, CancellationToken.None);
        var first = Assert.Single(h.Observations.DailyCalls);
        Assert.Equal((new DateOnly(2026, 10, 15), new DateOnly(2026, 10, 21)), (first.From, first.To));

        // Yesterday is still owed, so the next run is this morning's 08:00 in Oslo, not tomorrow's.
        var schedule = await h.NewContext().WeatherPointSchedules.SingleAsync(s => s.WeatherPointId == location.Id);
        Assert.Equal(new DateTime(2026, 10, 23, 6, 0, 0), schedule.NextObservationFetchAfterUtc);

        // Nothing more before then: a place that has been fetched keeps to the morning rhythm.
        h.Time.Now = new DateTimeOffset(2026, 10, 23, 3, 0, 0, TimeSpan.Zero);
        await h.Fetch(h.NewContext()).FetchObservationsAsync(location.Id, CancellationToken.None);
        Assert.Single(h.Observations.DailyCalls);

        h.Time.Now = new DateTimeOffset(2026, 10, 23, 6, 30, 0, TimeSpan.Zero);
        await h.Fetch(h.NewContext()).FetchObservationsAsync(location.Id, CancellationToken.None);
        Assert.Equal(new DateOnly(2026, 10, 22), h.Observations.DailyCalls[^1].To);
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 0)]
    public async Task A_place_parked_before_its_first_lookup_starts_now_unless_a_failure_pause_holds(bool failed, int stationCalls)
    {
        // An earlier release parked a never-fetched place until the next morning's window.
        using var h = new WeatherTestHarness(new DateTimeOffset(2026, 10, 23, 1, 0, 0, TimeSpan.Zero));
        h.Secrets.Frost = SecretResult.Found("client-id");
        var location = await h.EnableAsync();
        var schedule = await h.Context.WeatherPointSchedules.SingleAsync(s => s.WeatherPointId == location.Id);
        schedule.NextObservationFetchAfterUtc = new DateTime(2026, 10, 23, 6, 0, 0);
        if (failed)
            schedule.ObservationFailureKind = WeatherFetchFailureKind.RateLimited;
        await h.Context.SaveChangesAsync();
        h.Context.ChangeTracker.Clear();

        await h.Fetch(h.NewContext()).FetchObservationsAsync(location.Id, CancellationToken.None);

        Assert.Equal(stationCalls, h.Observations.StationCalls);
    }

    [Fact]
    public async Task The_schedule_job_starts_a_place_parked_before_its_first_lookup()
    {
        // 01:00 UTC is 03:00 in Oslo; an earlier release parked the place until 08:00 there.
        using var h = new WeatherTestHarness(new DateTimeOffset(2026, 10, 23, 1, 0, 0, TimeSpan.Zero));
        h.Secrets.Frost = SecretResult.Found("client-id");
        var location = await h.EnableAsync(tracked: false);
        var schedule = await h.Context.WeatherPointSchedules.SingleAsync(s => s.WeatherPointId == location.Id);
        schedule.NextObservationFetchAfterUtc = new DateTime(2026, 10, 23, 6, 0, 0);
        await h.Context.SaveChangesAsync();
        h.Context.ChangeTracker.Clear();

        await Job(h).RunOnceAsync(CancellationToken.None);

        Assert.Equal(1, h.Observations.StationCalls);
    }

    [Fact]
    public async Task One_locations_unexpected_failure_does_not_end_the_schedule_run()
    {
        using var h = new WeatherTestHarness();
        await h.EnableAsync("Home");
        await h.EnableAsync("Cabin");
        var calls = 0;
        h.Forecasts.Respond = _ => ++calls == 1
            ? throw new InvalidOperationException("Something nobody planned for.")
            : Task.FromResult(new ForecastFetchResult(WeatherFetchOutcome.NotModified));
        var job = Job(h);

        await job.RunOnceAsync(CancellationToken.None);

        // The queue is ordered, so without isolation the same place would fail first on every tick and the one
        // behind it would never be fetched.
        Assert.Equal(2, h.Forecasts.Calls);
    }

    [Fact]
    public async Task Two_places_linked_to_one_point_cost_one_forecast_and_one_observation_request()
    {
        // AC 15: the schedule works per point, so the number of linked places changes nothing it sends.
        using var h = new WeatherTestHarness(new DateTimeOffset(2026, 10, 23, 9, 0, 0, TimeSpan.Zero));
        h.Secrets.Frost = SecretResult.Found("client-id");
        var point = await h.EnableAsync("Oslo");
        var street = await h.AddLocationAsync("Storgata 1");
        await h.LinkAsync(street.LocationId, point.Id);
        // The stations are resolved already, so the run's one observation request is the daily fetch.
        await using (var context = h.NewContext())
        {
            var row = await context.WeatherPoints.SingleAsync();
            row.StationResolvedAtUtc = h.Time.GetUtcNow().UtcDateTime;
            // History from inside the first daily window, so no backfill is owed after it.
            row.HistoryFrom = new DateOnly(2026, 10, 18);
            foreach (var group in new[] { WeatherMeasureGroup.Temperature, WeatherMeasureGroup.Precipitation, WeatherMeasureGroup.Wind })
            {
                context.WeatherStations.Add(new Odyssey.Context.WeatherStation
                {
                    WeatherPointId = point.Id, Group = group, StationId = "SN18700", ResolvedAtUtc = h.Time.GetUtcNow().UtcDateTime,
                });
            }

            await context.SaveChangesAsync();
        }

        var job = Job(h);
        await job.RunOnceAsync(CancellationToken.None);
        await job.RunOnceAsync(CancellationToken.None);

        Assert.Equal(1, h.Forecasts.Calls);
        Assert.Single(h.Observations.DailyCalls);
        Assert.Equal(0, h.Observations.StationCalls);
    }

    [Fact]
    public async Task The_point_and_link_audit_lines_carry_ids_and_counts_never_a_name_or_a_coordinate()
    {
        // AC 21 and 27.
        using var h = new WeatherTestHarness();
        var pointLog = new RecordingLogger<WeatherPointService>();
        var linkLog = new RecordingLogger<WeatherLinkService>();
        var point = await new WeatherPointService(h.Context, h.Fetch(), h.Settings, new TrackedWeatherHistoryPurger(h.Context), h.Time, pointLog)
            .CreateAsync(new NewWeatherPoint
            {
                Name = "Secret Street 7", Latitude = 59.91349m, Longitude = 10.73889m, TimeZoneId = "Europe/Oslo",
                ForecastEnabled = false, HistoryEnabled = false, IsTracked = false,
            }, WeatherTestHarness.AdminId, CancellationToken.None);
        var first = await h.AddLocationAsync("Hidden Place One");
        var second = await h.AddLocationAsync("Hidden Place Two");
        foreach (var place in new[] { first, second })
        {
            await new WeatherLinkService(h.NewContext(), h.Time, linkLog).SetAsync(place.LocationId, point.WeatherPointId, WeatherTestHarness.AdminId, CancellationToken.None);
        }

        var admin = new WeatherPointService(h.NewContext(), h.Fetch(), h.Settings, new TrackedWeatherHistoryPurger(h.NewContext()), h.Time, pointLog);
        await admin.UpdateAsync(point.WeatherPointId, WeatherPointServiceTestExtensions.Update(point, latitude: 60.1m),
            WeatherTestHarness.AdminId, true, CancellationToken.None);
        Assert.True(await new WeatherPointService(h.NewContext(), h.Fetch(), h.Settings, new TrackedWeatherHistoryPurger(h.NewContext()), h.Time, pointLog)
            .DeleteAsync(point.WeatherPointId, WeatherTestHarness.AdminId, CancellationToken.None));

        var lines = pointLog.Lines.Concat(linkLog.Lines).ToList();
        Assert.Contains(lines, l => l.Contains("created", StringComparison.Ordinal) && l.Contains(point.WeatherPointId.ToString(), StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains("moved", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains($"{point.WeatherPointId} deleted", StringComparison.Ordinal) && l.Contains("2 linked location(s) unlinked", StringComparison.Ordinal));
        Assert.Equal(2, linkLog.Lines.Count(l => l.Contains(" set by ", StringComparison.Ordinal)));
        Assert.All(lines, l =>
        {
            Assert.DoesNotContain("Secret Street", l, StringComparison.Ordinal);
            Assert.DoesNotContain("Hidden Place", l, StringComparison.Ordinal);
            Assert.DoesNotContain("59.9", l, StringComparison.Ordinal);
            Assert.DoesNotContain("60.1", l, StringComparison.Ordinal);
            Assert.DoesNotContain("10.7", l, StringComparison.Ordinal);
        });
        Assert.Equal(2, await h.NewContext().Locations.CountAsync(l => l.WeatherPointId == null));
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

    private static WeatherScheduleJob Job(WeatherTestHarness h)
    {
        var services = new ServiceCollection()
            .AddScoped(_ => h.NewContext())
            .AddScoped(sp => h.Fetch(sp.GetRequiredService<Odyssey.Context.OdysseyContext>()))
            .AddSingleton<IWeatherSettingsLookup>(h.Settings)
            .AddJobRunRecordingWith(h.Time)
            .BuildServiceProvider();
        return new WeatherScheduleJob(services.GetRequiredService<IServiceScopeFactory>(), h.Time, NullLogger<WeatherScheduleJob>.Instance);
    }

    // ── Admin: create / update ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Create_with_coordinates_rounds_site_one_and_links_nothing()
    {
        using var h = new WeatherTestHarness();
        var location = await h.AddLocationAsync();

        var created = await h.Admin().CreateAsync(new NewWeatherPoint
        {
            Name = "Oslo", Latitude = 59.91349m, Longitude = 10.73889m, AltitudeMeters = 23, TimeZoneId = "Europe/Oslo",
            ForecastEnabled = true, HistoryEnabled = true, IsTracked = true,
        }, WeatherTestHarness.AdminId, CancellationToken.None);

        Assert.Equal("Oslo", created.Name);
        Assert.Equal(1, created.Site.Version);
        Assert.Equal((59.913m, 10.739m, 23, "Europe/Oslo"), (created.Site.Latitude, created.Site.Longitude, created.Site.AltitudeMeters!.Value, created.Site.TimeZoneId));
        Assert.Equal(new DateOnly(2025, 10, 23), created.HistoryFrom);
        Assert.NotEqual(Guid.Empty, created.SettingsVersion);
        Assert.Equal(1, await h.NewContext().WeatherPointSchedules.CountAsync());
        Assert.Null((await h.NewContext().Locations.SingleAsync(l => l.LocationId == location.LocationId)).WeatherPointId);
    }

    [Fact]
    public async Task Create_from_a_location_copies_its_reviewed_position_and_leaves_it_unlinked()
    {
        using var h = new WeatherTestHarness();
        var location = await h.AddLocationAsync("Storgata 1");
        var fingerprint = WeatherCoordinates.FingerprintOrNull(location)!;
        Assert.Equal(WeatherRules.Fingerprint(59.913m, 10.750m, 12, "Europe/Oslo"), fingerprint);

        var stale = await Assert.ThrowsAsync<WeatherConflictException>(() => h.Admin().CreateAsync(new NewWeatherPoint
        {
            FromLocationId = location.LocationId, ExpectedLocationFingerprint = "v1:0.000:0.000:-:UTC",
            ForecastEnabled = true, HistoryEnabled = true, IsTracked = true,
        }, WeatherTestHarness.AdminId, CancellationToken.None));
        Assert.Equal(WeatherConflictException.LocationChanged, stale.Code);
        Assert.DoesNotContain("59.9", stale.Message, StringComparison.Ordinal);

        var created = await h.Admin(h.NewContext()).CreateAsync(new NewWeatherPoint
        {
            FromLocationId = location.LocationId, ExpectedLocationFingerprint = fingerprint,
            ForecastEnabled = true, HistoryEnabled = false, IsTracked = false,
        }, WeatherTestHarness.AdminId, CancellationToken.None);

        Assert.Equal("Storgata 1", created.Name);
        Assert.Equal((59.913m, 10.750m, 12, "Europe/Oslo"), (created.Site.Latitude, created.Site.Longitude, created.Site.AltitudeMeters!.Value, created.Site.TimeZoneId));
        Assert.Null((await h.NewContext().Locations.SingleAsync(l => l.LocationId == location.LocationId)).WeatherPointId);
    }

    [Fact]
    public async Task Create_from_a_location_without_coordinates_or_an_unknown_one_is_a_422_keyed_fromLocationId()
    {
        using var h = new WeatherTestHarness();
        var now = h.Time.GetUtcNow().UtcDateTime;
        var bare = new Odyssey.Context.Location { LocationId = Guid.NewGuid(), Name = "Nowhere", CreatedAt = now, UpdatedAt = now };
        h.Context.Locations.Add(bare);
        await h.Context.SaveChangesAsync();

        foreach (var id in new[] { bare.LocationId, Guid.NewGuid() })
        {
            var error = await Assert.ThrowsAsync<DomainUnprocessableException>(() => h.Admin(h.NewContext()).CreateAsync(new NewWeatherPoint
            {
                FromLocationId = id, ExpectedLocationFingerprint = "v1:none", ForecastEnabled = true, HistoryEnabled = true, IsTracked = true,
            }, WeatherTestHarness.AdminId, CancellationToken.None));
            Assert.Contains("fromLocationId", error.Errors!.Keys);
        }

        Assert.Equal(0, await h.NewContext().WeatherPoints.CountAsync());
    }

    [Fact]
    public async Task A_duplicate_name_is_a_409_duplicate_name_on_create_and_on_update()
    {
        using var h = new WeatherTestHarness();
        await h.EnableAsync("Oslo");
        var bergen = await h.EnableAsync("Bergen");

        var onCreate = await Assert.ThrowsAsync<WeatherConflictException>(() => h.Admin(h.NewContext()).CreateAsync(new NewWeatherPoint
        {
            Name = "oslo", Latitude = 1, Longitude = 1, TimeZoneId = "UTC", ForecastEnabled = true, HistoryEnabled = true, IsTracked = false,
        }, WeatherTestHarness.AdminId, CancellationToken.None));
        Assert.Equal(WeatherConflictException.DuplicateName, onCreate.Code);
        Assert.Contains("name", onCreate.Errors!.Keys);

        var current = (await h.Admin(h.NewContext()).GetAsync(bergen.Id, true, CancellationToken.None))!;
        var request = WeatherPointServiceTestExtensions.Update(current);
        request.Name = "Oslo";
        var onUpdate = await Assert.ThrowsAsync<WeatherConflictException>(() => h.Admin(h.NewContext()).UpdateAsync(
            bergen.Id, request, WeatherTestHarness.AdminId, true, CancellationToken.None));
        Assert.Equal(WeatherConflictException.DuplicateName, onUpdate.Code);
    }

    [Fact]
    public async Task A_stale_settings_version_is_a_409_and_changes_nothing_but_a_worker_write_does_not_make_it_stale()
    {
        using var h = new WeatherTestHarness();
        var point = await h.EnableAsync();
        var read = (await h.Admin(h.NewContext()).GetAsync(point.Id, true, CancellationToken.None))!;

        // The worker resolves the stations between the Admin's read and the PUT (AC 7): no rotation.
        await using (var worker = h.NewContext())
        {
            var row = await worker.WeatherPoints.SingleAsync();
            row.StationResolvedAtUtc = Noon;
            await worker.SaveChangesAsync();
        }

        var first = WeatherPointServiceTestExtensions.Update(read, tracked: false);
        var saved = await h.Admin(h.NewContext()).UpdateAsync(point.Id, first, WeatherTestHarness.AdminId, true, CancellationToken.None);
        Assert.False(saved!.Point.IsTracked);
        Assert.NotEqual(read.SettingsVersion, saved.Point.SettingsVersion);

        // A second editor still holding the first read is refused.
        var second = WeatherPointServiceTestExtensions.Update(read, history: false);
        var stale = await Assert.ThrowsAsync<WeatherConflictException>(() => h.Admin(h.NewContext()).UpdateAsync(
            point.Id, second, WeatherTestHarness.AdminId, true, CancellationToken.None));
        Assert.Equal(WeatherConflictException.Stale, stale.Code);
        Assert.True((await h.NewContext().WeatherPoints.SingleAsync()).HistoryEnabled);
    }

    [Fact]
    public async Task Update_of_an_unknown_point_is_null_not_an_upsert()
    {
        using var h = new WeatherTestHarness();
        var point = await h.EnableAsync();
        var current = (await h.Admin().GetAsync(point.Id, true, CancellationToken.None))!;

        Assert.Null(await h.Admin(h.NewContext()).UpdateAsync(
            Guid.NewGuid(), WeatherPointServiceTestExtensions.Update(current), WeatherTestHarness.AdminId, true, CancellationToken.None));
        Assert.Equal(1, await h.NewContext().WeatherPoints.CountAsync());
    }

    [Fact]
    public async Task Tracking_beyond_the_cap_is_a_422_keyed_isTracked_and_linked_places_do_not_count()
    {
        using var h = new WeatherTestHarness();
        h.Settings.Value = h.Settings.Value with { MaxTrackedPoints = 1 };
        var home = await h.EnableAsync("Home");
        // Three more places linked to the one tracked point claim no scheduled slot (AC 8).
        for (var i = 0; i < 3; i++)
        {
            var place = await h.AddLocationAsync($"Street {i}");
            await h.LinkAsync(place.LocationId, home.Id);
        }

        var error = await Assert.ThrowsAsync<DomainUnprocessableException>(() => h.Admin(h.NewContext()).CreateAsync(new NewWeatherPoint
        {
            Name = "Cabin", Latitude = 61, Longitude = 9, TimeZoneId = "Europe/Oslo", ForecastEnabled = true, HistoryEnabled = true, IsTracked = true,
        }, WeatherTestHarness.AdminId, CancellationToken.None));
        Assert.Contains("isTracked", error.Errors!.Keys);
        Assert.Contains("1 of 1", error.Message, StringComparison.Ordinal);

        var untracked = await h.Admin(h.NewContext()).CreateAsync(new NewWeatherPoint
        {
            Name = "Cabin", Latitude = 61, Longitude = 9, TimeZoneId = "Europe/Oslo", ForecastEnabled = true, HistoryEnabled = true, IsTracked = false,
        }, WeatherTestHarness.AdminId, CancellationToken.None);
        Assert.False(untracked.IsTracked);
    }

    [Fact]
    public async Task Rows_past_a_lowered_cap_are_reported_suspended_in_enable_order()
    {
        using var h = new WeatherTestHarness();
        await h.EnableAsync("Alpha");
        h.Time.Advance(TimeSpan.FromMinutes(1));
        await h.EnableAsync("Beta");
        h.Settings.Value = h.Settings.Value with { MaxTrackedPoints = 1 };

        var rows = (await h.Admin().ListAsync(new WeatherPointsQueryParams(), true, CancellationToken.None)).Items;

        Assert.False(rows.Single(r => r.Name == "Alpha").TrackingSuspended);
        var beta = rows.Single(r => r.Name == "Beta");
        Assert.True(beta.TrackingSuspended);
        Assert.Equal(WeatherTrackingSuspendedReason.CapReached, beta.TrackingSuspendedReason);
    }

    [Fact]
    public async Task Disabling_the_household_default_is_a_409()
    {
        using var h = new WeatherTestHarness();
        var location = await h.EnableAsync();
        h.Settings.Value = h.Settings.Value with { HouseholdDefaultPointId = location.Id };

        var error = await Assert.ThrowsAsync<WeatherConflictException>(() => h.Admin().SetAsync(location.Id, false, false, true));
        Assert.Equal(WeatherConflictException.HouseholdDefault, error.Code);
    }

    [Fact]
    public async Task Moving_or_archiving_a_linked_place_never_touches_its_point()
    {
        using var h = new WeatherTestHarness();
        var point = await h.EnableAsync();
        var moved = await MoveAsync(h, point.LocationId, 60.39299m);
        moved.Archived = Noon;
        await using (var context = h.NewContext())
        {
            context.Locations.Update(moved);
            await context.SaveChangesAsync();
        }

        h.Forecasts.Respond = _ => Task.FromResult(WeatherTestHarness.Run(Noon.AddMinutes(-30), Noon, 6, 7));
        var attempt = await h.Fetch(h.NewContext()).FetchForecastAsync(point.Id, WeatherFetchMode.Scheduled, CancellationToken.None);

        // The point keeps its own site and is still fetched: the archived-place skip is gone with the key.
        Assert.Equal(WeatherAttemptStatus.Completed, attempt.Status);
        Assert.Equal(59.913m, h.Forecasts.LastSite!.Latitude);
        var row = (await h.Admin(h.NewContext()).GetAsync(point.Id, true, CancellationToken.None))!;
        Assert.Equal(1, row.Site.Version);
    }

    // ── Admin: move ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_changed_rounded_position_starts_site_two_and_warns_with_the_linked_count()
    {
        using var h = new WeatherTestHarness();
        var point = await h.EnableAsync();
        var second = await h.AddLocationAsync("Cabin");
        await h.LinkAsync(second.LocationId, point.Id);

        h.Time.Advance(TimeSpan.FromMinutes(1));
        var result = await h.Admin(h.NewContext()).SetAsync(point.Id, true, true, true, latitude: 60.39299m);

        Assert.Equal(2, result!.Point.Site.Version);
        Assert.Equal(60.393m, result.Point.Site.Latitude);
        Assert.Equal(2, await h.NewContext().WeatherPointSites.CountAsync());
        var warning = Assert.Single(result.Warnings);
        Assert.Equal(WeatherAdvisoryCode.LinkedLocationsFollowMove, warning.Code);
        Assert.Equal(2, warning.Count);
        Assert.Contains("2 places", warning.Message, StringComparison.Ordinal);

        // Without locations.read the warning carries no count.
        var blind = await h.Admin(h.NewContext()).UpdateAsync(point.Id,
            WeatherPointServiceTestExtensions.Update(result.Point, latitude: 61.5m), WeatherTestHarness.AdminId, canReadLocations: false, CancellationToken.None);
        Assert.Null(Assert.Single(blind!.Warnings).Count);
        Assert.Null(blind.Point.LinkedLocationCount);

        await h.Fetch(h.NewContext()).FetchForecastAsync(point.Id, WeatherFetchMode.Scheduled, CancellationToken.None);
        Assert.Equal(61.5m, h.Forecasts.LastSite!.Latitude);
    }

    [Fact]
    public async Task A_change_below_the_rounding_starts_no_new_site()
    {
        using var h = new WeatherTestHarness();
        var point = await h.EnableAsync();

        // 59.91273 rounds to 59.913, and so does 59.9133.
        var result = await h.Admin().SetAsync(point.Id, true, true, true, latitude: 59.9133m);

        Assert.Equal(1, result!.Point.Site.Version);
        Assert.Empty(result.Warnings);
        Assert.Equal(1, await h.NewContext().WeatherPointSites.CountAsync());
    }

    [Fact]
    public async Task A_move_never_shortens_a_pause()
    {
        using var h = new WeatherTestHarness();
        var location = await h.EnableAsync();
        h.Forecasts.Respond = _ => Task.FromResult(new ForecastFetchResult(WeatherFetchOutcome.Failed, FailureKind: WeatherFetchFailureKind.RateLimited));
        await h.Fetch().FetchForecastAsync(location.Id, WeatherFetchMode.Scheduled, CancellationToken.None);

        await h.Admin(h.NewContext()).SetAsync(location.Id, true, true, true, latitude: 60.39299m);

        var schedule = await h.NewContext().WeatherPointSchedules.SingleAsync();
        Assert.Equal(Noon.AddHours(6), schedule.NextForecastFetchAfterUtc);
    }

    // ── Admin: purge ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Delete_needs_both_halves_off_removes_every_row_and_unlinks_the_places()
    {
        using var h = new WeatherTestHarness();
        var location = await h.EnableAsync();
        h.Forecasts.Respond = _ => Task.FromResult(WeatherTestHarness.Run(Noon.AddMinutes(-30), Noon, 24, 7));
        await h.Fetch().FetchForecastAsync(location.Id, WeatherFetchMode.Scheduled, CancellationToken.None);

        var enabled = await Assert.ThrowsAsync<WeatherConflictException>(() => h.Admin(h.NewContext()).DeleteAsync(location.Id, WeatherTestHarness.AdminId, CancellationToken.None));
        Assert.Equal(WeatherConflictException.StillEnabled, enabled.Code);
        await h.Admin(h.NewContext()).SetAsync(location.Id, false, true, true);
        var historyOn = await Assert.ThrowsAsync<WeatherConflictException>(() => h.Admin(h.NewContext()).DeleteAsync(location.Id, WeatherTestHarness.AdminId, CancellationToken.None));
        Assert.Equal(WeatherConflictException.StillEnabled, historyOn.Code);

        await h.Admin(h.NewContext()).SetAsync(location.Id, false, false, true);
        Assert.True(await h.Admin(h.NewContext()).DeleteAsync(location.Id, WeatherTestHarness.AdminId, CancellationToken.None));

        var check = h.NewContext();
        Assert.Equal(0, await check.WeatherPoints.CountAsync());
        Assert.Equal(0, await check.WeatherPointSites.CountAsync());
        Assert.Equal(0, await check.WeatherPointSchedules.CountAsync());
        Assert.Equal(0, await check.WeatherForecastRuns.CountAsync());
        Assert.Equal(0, await check.WeatherForecastSteps.CountAsync());
        Assert.Equal(0, await check.WeatherDays.CountAsync());
        var place = await check.Locations.SingleAsync(l => l.LocationId == location.LocationId);
        Assert.Null(place.WeatherPointId);
        Assert.False(await h.Admin(h.NewContext()).DeleteAsync(location.Id, WeatherTestHarness.AdminId, CancellationToken.None));
    }

    [Fact]
    public async Task Deleting_the_household_default_is_a_409()
    {
        using var h = new WeatherTestHarness();
        var home = await h.EnableAsync(forecast: false, history: false);
        h.Settings.Value = h.Settings.Value with { HouseholdDefaultPointId = home.Id };

        var error = await Assert.ThrowsAsync<WeatherConflictException>(() => h.Admin().DeleteAsync(home.Id, WeatherTestHarness.AdminId, CancellationToken.None));
        Assert.Equal(WeatherConflictException.HouseholdDefault, error.Code);
    }

    [Fact]
    public async Task An_interrupted_full_delete_refuses_a_history_purge_and_completes_under_delete()
    {
        using var h = new WeatherTestHarness();
        var point = await h.EnableAsync(forecast: false, history: false);
        await FenceAsync(h, WeatherPurgeMode.Full);

        var refused = await Assert.ThrowsAsync<WeatherConflictException>(() =>
            h.Admin(h.NewContext()).PurgeHistoryAsync(point.Id, WeatherTestHarness.AdminId, CancellationToken.None));
        Assert.Equal(WeatherConflictException.PurgeInProgress, refused.Code);

        Assert.True(await h.Admin(h.NewContext()).DeleteAsync(point.Id, WeatherTestHarness.AdminId, CancellationToken.None));
        Assert.Equal(0, await h.NewContext().WeatherPoints.CountAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_interrupted_history_purge_completes_under_either_endpoint(bool viaDelete)
    {
        using var h = new WeatherTestHarness();
        // Both halves off, so the full delete's preconditions would hold too; a resume skips them anyway.
        var point = await h.EnableAsync(forecast: false, history: false);
        await FenceAsync(h, WeatherPurgeMode.HistoryOnly);

        var service = h.Admin(h.NewContext());
        Assert.True(viaDelete
            ? await service.DeleteAsync(point.Id, WeatherTestHarness.AdminId, CancellationToken.None)
            : await service.PurgeHistoryAsync(point.Id, WeatherTestHarness.AdminId, CancellationToken.None));

        var check = h.NewContext();
        if (viaDelete)
        {
            Assert.Equal(0, await check.WeatherPoints.CountAsync());
        }
        else
        {
            var row = await check.WeatherPoints.SingleAsync();
            Assert.Null(row.PurgeStartedAt);
            Assert.Null(row.PurgeMode);
        }
    }

    private static async Task FenceAsync(WeatherTestHarness h, WeatherPurgeMode mode)
    {
        await using var context = h.NewContext();
        var row = await context.WeatherPoints.SingleAsync();
        row.PurgeStartedAt = Noon;
        row.PurgeMode = mode;
        var schedule = await context.WeatherPointSchedules.SingleAsync();
        schedule.LeaseUntilUtc = DateTime.MaxValue;
        await context.SaveChangesAsync();
    }

    // ── Forecast and historical data are separate halves ─────────────────────────────────────────

    [Fact]
    public async Task Options_list_every_point_by_name_with_its_switches_and_omit_one_being_purged()
    {
        using var h = new WeatherTestHarness();
        await h.EnableAsync("Both");
        await h.EnableAsync("Forecast only", history: false);
        await h.EnableAsync("History only", forecast: false);
        await h.EnableAsync("Purging", forecast: false, history: false);
        await using (var context = h.NewContext())
        {
            var purging = await context.WeatherPoints.SingleAsync(p => p.Name == "Purging");
            purging.PurgeStartedAt = Noon;
            purging.PurgeMode = WeatherPurgeMode.Full;
            await context.SaveChangesAsync();
        }

        var options = (await h.Read().OptionsAsync(null, CancellationToken.None))!;

        Assert.Equal(["Both", "Forecast only", "History only"], options.Points.Select(p => p.Name));
        Assert.Equal([(true, true), (true, false), (false, true)], options.Points.Select(p => (p.ForecastEnabled, p.HistoryEnabled)));
        Assert.All(options.Points, p => Assert.Null(p.DistanceKm));
    }

    [Fact]
    public async Task Options_near_a_place_sort_the_whole_set_by_distance_before_truncating()
    {
        using var h = new WeatherTestHarness();
        var origin = await h.AddLocationAsync("Origin", latitude: 59.9m, longitude: 10.7m);
        var now = h.Time.GetUtcNow().UtcDateTime;
        // 250 points named so the nearest sorts LAST by name: a truncate-then-sort would lose it (AC 17).
        for (var i = 0; i < 250; i++)
        {
            var id = Guid.NewGuid();
            var near = i == 0;
            h.Context.WeatherPoints.Add(new Odyssey.Context.WeatherPoint
            {
                WeatherPointId = id, Name = near ? "zzz Nearest" : $"Point {i:D3}", ForecastEnabled = true, HistoryFrom = new DateOnly(2025, 1, 1),
                CurrentSiteVersion = 1, SettingsVersion = Guid.NewGuid(), EnabledAt = now, CreatedAt = now, UpdatedAt = now,
            });
            h.Context.WeatherPointSites.Add(new Odyssey.Context.WeatherPointSite
            {
                WeatherPointId = id, SiteVersion = 1, Latitude = near ? 59.91m : 40 + i * 0.01m, Longitude = near ? 10.71m : 0, TimeZoneId = "UTC", AppliedAt = now,
            });
        }

        await h.Context.SaveChangesAsync();
        h.Context.ChangeTracker.Clear();

        var options = (await h.Read().OptionsAsync(origin.LocationId, CancellationToken.None))!;

        Assert.Equal(WeatherRules.OptionsMax, options.Points.Count);
        Assert.Equal("zzz Nearest", options.Points[0].Name);
        Assert.Equal(1, options.Points[0].DistanceKm);
        Assert.True(options.Points.Zip(options.Points.Skip(1)).All(pair => pair.First.DistanceKm <= pair.Second.DistanceKm));
        Assert.Null(await h.Read().OptionsAsync(Guid.NewGuid(), CancellationToken.None));
    }

    [Fact]
    public async Task A_history_only_place_is_not_selectable_and_never_fetches_a_forecast_but_measures()
    {
        using var h = new WeatherTestHarness(new DateTimeOffset(2026, 10, 23, 9, 0, 0, TimeSpan.Zero));
        h.Secrets.Frost = SecretResult.Found("client-id");
        var cabin = await h.EnableAsync("Cabin", forecast: false);

        Assert.False(await new WeatherPointRules().IsSelectableAsync(h.NewContext(), cabin.Id, CancellationToken.None));
        var forecast = await h.Fetch().FetchForecastAsync(cabin.Id, WeatherFetchMode.Scheduled, CancellationToken.None);
        Assert.Equal(WeatherAttemptStatus.Skipped, forecast.Status);
        // An explicit read of its history is a pure read: not selectable, so no on-demand forecast.
        await h.Read(h.NewContext()).DailyAsync(Daily(cabin.Id, new DateOnly(2026, 10, 20), new DateOnly(2026, 10, 25)), "u1", CancellationToken.None);
        Assert.Equal(0, h.Forecasts.Calls);

        await h.Fetch(h.NewContext()).FetchObservationsAsync(cabin.Id, CancellationToken.None);
        Assert.Equal(1, h.Observations.StationCalls);
    }

    [Fact]
    public async Task A_forecast_only_place_never_fetches_measured_history()
    {
        using var h = new WeatherTestHarness(new DateTimeOffset(2026, 10, 23, 9, 0, 0, TimeSpan.Zero));
        h.Secrets.Frost = SecretResult.Found("client-id");
        var home = await h.EnableAsync(history: false);

        await h.Fetch().FetchObservationsAsync(home.Id, CancellationToken.None);

        Assert.Equal(0, h.Observations.StationCalls);
        Assert.Empty(h.Observations.DailyCalls);
    }

    [Fact]
    public async Task The_household_default_may_turn_history_off_but_not_its_forecast()
    {
        using var h = new WeatherTestHarness();
        var home = await h.EnableAsync();
        h.Settings.Value = h.Settings.Value with { HouseholdDefaultPointId = home.Id };

        var row = (await h.Admin().SetAsync(home.Id, true, false, true))!.Point;
        Assert.True(row.ForecastEnabled);
        Assert.False(row.HistoryEnabled);
        Assert.True(row.IsHouseholdDefault);

        var error = await Assert.ThrowsAsync<WeatherConflictException>(() => h.Admin(h.NewContext()).SetAsync(home.Id, false, false, true));
        Assert.Equal(WeatherConflictException.HouseholdDefault, error.Code);
    }

    [Fact]
    public async Task The_scheduling_cap_counts_forecast_points_only()
    {
        using var h = new WeatherTestHarness();
        h.Settings.Value = h.Settings.Value with { MaxTrackedPoints = 1 };
        await h.EnableAsync("Home");

        // IsTracked is a forecast setting: with the forecast off it claims no scheduled slot.
        var cabin = await h.EnableAsync("Cabin", forecast: false);

        var error = await Assert.ThrowsAsync<DomainUnprocessableException>(() => h.Admin(h.NewContext()).SetAsync(cabin.Id, true, true, true));
        Assert.Contains("isTracked", error.Errors!.Keys);
    }

    [Fact]
    public async Task A_history_purge_is_refused_while_history_is_on_even_with_the_forecast_off()
    {
        using var h = new WeatherTestHarness();
        var cabin = await h.EnableAsync("Cabin", forecast: false);

        var error = await Assert.ThrowsAsync<WeatherConflictException>(() => h.Admin().PurgeHistoryAsync(cabin.Id, WeatherTestHarness.AdminId, CancellationToken.None));
        Assert.Equal(WeatherConflictException.StillEnabled, error.Code);
    }

    [Fact]
    public async Task With_the_forecast_on_a_purge_removes_only_the_measured_data()
    {
        using var h = new WeatherTestHarness();
        var home = await h.EnableAsync();
        h.Settings.Value = h.Settings.Value with { HouseholdDefaultPointId = home.Id };
        h.Forecasts.Respond = _ => Task.FromResult(WeatherTestHarness.Run(Noon.AddMinutes(-30), Noon, 24, 7));
        await h.Fetch().FetchForecastAsync(home.Id, WeatherFetchMode.Scheduled, CancellationToken.None);
        var weather = await h.Context.WeatherPoints.SingleAsync(w => w.WeatherPointId == home.Id);
        weather.StationResolvedAtUtc = Noon;
        h.Context.WeatherStations.Add(new Odyssey.Context.WeatherStation
        {
            WeatherPointId = home.Id, Group = WeatherMeasureGroup.Temperature, StationId = "SN18700", ResolvedAtUtc = Noon,
        });
        // One day measured only, and the forecast's own day measured too.
        var forecastDay = await h.Context.WeatherDays.FirstAsync(d => d.WeatherPointId == home.Id);
        forecastDay.ObservedMeanTempC = 5;
        forecastDay.ObservedFetchedAtUtc = Noon;
        h.Context.WeatherDays.Add(MeasuredDay(home.Id, new DateOnly(2026, 9, 1)));
        h.Context.WeatherObservations.Add(new Odyssey.Context.WeatherObservation
        {
            WeatherObservationId = Guid.NewGuid(), WeatherPointId = home.Id, SiteVersion = 1, TemperatureStationId = "SN18700",
            LocalDate = new DateOnly(2026, 9, 1), MeanTempC = 5,
        });
        var schedule = await h.Context.WeatherPointSchedules.SingleAsync(s => s.WeatherPointId == home.Id);
        schedule.ObservationsCheckedThroughDate = new DateOnly(2026, 10, 22);
        schedule.LastObservationSuccessUtc = Noon;
        await h.Context.SaveChangesAsync();
        h.Context.ChangeTracker.Clear();
        await h.Admin().SetAsync(home.Id, true, false, true);

        // The household default is kept, so this purge is allowed for it.
        Assert.True(await h.Admin(h.NewContext()).PurgeHistoryAsync(home.Id, WeatherTestHarness.AdminId, CancellationToken.None));

        var check = h.NewContext();
        var row = await check.WeatherPoints.SingleAsync(w => w.WeatherPointId == home.Id);
        Assert.True(row.ForecastEnabled);
        Assert.Null(row.PurgeStartedAt);
        Assert.Null(row.StationResolvedAtUtc);
        Assert.Equal(0, await check.WeatherStations.CountAsync());
        Assert.NotEqual(0, await check.WeatherForecastRuns.CountAsync());
        Assert.NotEqual(0, await check.WeatherForecastSteps.CountAsync());
        Assert.Equal(0, await check.WeatherObservations.CountAsync());
        Assert.False(await check.WeatherDays.AnyAsync(d => d.ObservedFetchedAtUtc != null || d.ObservedMeanTempC != null));
        Assert.False(await check.WeatherDays.AnyAsync(d => d.LocalDate == new DateOnly(2026, 9, 1)));
        Assert.True(await check.WeatherDays.AnyAsync(d => d.LocalDate == forecastDay.LocalDate && d.ForecastCoverageHours != null));
        var afterSchedule = await check.WeatherPointSchedules.SingleAsync(s => s.WeatherPointId == home.Id);
        Assert.Null(afterSchedule.ObservationsCheckedThroughDate);
        Assert.Null(afterSchedule.LastObservationSuccessUtc);
        Assert.Null(afterSchedule.LeaseUntilUtc);

        // The forecast keeps working after the purge.
        h.Time.Advance(TimeSpan.FromHours(2));
        var again = await h.Fetch(h.NewContext()).FetchForecastAsync(home.Id, WeatherFetchMode.Refresh, CancellationToken.None);
        Assert.NotEqual(WeatherAttemptStatus.Skipped, again.Status);
    }

    [Fact]
    public async Task Purge_refuses_while_a_fetch_holds_the_lease_and_resumes_once_started()
    {
        using var h = new WeatherTestHarness();
        var location = await h.EnableAsync();
        await h.Admin().SetAsync(location.Id, false, false, true);
        var context = h.NewContext();
        var schedule = await context.WeatherPointSchedules.SingleAsync();
        schedule.LeaseUntilUtc = Noon.AddMinutes(1);
        await context.SaveChangesAsync();

        var leased = await Assert.ThrowsAsync<WeatherConflictException>(() => h.Admin(h.NewContext()).DeleteAsync(location.Id, WeatherTestHarness.AdminId, CancellationToken.None));
        Assert.Equal(WeatherConflictException.FetchInProgress, leased.Code);

        // An interrupted purge: the fence is set, the lease is far future, the rows are still there.
        var weather = await context.WeatherPoints.SingleAsync();
        weather.PurgeStartedAt = Noon;
        weather.PurgeMode = WeatherPurgeMode.Full;
        schedule.LeaseUntilUtc = DateTime.MaxValue;
        await context.SaveChangesAsync();

        var row = await h.Admin(h.NewContext()).GetAsync(location.Id, true, CancellationToken.None);
        Assert.True(row!.PurgeInProgress);
        var enable = await Assert.ThrowsAsync<WeatherConflictException>(() => h.Admin(h.NewContext()).SetAsync(location.Id, true, true, true));
        Assert.Equal(WeatherConflictException.PurgeInProgress, enable.Code);

        Assert.True(await h.Admin(h.NewContext()).DeleteAsync(location.Id, WeatherTestHarness.AdminId, CancellationToken.None));
        Assert.Equal(0, await h.NewContext().WeatherPoints.CountAsync());
    }

    // ── Reads ─────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Resolution_is_explicit_then_preference_then_household_default_then_none()
    {
        using var h = new WeatherTestHarness();
        var home = await h.EnableAsync("Home");
        var cabin = await h.EnableAsync("Cabin");
        var read = h.Read();

        Assert.Null((await read.ResolveAsync(null, "u1", CancellationToken.None)).Point);

        h.Settings.Value = h.Settings.Value with { HouseholdDefaultPointId = home.Id };
        Assert.Equal(home.Id, (await read.ResolveAsync(null, "u1", CancellationToken.None)).Point!.WeatherPointId);

        await read.SetPreferenceAsync("u1", new UpdateWeatherPreferences { DefaultPointId = cabin.Id }, CancellationToken.None);
        Assert.Equal(cabin.Id, (await read.ResolveAsync(null, "u1", CancellationToken.None)).Point!.WeatherPointId);
        Assert.Equal(home.Id, (await read.ResolveAsync(null, "u2", CancellationToken.None)).Point!.WeatherPointId);

        Assert.Equal(home.Id, (await read.ResolveAsync(home.Id, "u1", CancellationToken.None)).Point!.WeatherPointId);
        await Assert.ThrowsAsync<DomainNotFoundException>(() => read.ResolveAsync(Guid.NewGuid(), "u1", CancellationToken.None));
    }

    [Fact]
    public async Task A_preference_for_an_unknown_or_forecast_off_point_is_a_400_keyed_defaultPointId()
    {
        using var h = new WeatherTestHarness();
        var off = await h.EnableAsync(forecast: false);

        foreach (var id in new[] { Guid.NewGuid(), off.Id })
        {
            var error = await Assert.ThrowsAsync<DomainValidationException>(() => h.Read().SetPreferenceAsync("u1",
                new UpdateWeatherPreferences { DefaultPointId = id }, CancellationToken.None));
            Assert.Contains("defaultPointId", error.Errors!.Keys);
        }
    }

    [Fact]
    public async Task The_preference_is_its_own_user_preference_row_and_leaves_the_others_alone()
    {
        using var h = new WeatherTestHarness();
        var cabin = await h.EnableAsync("Cabin");
        var preferences = new UserPreferencesService(h.Context, h.Time);
        await preferences.UpsertAsync("u1", "preferences-page", """{"isDarkMode":true}""", CancellationToken.None);

        await h.Read().SetPreferenceAsync("u1", new UpdateWeatherPreferences { DefaultPointId = cabin.Id }, CancellationToken.None);

        Assert.Equal("""{"isDarkMode":true}""", (await preferences.GetAsync("u1", "preferences-page", CancellationToken.None))!.PreferencesJson);
        Assert.Equal($$"""{"defaultPointId":"{{cabin.Id}}"}""",
            (await preferences.GetAsync("u1", UserPreferencesService.WeatherKey, CancellationToken.None))!.PreferencesJson);
        Assert.Equal(cabin.Id, (await h.Read().PreferencesAsync("u1", CancellationToken.None)).DefaultPoint!.WeatherPointId);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"defaultPointId":"not-a-guid"}""")]
    [InlineData("null")]
    public async Task An_unreadable_stored_preference_falls_back_to_the_household_default(string stored)
    {
        using var h = new WeatherTestHarness();
        var home = await h.EnableAsync("Home");
        h.Settings.Value = h.Settings.Value with { HouseholdDefaultPointId = home.Id };
        await new UserPreferencesService(h.Context, h.Time)
            .UpsertServerOwnedAsync("u1", UserPreferencesService.WeatherKey, stored, CancellationToken.None);

        Assert.Equal(home.Id, (await h.Read().ResolveAsync(null, "u1", CancellationToken.None)).Point!.WeatherPointId);
    }

    [Theory]
    [InlineData("weather-preferences")]
    [InlineData("Weather-Preferences")]
    [InlineData("weather-preferences  ")]
    public async Task The_generic_preference_write_refuses_the_weather_key(string key)
    {
        using var h = new WeatherTestHarness();

        var error = await Assert.ThrowsAsync<DomainValidationException>(() => new UserPreferencesService(h.Context, h.Time)
            .UpsertAsync("u1", key, """{"defaultPointId":null}""", CancellationToken.None));
        Assert.Contains("pageKey", error.Errors!.Keys);
    }

    [Fact]
    public async Task Current_with_no_place_is_NoPlace_and_with_no_contact_is_NotConfigured()
    {
        using var h = new WeatherTestHarness();
        Assert.Equal(WeatherStatus.NoPlace, (await h.Read().CurrentAsync(null, "u1", CancellationToken.None)).Status);

        var home = await h.EnableAsync(tracked: false);
        h.Settings.Value = h.Settings.Value with { Contact = null };
        var current = await h.Read().CurrentAsync(home.Id, "u1", CancellationToken.None);
        Assert.Equal(WeatherStatus.NotConfigured, current.Status);
        Assert.Equal(0, h.Forecasts.Calls);
    }

    [Fact]
    public async Task An_untracked_location_is_fetched_on_demand_and_a_tracked_one_is_not()
    {
        using var h = new WeatherTestHarness();
        var untracked = await h.EnableAsync("Cabin", tracked: false);
        var tracked = await h.EnableAsync("Home", tracked: true);
        h.Forecasts.Respond = _ => Task.FromResult(WeatherTestHarness.Run(Noon.AddMinutes(-30), Noon.AddHours(-1), 24, 7));

        var cabin = await h.Read().CurrentAsync(untracked.Id, "u1", CancellationToken.None);
        Assert.Equal(1, h.Forecasts.Calls);
        Assert.Equal(WeatherStatus.Ok, cabin.Status);
        Assert.Equal(7, cabin.Step!.AirTemperatureC);

        var home = await h.Read(h.NewContext()).CurrentAsync(tracked.Id, "u1", CancellationToken.None);
        Assert.Equal(1, h.Forecasts.Calls);
        Assert.Equal(WeatherStatus.NoData, home.Status);
        Assert.Equal("Weather data from MET Norway", home.Attribution.Text);
    }

    [Fact]
    public async Task A_daily_range_ending_before_the_local_today_never_fetches_on_demand()
    {
        using var h = new WeatherTestHarness();
        var cabin = await h.EnableAsync("Cabin", tracked: false);
        h.Forecasts.Respond = _ => Task.FromResult(WeatherTestHarness.Run(Noon.AddMinutes(-30), Noon.AddHours(-1), 24, 7));
        var today = new DateOnly(2026, 10, 23);

        await h.Read().DailyAsync(Daily(cabin.Id, today.AddDays(-60), today.AddDays(-1)), "u1", CancellationToken.None);
        Assert.Equal(0, h.Forecasts.Calls);

        await h.Read(h.NewContext()).DailyAsync(Daily(cabin.Id, today.AddDays(-2), today), "u1", CancellationToken.None);
        Assert.Equal(1, h.Forecasts.Calls);
    }

    [Fact]
    public async Task A_daily_range_reaching_a_west_of_utc_local_today_still_fetches_on_demand()
    {
        // 03:00 UTC on the 24th is still the 23rd in Los Angeles, so a range ending on the 23rd reaches today there.
        using var h = new WeatherTestHarness(new DateTimeOffset(2026, 10, 24, 3, 0, 0, TimeSpan.Zero));
        var place = await h.EnableAsync("Cabin", tracked: false, zone: "America/Los_Angeles");
        var now = h.Time.GetUtcNow().UtcDateTime;
        h.Forecasts.Respond = _ => Task.FromResult(WeatherTestHarness.Run(now.AddMinutes(-30), now.AddHours(-1), 24, 7));

        await h.Read().DailyAsync(Daily(place.Id, new DateOnly(2026, 10, 21), new DateOnly(2026, 10, 23)), "u1", CancellationToken.None);

        Assert.Equal(1, h.Forecasts.Calls);
    }

    [Fact]
    public async Task Daily_carries_the_measured_history_bounds_whatever_range_was_asked()
    {
        using var h = new WeatherTestHarness();
        var home = await h.EnableAsync();
        h.Context.WeatherDays.AddRange(
            MeasuredDay(home.Id, new DateOnly(2026, 3, 2)),
            MeasuredDay(home.Id, new DateOnly(2026, 10, 21)),
            // A forecast-only day is not measured history, however recent.
            new Odyssey.Context.WeatherDay { WeatherPointId = home.Id, LocalDate = new DateOnly(2026, 10, 24), ForecastMaxTempC = 9, UpdatedAtUtc = Noon });
        await h.Context.SaveChangesAsync();
        h.Context.ChangeTracker.Clear();

        var daily = await h.Read().DailyAsync(Daily(home.Id, new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 30)), "u1", CancellationToken.None);

        Assert.Empty(daily.Days);
        Assert.Equal(new DateOnly(2026, 3, 2), daily.History!.MeasuredFrom);
        Assert.Equal(new DateOnly(2026, 10, 21), daily.History.MeasuredThrough);
        Assert.True(daily.History.HasObservationStation);
    }

    [Fact]
    public async Task A_weather_off_place_still_serves_its_measured_history_and_never_fetches()
    {
        using var h = new WeatherTestHarness();
        var boat = await h.EnableAsync("Boat", tracked: false);
        h.Context.WeatherDays.Add(MeasuredDay(boat.Id, new DateOnly(2026, 8, 30)));
        await h.Context.SaveChangesAsync();
        await h.Admin().SetAsync(boat.Id, false, false, false);
        h.Context.ChangeTracker.Clear();

        // The location record's history chart asks for the window ending on the newest measured day, which
        // reaches today only for a place still measuring; this one stopped, and its history is kept.
        var daily = await h.Read(h.NewContext()).DailyAsync(
            Daily(boat.Id, new DateOnly(2026, 8, 1), new DateOnly(2026, 10, 23)), "u1", CancellationToken.None);

        Assert.Equal(new DateOnly(2026, 8, 30), Assert.Single(daily.Days).Date);
        Assert.Equal(new DateOnly(2026, 8, 30), daily.History!.MeasuredThrough);
        Assert.Equal(0, h.Forecasts.Calls);
    }

    [Fact]
    public async Task A_position_never_looked_up_is_not_reported_as_having_no_station()
    {
        // Frost not configured is the ordinary way to get here: no lookup has run, so nothing is known about
        // stations, and saying "none within 25 km" would send an administrator after the wrong cause.
        using var h = new WeatherTestHarness();
        var home = await h.EnableAsync();

        var daily = await h.Read().DailyAsync(Daily(home.Id, new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 22)), "u1", CancellationToken.None);

        Assert.Null(daily.History!.MeasuredFrom);
        Assert.Null(daily.History.MeasuredThrough);
        Assert.True(daily.History.HasObservationStation);
        Assert.False(daily.History.ObservationsConfigured);
        Assert.True((await h.Admin().ListAsync(new WeatherPointsQueryParams(), true, CancellationToken.None)).Items.Single().HasObservationStation);
    }

    [Fact]
    public async Task A_lookup_that_found_nothing_reports_no_station()
    {
        using var h = new WeatherTestHarness();
        var home = await h.EnableAsync();
        var weather = await h.Context.WeatherPoints.SingleAsync(w => w.WeatherPointId == home.Id);
        weather.StationResolvedAtUtc = Noon;
        await h.Context.SaveChangesAsync();
        h.Context.ChangeTracker.Clear();

        var daily = await h.Read().DailyAsync(Daily(home.Id, new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 22)), "u1", CancellationToken.None);

        Assert.False(daily.History!.HasObservationStation);
        Assert.False((await h.Admin(h.NewContext()).ListAsync(new WeatherPointsQueryParams(), true, CancellationToken.None)).Items.Single().HasObservationStation);
    }

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    public async Task Observations_are_configured_with_both_a_contact_and_a_frost_client_id(bool contact, bool clientId, bool expected)
    {
        using var h = new WeatherTestHarness();
        var home = await h.EnableAsync();
        if (!contact) h.Settings.Value = h.Settings.Value with { Contact = null };
        if (clientId) h.Secrets.Frost = SecretResult.Found("frost-client");

        var daily = await h.Read().DailyAsync(Daily(home.Id, new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 22)), "u1", CancellationToken.None);

        Assert.Equal(expected, daily.History!.ObservationsConfigured);
    }

    private static WeatherDailyQuery Daily(Guid locationId, DateOnly from, DateOnly to) =>
        new() { WeatherPointId = locationId, From = from, To = to };

    private static Odyssey.Context.WeatherDay MeasuredDay(Guid locationId, DateOnly date) => new()
    {
        WeatherPointId = locationId,
        LocalDate = date,
        ObservedMinTempC = 2,
        ObservedMaxTempC = 9,
        ObservedMeanTempC = 5,
        ObservedFetchedAtUtc = Noon,
        UpdatedAtUtc = Noon,
    };

    [Fact]
    public async Task A_failed_on_demand_fetch_is_reported_stale()
    {
        using var h = new WeatherTestHarness();
        var cabin = await h.EnableAsync("Cabin", tracked: false);
        h.Forecasts.Respond = _ => Task.FromResult(new ForecastFetchResult(WeatherFetchOutcome.Failed, FailureKind: WeatherFetchFailureKind.Timeout));

        var current = await h.Read().CurrentAsync(cabin.Id, "u1", CancellationToken.None);

        Assert.True(current.Stale);
    }

    private static async Task<Odyssey.Context.Location> MoveAsync(WeatherTestHarness h, Guid locationId, decimal latitude)
    {
        var context = h.NewContext();
        var live = await context.Locations.SingleAsync(l => l.LocationId == locationId);
        live.Latitude = latitude;
        live.UpdatedAt = h.Time.GetUtcNow().UtcDateTime;
        await context.SaveChangesAsync();
        return live;
    }
}
