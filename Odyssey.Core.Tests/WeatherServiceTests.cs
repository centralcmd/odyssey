using Microsoft.EntityFrameworkCore;
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

        var attempt = await h.Fetch().FetchForecastAsync(location.LocationId, WeatherFetchMode.Scheduled, CancellationToken.None);

        Assert.Equal(WeatherAttemptStatus.Completed, attempt.Status);
        Assert.True(attempt.StoredNewRun);
        Assert.Equal(new WeatherSiteQuery(59.913m, 10.750m, 12), h.Forecasts.LastSite);
        Assert.Equal(1, await h.Context.WeatherForecastRuns.CountAsync());
        Assert.Equal(36, await h.Context.WeatherForecastSteps.CountAsync());
        Assert.True(await h.Context.WeatherDays.AnyAsync(d => d.LocalDate == new DateOnly(2026, 10, 23) && d.ForecastMaxTempC == 7));
        var schedule = await h.Context.WeatherLocationSchedules.AsNoTracking().SingleAsync();
        Assert.Null(schedule.LeaseUntilUtc is { } lease && lease > Noon ? schedule.LeaseUntilUtc : null);
        Assert.True(schedule.NextForecastFetchAfterUtc >= Noon.AddMinutes(60));
    }

    [Fact]
    public async Task The_same_issue_time_stores_nothing_new()
    {
        using var h = new WeatherTestHarness();
        var location = await h.EnableAsync();
        h.Forecasts.Respond = _ => Task.FromResult(WeatherTestHarness.Run(Noon.AddMinutes(-30), Noon, 6, 7));
        await h.Fetch().FetchForecastAsync(location.LocationId, WeatherFetchMode.Scheduled, CancellationToken.None);

        h.Time.Advance(TimeSpan.FromHours(2));
        var second = await h.Fetch(h.NewContext()).FetchForecastAsync(location.LocationId, WeatherFetchMode.Scheduled, CancellationToken.None);

        Assert.Equal(WeatherAttemptStatus.Completed, second.Status);
        Assert.False(second.StoredNewRun);
        Assert.Equal(1, await h.NewContext().WeatherForecastRuns.CountAsync());
    }

    [Fact]
    public async Task A_fetch_is_not_sent_before_it_is_due()
    {
        using var h = new WeatherTestHarness();
        var location = await h.EnableAsync();
        await h.Fetch().FetchForecastAsync(location.LocationId, WeatherFetchMode.Scheduled, CancellationToken.None);

        h.Time.Advance(TimeSpan.FromMinutes(10));
        var again = await h.Fetch(h.NewContext()).FetchForecastAsync(location.LocationId, WeatherFetchMode.Scheduled, CancellationToken.None);

        Assert.Equal(WeatherAttemptStatus.Skipped, again.Status);
        Assert.Equal(1, h.Forecasts.Calls);
    }

    [Fact]
    public async Task No_contact_means_nothing_is_sent()
    {
        using var h = new WeatherTestHarness();
        var location = await h.EnableAsync();
        h.Settings.Value = h.Settings.Value with { Contact = null };

        var attempt = await h.Fetch().FetchForecastAsync(location.LocationId, WeatherFetchMode.Scheduled, CancellationToken.None);

        Assert.Equal(WeatherAttemptStatus.NotConfigured, attempt.Status);
        Assert.Equal(0, h.Forecasts.Calls);
    }

    [Fact]
    public async Task A_403_pauses_six_hours_and_a_refresh_cannot_shorten_the_pause()
    {
        using var h = new WeatherTestHarness();
        var location = await h.EnableAsync();
        h.Forecasts.Respond = _ => Task.FromResult(new ForecastFetchResult(WeatherFetchOutcome.Failed, FailureKind: WeatherFetchFailureKind.ProviderRejected));

        var failed = await h.Fetch().FetchForecastAsync(location.LocationId, WeatherFetchMode.Scheduled, CancellationToken.None);
        h.Time.Advance(TimeSpan.FromMinutes(5));
        var refresh = await h.Fetch(h.NewContext()).FetchForecastAsync(location.LocationId, WeatherFetchMode.Refresh, CancellationToken.None);

        Assert.Equal(WeatherAttemptStatus.Failed, failed.Status);
        Assert.Equal(WeatherAttemptStatus.Skipped, refresh.Status);
        Assert.Equal(1, h.Forecasts.Calls);
        var schedule = await h.NewContext().WeatherLocationSchedules.SingleAsync();
        Assert.Equal(WeatherFetchFailureKind.ProviderRejected, schedule.ForecastFailureKind);
        Assert.Equal(Noon.AddHours(6), schedule.NextForecastFetchAfterUtc);
    }

    [Fact]
    public async Task A_scheduled_fetch_retries_a_5xx_but_never_a_429()
    {
        using var h = new WeatherTestHarness();
        var location = await h.EnableAsync();
        h.Forecasts.Respond = _ => Task.FromResult(new ForecastFetchResult(WeatherFetchOutcome.Failed, FailureKind: WeatherFetchFailureKind.ProviderError));
        await h.Fetch().FetchForecastAsync(location.LocationId, WeatherFetchMode.Scheduled, CancellationToken.None);
        Assert.Equal(3, h.Forecasts.Calls);

        using var h2 = new WeatherTestHarness();
        var other = await h2.EnableAsync();
        h2.Forecasts.Respond = _ => Task.FromResult(new ForecastFetchResult(WeatherFetchOutcome.Failed, FailureKind: WeatherFetchFailureKind.RateLimited));
        await h2.Fetch().FetchForecastAsync(other.LocationId, WeatherFetchMode.Scheduled, CancellationToken.None);
        Assert.Equal(1, h2.Forecasts.Calls);
    }

    [Fact]
    public async Task A_refresh_bypasses_the_interval_but_honours_expires()
    {
        using var h = new WeatherTestHarness();
        var location = await h.EnableAsync();
        h.Forecasts.Respond = _ => Task.FromResult(WeatherTestHarness.Run(Noon.AddMinutes(-30), Noon, 6, 7));
        await h.Fetch().FetchForecastAsync(location.LocationId, WeatherFetchMode.Scheduled, CancellationToken.None);

        // Run() expires 30 min after issue, i.e. at Noon. Ten minutes later the interval is still pending.
        h.Time.Advance(TimeSpan.FromMinutes(10));
        var refresh = await h.Fetch(h.NewContext()).FetchForecastAsync(location.LocationId, WeatherFetchMode.Refresh, CancellationToken.None);

        Assert.Equal(WeatherAttemptStatus.Completed, refresh.Status);
        Assert.Equal(2, h.Forecasts.Calls);
    }

    // ── Fencing (AC 43) ───────────────────────────────────────────────────────────────────────────

    public static TheoryData<string> FencingActions => new() { "disable", "apply", "purge-start" };

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
                    await h.Admin(other).UpsertAsync(location.LocationId, new UpsertWeatherLocation { Enabled = false, IsTracked = true }, WeatherTestHarness.AdminId, CancellationToken.None);
                    break;
                case "apply":
                    var live = await other.Locations.SingleAsync(l => l.LocationId == location.LocationId);
                    live.Latitude = 60.39299m;
                    await other.SaveChangesAsync();
                    await h.Admin(other).ApplyAsync(location.LocationId, new ApplyWeatherLocation { ExpectedLocationFingerprint = WeatherCoordinates.Fingerprint(live) }, WeatherTestHarness.AdminId, CancellationToken.None);
                    break;
                case "purge-start":
                    var weather = await other.WeatherLocations.SingleAsync();
                    var schedule = await other.WeatherLocationSchedules.SingleAsync();
                    weather.PurgeStartedAt = Noon;
                    schedule.LeaseUntilUtc = DateTime.MaxValue;
                    schedule.Version = Guid.NewGuid();
                    await other.SaveChangesAsync();
                    break;
            }

            return WeatherTestHarness.Run(Noon.AddMinutes(-30), Noon, 12, 7);
        };

        var attempt = await h.Fetch().FetchForecastAsync(location.LocationId, WeatherFetchMode.Scheduled, CancellationToken.None);

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
                concurrent = await h.Fetch(h.NewContext()).FetchForecastAsync(location.LocationId, WeatherFetchMode.Scheduled, CancellationToken.None);
            }

            return WeatherTestHarness.Run(Noon.AddMinutes(-30), Noon, 6, 7);
        };

        var first = await h.Fetch().FetchForecastAsync(location.LocationId, WeatherFetchMode.Scheduled, CancellationToken.None);

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
                WeatherForecastRunId = Guid.NewGuid(), LocationId = location.LocationId, SiteVersion = 1,
                Provider = WeatherProvider.MetNorway, ProviderUpdatedAt = issued, FetchedAtUtc = issued,
            };
            for (var hour = 0; hour < 24; hour++)
            {
                run.Steps.Add(new Odyssey.Context.WeatherForecastStep
                {
                    WeatherForecastRunId = run.WeatherForecastRunId, LocationId = location.LocationId, SiteVersion = 1,
                    RunProviderUpdatedAt = issued, RunFetchedAtUtc = issued, TimeUtc = Noon.AddHours(hour),
                    PeriodHours = 1, AirTemperatureC = i, SymbolCode = "cloudy",
                });
            }

            h.Context.WeatherForecastRuns.Add(run);
        }

        await h.Context.SaveChangesAsync();
        h.Context.ChangeTracker.Clear();

        var query = WeatherComposer.EffectiveStepsQuery(h.Context.WeatherForecastSteps, location.LocationId, Noon, Noon.AddHours(24));
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
        await adminContext.WeatherLocationSchedules.SingleAsync();
        await using (var other = h.NewContext())
        {
            var schedule = await other.WeatherLocationSchedules.SingleAsync();
            schedule.Version = Guid.NewGuid();
            await other.SaveChangesAsync();
        }

        var error = await Assert.ThrowsAsync<WeatherConflictException>(() => service.UpsertAsync(location.LocationId,
            new UpsertWeatherLocation { Enabled = false, IsTracked = true }, WeatherTestHarness.AdminId, CancellationToken.None));
        Assert.Equal(WeatherConflictException.FetchInProgress, error.Code);
    }

    [Fact]
    public async Task A_forecast_older_than_three_intervals_is_stale_even_with_no_failure()
    {
        using var h = new WeatherTestHarness();
        var cabin = await h.EnableAsync("Cabin", tracked: true);
        h.Forecasts.Respond = _ => Task.FromResult(WeatherTestHarness.Run(Noon.AddMinutes(-30), Noon, 48, 7));
        await h.Fetch().FetchForecastAsync(cabin.LocationId, WeatherFetchMode.Scheduled, CancellationToken.None);

        Assert.False((await h.Read(h.NewContext()).CurrentAsync(cabin.LocationId, "u1", CancellationToken.None)).Stale);

        // The scheduler stops (or the place is paused past the cap): no failure is ever recorded.
        h.Time.Advance(TimeSpan.FromHours(3) + TimeSpan.FromMinutes(1));
        Assert.True((await h.Read(h.NewContext()).CurrentAsync(cabin.LocationId, "u1", CancellationToken.None)).Stale);
    }

    // ── Observations ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Observations_need_the_frost_secret_and_report_an_unreadable_one()
    {
        using var h = new WeatherTestHarness(new DateTimeOffset(2026, 10, 23, 9, 0, 0, TimeSpan.Zero));
        var location = await h.EnableAsync();

        Assert.Equal(SecretReadState.NotSet, await h.Fetch().FetchObservationsAsync(location.LocationId, CancellationToken.None));
        h.Secrets.Frost = SecretResult.Unreadable;
        Assert.Equal(SecretReadState.Unreadable, await h.Fetch().FetchObservationsAsync(location.LocationId, CancellationToken.None));
        Assert.Equal(0, h.Observations.StationCalls);
    }

    [Fact]
    public async Task Observations_resolve_a_station_then_fetch_one_request_per_call()
    {
        using var h = new WeatherTestHarness(new DateTimeOffset(2026, 10, 23, 9, 0, 0, TimeSpan.Zero));
        h.Secrets.Frost = SecretResult.Found("client-id");
        var location = await h.EnableAsync();
        h.Observations.Daily = (from, _) => new ObservationFetchResult(true,
            [new ObservationDayData(new DateOnly(2026, 10, 22), 6.8m, 3.6m, 10.1m, 4.9m, 15.2m, 7.4m)]);

        await h.Fetch().FetchObservationsAsync(location.LocationId, CancellationToken.None);
        Assert.Equal(1, h.Observations.StationCalls);
        Assert.Empty(h.Observations.DailyCalls);

        await h.Fetch(h.NewContext()).FetchObservationsAsync(location.LocationId, CancellationToken.None);

        var call = Assert.Single(h.Observations.DailyCalls);
        Assert.True(call.To <= new DateOnly(2026, 10, 22));
        var check = h.NewContext();
        Assert.Equal(1, await check.WeatherObservations.CountAsync());
        Assert.True(await check.WeatherDays.AnyAsync(d => d.LocalDate == new DateOnly(2026, 10, 22) && d.ObservedMaxTempC == 10.1m));
    }

    // ── Admin: enable / configure / disable ───────────────────────────────────────────────────────

    [Fact]
    public async Task Enable_requires_the_reviewed_fingerprint_and_snapshots_site_one()
    {
        using var h = new WeatherTestHarness();
        var location = await h.AddLocationAsync();

        await Assert.ThrowsAsync<DomainValidationException>(() => h.Admin().UpsertAsync(location.LocationId,
            new UpsertWeatherLocation { Enabled = true, IsTracked = true }, WeatherTestHarness.AdminId, CancellationToken.None));

        var stale = await Assert.ThrowsAsync<WeatherConflictException>(() => h.Admin().UpsertAsync(location.LocationId,
            new UpsertWeatherLocation { Enabled = true, IsTracked = true, ExpectedLocationFingerprint = "v1:0.000:0.000:-:UTC" },
            WeatherTestHarness.AdminId, CancellationToken.None));
        Assert.Equal(WeatherConflictException.LocationChanged, stale.Code);
        Assert.DoesNotContain("59.9", stale.Message, StringComparison.Ordinal);

        var created = await h.Admin().UpsertAsync(location.LocationId,
            new UpsertWeatherLocation { Enabled = true, IsTracked = true, ExpectedLocationFingerprint = WeatherCoordinates.Fingerprint(location) },
            WeatherTestHarness.AdminId, CancellationToken.None);

        Assert.True(created!.Created);
        Assert.Equal(1, created.Row.Site.Version);
        Assert.Equal(59.913m, created.Row.Site.Latitude);
        Assert.Equal(new DateOnly(2025, 10, 23), created.Row.HistoryFrom);
        Assert.False(created.Row.LocationMoved);
    }

    [Fact]
    public async Task Enable_of_an_unknown_location_is_null_and_of_an_archived_one_is_a_409()
    {
        using var h = new WeatherTestHarness();
        Assert.Null(await h.Admin().UpsertAsync(Guid.NewGuid(),
            new UpsertWeatherLocation { Enabled = true, IsTracked = true, ExpectedLocationFingerprint = "x" }, WeatherTestHarness.AdminId, CancellationToken.None));

        var archived = await h.AddLocationAsync(archived: true);
        var error = await Assert.ThrowsAsync<WeatherConflictException>(() => h.Admin().UpsertAsync(archived.LocationId,
            new UpsertWeatherLocation { Enabled = true, IsTracked = true, ExpectedLocationFingerprint = WeatherCoordinates.Fingerprint(archived) },
            WeatherTestHarness.AdminId, CancellationToken.None));
        Assert.Equal(WeatherConflictException.Archived, error.Code);
    }

    [Fact]
    public async Task Tracking_beyond_the_cap_is_a_422_keyed_isTracked()
    {
        using var h = new WeatherTestHarness();
        h.Settings.Value = h.Settings.Value with { MaxTrackedLocations = 1 };
        await h.EnableAsync("Home");
        var cabin = await h.AddLocationAsync("Cabin");

        var error = await Assert.ThrowsAsync<DomainUnprocessableException>(() => h.Admin().UpsertAsync(cabin.LocationId,
            new UpsertWeatherLocation { Enabled = true, IsTracked = true, ExpectedLocationFingerprint = WeatherCoordinates.Fingerprint(cabin) },
            WeatherTestHarness.AdminId, CancellationToken.None));
        Assert.Contains("isTracked", error.Errors!.Keys);

        var untracked = await h.Admin().UpsertAsync(cabin.LocationId,
            new UpsertWeatherLocation { Enabled = true, IsTracked = false, ExpectedLocationFingerprint = WeatherCoordinates.Fingerprint(cabin) },
            WeatherTestHarness.AdminId, CancellationToken.None);
        Assert.NotNull(untracked);
    }

    [Fact]
    public async Task Rows_past_a_lowered_cap_are_reported_suspended_in_enable_order()
    {
        using var h = new WeatherTestHarness();
        await h.EnableAsync("Alpha");
        h.Time.Advance(TimeSpan.FromMinutes(1));
        await h.EnableAsync("Beta");
        h.Settings.Value = h.Settings.Value with { MaxTrackedLocations = 1 };

        var rows = (await h.Admin().ListAsync(new WeatherLocationsQueryParams(), CancellationToken.None)).Items;

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
        h.Settings.Value = h.Settings.Value with { HouseholdDefaultLocationId = location.LocationId };

        var error = await Assert.ThrowsAsync<WeatherConflictException>(() => h.Admin().UpsertAsync(location.LocationId,
            new UpsertWeatherLocation { Enabled = false, IsTracked = true }, WeatherTestHarness.AdminId, CancellationToken.None));
        Assert.Equal(WeatherConflictException.HouseholdDefault, error.Code);
    }

    [Fact]
    public async Task Re_enabling_keeps_the_existing_site_even_when_the_location_moved()
    {
        using var h = new WeatherTestHarness();
        var location = await h.EnableAsync();
        await h.Admin().UpsertAsync(location.LocationId, new UpsertWeatherLocation { Enabled = false, IsTracked = true }, WeatherTestHarness.AdminId, CancellationToken.None);
        await MoveAsync(h, location.LocationId, 60.39299m);

        var row = (await h.Admin(h.NewContext()).UpsertAsync(location.LocationId,
            new UpsertWeatherLocation { Enabled = true, IsTracked = true }, WeatherTestHarness.AdminId, CancellationToken.None))!.Row;

        Assert.Equal(1, row.Site.Version);
        Assert.Equal(59.913m, row.Site.Latitude);
        Assert.True(row.LocationMoved);
    }

    // ── Admin: apply ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_move_changes_nothing_until_an_admin_applies_it()
    {
        using var h = new WeatherTestHarness();
        var location = await h.EnableAsync();
        var moved = await MoveAsync(h, location.LocationId, 60.39299m);

        await h.Fetch(h.NewContext()).FetchForecastAsync(location.LocationId, WeatherFetchMode.Scheduled, CancellationToken.None);
        Assert.Equal(59.913m, h.Forecasts.LastSite!.Latitude);

        h.Time.Advance(TimeSpan.FromMinutes(1));
        var row = await h.Admin(h.NewContext()).ApplyAsync(location.LocationId,
            new ApplyWeatherLocation { ExpectedLocationFingerprint = WeatherCoordinates.Fingerprint(moved) }, WeatherTestHarness.AdminId, CancellationToken.None);

        Assert.Equal(2, row!.Site.Version);
        Assert.Equal(60.393m, row.Site.Latitude);
        Assert.False(row.LocationMoved);
        Assert.Equal(2, await h.NewContext().WeatherLocationSites.CountAsync());
    }

    [Fact]
    public async Task Apply_with_nothing_to_apply_or_a_stale_review_is_a_409()
    {
        using var h = new WeatherTestHarness();
        var location = await h.EnableAsync();

        var nothing = await Assert.ThrowsAsync<WeatherConflictException>(() => h.Admin().ApplyAsync(location.LocationId,
            new ApplyWeatherLocation { ExpectedLocationFingerprint = WeatherCoordinates.Fingerprint(location) }, WeatherTestHarness.AdminId, CancellationToken.None));
        Assert.Equal(WeatherConflictException.NothingToApply, nothing.Code);

        await MoveAsync(h, location.LocationId, 60.39299m);
        var stale = await Assert.ThrowsAsync<WeatherConflictException>(() => h.Admin(h.NewContext()).ApplyAsync(location.LocationId,
            new ApplyWeatherLocation { ExpectedLocationFingerprint = WeatherCoordinates.Fingerprint(location) }, WeatherTestHarness.AdminId, CancellationToken.None));
        Assert.Equal(WeatherConflictException.LocationChanged, stale.Code);
    }

    [Fact]
    public async Task Apply_never_shortens_a_pause()
    {
        using var h = new WeatherTestHarness();
        var location = await h.EnableAsync();
        h.Forecasts.Respond = _ => Task.FromResult(new ForecastFetchResult(WeatherFetchOutcome.Failed, FailureKind: WeatherFetchFailureKind.RateLimited));
        await h.Fetch().FetchForecastAsync(location.LocationId, WeatherFetchMode.Scheduled, CancellationToken.None);
        var moved = await MoveAsync(h, location.LocationId, 60.39299m);

        await h.Admin(h.NewContext()).ApplyAsync(location.LocationId,
            new ApplyWeatherLocation { ExpectedLocationFingerprint = WeatherCoordinates.Fingerprint(moved) }, WeatherTestHarness.AdminId, CancellationToken.None);

        var schedule = await h.NewContext().WeatherLocationSchedules.SingleAsync();
        Assert.Equal(Noon.AddHours(6), schedule.NextForecastFetchAfterUtc);
    }

    // ── Admin: purge ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Purge_needs_weather_off_and_removes_every_weather_row_but_not_the_location()
    {
        using var h = new WeatherTestHarness();
        var location = await h.EnableAsync();
        h.Forecasts.Respond = _ => Task.FromResult(WeatherTestHarness.Run(Noon.AddMinutes(-30), Noon, 24, 7));
        await h.Fetch().FetchForecastAsync(location.LocationId, WeatherFetchMode.Scheduled, CancellationToken.None);

        var enabled = await Assert.ThrowsAsync<WeatherConflictException>(() => h.Admin(h.NewContext()).PurgeAsync(location.LocationId, WeatherTestHarness.AdminId, CancellationToken.None));
        Assert.Equal(WeatherConflictException.StillEnabled, enabled.Code);

        await h.Admin(h.NewContext()).UpsertAsync(location.LocationId, new UpsertWeatherLocation { Enabled = false, IsTracked = true }, WeatherTestHarness.AdminId, CancellationToken.None);
        Assert.True(await h.Admin(h.NewContext()).PurgeAsync(location.LocationId, WeatherTestHarness.AdminId, CancellationToken.None));

        var check = h.NewContext();
        Assert.Equal(0, await check.WeatherLocations.CountAsync());
        Assert.Equal(0, await check.WeatherLocationSites.CountAsync());
        Assert.Equal(0, await check.WeatherLocationSchedules.CountAsync());
        Assert.Equal(0, await check.WeatherForecastRuns.CountAsync());
        Assert.Equal(0, await check.WeatherForecastSteps.CountAsync());
        Assert.Equal(0, await check.WeatherDays.CountAsync());
        Assert.True(await check.Locations.AnyAsync(l => l.LocationId == location.LocationId));
        Assert.False(await h.Admin(h.NewContext()).PurgeAsync(location.LocationId, WeatherTestHarness.AdminId, CancellationToken.None));
    }

    [Fact]
    public async Task Purge_refuses_while_a_fetch_holds_the_lease_and_resumes_once_started()
    {
        using var h = new WeatherTestHarness();
        var location = await h.EnableAsync();
        await h.Admin().UpsertAsync(location.LocationId, new UpsertWeatherLocation { Enabled = false, IsTracked = true }, WeatherTestHarness.AdminId, CancellationToken.None);
        var context = h.NewContext();
        var schedule = await context.WeatherLocationSchedules.SingleAsync();
        schedule.LeaseUntilUtc = Noon.AddMinutes(1);
        await context.SaveChangesAsync();

        var leased = await Assert.ThrowsAsync<WeatherConflictException>(() => h.Admin(h.NewContext()).PurgeAsync(location.LocationId, WeatherTestHarness.AdminId, CancellationToken.None));
        Assert.Equal(WeatherConflictException.FetchInProgress, leased.Code);

        // An interrupted purge: the fence is set, the lease is far future, the rows are still there.
        var weather = await context.WeatherLocations.SingleAsync();
        weather.PurgeStartedAt = Noon;
        schedule.LeaseUntilUtc = DateTime.MaxValue;
        await context.SaveChangesAsync();

        var row = await h.Admin(h.NewContext()).GetAsync(location.LocationId, CancellationToken.None);
        Assert.True(row!.PurgeInProgress);
        var enable = await Assert.ThrowsAsync<WeatherConflictException>(() => h.Admin(h.NewContext()).UpsertAsync(location.LocationId,
            new UpsertWeatherLocation { Enabled = true, IsTracked = true }, WeatherTestHarness.AdminId, CancellationToken.None));
        Assert.Equal(WeatherConflictException.PurgeInProgress, enable.Code);

        Assert.True(await h.Admin(h.NewContext()).PurgeAsync(location.LocationId, WeatherTestHarness.AdminId, CancellationToken.None));
        Assert.Equal(0, await h.NewContext().WeatherLocations.CountAsync());
    }

    // ── Reads ─────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Resolution_is_explicit_then_preference_then_household_default_then_none()
    {
        using var h = new WeatherTestHarness();
        var home = await h.EnableAsync("Home");
        var cabin = await h.EnableAsync("Cabin");
        var read = h.Read();

        Assert.Null((await read.ResolveAsync(null, "u1", CancellationToken.None)).Location);

        h.Settings.Value = h.Settings.Value with { HouseholdDefaultLocationId = home.LocationId };
        Assert.Equal(home.LocationId, (await read.ResolveAsync(null, "u1", CancellationToken.None)).Location!.LocationId);

        await read.SetPreferenceAsync("u1", new UpdateWeatherPreferences { DefaultLocationId = cabin.LocationId }, CancellationToken.None);
        Assert.Equal(cabin.LocationId, (await read.ResolveAsync(null, "u1", CancellationToken.None)).Location!.LocationId);
        Assert.Equal(home.LocationId, (await read.ResolveAsync(null, "u2", CancellationToken.None)).Location!.LocationId);

        Assert.Equal(home.LocationId, (await read.ResolveAsync(home.LocationId, "u1", CancellationToken.None)).Location!.LocationId);
        await Assert.ThrowsAsync<DomainNotFoundException>(() => read.ResolveAsync(Guid.NewGuid(), "u1", CancellationToken.None));
    }

    [Fact]
    public async Task A_preference_for_a_location_without_weather_is_a_400_keyed_defaultLocationId()
    {
        using var h = new WeatherTestHarness();
        var bare = await h.AddLocationAsync();

        var error = await Assert.ThrowsAsync<DomainValidationException>(() => h.Read().SetPreferenceAsync("u1",
            new UpdateWeatherPreferences { DefaultLocationId = bare.LocationId }, CancellationToken.None));
        Assert.Contains("defaultLocationId", error.Errors!.Keys);
    }

    [Fact]
    public async Task The_preference_is_its_own_user_preference_row_and_leaves_the_others_alone()
    {
        using var h = new WeatherTestHarness();
        var cabin = await h.EnableAsync("Cabin");
        var preferences = new UserPreferencesService(h.Context, h.Time);
        await preferences.UpsertAsync("u1", "preferences-page", """{"isDarkMode":true}""", CancellationToken.None);

        await h.Read().SetPreferenceAsync("u1", new UpdateWeatherPreferences { DefaultLocationId = cabin.LocationId }, CancellationToken.None);

        Assert.Equal("""{"isDarkMode":true}""", (await preferences.GetAsync("u1", "preferences-page", CancellationToken.None))!.PreferencesJson);
        Assert.Equal($$"""{"defaultLocationId":"{{cabin.LocationId}}"}""",
            (await preferences.GetAsync("u1", UserPreferencesService.WeatherKey, CancellationToken.None))!.PreferencesJson);
        Assert.Equal(cabin.LocationId, (await h.Read().PreferencesAsync("u1", CancellationToken.None)).DefaultLocation!.LocationId);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"defaultLocationId":"not-a-guid"}""")]
    [InlineData("null")]
    public async Task An_unreadable_stored_preference_falls_back_to_the_household_default(string stored)
    {
        using var h = new WeatherTestHarness();
        var home = await h.EnableAsync("Home");
        h.Settings.Value = h.Settings.Value with { HouseholdDefaultLocationId = home.LocationId };
        await new UserPreferencesService(h.Context, h.Time)
            .UpsertServerOwnedAsync("u1", UserPreferencesService.WeatherKey, stored, CancellationToken.None);

        Assert.Equal(home.LocationId, (await h.Read().ResolveAsync(null, "u1", CancellationToken.None)).Location!.LocationId);
    }

    [Theory]
    [InlineData("weather-preferences")]
    [InlineData("Weather-Preferences")]
    [InlineData("weather-preferences  ")]
    public async Task The_generic_preference_write_refuses_the_weather_key(string key)
    {
        using var h = new WeatherTestHarness();

        var error = await Assert.ThrowsAsync<DomainValidationException>(() => new UserPreferencesService(h.Context, h.Time)
            .UpsertAsync("u1", key, """{"defaultLocationId":null}""", CancellationToken.None));
        Assert.Contains("pageKey", error.Errors!.Keys);
    }

    [Fact]
    public async Task Current_with_no_place_is_NoPlace_and_with_no_contact_is_NotConfigured()
    {
        using var h = new WeatherTestHarness();
        Assert.Equal(WeatherStatus.NoPlace, (await h.Read().CurrentAsync(null, "u1", CancellationToken.None)).Status);

        var home = await h.EnableAsync(tracked: false);
        h.Settings.Value = h.Settings.Value with { Contact = null };
        var current = await h.Read().CurrentAsync(home.LocationId, "u1", CancellationToken.None);
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

        var cabin = await h.Read().CurrentAsync(untracked.LocationId, "u1", CancellationToken.None);
        Assert.Equal(1, h.Forecasts.Calls);
        Assert.Equal(WeatherStatus.Ok, cabin.Status);
        Assert.Equal(7, cabin.Step!.AirTemperatureC);

        var home = await h.Read(h.NewContext()).CurrentAsync(tracked.LocationId, "u1", CancellationToken.None);
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

        await h.Read().DailyAsync(Daily(cabin.LocationId, today.AddDays(-60), today.AddDays(-1)), "u1", CancellationToken.None);
        Assert.Equal(0, h.Forecasts.Calls);

        await h.Read(h.NewContext()).DailyAsync(Daily(cabin.LocationId, today.AddDays(-2), today), "u1", CancellationToken.None);
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

        await h.Read().DailyAsync(Daily(place.LocationId, new DateOnly(2026, 10, 21), new DateOnly(2026, 10, 23)), "u1", CancellationToken.None);

        Assert.Equal(1, h.Forecasts.Calls);
    }

    [Fact]
    public async Task Daily_carries_the_measured_history_bounds_whatever_range_was_asked()
    {
        using var h = new WeatherTestHarness();
        var home = await h.EnableAsync();
        var weather = await h.Context.WeatherLocations.SingleAsync(w => w.LocationId == home.LocationId);
        weather.FrostStationId = "SN18700";
        h.Context.WeatherDays.AddRange(
            MeasuredDay(home.LocationId, new DateOnly(2026, 3, 2)),
            MeasuredDay(home.LocationId, new DateOnly(2026, 10, 21)),
            // A forecast-only day is not measured history, however recent.
            new Odyssey.Context.WeatherDay { LocationId = home.LocationId, LocalDate = new DateOnly(2026, 10, 24), ForecastMaxTempC = 9, UpdatedAtUtc = Noon });
        await h.Context.SaveChangesAsync();
        h.Context.ChangeTracker.Clear();

        var daily = await h.Read().DailyAsync(Daily(home.LocationId, new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 30)), "u1", CancellationToken.None);

        Assert.Empty(daily.Days);
        Assert.Equal(new DateOnly(2026, 3, 2), daily.History!.MeasuredFrom);
        Assert.Equal(new DateOnly(2026, 10, 21), daily.History.MeasuredThrough);
        Assert.True(daily.History.HasObservationStation);
    }

    [Fact]
    public async Task Daily_history_bounds_are_empty_with_no_station_and_nothing_measured()
    {
        using var h = new WeatherTestHarness();
        var home = await h.EnableAsync();

        var daily = await h.Read().DailyAsync(Daily(home.LocationId, new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 22)), "u1", CancellationToken.None);

        Assert.Null(daily.History!.MeasuredFrom);
        Assert.Null(daily.History.MeasuredThrough);
        Assert.False(daily.History.HasObservationStation);
    }

    private static WeatherDailyQuery Daily(Guid locationId, DateOnly from, DateOnly to) =>
        new() { LocationId = locationId, From = from, To = to };

    private static Odyssey.Context.WeatherDay MeasuredDay(Guid locationId, DateOnly date) => new()
    {
        LocationId = locationId,
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

        var current = await h.Read().CurrentAsync(cabin.LocationId, "u1", CancellationToken.None);

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
