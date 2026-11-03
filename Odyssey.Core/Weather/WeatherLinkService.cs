using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Odyssey.Context;
using Odyssey.Core.Locations;
using Odyssey.Dtos.Journal;

namespace Odyssey.Core.Weather;

/// <summary>
/// Sets or clears a place's weather point (issue #387 §5 endpoint 10) — the ONLY writer of
/// <c>Locations.WeatherPointId</c>. One transaction whose first statement locks the place <c>FOR UPDATE</c>, the
/// order <c>LocationService</c>'s merge and delete already take, so a link racing a merge or a delete of the same
/// place is serialised: the loser sees a forwarder (<c>422</c>) or no place (<c>404</c>). The point is read on the
/// same snapshot; a point deleted after that read is caught by the key (MariaDB 1452) and is the same <c>422</c>.
/// </summary>
/// <remarks>
/// Linking sends nothing to MET or Frost — only an Admin creates or moves a point. Archived places may be linked:
/// archiving governs new references TO a place, and this is the place itself. The audit line carries ids only.
/// </remarks>
public sealed class WeatherLinkService
{
    private const string PointField = "weatherPointId";

    private readonly OdysseyContext context;
    private readonly TimeProvider time;
    private readonly ILogger<WeatherLinkService> logger;

    public WeatherLinkService(OdysseyContext context, TimeProvider? time = null, ILogger<WeatherLinkService>? logger = null)
    {
        this.context = context;
        this.time = time ?? TimeProvider.System;
        this.logger = logger ?? NullLogger<WeatherLinkService>.Instance;
    }

    /// <summary>Null when the place does not exist.</summary>
    public async Task<LocationWeatherPointLink?> SetAsync(
        Guid locationId, Guid? pointId, string userId, CancellationToken cancellationToken)
    {
        var found = false;
        Guid? previous = null;
        var changed = false;

        try
        {
            await LocationTransactions.ExecuteAsync(context, async () =>
            {
                found = false;
                changed = false;
                await LocationTransactions.LockAsync(context, [locationId], cancellationToken);
                var location = await context.Locations.FirstOrDefaultAsync(l => l.LocationId == locationId, cancellationToken);
                if (location is null)
                {
                    return;
                }

                found = true;
                if (location.MergedIntoLocationId is not null)
                {
                    throw new DomainUnprocessableException(
                        $"Location ID {locationId} was merged into another place. Link the place it was merged into.", "id");
                }

                if (pointId is { } id
                    && !await context.WeatherPoints.AnyAsync(p => p.WeatherPointId == id && p.PurgeStartedAt == null, cancellationToken))
                {
                    throw UnusablePoint();
                }

                previous = location.WeatherPointId;
                if (previous == pointId)
                {
                    return;
                }

                location.WeatherPointId = pointId;
                location.UpdatedAt = time.GetUtcNow().UtcDateTime;
                location.UpdatedByUserId = userId;
                await context.SaveChangesAsync(cancellationToken);
                changed = true;
            }, IsolationLevel.RepeatableRead, cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is MySqlConnector.MySqlException { Number: 1452 })
        {
            // The point was deleted between the check and the save: the key refused it.
            context.ChangeTracker.Clear();
            throw UnusablePoint();
        }

        if (!found)
        {
            return null;
        }

        if (changed)
        {
            logger.LogInformation(
                "Location {LocationId} weather point {Operation} by {ActorUserId}: {PreviousWeatherPointId} -> {WeatherPointId}.",
                locationId, pointId is null ? "cleared" : previous is null ? "set" : "changed", userId, previous,
                pointId is Guid linked ? linked : null);
        }

        return await DescribeAsync(locationId, cancellationToken);
    }

    /// <summary>The response, with the whole-km distance and the advisories (computed after the write commits).</summary>
    private async Task<LocationWeatherPointLink> DescribeAsync(Guid locationId, CancellationToken cancellationToken)
    {
        var location = await context.Locations.AsNoTracking()
            .Where(l => l.LocationId == locationId)
            .Select(l => new { l.Latitude, l.Longitude, l.TimeZoneId, l.WeatherPointId })
            .FirstAsync(cancellationToken);
        if (location.WeatherPointId is not { } pointId)
        {
            return new LocationWeatherPointLink { LocationId = locationId };
        }

        var point = await (
                from p in context.WeatherPoints.AsNoTracking()
                join site in context.WeatherPointSites.AsNoTracking()
                    on new { p.WeatherPointId, Version = p.CurrentSiteVersion } equals new { site.WeatherPointId, Version = site.SiteVersion }
                where p.WeatherPointId == pointId
                select new { p.Name, p.ForecastEnabled, site.Latitude, site.Longitude, site.TimeZoneId })
            .FirstAsync(cancellationToken);

        var distance = WeatherDistance.KmOrNull(location.Latitude, location.Longitude, point.Latitude, point.Longitude);
        var warnings = new List<WeatherAdvisory>();
        if (location.TimeZoneId is { } zone && !string.Equals(zone, point.TimeZoneId, StringComparison.Ordinal))
        {
            warnings.Add(new WeatherAdvisory
            {
                Code = WeatherAdvisoryCode.TimeZoneMismatch,
                Message = "This weather point keeps a different time zone from this place, so a weather day may be off by one.",
            });
        }

        if (distance is null)
        {
            warnings.Add(new WeatherAdvisory
            {
                Code = WeatherAdvisoryCode.NoLocationCoordinates,
                Message = "This place has no map coordinates, so the distance to the weather point is unknown.",
            });
        }

        if (!point.ForecastEnabled)
        {
            warnings.Add(new WeatherAdvisory
            {
                Code = WeatherAdvisoryCode.PointNotSelectable,
                Message = "This weather point's forecast is off, so this place shows only stored history.",
            });
        }

        return new LocationWeatherPointLink
        {
            LocationId = locationId,
            WeatherPoint = new WeatherPointRef { WeatherPointId = pointId, Name = point.Name },
            DistanceKm = distance,
            Warnings = warnings,
        };
    }

    private static DomainUnprocessableException UnusablePoint() =>
        new("That weather point does not exist or is being deleted. Choose another.", PointField);
}
