using Microsoft.EntityFrameworkCore;
using Odyssey.Context;
using Odyssey.Dtos.Journal;

namespace Odyssey.Core.Locations;

/// <summary>
/// The read embed of a linked place (issue #380 §5.1): name, archived flag and whether it has coordinates,
/// for every id on a page in one query. Filled only for a <c>locations.read</c> holder; for anyone else the
/// embed stays <see langword="null"/> while the scalar <c>locationId</c> is still returned.
/// </summary>
public static class LocationRefs
{
    public static async Task<Dictionary<Guid, LocationRef>> LoadAsync(
        OdysseyContext context, IEnumerable<Guid?> locationIds, LocationClaims claims, CancellationToken cancellationToken)
    {
        var ids = locationIds.OfType<Guid>().Distinct().ToList();
        if (!claims.LocationsRead || ids.Count == 0)
        {
            return [];
        }

        return await context.Locations.AsNoTracking()
            .Where(l => ids.Contains(l.LocationId))
            .Select(l => new LocationRef
            {
                LocationId = l.LocationId,
                Name = l.Name,
                Archived = l.Archived != null,
                HasCoordinates = l.Latitude != null && l.Longitude != null && l.TimeZoneId != null,
            })
            .ToDictionaryAsync(r => r.LocationId, cancellationToken);
    }

    /// <summary>The embed for <paramref name="locationId"/> from <paramref name="refs"/>, or null.</summary>
    public static LocationRef? For(this IReadOnlyDictionary<Guid, LocationRef> refs, Guid? locationId) =>
        locationId is { } id && refs.TryGetValue(id, out var found) ? found : null;
}
