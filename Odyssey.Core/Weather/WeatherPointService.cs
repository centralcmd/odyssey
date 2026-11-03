using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Odyssey.Context;
using Odyssey.Core.Journal;
using Odyssey.Core.Pagination;
using Odyssey.Dtos;
using Odyssey.Dtos.Journal;

namespace Odyssey.Core.Weather;

/// <summary>
/// The Admin side of weather points (issue #387 §5 endpoints 1–8): the list, one point, the places linked to it,
/// create, update (and move), an on-demand refresh, the history purge and the full delete. A point has no key to a
/// place: it reads <c>Locations</c> only to copy a reviewed position on create and to count or list its links,
/// and never writes one — except that deleting a point unlinks its places through the <c>SET NULL</c> key.
/// </summary>
/// <remarks>
/// Audit lines carry ids, counts and the names of what changed — never a point name, place name or coordinate
/// (§7.7, AC 27). Create, update, move and refresh log at Information; a purge at Warning, at start and completion.
/// </remarks>
public sealed class WeatherPointService
{
    private const string NameField = "name";

    private readonly OdysseyContext context;
    private readonly WeatherFetchService fetch;
    private readonly IWeatherSettingsLookup settings;
    private readonly IWeatherHistoryPurger purger;
    private readonly TimeProvider time;
    private readonly ILogger<WeatherPointService> logger;

    public WeatherPointService(
        OdysseyContext context,
        WeatherFetchService fetch,
        IWeatherSettingsLookup settings,
        IWeatherHistoryPurger purger,
        TimeProvider? time = null,
        ILogger<WeatherPointService>? logger = null)
    {
        this.context = context;
        this.fetch = fetch;
        this.settings = settings;
        this.purger = purger;
        this.time = time ?? TimeProvider.System;
        this.logger = logger ?? NullLogger<WeatherPointService>.Instance;
    }

    private DateTime Now => time.GetUtcNow().UtcDateTime;

    // ── Read (endpoints 1–3) ──────────────────────────────────────────────────────────────────────

    /// <param name="canReadLocations">
    /// Whether the caller holds <c>locations.read</c>: without it <see cref="ExistingWeatherPoint.LinkedLocationCount"/>
    /// is null. The controller refuses <c>sortBy=LinkedLocationCount</c> before this is called.
    /// </param>
    public async Task<PagedResult<ExistingWeatherPoint>> ListAsync(
        WeatherPointsQueryParams query, bool canReadLocations, CancellationToken cancellationToken)
    {
        var q = context.WeatherPoints.AsNoTracking();
        if (ListQuery.NormalizeSearch(query.Search) is { } term)
        {
            var pattern = ListQuery.ContainsPattern(term);
            q = q.Where(point => EF.Functions.Like(point.Name, pattern));
        }

        var ascending = ListQuery.Ascending(query.SortDir, naturalDefaultAscending: query.SortBy is null or WeatherPointSortBy.Name);
        IOrderedQueryable<WeatherPoint> sorted = query.SortBy switch
        {
            WeatherPointSortBy.EnabledAt => ascending ? q.OrderBy(p => p.EnabledAt) : q.OrderByDescending(p => p.EnabledAt),
            WeatherPointSortBy.LinkedLocationCount => ascending
                ? q.OrderBy(p => context.Locations.Count(l => l.WeatherPointId == p.WeatherPointId))
                : q.OrderByDescending(p => context.Locations.Count(l => l.WeatherPointId == p.WeatherPointId)),
            _ => ascending ? q.OrderBy(p => p.Name) : q.OrderByDescending(p => p.Name),
        };
        q = sorted.ThenBy(p => p.WeatherPointId);

        var page = await q.ToPagedResultAsync(query.Offset, query.Limit, cancellationToken);
        var items = await ToDtosAsync(page.Items, canReadLocations, cancellationToken);
        return new PagedResult<ExistingWeatherPoint>
        {
            Items = items,
            Offset = page.Offset,
            Limit = page.Limit,
            TotalCount = page.TotalCount,
        };
    }

    public async Task<ExistingWeatherPoint?> GetAsync(Guid pointId, bool canReadLocations, CancellationToken cancellationToken)
    {
        var point = await context.WeatherPoints.AsNoTracking().FirstOrDefaultAsync(p => p.WeatherPointId == pointId, cancellationToken);
        return point is null ? null : (await ToDtosAsync([point], canReadLocations, cancellationToken))[0];
    }

    /// <summary>The places linked to a point, by name, with the whole-km distance. Null when the point is unknown.</summary>
    public async Task<IReadOnlyList<WeatherPointLinkedLocation>?> LinkedLocationsAsync(Guid pointId, CancellationToken cancellationToken)
    {
        var point = await context.WeatherPoints.AsNoTracking()
            .Where(p => p.WeatherPointId == pointId)
            .Select(p => new { p.CurrentSiteVersion })
            .FirstOrDefaultAsync(cancellationToken);
        if (point is null)
        {
            return null;
        }

        var site = await CurrentSiteAsync(pointId, point.CurrentSiteVersion, cancellationToken);
        var places = await context.Locations.AsNoTracking()
            .Where(l => l.WeatherPointId == pointId)
            .OrderBy(l => l.Name).ThenBy(l => l.LocationId)
            .Select(l => new { l.LocationId, l.Name, l.Latitude, l.Longitude })
            .ToListAsync(cancellationToken);
        return places
            .Select(l => new WeatherPointLinkedLocation
            {
                LocationId = l.LocationId,
                Name = l.Name,
                DistanceKm = WeatherDistance.KmOrNull(l.Latitude, l.Longitude, site?.Latitude, site?.Longitude),
            })
            .ToList();
    }

    // ── Create (endpoint 4) ───────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a point from explicit coordinates or from a reviewed place's position. The place's values are
    /// <b>copied</b>; no link is created. The caller has already been checked for <c>locations.read</c> when
    /// <see cref="NewWeatherPoint.FromLocationId"/> is set.
    /// </summary>
    public async Task<ExistingWeatherPoint> CreateAsync(
        NewWeatherPoint request, string userId, CancellationToken cancellationToken, bool canReadLocations = false)
    {
        var forecast = request.ForecastEnabled!.Value;
        var history = request.HistoryEnabled!.Value;
        var tracked = request.IsTracked!.Value;
        string name;
        decimal latitude;
        decimal longitude;
        int? altitude;
        string zone;

        if (request.FromLocationId is { } locationId)
        {
            var location = await context.Locations.AsNoTracking().FirstOrDefaultAsync(l => l.LocationId == locationId, cancellationToken)
                ?? throw new DomainUnprocessableException("That place does not exist.", "fromLocationId");
            if (WeatherCoordinates.Position(location) is not { } position)
            {
                throw new DomainUnprocessableException(
                    "That place has no map coordinates or time zone to copy. Add them first, or enter coordinates.", "fromLocationId");
            }

            // Compared against, and copied from, this one row read — so what is sent to MET is exactly what the
            // Admin reviewed (architect finding 8).
            if (!string.Equals(WeatherCoordinates.FingerprintOrNull(location), request.ExpectedLocationFingerprint, StringComparison.Ordinal))
            {
                throw new WeatherConflictException(WeatherConflictException.LocationChanged,
                    "The place's position changed while you were reviewing it. Check it again, then retry.");
            }

            name = CleanName(request.Name) ?? location.Name;
            latitude = position.Latitude;
            longitude = position.Longitude;
            altitude = location.AltitudeMeters;
            zone = position.TimeZoneId;
        }
        else
        {
            name = CleanName(request.Name) ?? throw new DomainValidationException("Give the weather point a name.", code: null, field: NameField);
            latitude = request.Latitude!.Value;
            longitude = request.Longitude!.Value;
            altitude = request.AltitudeMeters;
            zone = RequireZone(request.TimeZoneId);
        }

        await EnsureNameAvailableAsync(name, null, cancellationToken);
        var configured = await settings.GetAsync(cancellationToken);
        if (forecast && tracked)
        {
            await EnsureTrackedCapacityAsync(null, configured, cancellationToken);
        }

        var now = Now;
        var point = new WeatherPoint
        {
            WeatherPointId = Guid.NewGuid(),
            Name = name,
            ForecastEnabled = forecast,
            HistoryEnabled = history,
            IsTracked = tracked,
            HistoryFrom = request.HistoryFrom ?? DateOnly.FromDateTime(now).AddYears(-1),
            EnabledAt = now,
            CurrentSiteVersion = 1,
            SettingsVersion = Guid.NewGuid(),
            CreatedByUserId = userId,
            UpdatedByUserId = userId,
            CreatedAt = now,
            UpdatedAt = now,
        };

        // The point and its current site go in one save: the invariant the missing key cannot hold.
        context.WeatherPoints.Add(point);
        context.WeatherPointSites.Add(NewSite(point.WeatherPointId, 1, latitude, longitude, altitude, zone, now, userId));
        context.WeatherPointSchedules.Add(new WeatherPointSchedule { WeatherPointId = point.WeatherPointId, Version = Guid.NewGuid() });
        await SaveNamedAsync(cancellationToken);

        logger.LogInformation(
            "Weather point {WeatherPointId} created by {ActorUserId}; copied from location {SourceLocationId}.",
            point.WeatherPointId, userId, request.FromLocationId is Guid source ? source : null);
        return (await GetAsync(point.WeatherPointId, canReadLocations, cancellationToken))!;
    }

    // ── Update and move (endpoint 5) ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Replaces a point's settings and, when the rounded position, altitude or time zone changed, starts a new site
    /// version (the former <c>apply-location</c> semantics). Null when the point is unknown — not an upsert.
    /// </summary>
    public async Task<WeatherPointUpdateResult?> UpdateAsync(
        Guid pointId, UpdateWeatherPoint request, string userId, bool canReadLocations, CancellationToken cancellationToken)
    {
        var forecast = request.ForecastEnabled!.Value;
        var history = request.HistoryEnabled!.Value;
        var tracked = request.IsTracked!.Value;
        var name = CleanName(request.Name) ?? throw new DomainValidationException("Give the weather point a name.", code: null, field: NameField);
        var zone = RequireZone(request.TimeZoneId);
        var changes = new List<string>();
        var moved = false;
        var fromVersion = 0;

        var found = await FencedAsync(() => InTransactionAsync(async () =>
        {
            changes.Clear();
            moved = false;
            var point = await context.WeatherPoints.FirstOrDefaultAsync(p => p.WeatherPointId == pointId, cancellationToken);
            if (point is null)
            {
                return false;
            }

            if (point.SettingsVersion != request.SettingsVersion)
            {
                throw StaleConflict();
            }

            if (point.PurgeStartedAt is not null)
            {
                throw PurgeConflict();
            }

            if (!string.Equals(point.Name, name, StringComparison.Ordinal))
            {
                await EnsureNameAvailableAsync(name, pointId, cancellationToken);
                changes.Add(nameof(WeatherPoint.Name));
                point.Name = name;
            }

            var configured = await settings.GetAsync(cancellationToken);

            // The household default is the dashboard's fallback point, so its forecast must stay on; its history
            // is its own and may be turned off.
            if (!forecast && point.ForecastEnabled && configured.HouseholdDefaultPointId == pointId)
            {
                throw new WeatherConflictException(WeatherConflictException.HouseholdDefault,
                    "This is the household's default weather point. Choose another default first.");
            }

            if (forecast && tracked && !(point.ForecastEnabled && point.IsTracked))
            {
                await EnsureTrackedCapacityAsync(pointId, configured, cancellationToken);
            }

            if (point.ForecastEnabled != forecast)
            {
                changes.Add(nameof(WeatherPoint.ForecastEnabled));
            }

            if (point.HistoryEnabled != history)
            {
                changes.Add(nameof(WeatherPoint.HistoryEnabled));
            }

            if (point.IsTracked != tracked)
            {
                changes.Add(nameof(WeatherPoint.IsTracked));
            }

            if (request.HistoryFrom is { } historyFrom && historyFrom != point.HistoryFrom)
            {
                changes.Add(nameof(WeatherPoint.HistoryFrom));
                point.HistoryFrom = historyFrom;
            }

            var now = Now;
            if ((forecast || history) && !(point.ForecastEnabled || point.HistoryEnabled))
            {
                point.EnabledAt = now;
            }

            var forecastTurnedOn = forecast && !point.ForecastEnabled;
            var historyTurnedOn = history && !point.HistoryEnabled;
            point.ForecastEnabled = forecast;
            point.HistoryEnabled = history;
            point.IsTracked = tracked;

            var site = await context.WeatherPointSites.AsNoTracking()
                .FirstAsync(s => s.WeatherPointId == pointId && s.SiteVersion == point.CurrentSiteVersion, cancellationToken);
            moved = WeatherRules.Moved(request.Latitude!.Value, request.Longitude!.Value, request.AltitudeMeters, zone,
                site.Latitude, site.Longitude, site.AltitudeMeters, site.TimeZoneId);

            // Turning either half off fences off a fetch in flight (§3 Write fencing); turning one on makes that half
            // due now; a move rotates it too, so a fetch for the old site cannot store.
            var switched = changes.Contains(nameof(WeatherPoint.ForecastEnabled)) || changes.Contains(nameof(WeatherPoint.HistoryEnabled));
            if (switched || moved)
            {
                var schedule = await context.WeatherPointSchedules.FirstAsync(s => s.WeatherPointId == pointId, cancellationToken);
                schedule.Version = Guid.NewGuid();
                schedule.LeaseUntilUtc = null;
                if (forecastTurnedOn && schedule.NextForecastFetchAfterUtc is { } nextForecast && nextForecast > now && schedule.ForecastFailureKind is null)
                {
                    schedule.NextForecastFetchAfterUtc = now;
                }

                if (historyTurnedOn && schedule.NextObservationFetchAfterUtc is { } nextObservation && nextObservation > now && schedule.ObservationFailureKind is null)
                {
                    schedule.NextObservationFetchAfterUtc = now;
                }

                if (moved)
                {
                    fromVersion = site.SiteVersion;
                    changes.Add("Site");
                    var next = NewSite(pointId, site.SiteVersion + 1, request.Latitude!.Value, request.Longitude!.Value,
                        request.AltitudeMeters, zone, now, userId);
                    context.WeatherPointSites.Add(next);
                    point.CurrentSiteVersion = next.SiteVersion;

                    // The stations were chosen for the old position: every group is looked up again for the new one.
                    context.WeatherStations.RemoveRange(
                        await context.WeatherStations.Where(st => st.WeatherPointId == pointId).ToListAsync(cancellationToken));
                    point.StationResolvedAtUtc = null;

                    schedule.ForecastLastModified = null;
                    schedule.ForecastExpiresAtUtc = null;
                    // A move never shortens a pause.
                    schedule.NextForecastFetchAfterUtc = schedule.NextForecastFetchAfterUtc is { } pending && pending > now ? pending : now;
                    // The new site's observations start at its first owned date (the move day belongs to it).
                    var firstOwned = WeatherSiteTimeline.LocalDate(now, next);
                    schedule.ObservationsCheckedThroughDate = firstOwned.AddDays(-1);
                    schedule.ObservationsBackfilledFromDate = firstOwned;
                    schedule.NextObservationFetchAfterUtc = schedule.NextObservationFetchAfterUtc is { } obs && obs > now ? obs : now;
                }
            }

            // Every Admin PUT rotates the token, so a second editor holding the old one is refused (AC 7).
            point.SettingsVersion = Guid.NewGuid();
            point.UpdatedAt = now;
            point.UpdatedByUserId = userId;
            await SaveNamedAsync(cancellationToken);
            return true;
        }, cancellationToken));

        if (!found)
        {
            return null;
        }

        var warnings = new List<WeatherAdvisory>();
        if (moved)
        {
            logger.LogInformation(
                "Weather point {WeatherPointId} moved from site {FromVersion} to {ToVersion} by {ActorUserId}.",
                pointId, fromVersion, fromVersion + 1, userId);
            var linked = await context.Locations.AsNoTracking().CountAsync(l => l.WeatherPointId == pointId, cancellationToken);
            if (linked > 0)
            {
                warnings.Add(new WeatherAdvisory
                {
                    Code = WeatherAdvisoryCode.LinkedLocationsFollowMove,
                    Message = canReadLocations
                        ? $"{linked} {(linked == 1 ? "place uses" : "places use")} this weather point and now read{(linked == 1 ? "s" : string.Empty)} its new position."
                        : "Places that use this weather point now read its new position.",
                    Count = canReadLocations ? linked : null,
                });
            }
        }

        logger.LogInformation(
            "Weather point {WeatherPointId} updated by {ActorUserId}; changed: [{ChangedFields}].",
            pointId, userId, string.Join(", ", changes));
        return new WeatherPointUpdateResult
        {
            Point = (await GetAsync(pointId, canReadLocations, cancellationToken))!,
            Warnings = warnings,
        };
    }

    // ── Refresh (endpoint 6) ──────────────────────────────────────────────────────────────────────

    /// <summary>Forces a forecast fetch, still honouring MET's <c>Expires</c> and any pause. Null: no point with forecast on.</summary>
    public async Task<WeatherRefreshResult?> RefreshAsync(Guid pointId, string userId, CancellationToken cancellationToken)
    {
        var point = await context.WeatherPoints.AsNoTracking().FirstOrDefaultAsync(p => p.WeatherPointId == pointId, cancellationToken);
        if (point is null || !point.ForecastEnabled)
        {
            return null;
        }

        if (point.PurgeStartedAt is not null)
        {
            throw PurgeConflict();
        }

        if (!(await settings.GetAsync(cancellationToken)).IsConfigured)
        {
            throw new WeatherNotConfiguredException();
        }

        var attempt = await fetch.FetchForecastAsync(pointId, WeatherFetchMode.Refresh, cancellationToken);
        var issued = attempt.IssuedAtUtc ?? await context.WeatherForecastRuns.AsNoTracking()
            .Where(r => r.WeatherPointId == pointId)
            .MaxAsync(r => (DateTime?)r.ProviderUpdatedAt, cancellationToken);
        var failure = await context.WeatherPointSchedules.AsNoTracking()
            .Where(s => s.WeatherPointId == pointId).Select(s => s.ForecastFailureKind).FirstOrDefaultAsync(cancellationToken);

        logger.LogInformation(
            "Weather forecast refresh for point {WeatherPointId} by {ActorUserId}: {Outcome}.",
            pointId, userId, attempt.Status);
        return new WeatherRefreshResult
        {
            Fetched = attempt.Status == WeatherAttemptStatus.Completed,
            IssuedAtUtc = issued is { } i ? DateTime.SpecifyKind(i, DateTimeKind.Utc) : null,
            ForecastFailureKind = failure,
        };
    }

    // ── Purge (endpoints 7–8) ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Endpoint 7: deletes the point's measured data only. Allowed once history is off. Resumable: an interrupted
    /// history purge is completed by a repeat; an interrupted <b>full</b> delete is refused here
    /// (<c>purge-in-progress</c>) — endpoint 8 completes it. False when the point is unknown.
    /// </summary>
    public Task<bool> PurgeHistoryAsync(Guid pointId, string userId, CancellationToken cancellationToken) =>
        PurgeAsync(pointId, WeatherPurgeMode.HistoryOnly, userId, cancellationToken);

    /// <summary>
    /// Endpoint 8: deletes the point and every row keyed on it, once both halves are off and it is not the household
    /// default. Its linked places are unlinked by the key's <c>SET NULL</c>. Completes any interrupted purge (a full
    /// delete is a superset). False when the point is unknown.
    /// </summary>
    public Task<bool> DeleteAsync(Guid pointId, string userId, CancellationToken cancellationToken) =>
        PurgeAsync(pointId, WeatherPurgeMode.Full, userId, cancellationToken);

    private async Task<bool> PurgeAsync(Guid pointId, WeatherPurgeMode mode, string userId, CancellationToken cancellationToken)
    {
        var point = await context.WeatherPoints.FirstOrDefaultAsync(p => p.WeatherPointId == pointId, cancellationToken);
        if (point is null)
        {
            return false;
        }

        if (point.PurgeStartedAt is null)
        {
            if (point.HistoryEnabled || (mode == WeatherPurgeMode.Full && point.ForecastEnabled))
            {
                throw new WeatherConflictException(WeatherConflictException.StillEnabled, mode == WeatherPurgeMode.Full
                    ? "Turn both the forecast and historical data off before deleting this weather point."
                    : "Turn historical weather data off before deleting it.");
            }

            if (mode == WeatherPurgeMode.Full && (await settings.GetAsync(cancellationToken)).HouseholdDefaultPointId == pointId)
            {
                throw new WeatherConflictException(WeatherConflictException.HouseholdDefault,
                    "This is the household's default weather point. Choose another default first.");
            }

            var schedule = await context.WeatherPointSchedules.FirstAsync(s => s.WeatherPointId == pointId, cancellationToken);
            var now = Now;
            if (schedule.LeaseUntilUtc is { } lease && lease > now)
            {
                throw FetchConflict("A forecast fetch is running for this point right now. Try again in a minute.");
            }

            // Step 1: fence. From here no fetch can claim the point, a fenced ingest discards, and every write but a
            // repeat of a purge returns 409. A concurrent DELETE loses on a token and gets a 409.
            point.PurgeStartedAt = now;
            point.PurgeMode = mode;
            point.SettingsVersion = Guid.NewGuid();
            schedule.LeaseUntilUtc = DateTime.MaxValue;
            schedule.Version = Guid.NewGuid();
            try
            {
                await context.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateConcurrencyException)
            {
                context.ChangeTracker.Clear();
                throw FetchConflict("This weather point is changing right now. Try again in a minute.");
            }

            logger.LogWarning("Weather purge ({Mode}) of point {WeatherPointId} started by {ActorUserId}.", mode, pointId, userId);
        }
        else
        {
            // An interrupted full delete has already removed data the point's forecast needed; only another full
            // delete may finish it (issue #387 §5 endpoint 7, AC 11a).
            if (mode == WeatherPurgeMode.HistoryOnly && point.PurgeMode != WeatherPurgeMode.HistoryOnly)
            {
                throw new WeatherConflictException(WeatherConflictException.PurgeInProgress,
                    "This weather point is being deleted. Finish deleting it instead.");
            }

            if (mode == WeatherPurgeMode.Full && point.PurgeMode != WeatherPurgeMode.Full)
            {
                point.PurgeMode = WeatherPurgeMode.Full;
                try
                {
                    await context.SaveChangesAsync(cancellationToken);
                }
                catch (DbUpdateConcurrencyException)
                {
                    context.ChangeTracker.Clear();
                    throw FetchConflict("This weather point is changing right now. Try again in a minute.");
                }
            }

            logger.LogWarning("Weather purge ({Mode}) of point {WeatherPointId} resumed by {ActorUserId}.", mode, pointId, userId);
        }

        var unlinked = mode == WeatherPurgeMode.Full
            ? await context.Locations.AsNoTracking().CountAsync(l => l.WeatherPointId == pointId, cancellationToken)
            : 0;
        context.ChangeTracker.Clear();
        var counts = await purger.PurgeAsync(pointId, mode, cancellationToken);
        logger.LogWarning(
            "Weather purge ({Mode}) of point {WeatherPointId} completed by {ActorUserId}: {Steps} steps, {Runs} runs, {Observations} observations, {Days} days, {Sites} sites.",
            mode, pointId, userId, counts.Steps, counts.Runs, counts.Observations, counts.Days, counts.Sites);
        if (mode == WeatherPurgeMode.Full)
        {
            logger.LogInformation(
                "Weather point {WeatherPointId} deleted by {ActorUserId}; {UnlinkedCount} linked location(s) unlinked.",
                pointId, userId, unlinked);
        }

        return true;
    }

    // ── Shared ────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Tracked points in cap order — the one definition the scheduler and the Admin list share (§3 Tracked cap): of
    /// the tracked, forecast-on, not-purging points ordered by <c>EnabledAt</c>, only the first <i>N</i> refresh.
    /// Linked places never count (AC 8).
    /// </summary>
    public static IQueryable<WeatherPoint> TrackedQueue(OdysseyContext context) =>
        context.WeatherPoints.AsNoTracking()
            .Where(p => p.ForecastEnabled && p.IsTracked && p.PurgeStartedAt == null)
            .OrderBy(p => p.EnabledAt).ThenBy(p => p.WeatherPointId);

    private async Task<List<ExistingWeatherPoint>> ToDtosAsync(
        IReadOnlyList<WeatherPoint> points, bool canReadLocations, CancellationToken cancellationToken)
    {
        if (points.Count == 0)
        {
            return [];
        }

        var configured = await settings.GetAsync(cancellationToken);
        var queue = await TrackedQueue(context).Select(p => p.WeatherPointId).ToListAsync(cancellationToken);
        var suspended = queue.Skip(configured.MaxTrackedPoints).ToHashSet();
        var ids = points.Select(p => p.WeatherPointId).ToList();

        // Only each point's current site version: the history of moves is not part of the row.
        var sites = await context.WeatherPointSites.AsNoTracking()
            .Where(s => context.WeatherPoints.Any(p =>
                ids.Contains(p.WeatherPointId) && p.WeatherPointId == s.WeatherPointId && p.CurrentSiteVersion == s.SiteVersion))
            .ToDictionaryAsync(s => s.WeatherPointId, cancellationToken);
        var schedules = await context.WeatherPointSchedules.AsNoTracking().Where(s => ids.Contains(s.WeatherPointId))
            .ToDictionaryAsync(s => s.WeatherPointId, cancellationToken);
        var stations = (await context.WeatherStations.AsNoTracking().Where(st => ids.Contains(st.WeatherPointId)).ToListAsync(cancellationToken))
            .ToLookup(st => st.WeatherPointId);
        var linked = canReadLocations
            ? await context.Locations.AsNoTracking()
                .Where(l => l.WeatherPointId != null && ids.Contains(l.WeatherPointId.Value))
                .GroupBy(l => l.WeatherPointId)
                .Select(g => new { Id = g.Key!.Value, Count = g.Count() })
                .ToDictionaryAsync(x => x.Id, x => x.Count, cancellationToken)
            : null;

        return points.Select(point =>
        {
            // CurrentSiteVersion has no key; the point and its site are written in one save, so a miss is corruption.
            var site = sites.GetValueOrDefault(point.WeatherPointId)
                ?? throw new InvalidOperationException($"Weather point {point.WeatherPointId} has no current site row.");
            var schedule = schedules.GetValueOrDefault(point.WeatherPointId);
            var isSuspended = suspended.Contains(point.WeatherPointId);
            var pointStations = stations[point.WeatherPointId].ToList();
            return new ExistingWeatherPoint
            {
                WeatherPointId = point.WeatherPointId,
                Name = point.Name,
                SettingsVersion = point.SettingsVersion,
                ForecastEnabled = point.ForecastEnabled,
                HistoryEnabled = point.HistoryEnabled,
                IsTracked = point.IsTracked,
                TrackingSuspended = isSuspended,
                TrackingSuspendedReason = isSuspended ? WeatherTrackingSuspendedReason.CapReached : null,
                HistoryFrom = point.HistoryFrom,
                Site = new WeatherPointSiteDto
                {
                    Version = site.SiteVersion,
                    Latitude = site.Latitude,
                    Longitude = site.Longitude,
                    AltitudeMeters = site.AltitudeMeters,
                    TimeZoneId = site.TimeZoneId,
                    AppliedAt = Utc(site.AppliedAt),
                },
                PurgeInProgress = point.PurgeStartedAt is not null,
                IsHouseholdDefault = configured.HouseholdDefaultPointId == point.WeatherPointId,
                LinkedLocationCount = linked?.GetValueOrDefault(point.WeatherPointId),
                // As on the daily response: no station only once every lookup came back empty, never before one ran.
                HasObservationStation = WeatherStationGroups.HasAny(point.StationResolvedAtUtc, pointStations),
                Stations = WeatherStationGroups.Refs(pointStations),
                LastForecastSuccessUtc = Utc(schedule?.LastForecastSuccessUtc),
                ForecastFailureKind = schedule?.ForecastFailureKind,
                LastObservationSuccessUtc = Utc(schedule?.LastObservationSuccessUtc),
                ObservationFailureKind = schedule?.ObservationFailureKind,
                EnabledAt = Utc(point.EnabledAt),
                UpdatedAt = Utc(point.UpdatedAt),
            };
        }).ToList();
    }

    private Task<WeatherPointSite?> CurrentSiteAsync(Guid pointId, int version, CancellationToken cancellationToken) =>
        context.WeatherPointSites.AsNoTracking()
            .FirstOrDefaultAsync(s => s.WeatherPointId == pointId && s.SiteVersion == version, cancellationToken);

    private async Task EnsureTrackedCapacityAsync(Guid? pointId, WeatherSettings configured, CancellationToken cancellationToken)
    {
        var tracked = await TrackedQueue(context).CountAsync(p => p.WeatherPointId != pointId, cancellationToken);
        if (tracked >= configured.MaxTrackedPoints)
        {
            throw new DomainUnprocessableException(
                $"{configured.MaxTrackedPoints} of {configured.MaxTrackedPoints} scheduled weather points are in use. Turn scheduling off for another point, or raise the limit in System settings.",
                "isTracked");
        }
    }

    /// <summary>
    /// Case-insensitive, like the unique index's collation, so <c>Oslo</c> and <c>oslo</c> conflict on every
    /// provider; the index is the backstop for a concurrent insert (<see cref="SaveNamedAsync"/>).
    /// </summary>
    private async Task EnsureNameAvailableAsync(string name, Guid? excludeId, CancellationToken cancellationToken)
    {
        var names = await context.WeatherPoints.AsNoTracking()
            .Where(p => excludeId == null || p.WeatherPointId != excludeId)
            .Select(p => p.Name)
            .ToListAsync(cancellationToken);
        if (names.Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase)))
        {
            throw DuplicateName();
        }
    }

    private async Task SaveNamedAsync(CancellationToken cancellationToken)
    {
        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (DbErrors.IsDuplicateKeyOn(ex, "IX_WeatherPoints_Name"))
        {
            context.ChangeTracker.Clear();
            throw DuplicateName();
        }
    }

    private static WeatherConflictException DuplicateName() =>
        new(WeatherConflictException.DuplicateName, "Another weather point has this name.", NameField);

    private static WeatherConflictException StaleConflict() =>
        new(WeatherConflictException.Stale, "Someone else changed this weather point. Reload it and try again.");

    private static WeatherConflictException PurgeConflict() =>
        new(WeatherConflictException.PurgeInProgress, "This weather point's data is being deleted.");

    private static WeatherConflictException FetchConflict(string message) =>
        new(WeatherConflictException.FetchInProgress, message);

    private static string? CleanName(string? name) =>
        string.IsNullOrWhiteSpace(name) ? null : name.Trim();

    private static string RequireZone(string? zone) =>
        zone is { } z && IanaTimeZones.IsValid(z.Trim())
            ? z.Trim()
            : throw new DomainValidationException("The time zone must be an IANA time zone, e.g. Europe/Oslo.", code: null, field: "timeZoneId");

    private static WeatherPointSite NewSite(
        Guid pointId, int version, decimal latitude, decimal longitude, int? altitude, string zone, DateTime now, string userId) => new()
    {
        WeatherPointId = pointId,
        SiteVersion = version,
        Latitude = WeatherCoordinates.Round(latitude),
        Longitude = WeatherCoordinates.Round(longitude),
        AltitudeMeters = altitude,
        TimeZoneId = zone,
        AppliedAt = now,
        AppliedByUserId = userId,
    };

    private static DateTime Utc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);

    private static DateTime? Utc(DateTime? value) => value is { } v ? Utc(v) : null;

    /// <summary>
    /// An Admin write rotates the point's <see cref="WeatherPoint.SettingsVersion"/> and, when it fences a fetch, the
    /// schedule's <c>Version</c>. Losing on the first is a concurrent Admin edit (<c>stale</c>); losing on the second
    /// is a fetch claim or a purge fence that got there first (<c>fetch-in-progress</c>). Either is a 409, never a 500.
    /// </summary>
    private async Task<bool> FencedAsync(Func<Task<bool>> work)
    {
        try
        {
            return await work();
        }
        catch (DbUpdateConcurrencyException ex)
        {
            context.ChangeTracker.Clear();
            if (ex.Entries.Any(e => e.Entity is WeatherPoint))
            {
                throw StaleConflict();
            }

            throw FetchConflict("This weather point is changing right now. Try again in a minute.");
        }
    }

    /// <summary>
    /// Runs <paramref name="work"/> in one transaction inside the execution strategy (a retrying strategy refuses a
    /// bare user transaction). The tracker is reset on a retry so a failed attempt's changes cannot ride along.
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
