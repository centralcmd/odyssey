using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Odyssey.Context;
using Odyssey.Context.Secrets;
using Odyssey.Core.Journal;
using Odyssey.Dtos.Application;
using Odyssey.Dtos.Journal;

namespace Odyssey.Core.Weather;

/// <summary>Why a forecast fetch was asked for. It decides the time budget and what may bring it forward.</summary>
public enum WeatherFetchMode
{
    /// <summary>The scheduler: retries on 5xx and timeouts, 10 s per attempt, 30 s in all.</summary>
    Scheduled,

    /// <summary>A read for an untracked location: one attempt within 5 s, no retry.</summary>
    OnDemand,

    /// <summary>An Admin's refresh: bypasses the refresh interval, never MET's <c>Expires</c> or a pause.</summary>
    Refresh,
}

public enum WeatherAttemptStatus
{
    /// <summary>A request was sent and its result stored (a new run, or a 304 / same <c>updated_at</c>).</summary>
    Completed,

    /// <summary>A request was sent and failed; the failure and backoff were recorded.</summary>
    Failed,

    /// <summary>Nothing was sent: not due, paused, leased elsewhere, disabled, archived or purging.</summary>
    Skipped,

    /// <summary>No usable contact address, so nothing may be sent.</summary>
    NotConfigured,

    /// <summary>A request was sent, but the location was disabled, applied or purged meanwhile: nothing stored.</summary>
    Discarded,
}

public sealed record WeatherAttempt(
    WeatherAttemptStatus Status,
    bool StoredNewRun = false,
    DateTime? IssuedAtUtc = null,
    WeatherFetchFailureKind? FailureKind = null);

/// <summary>
/// Fetching and storing weather for one location (issue #348 §3): claiming, the request, write fencing,
/// backoff, and the <see cref="WeatherDay"/> rebuild. Shared by the scheduler, the on-demand read and the
/// Admin refresh, so all three spend one backoff.
/// </summary>
/// <remarks>
/// <para>
/// <b>Claiming.</b> Before any outbound call the schedule row's <see cref="WeatherLocationSchedule.Version"/>
/// is rotated together with a two-minute lease. A concurrency failure means another replica holds the job.
/// </para>
/// <para>
/// <b>Write fencing.</b> The rows a fetch produces and the schedule update are saved in one
/// <c>SaveChangesAsync</c> under the version it claimed with. Disable, apply and purge each rotate the version,
/// so a fetch in flight when they happen fails its save and stores nothing (AC 43). Before saving it also
/// re-reads the location and discards when weather was turned off, a purge started, or the site changed.
/// </para>
/// </remarks>
public sealed class WeatherFetchService
{
    public static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan MaxBackoff = TimeSpan.FromHours(6);
    private static readonly TimeSpan BaseBackoff = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan ScheduledAttemptBudget = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan OnDemandBudget = TimeSpan.FromSeconds(5);
    private const int ScheduledAttempts = 3;

    /// <summary>Observations for a day are fetched once it is past this site-local hour.</summary>
    public const int ObservationLocalHour = 8;

    private readonly OdysseyContext context;
    private readonly IWeatherForecastSource forecasts;
    private readonly IWeatherObservationSource observations;
    private readonly IWeatherSettingsLookup settings;
    private readonly ISecretSettingsReader secrets;
    private readonly TimeProvider time;
    private readonly ILogger<WeatherFetchService> logger;

    public WeatherFetchService(
        OdysseyContext context,
        IWeatherForecastSource forecasts,
        IWeatherObservationSource observations,
        IWeatherSettingsLookup settings,
        ISecretSettingsReader secrets,
        TimeProvider? time = null,
        ILogger<WeatherFetchService>? logger = null)
    {
        this.context = context;
        this.forecasts = forecasts;
        this.observations = observations;
        this.settings = settings;
        this.secrets = secrets;
        this.time = time ?? TimeProvider.System;
        this.logger = logger ?? NullLogger<WeatherFetchService>.Instance;
    }

    private DateTime Now => time.GetUtcNow().UtcDateTime;

    // ── Forecast ──────────────────────────────────────────────────────────────────────────────────

    public async Task<WeatherAttempt> FetchForecastAsync(Guid locationId, WeatherFetchMode mode, CancellationToken cancellationToken)
    {
        var configured = await settings.GetAsync(cancellationToken);
        if (configured.Contact is not { } contact)
        {
            return new WeatherAttempt(WeatherAttemptStatus.NotConfigured);
        }

        var weather = await context.WeatherLocations.AsNoTracking()
            .FirstOrDefaultAsync(w => w.LocationId == locationId, cancellationToken);
        if (weather is null || !weather.Enabled || weather.PurgeStartedAt is not null
            || await IsArchivedAsync(locationId, cancellationToken))
        {
            return new WeatherAttempt(WeatherAttemptStatus.Skipped);
        }

        var schedule = await context.WeatherLocationSchedules
            .FirstOrDefaultAsync(s => s.LocationId == locationId, cancellationToken);
        var now = Now;
        if (schedule is null || !ForecastDue(schedule, mode, now) || schedule.LeaseUntilUtc > now)
        {
            return new WeatherAttempt(WeatherAttemptStatus.Skipped, FailureKind: schedule?.ForecastFailureKind);
        }

        if (!await ClaimAsync(schedule, now, cancellationToken))
        {
            return new WeatherAttempt(WeatherAttemptStatus.Skipped);
        }

        var site = await context.WeatherLocationSites.AsNoTracking()
            .FirstAsync(s => s.LocationId == locationId && s.SiteVersion == weather.CurrentSiteVersion, cancellationToken);
        var query = new WeatherSiteQuery(site.Latitude, site.Longitude, site.AltitudeMeters);

        var result = await RequestForecastAsync(query, contact, schedule.ForecastLastModified, mode, cancellationToken);
        now = Now;

        WeatherForecastRun? run = null;
        DateTime? issuedAt = null;
        switch (result.Outcome)
        {
            case WeatherFetchOutcome.Ok:
                var payload = result.Payload!;
                issuedAt = payload.UpdatedAtUtc;
                var newest = await context.WeatherForecastRuns.AsNoTracking()
                    .Where(r => r.LocationId == locationId && r.SiteVersion == site.SiteVersion)
                    .MaxAsync(r => (DateTime?)r.ProviderUpdatedAt, cancellationToken);
                if (newest != payload.UpdatedAtUtc)
                {
                    run = BuildRun(locationId, site, payload, result.ExpiresUtc, now);
                    context.WeatherForecastRuns.Add(run);
                }

                RecordForecastSuccess(schedule, result, configured, now);
                break;
            case WeatherFetchOutcome.NotModified:
                RecordForecastSuccess(schedule, result, configured, now);
                break;
            default:
                RecordForecastFailure(schedule, result.FailureKind ?? WeatherFetchFailureKind.ProviderError, result.RetryAfter, now);
                break;
        }

        if (!await FencedSaveAsync(schedule, site.SiteVersion, cancellationToken))
        {
            return new WeatherAttempt(WeatherAttemptStatus.Discarded);
        }

        if (run is not null)
        {
            var timeline = await TimelineAsync(locationId, cancellationToken);
            // A step's period can reach into the next date, whose coverage it then changes too.
            var dates = run.Steps
                .SelectMany(step => new[] { step.TimeUtc, step.TimeUtc.AddHours(step.PeriodHours).AddTicks(-1) })
                .Select(timeline.DateForInstant).Distinct().ToList();
            await RebuildDaysAsync(locationId, timeline, dates, cancellationToken);
        }

        return result.Outcome == WeatherFetchOutcome.Failed
            ? new WeatherAttempt(WeatherAttemptStatus.Failed, FailureKind: schedule.ForecastFailureKind)
            : new WeatherAttempt(WeatherAttemptStatus.Completed, run is not null, issuedAt);
    }

    /// <summary>
    /// The scheduler and the on-demand read honour <see cref="WeatherLocationSchedule.NextForecastFetchAfterUtc"/>,
    /// which carries the interval, MET's <c>Expires</c> and any backoff. A refresh honours <c>Expires</c> and a
    /// pause but not the interval: it may bring a fetch forward, never shorten a pause (§7.14).
    /// </summary>
    private static bool ForecastDue(WeatherLocationSchedule schedule, WeatherFetchMode mode, DateTime now)
    {
        if (mode != WeatherFetchMode.Refresh || schedule.ForecastFailureKind is not null)
        {
            return schedule.NextForecastFetchAfterUtc is not { } next || next <= now;
        }

        return schedule.ForecastExpiresAtUtc is not { } expires || expires <= now;
    }

    private async Task<ForecastFetchResult> RequestForecastAsync(
        WeatherSiteQuery query, string contact, string? lastModified, WeatherFetchMode mode, CancellationToken cancellationToken)
    {
        if (mode == WeatherFetchMode.OnDemand)
        {
            return await forecasts.FetchAsync(query, contact, lastModified, OnDemandBudget, cancellationToken);
        }

        var attempts = mode == WeatherFetchMode.Scheduled ? ScheduledAttempts : 1;
        ForecastFetchResult result = null!;
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            result = await forecasts.FetchAsync(query, contact, lastModified, ScheduledAttemptBudget, cancellationToken);
            // Only a 5xx, a timeout or a network error is retried — never a 403, a 429 or a bad body.
            if (result.Outcome != WeatherFetchOutcome.Failed
                || result.FailureKind is not (WeatherFetchFailureKind.ProviderError or WeatherFetchFailureKind.Timeout))
            {
                break;
            }
        }

        return result;
    }

    private void RecordForecastSuccess(WeatherLocationSchedule schedule, ForecastFetchResult result, WeatherSettings configured, DateTime now)
    {
        var interval = now.AddMinutes(configured.RefreshIntervalMinutes);
        var expires = result.ExpiresUtc ?? now;
        schedule.NextForecastFetchAfterUtc = (expires > interval ? expires : interval).Add(Jitter());
        schedule.ForecastExpiresAtUtc = result.ExpiresUtc;
        schedule.ForecastLastModified = result.LastModified;
        schedule.LastForecastSuccessUtc = now;
        schedule.ForecastFailureKind = null;
        schedule.ForecastConsecutiveFailures = 0;
    }

    private static void RecordForecastFailure(
        WeatherLocationSchedule schedule, WeatherFetchFailureKind kind, TimeSpan? retryAfter, DateTime now)
    {
        schedule.ForecastConsecutiveFailures++;
        schedule.ForecastFailureKind = kind;
        schedule.NextForecastFetchAfterUtc = now.Add(Backoff(kind, schedule.ForecastConsecutiveFailures, retryAfter));
    }

    /// <summary>
    /// <c>15 min × 2ⁿ</c>, capped at 6 h; a <c>403</c> or <c>429</c> goes straight to the cap, and a longer
    /// <c>Retry-After</c> wins (§3 step 7).
    /// </summary>
    public static TimeSpan Backoff(WeatherFetchFailureKind kind, int consecutiveFailures, TimeSpan? retryAfter)
    {
        var delay = kind is WeatherFetchFailureKind.ProviderRejected or WeatherFetchFailureKind.RateLimited
            ? MaxBackoff
            : TimeSpan.FromTicks(Math.Min(MaxBackoff.Ticks, BaseBackoff.Ticks * (1L << Math.Clamp(consecutiveFailures - 1, 0, 10))));
        return retryAfter is { } wait && wait > delay ? wait : delay;
    }

    private static TimeSpan Jitter() => TimeSpan.FromSeconds(Random.Shared.Next(0, 121));

    private static WeatherForecastRun BuildRun(
        Guid locationId, WeatherLocationSite site, ForecastPayload payload, DateTime? expires, DateTime now)
    {
        var run = new WeatherForecastRun
        {
            WeatherForecastRunId = Guid.NewGuid(),
            LocationId = locationId,
            SiteVersion = site.SiteVersion,
            Provider = WeatherProvider.MetNorway,
            ProviderUpdatedAt = payload.UpdatedAtUtc,
            FetchedAtUtc = now,
            ExpiresAtUtc = expires,
            Latitude = site.Latitude,
            Longitude = site.Longitude,
            AltitudeMeters = site.AltitudeMeters,
        };

        foreach (var step in payload.Steps)
        {
            run.Steps.Add(new WeatherForecastStep
            {
                WeatherForecastRunId = run.WeatherForecastRunId,
                LocationId = locationId,
                SiteVersion = site.SiteVersion,
                RunProviderUpdatedAt = payload.UpdatedAtUtc,
                RunFetchedAtUtc = now,
                TimeUtc = DateTime.SpecifyKind(step.TimeUtc, DateTimeKind.Utc),
                PeriodHours = step.PeriodHours,
                AirTemperatureC = step.AirTemperatureC,
                RelativeHumidityPct = step.RelativeHumidityPct,
                CloudAreaFractionPct = step.CloudAreaFractionPct,
                AirPressureHpa = step.AirPressureHpa,
                WindSpeedMps = step.WindSpeedMps,
                WindGustMps = step.WindGustMps,
                WindFromDirectionDeg = step.WindFromDirectionDeg,
                UvIndexClearSky = step.UvIndexClearSky,
                SymbolCode = step.SymbolCode,
                PrecipitationMm = step.PrecipitationMm,
                PrecipitationProbabilityPct = step.PrecipitationProbabilityPct,
                TempMinC = step.TempMinC,
                TempMaxC = step.TempMaxC,
            });
        }

        return run;
    }

    // ── Observations ──────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// One Frost step for one location (§3 observations): resolve the station, then either the daily fetch
    /// of the last seven days or one year of backfill — one request per call, so the scheduler stays at or
    /// below 1 request/s. Returns the secret's state so the scheduler can log an unreadable one.
    /// </summary>
    public async Task<SecretReadState> FetchObservationsAsync(Guid locationId, CancellationToken cancellationToken)
    {
        var configured = await settings.GetAsync(cancellationToken);
        var secret = await secrets.GetAsync(SecretSettingKeys.WeatherFrostClientId, cancellationToken);
        if (configured.Contact is not { } contact || !secret.TryGetValue(out var clientId))
        {
            return secret.State;
        }

        var weather = await context.WeatherLocations
            .FirstOrDefaultAsync(w => w.LocationId == locationId, cancellationToken);
        if (weather is null || !weather.Enabled || weather.PurgeStartedAt is not null
            || await IsArchivedAsync(locationId, cancellationToken))
        {
            return secret.State;
        }

        var schedule = await context.WeatherLocationSchedules
            .FirstOrDefaultAsync(s => s.LocationId == locationId, cancellationToken);
        var now = Now;
        if (schedule is null || schedule.NextObservationFetchAfterUtc > now || schedule.LeaseUntilUtc > now)
        {
            return secret.State;
        }

        var timeline = await TimelineAsync(locationId, cancellationToken);
        var site = timeline.Current;
        var localNow = WeatherSiteTimeline.LocalTime(now, site);
        var today = DateOnly.FromDateTime(localNow);
        var yesterday = today.AddDays(-1);
        var firstOwned = site.SiteVersion == 1 ? DateOnly.MinValue : timeline.FirstOwnedDate(site);
        var floor = Max(weather.HistoryFrom, firstOwned);

        var dailyPending = schedule.ObservationsCheckedThroughDate is not { } checkedThrough || checkedThrough < yesterday;
        var dailyAllowed = dailyPending && localNow.Hour >= ObservationLocalHour;
        var backfillPending = schedule.ObservationsBackfilledFromDate is { } backfilledFrom && backfilledFrom > floor;

        if (!dailyAllowed && !backfillPending)
        {
            // Nothing to send: only the wake-up time moves. Saved WITHOUT rotating the version, so it can
            // never fence off a forecast fetch another worker holds on the same location.
            schedule.NextObservationFetchAfterUtc = NextObservationWindow(site, now, dailyPending);
            try
            {
                await context.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateConcurrencyException)
            {
                context.ChangeTracker.Clear();
            }

            return secret.State;
        }

        if (!await ClaimAsync(schedule, now, cancellationToken))
        {
            return secret.State;
        }

        // Station resolution: at most once per 24 h, for the current site only (§3 step 1).
        if (weather.FrostStationId is null
            && (weather.StationResolvedAtUtc is not { } resolved || resolved <= now.AddHours(-24)))
        {
            var lookup = await observations.FindStationAsync(
                new WeatherSiteQuery(site.Latitude, site.Longitude, site.AltitudeMeters), contact, clientId, cancellationToken);
            now = Now;
            if (!lookup.Succeeded)
            {
                RecordObservationFailure(schedule, lookup.FailureKind ?? WeatherFetchFailureKind.ProviderError, lookup.RetryAfter, now);
                await FencedSaveAsync(schedule, site.SiteVersion, cancellationToken);
                return secret.State;
            }

            weather.FrostStationId = lookup.StationId;
            weather.StationResolvedAtUtc = now;
            // One request per call keeps the scheduler at ≤ 1 request/s: the lookup was this call's request,
            // so the daily fetch waits for the next tick (immediately due when a station was found).
            schedule.NextObservationFetchAfterUtc = lookup.StationId is null ? now.AddHours(24) : now;
            await FencedSaveAsync(schedule, site.SiteVersion, cancellationToken);
            return secret.State;
        }

        if (weather.FrostStationId is not { } station)
        {
            schedule.NextObservationFetchAfterUtc = now.AddHours(24);
            await FencedSaveAsync(schedule, site.SiteVersion, cancellationToken);
            return secret.State;
        }

        DateOnly from, to;
        var daily = dailyAllowed;
        if (daily)
        {
            from = Max(floor, yesterday.AddDays(-6));
            to = yesterday;
        }
        else
        {
            var lowest = schedule.ObservationsBackfilledFromDate!.Value;
            from = Max(floor, lowest.AddDays(-365));
            to = lowest.AddDays(-1);
        }

        var inserted = new List<DateOnly>();
        if (from <= to)
        {
            var fetched = await observations.FetchDailyAsync(station, from, to, contact, clientId, cancellationToken);
            now = Now;
            if (!fetched.Succeeded)
            {
                RecordObservationFailure(schedule, fetched.FailureKind ?? WeatherFetchFailureKind.ProviderError, fetched.RetryAfter, now);
                await FencedSaveAsync(schedule, site.SiteVersion, cancellationToken);
                return secret.State;
            }

            inserted = await StageObservationsAsync(locationId, site.SiteVersion, station, fetched.Days, now, cancellationToken);
        }

        // Markers advance even on an empty result, so an empty range is never re-requested (AC 16).
        if (daily)
        {
            schedule.ObservationsCheckedThroughDate = yesterday;
            if (schedule.ObservationsBackfilledFromDate is null || schedule.ObservationsBackfilledFromDate > from)
            {
                schedule.ObservationsBackfilledFromDate = from;
            }
        }
        else
        {
            schedule.ObservationsBackfilledFromDate = from;
        }

        schedule.LastObservationSuccessUtc = now;
        schedule.ObservationFailureKind = null;
        schedule.ObservationConsecutiveFailures = 0;
        var moreBackfill = schedule.ObservationsBackfilledFromDate > floor;
        schedule.NextObservationFetchAfterUtc = moreBackfill ? now : NextObservationWindow(site, now, dailyPending: false);

        if (await FencedSaveAsync(schedule, site.SiteVersion, cancellationToken) && inserted.Count > 0)
        {
            await RebuildDaysAsync(locationId, timeline, inserted, cancellationToken);
        }

        return secret.State;
    }

    /// <summary>Insert a row only when none exists for (location, site, station, date), or when a value differs (§3 step 4).</summary>
    private async Task<List<DateOnly>> StageObservationsAsync(
        Guid locationId, int siteVersion, string station, IReadOnlyList<ObservationDayData> days, DateTime now,
        CancellationToken cancellationToken)
    {
        if (days.Count == 0)
        {
            return [];
        }

        var first = days.Min(day => day.Date);
        var last = days.Max(day => day.Date);
        var existing = (await context.WeatherObservations.AsNoTracking()
                .Where(o => o.LocationId == locationId && o.SiteVersion == siteVersion && o.StationId == station
                    && o.LocalDate >= first && o.LocalDate <= last)
                .ToListAsync(cancellationToken))
            .GroupBy(o => o.LocalDate)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(o => o.FetchedAtUtc).First());

        var inserted = new List<DateOnly>();
        foreach (var day in days)
        {
            if (existing.TryGetValue(day.Date, out var current)
                && current.MeanTempC == day.MeanTempC && current.MinTempC == day.MinTempC && current.MaxTempC == day.MaxTempC
                && current.MeanWindSpeedMps == day.MeanWindSpeedMps && current.MaxWindGustMps == day.MaxWindGustMps
                && current.PrecipitationMm == day.PrecipitationMm)
            {
                continue;
            }

            context.WeatherObservations.Add(new WeatherObservation
            {
                WeatherObservationId = Guid.NewGuid(),
                LocationId = locationId,
                SiteVersion = siteVersion,
                Provider = WeatherProvider.MetNorway,
                StationId = station,
                LocalDate = day.Date,
                MeanTempC = day.MeanTempC,
                MinTempC = day.MinTempC,
                MaxTempC = day.MaxTempC,
                MeanWindSpeedMps = day.MeanWindSpeedMps,
                MaxWindGustMps = day.MaxWindGustMps,
                PrecipitationMm = day.PrecipitationMm,
                FetchedAtUtc = now,
            });
            inserted.Add(day.Date);
        }

        return inserted;
    }

    private static void RecordObservationFailure(
        WeatherLocationSchedule schedule, WeatherFetchFailureKind kind, TimeSpan? retryAfter, DateTime now)
    {
        schedule.ObservationConsecutiveFailures++;
        schedule.ObservationFailureKind = kind;
        schedule.NextObservationFetchAfterUtc = now.Add(Backoff(kind, schedule.ObservationConsecutiveFailures, retryAfter));
    }

    /// <summary>The next 08:00 site-local after <paramref name="now"/> — today's if it is still pending and ahead.</summary>
    private static DateTime NextObservationWindow(WeatherLocationSite site, DateTime now, bool dailyPending)
    {
        var zone = WeatherSiteTimeline.Zone(site);
        var local = WeatherSiteTimeline.LocalTime(now, site);
        var date = DateOnly.FromDateTime(local);
        if (!dailyPending || local.Hour >= ObservationLocalHour)
        {
            date = date.AddDays(1);
        }

        return WeatherSiteTimeline.StartOfDayUtc(date, zone).AddHours(ObservationLocalHour);
    }

    // ── Shared ────────────────────────────────────────────────────────────────────────────────────

    private async Task<bool> ClaimAsync(WeatherLocationSchedule schedule, DateTime now, CancellationToken cancellationToken)
    {
        schedule.LeaseUntilUtc = now.Add(LeaseDuration);
        schedule.Version = Guid.NewGuid();
        try
        {
            await context.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            context.ChangeTracker.Clear();
            return false;
        }
    }

    /// <summary>
    /// Saves the staged rows and the schedule under the claimed version, after re-checking the fence.
    /// Returns false — with nothing stored — when the job was fenced off.
    /// </summary>
    private async Task<bool> FencedSaveAsync(WeatherLocationSchedule schedule, int claimedSite, CancellationToken cancellationToken)
    {
        var fence = await context.WeatherLocations.AsNoTracking()
            .Where(w => w.LocationId == schedule.LocationId)
            .Select(w => new { w.Enabled, w.PurgeStartedAt, w.CurrentSiteVersion })
            .FirstOrDefaultAsync(cancellationToken);

        if (fence is null || !fence.Enabled || fence.PurgeStartedAt is not null || fence.CurrentSiteVersion != claimedSite)
        {
            Discard(schedule.LocationId);
            return false;
        }

        schedule.Version = Guid.NewGuid();
        schedule.LeaseUntilUtc = null;
        try
        {
            await context.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            Discard(schedule.LocationId);
            return false;
        }
        catch (DbUpdateException ex) when (DbErrors.IsDuplicateKey(ex))
        {
            // Another replica stored the same run first; the unique index makes ours a no-op.
            Discard(schedule.LocationId);
            return false;
        }
    }

    private void Discard(Guid locationId)
    {
        context.ChangeTracker.Clear();
        logger.LogDebug("Weather fetch for location {LocationId} was fenced off; nothing stored.", locationId);
    }

    private Task<bool> IsArchivedAsync(Guid locationId, CancellationToken cancellationToken) =>
        context.Locations.AsNoTracking().AnyAsync(l => l.LocationId == locationId && l.Archived != null, cancellationToken);

    internal async Task<WeatherSiteTimeline> TimelineAsync(Guid locationId, CancellationToken cancellationToken) =>
        new(await context.WeatherLocationSites.AsNoTracking()
            .Where(s => s.LocationId == locationId)
            .ToListAsync(cancellationToken));

    /// <summary>
    /// Upserts <see cref="WeatherDay"/> for <paramref name="dates"/> from the append-only tables (§4). A date
    /// with neither half is removed, so the derived table never holds an empty row.
    /// </summary>
    public async Task RebuildDaysAsync(
        Guid locationId, WeatherSiteTimeline timeline, IEnumerable<DateOnly> dates, CancellationToken cancellationToken)
    {
        var now = Now;
        foreach (var date in dates.Distinct().Order())
        {
            var owning = timeline.SiteForDate(date);
            var (fromUtc, toUtc) = timeline.DayBoundsUtc(date);
            // From one longest period (12 h) before the day: a step that starts earlier can still cover its
            // first hours, which is all it contributes (the coverage); its values belong to its own day.
            var reachFrom = fromUtc.AddHours(-12);
            var candidates = await context.WeatherForecastSteps.AsNoTracking()
                .Where(s => s.LocationId == locationId && s.TimeUtc >= reachFrom && s.TimeUtc < toUtc)
                .ToListAsync(cancellationToken);
            var effective = WeatherComposer.EffectiveSteps(candidates);
            var half = WeatherComposer.ForecastHalf(
                effective.Where(s => s.TimeUtc >= fromUtc).ToList(), fromUtc, toUtc, timeline.NoonUtc(date),
                effective.Where(s => s.TimeUtc.AddHours(s.PeriodHours) > fromUtc).ToList());

            var observed = await context.WeatherObservations.AsNoTracking()
                .Where(o => o.LocationId == locationId && o.LocalDate == date)
                .OrderByDescending(o => o.SiteVersion).ThenByDescending(o => o.FetchedAtUtc)
                .FirstOrDefaultAsync(cancellationToken);

            var day = await context.WeatherDays.FirstOrDefaultAsync(d => d.LocationId == locationId && d.LocalDate == date, cancellationToken);
            if (half is null && observed is null)
            {
                if (day is not null)
                {
                    context.WeatherDays.Remove(day);
                }

                continue;
            }

            if (day is null)
            {
                day = new WeatherDay { LocationId = locationId, LocalDate = date };
                context.WeatherDays.Add(day);
            }

            day.ForecastSymbolCode = half?.SymbolCode;
            day.ForecastMinTempC = half?.MinTempC;
            day.ForecastMaxTempC = half?.MaxTempC;
            day.ForecastMeanWindSpeedMps = half?.MeanWindSpeedMps;
            day.ForecastMaxWindGustMps = half?.MaxWindGustMps;
            day.ForecastPrecipitationMm = half?.PrecipitationMm;
            day.ForecastCoverageHours = half?.CoverageHours;
            day.ForecastIssuedAtUtc = half?.IssuedAtUtc;
            day.ForecastSiteVersion = half is null ? null : owning.SiteVersion;
            day.ObservedMinTempC = observed?.MinTempC;
            day.ObservedMaxTempC = observed?.MaxTempC;
            day.ObservedMeanTempC = observed?.MeanTempC;
            day.ObservedMeanWindSpeedMps = observed?.MeanWindSpeedMps;
            day.ObservedMaxWindGustMps = observed?.MaxWindGustMps;
            day.ObservedPrecipitationMm = observed?.PrecipitationMm;
            day.ObservedFetchedAtUtc = observed?.FetchedAtUtc;
            day.ObservedSiteVersion = observed?.SiteVersion;
            day.UpdatedAtUtc = now;
        }

        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (DbErrors.IsDuplicateKey(ex))
        {
            // A concurrent rebuild inserted the same (location, date) first: retry once as an update.
            context.ChangeTracker.Clear();
            await RebuildDaysAsync(locationId, timeline, dates, cancellationToken);
        }
    }

    private static DateOnly Max(DateOnly left, DateOnly right) => left > right ? left : right;
}
