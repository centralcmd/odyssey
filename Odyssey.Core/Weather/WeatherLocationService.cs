using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Odyssey.Context;
using Odyssey.Core.Pagination;
using Odyssey.Dtos;
using Odyssey.Dtos.Journal;
using LocationEntity = Odyssey.Context.Location;

namespace Odyssey.Core.Weather;

/// <summary>
/// The Admin side of weather (issue #348 §5 endpoints 6–10): which locations have weather, their settings,
/// applying a moved location, an on-demand refresh and the history purge. Reads <see cref="LocationEntity"/>
/// and never writes it — <c>LocationService</c> does not know weather exists.
/// </summary>
/// <remarks>
/// Audit lines carry the location id, the actor and what changed — never coordinates (§7.7). Enable,
/// configure, disable, apply and refresh log at Information; a purge at Warning, at start and completion.
/// </remarks>
public sealed class WeatherLocationService
{
    private readonly OdysseyContext context;
    private readonly WeatherFetchService fetch;
    private readonly IWeatherSettingsLookup settings;
    private readonly IWeatherHistoryPurger purger;
    private readonly TimeProvider time;
    private readonly ILogger<WeatherLocationService> logger;

    public WeatherLocationService(
        OdysseyContext context,
        WeatherFetchService fetch,
        IWeatherSettingsLookup settings,
        IWeatherHistoryPurger purger,
        TimeProvider? time = null,
        ILogger<WeatherLocationService>? logger = null)
    {
        this.context = context;
        this.fetch = fetch;
        this.settings = settings;
        this.purger = purger;
        this.time = time ?? TimeProvider.System;
        this.logger = logger ?? NullLogger<WeatherLocationService>.Instance;
    }

    private DateTime Now => time.GetUtcNow().UtcDateTime;

    // ── Read ──────────────────────────────────────────────────────────────────────────────────────

    public async Task<PagedResult<ExistingWeatherLocation>> ListAsync(
        WeatherLocationsQueryParams query, CancellationToken cancellationToken)
    {
        var q = from weather in context.WeatherLocations.AsNoTracking()
                join location in context.Locations.AsNoTracking() on weather.LocationId equals location.LocationId
                select new { weather, location };

        if (ListQuery.NormalizeSearch(query.Search) is { } term)
        {
            var pattern = ListQuery.ContainsPattern(term);
            q = q.Where(row => EF.Functions.Like(row.location.Name, pattern));
        }

        var ascending = ListQuery.Ascending(query.SortDir, naturalDefaultAscending: true);
        q = ascending
            ? q.OrderBy(row => row.location.Name).ThenBy(row => row.location.LocationId)
            : q.OrderByDescending(row => row.location.Name).ThenBy(row => row.location.LocationId);

        var rowContext = await RowContextAsync(cancellationToken);
        var page = await q.ToPagedResultAsync(query.Offset, query.Limit, cancellationToken);
        var items = new List<ExistingWeatherLocation>();
        foreach (var row in page.Items)
        {
            items.Add(await ToDtoAsync(row.weather, row.location, rowContext, cancellationToken));
        }

        return new PagedResult<ExistingWeatherLocation>
        {
            Items = items,
            Offset = page.Offset,
            Limit = page.Limit,
            TotalCount = page.TotalCount,
        };
    }

    public async Task<ExistingWeatherLocation?> GetAsync(Guid locationId, CancellationToken cancellationToken)
    {
        var weather = await context.WeatherLocations.AsNoTracking().FirstOrDefaultAsync(w => w.LocationId == locationId, cancellationToken);
        var location = await context.Locations.AsNoTracking().FirstOrDefaultAsync(l => l.LocationId == locationId, cancellationToken);
        return weather is null || location is null
            ? null
            : await ToDtoAsync(weather, location, await RowContextAsync(cancellationToken), cancellationToken);
    }

    // ── Enable / configure / disable (endpoint 7) ─────────────────────────────────────────────────

    public sealed record UpsertResult(ExistingWeatherLocation Row, bool Created);

    /// <summary>Creates or updates the setting. Null when the location does not exist.</summary>
    public async Task<UpsertResult?> UpsertAsync(
        Guid locationId, UpsertWeatherLocation request, string userId, CancellationToken cancellationToken)
    {
        var enabled = request.Enabled!.Value;
        var tracked = request.IsTracked!.Value;
        var created = false;
        var changes = new List<string>();

        var found = await InTransactionAsync(async () =>
        {
            changes.Clear();
            var location = await context.Locations.FirstOrDefaultAsync(l => l.LocationId == locationId, cancellationToken);
            if (location is null)
            {
                return false;
            }

            var weather = await context.WeatherLocations.FirstOrDefaultAsync(w => w.LocationId == locationId, cancellationToken);
            var configured = await settings.GetAsync(cancellationToken);
            var now = Now;

            if (weather is null)
            {
                created = true;
                if (string.IsNullOrEmpty(request.ExpectedLocationFingerprint))
                {
                    throw new DomainValidationException(
                        "Review the location's position before turning weather on.", code: null, field: "expectedLocationFingerprint");
                }

                // Compared against, and copied from, this one row read — so the site written is exactly the
                // state the Admin reviewed (SB, security note on B).
                EnsureFingerprint(location, request.ExpectedLocationFingerprint);
                if (location.Archived is not null && enabled)
                {
                    throw Conflict(WeatherConflictException.Archived, "Restore the location before turning weather on.");
                }

                if (enabled && tracked)
                {
                    await EnsureTrackedCapacityAsync(locationId, configured, cancellationToken);
                }

                var site = NewSite(location, 1, now, userId);
                context.WeatherLocationSites.Add(site);
                weather = new WeatherLocation
                {
                    LocationId = locationId,
                    Enabled = enabled,
                    IsTracked = tracked,
                    HistoryFrom = request.HistoryFrom ?? DateOnly.FromDateTime(now).AddYears(-1),
                    EnabledAt = now,
                    CurrentSiteVersion = 1,
                    CreatedByUserId = userId,
                    UpdatedByUserId = userId,
                    CreatedAt = now,
                    UpdatedAt = now,
                };
                context.WeatherLocations.Add(weather);
                context.WeatherLocationSchedules.Add(new WeatherLocationSchedule
                {
                    LocationId = locationId,
                    Version = Guid.NewGuid(),
                });
                changes.Add("Created");
            }
            else
            {
                if (weather.PurgeStartedAt is not null)
                {
                    throw Conflict(WeatherConflictException.PurgeInProgress, "This location's weather history is being deleted.");
                }

                if (enabled && !weather.Enabled && location.Archived is not null)
                {
                    throw Conflict(WeatherConflictException.Archived, "Restore the location before turning weather back on.");
                }

                if (!enabled && weather.Enabled && configured.HouseholdDefaultLocationId == locationId)
                {
                    throw Conflict(WeatherConflictException.HouseholdDefault,
                        "This is the household's default weather location. Choose another default first.");
                }

                if (enabled && tracked && !(weather.Enabled && weather.IsTracked))
                {
                    await EnsureTrackedCapacityAsync(locationId, configured, cancellationToken);
                }

                if (weather.Enabled != enabled)
                {
                    changes.Add(nameof(WeatherLocation.Enabled));
                }

                if (weather.IsTracked != tracked)
                {
                    changes.Add(nameof(WeatherLocation.IsTracked));
                }

                if (request.HistoryFrom is { } historyFrom && historyFrom != weather.HistoryFrom)
                {
                    changes.Add(nameof(WeatherLocation.HistoryFrom));
                    weather.HistoryFrom = historyFrom;
                }

                if (enabled && !weather.Enabled)
                {
                    weather.EnabledAt = now;
                }

                weather.Enabled = enabled;
                weather.IsTracked = tracked;
                if (changes.Count > 0)
                {
                    weather.UpdatedAt = now;
                    weather.UpdatedByUserId = userId;
                }

                // Disabling fences off a fetch in flight (§3 Write fencing).
                if (changes.Contains(nameof(WeatherLocation.Enabled)))
                {
                    var schedule = await context.WeatherLocationSchedules.FirstAsync(s => s.LocationId == locationId, cancellationToken);
                    schedule.Version = Guid.NewGuid();
                    schedule.LeaseUntilUtc = null;
                }
            }

            await context.SaveChangesAsync(cancellationToken);
            return true;
        }, cancellationToken);

        if (!found)
        {
            return null;
        }

        logger.LogInformation(
            "Weather for location {LocationId} {Operation} by {ActorUserId}; changed: [{ChangedFields}].",
            locationId, created ? "enabled" : "configured", userId, string.Join(", ", changes));
        return new UpsertResult((await GetAsync(locationId, cancellationToken))!, created);
    }

    // ── Apply a moved location (endpoint 8) ───────────────────────────────────────────────────────

    /// <summary>Appends a new site from the live location. Null when there is no weather row.</summary>
    public async Task<ExistingWeatherLocation?> ApplyAsync(
        Guid locationId, ApplyWeatherLocation request, string userId, CancellationToken cancellationToken)
    {
        var previous = 0;
        var changed = new List<string>();
        var found = await InTransactionAsync(async () =>
        {
            changed.Clear();
            var weather = await context.WeatherLocations.FirstOrDefaultAsync(w => w.LocationId == locationId, cancellationToken);
            var location = await context.Locations.FirstOrDefaultAsync(l => l.LocationId == locationId, cancellationToken);
            if (weather is null || location is null)
            {
                return false;
            }

            if (weather.PurgeStartedAt is not null)
            {
                throw Conflict(WeatherConflictException.PurgeInProgress, "This location's weather history is being deleted.");
            }

            if (location.Archived is not null)
            {
                throw Conflict(WeatherConflictException.Archived, "Restore the location before applying its new position.");
            }

            if (!weather.Enabled)
            {
                throw Conflict(WeatherConflictException.Disabled, "Turn weather back on before applying the new position.");
            }

            EnsureFingerprint(location, request.ExpectedLocationFingerprint!);

            var site = await context.WeatherLocationSites.AsNoTracking()
                .FirstAsync(s => s.LocationId == locationId && s.SiteVersion == weather.CurrentSiteVersion, cancellationToken);
            if (!WeatherCoordinates.Moved(location, site))
            {
                throw Conflict(WeatherConflictException.NothingToApply, "Weather already uses this location's position.");
            }

            if (WeatherCoordinates.Round(location.Latitude) != site.Latitude || WeatherCoordinates.Round(location.Longitude) != site.Longitude)
            {
                changed.Add("Position");
            }

            if (location.AltitudeMeters != site.AltitudeMeters)
            {
                changed.Add(nameof(WeatherLocationSite.AltitudeMeters));
            }

            if (!string.Equals(location.TimeZoneId, site.TimeZoneId, StringComparison.Ordinal))
            {
                changed.Add(nameof(WeatherLocationSite.TimeZoneId));
            }

            var now = Now;
            previous = site.SiteVersion;
            var next = NewSite(location, site.SiteVersion + 1, now, userId);
            context.WeatherLocationSites.Add(next);
            weather.CurrentSiteVersion = next.SiteVersion;
            weather.FrostStationId = null;
            weather.StationResolvedAtUtc = null;
            weather.UpdatedAt = now;
            weather.UpdatedByUserId = userId;

            var schedule = await context.WeatherLocationSchedules.FirstAsync(s => s.LocationId == locationId, cancellationToken);
            schedule.Version = Guid.NewGuid();
            schedule.LeaseUntilUtc = null;
            schedule.ForecastLastModified = null;
            schedule.ForecastExpiresAtUtc = null;
            // An apply never shortens a pause (§3, AC 27).
            schedule.NextForecastFetchAfterUtc = schedule.NextForecastFetchAfterUtc is { } pending && pending > now ? pending : now;
            // The new site's observations start at its first owned date (the apply day belongs to it).
            var firstOwned = WeatherSiteTimeline.LocalDate(now, next);
            schedule.ObservationsCheckedThroughDate = firstOwned.AddDays(-1);
            schedule.ObservationsBackfilledFromDate = firstOwned;
            schedule.NextObservationFetchAfterUtc = schedule.NextObservationFetchAfterUtc is { } obs && obs > now ? obs : now;

            await context.SaveChangesAsync(cancellationToken);
            return true;
        }, cancellationToken);

        if (!found)
        {
            return null;
        }

        logger.LogInformation(
            "Weather for location {LocationId} moved from site {FromVersion} to {ToVersion} by {ActorUserId}; changed: [{ChangedFields}].",
            locationId, previous, previous + 1, userId, string.Join(", ", changed));
        return await GetAsync(locationId, cancellationToken);
    }

    // ── Refresh (endpoint 9) ──────────────────────────────────────────────────────────────────────

    /// <summary>Forces a forecast fetch, still honouring MET's <c>Expires</c> and any pause. Null: no enabled row.</summary>
    public async Task<WeatherRefreshResult?> RefreshAsync(Guid locationId, string userId, CancellationToken cancellationToken)
    {
        var weather = await context.WeatherLocations.AsNoTracking().FirstOrDefaultAsync(w => w.LocationId == locationId, cancellationToken);
        if (weather is null || !weather.Enabled)
        {
            return null;
        }

        if (weather.PurgeStartedAt is not null)
        {
            throw Conflict(WeatherConflictException.PurgeInProgress, "This location's weather history is being deleted.");
        }

        if (await context.Locations.AsNoTracking().AnyAsync(l => l.LocationId == locationId && l.Archived != null, cancellationToken))
        {
            throw Conflict(WeatherConflictException.Archived, "Restore the location before refreshing its forecast.");
        }

        if (!(await settings.GetAsync(cancellationToken)).IsConfigured)
        {
            throw new WeatherNotConfiguredException();
        }

        var attempt = await fetch.FetchForecastAsync(locationId, WeatherFetchMode.Refresh, cancellationToken);
        var issued = attempt.IssuedAtUtc ?? await context.WeatherForecastRuns.AsNoTracking()
            .Where(r => r.LocationId == locationId)
            .MaxAsync(r => (DateTime?)r.ProviderUpdatedAt, cancellationToken);
        var failure = await context.WeatherLocationSchedules.AsNoTracking()
            .Where(s => s.LocationId == locationId).Select(s => s.ForecastFailureKind).FirstOrDefaultAsync(cancellationToken);

        logger.LogInformation(
            "Weather forecast refresh for location {LocationId} by {ActorUserId}: {Outcome}.",
            locationId, userId, attempt.Status);
        return new WeatherRefreshResult
        {
            Fetched = attempt.Status == WeatherAttemptStatus.Completed,
            IssuedAtUtc = issued is { } i ? DateTime.SpecifyKind(i, DateTimeKind.Utc) : null,
            ForecastFailureKind = failure,
        };
    }

    // ── Purge (endpoint 10) ───────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Deletes every weather row of the location (amendment 1 order). False when there is no weather row.
    /// Resumable: an interrupted purge leaves <see cref="WeatherLocation.PurgeStartedAt"/> set and the
    /// location fenced, and a repeated call skips the preconditions and continues.
    /// </summary>
    public async Task<bool> PurgeAsync(Guid locationId, string userId, CancellationToken cancellationToken)
    {
        var weather = await context.WeatherLocations.FirstOrDefaultAsync(w => w.LocationId == locationId, cancellationToken);
        if (weather is null)
        {
            return false;
        }

        if (weather.PurgeStartedAt is null)
        {
            if (weather.Enabled)
            {
                throw Conflict(WeatherConflictException.StillEnabled, "Turn weather off before deleting its history.");
            }

            if ((await settings.GetAsync(cancellationToken)).HouseholdDefaultLocationId == locationId)
            {
                throw Conflict(WeatherConflictException.HouseholdDefault,
                    "This is the household's default weather location. Choose another default first.");
            }

            var schedule = await context.WeatherLocationSchedules.FirstAsync(s => s.LocationId == locationId, cancellationToken);
            var now = Now;
            if (schedule.LeaseUntilUtc is { } lease && lease > now)
            {
                throw Conflict(WeatherConflictException.FetchInProgress,
                    "A forecast fetch is running for this place right now. Try again in a minute.");
            }

            // Step 1: fence. From here no fetch can claim the location, a fenced ingest discards, and enable,
            // apply and refresh return 409. A concurrent DELETE loses on the version and gets a 409.
            weather.PurgeStartedAt = now;
            schedule.LeaseUntilUtc = DateTime.MaxValue;
            schedule.Version = Guid.NewGuid();
            try
            {
                await context.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateConcurrencyException)
            {
                context.ChangeTracker.Clear();
                throw Conflict(WeatherConflictException.FetchInProgress,
                    "This location's weather is changing right now. Try again in a minute.");
            }

            logger.LogWarning("Weather history purge for location {LocationId} started by {ActorUserId}.", locationId, userId);
        }
        else
        {
            logger.LogWarning("Weather history purge for location {LocationId} resumed by {ActorUserId}.", locationId, userId);
        }

        context.ChangeTracker.Clear();
        var counts = await purger.PurgeAsync(locationId, cancellationToken);
        logger.LogWarning(
            "Weather history purge for location {LocationId} completed by {ActorUserId}: {Steps} steps, {Runs} runs, {Observations} observations, {Days} days, {Sites} sites.",
            locationId, userId, counts.Steps, counts.Runs, counts.Observations, counts.Days, counts.Sites);
        return true;
    }

    // ── Shared ────────────────────────────────────────────────────────────────────────────────────

    private sealed record RowContext(Guid? HouseholdDefault, HashSet<Guid> Suspended);

    /// <summary>
    /// The cap view the scheduler applies (§3 Tracked cap): of the tracked, enabled, non-archived, not-purging
    /// locations ordered by <c>EnabledAt</c>, only the first <i>N</i> refresh; the rest are suspended.
    /// </summary>
    private async Task<RowContext> RowContextAsync(CancellationToken cancellationToken)
    {
        var configured = await settings.GetAsync(cancellationToken);
        var tracked = await TrackedQueue().Select(w => w.LocationId).ToListAsync(cancellationToken);
        return new RowContext(configured.HouseholdDefaultLocationId, tracked.Skip(configured.MaxTrackedLocations).ToHashSet());
    }

    /// <summary>Tracked locations in cap order — the one definition the scheduler and the Admin list share.</summary>
    public IQueryable<WeatherLocation> TrackedQueue() =>
        from weather in context.WeatherLocations.AsNoTracking()
        join location in context.Locations.AsNoTracking() on weather.LocationId equals location.LocationId
        where weather.Enabled && weather.IsTracked && weather.PurgeStartedAt == null && location.Archived == null
        orderby weather.EnabledAt, weather.LocationId
        select weather;

    private async Task<ExistingWeatherLocation> ToDtoAsync(
        WeatherLocation weather, LocationEntity location, RowContext rowContext, CancellationToken cancellationToken)
    {
        var site = await context.WeatherLocationSites.AsNoTracking()
            .FirstAsync(s => s.LocationId == weather.LocationId && s.SiteVersion == weather.CurrentSiteVersion, cancellationToken);
        var schedule = await context.WeatherLocationSchedules.AsNoTracking()
            .FirstOrDefaultAsync(s => s.LocationId == weather.LocationId, cancellationToken);
        var suspended = rowContext.Suspended.Contains(weather.LocationId);
        return new ExistingWeatherLocation
        {
            LocationId = weather.LocationId,
            Name = location.Name,
            Enabled = weather.Enabled,
            IsTracked = weather.IsTracked,
            TrackingSuspended = suspended,
            TrackingSuspendedReason = suspended ? WeatherTrackingSuspendedReason.CapReached : null,
            HistoryFrom = weather.HistoryFrom,
            Site = new WeatherLocationSiteDto
            {
                Version = site.SiteVersion,
                Latitude = site.Latitude,
                Longitude = site.Longitude,
                AltitudeMeters = site.AltitudeMeters,
                TimeZoneId = site.TimeZoneId,
                AppliedAt = Utc(site.AppliedAt),
            },
            LocationMoved = WeatherCoordinates.Moved(location, site),
            LocationArchived = location.Archived is not null,
            LocationUpdatedAt = Utc(location.UpdatedAt),
            LocationFingerprint = WeatherCoordinates.Fingerprint(location),
            PurgeInProgress = weather.PurgeStartedAt is not null,
            IsHouseholdDefault = rowContext.HouseholdDefault == weather.LocationId,
            HasObservationStation = weather.FrostStationId is not null,
            LastForecastSuccessUtc = Utc(schedule?.LastForecastSuccessUtc),
            ForecastFailureKind = schedule?.ForecastFailureKind,
            LastObservationSuccessUtc = Utc(schedule?.LastObservationSuccessUtc),
            ObservationFailureKind = schedule?.ObservationFailureKind,
            EnabledAt = Utc(weather.EnabledAt),
            UpdatedAt = Utc(weather.UpdatedAt),
        };
    }

    private async Task EnsureTrackedCapacityAsync(Guid locationId, WeatherSettings configured, CancellationToken cancellationToken)
    {
        var tracked = await TrackedQueue().CountAsync(w => w.LocationId != locationId, cancellationToken);
        if (tracked >= configured.MaxTrackedLocations)
        {
            throw new DomainUnprocessableException(
                $"{configured.MaxTrackedLocations} of {configured.MaxTrackedLocations} scheduled places are in use. Turn scheduling off for another place, or raise the limit in System settings.",
                "isTracked");
        }
    }

    private static void EnsureFingerprint(LocationEntity location, string expected)
    {
        if (!string.Equals(WeatherCoordinates.Fingerprint(location), expected, StringComparison.Ordinal))
        {
            throw Conflict(WeatherConflictException.LocationChanged,
                "The location's position changed while you were reviewing it. Check it again, then retry.");
        }
    }

    private static WeatherLocationSite NewSite(LocationEntity location, int version, DateTime now, string userId) => new()
    {
        LocationId = location.LocationId,
        SiteVersion = version,
        Latitude = WeatherCoordinates.Round(location.Latitude),
        Longitude = WeatherCoordinates.Round(location.Longitude),
        AltitudeMeters = location.AltitudeMeters,
        TimeZoneId = location.TimeZoneId,
        AppliedAt = now,
        AppliedByUserId = userId,
    };

    private static WeatherConflictException Conflict(string code, string message) => new(code, message);

    private static DateTime Utc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);

    private static DateTime? Utc(DateTime? value) => value is { } v ? Utc(v) : null;

    /// <summary>
    /// Runs <paramref name="work"/> in one transaction inside the execution strategy (a retrying strategy
    /// refuses a bare user transaction). The tracker is reset on a retry so a failed attempt's changes cannot
    /// ride along.
    /// </summary>
    private async Task<bool> InTransactionAsync(Func<Task<bool>> work, CancellationToken cancellationToken)
    {
        var attempt = 0;
        var strategy = context.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            if (attempt++ > 0)
            {
                context.ChangeTracker.Clear();
            }

            await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
            var result = await work();
            await transaction.CommitAsync(cancellationToken);
            return result;
        });
    }
}
