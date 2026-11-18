using Microsoft.EntityFrameworkCore;
using Odyssey.Context;
using Odyssey.Dtos.Journal;
using LocationEntity = Odyssey.Context.Location;

namespace Odyssey.Core.Locations;

/// <summary>
/// The one definition of "something uses this place" (issue #380 §3, A-R11). It counts references in the
/// five referencing tables — <c>CalendarEvents</c>, <c>RecurrencePatterns</c>, <c>JournalEntries</c>,
/// <c>Addresses</c>, <c>RealEstateDetails</c> — and, as a reference that is not a record, a merge forwarder.
/// Delete, merge, <c>?referenced=</c>, <c>usage</c> and <c>otherUses</c> all read it.
/// </summary>
/// <remarks>
/// Weather is no longer a use (issue #387 §8): a place's weather link is a column on the place itself
/// (<c>Locations.WeatherPointId</c>), and the preference and household default name a weather point, not a place.
/// </remarks>
/// <remarks>
/// Adding a sixth referencing table means extending <see cref="IsReferencedAsync"/>,
/// <see cref="WhereReferenced"/> and <see cref="CountAsync"/> together, and
/// <c>OdysseyContext.ConfigureLocationReferences</c> beside them.
/// </remarks>
public sealed class LocationReferenceGuard(OdysseyContext context)
{
    /// <summary>Per-kind record counts for one location. Every kind is counted; the caller masks by claim.</summary>
    public sealed record Counts(int Contacts, int Properties, int CalendarEvents, int RecurringSeries, int JournalEntries)
    {
        public static Counts Zero { get; } = new(0, 0, 0, 0, 0);
    }

    /// <summary>A record kind, for <see cref="OtherUses"/>'s "this row excluded".</summary>
    public enum Kind
    {
        Contact,
        Property,
    }

    /// <summary>Whether anything at all references <paramref name="locationId"/> — the delete guard (§5.7).</summary>
    public async Task<bool> IsReferencedAsync(Guid locationId, CancellationToken cancellationToken) =>
        await context.Addresses.AnyAsync(a => a.LocationId == locationId, cancellationToken)
        || await context.RealEstateDetails.AnyAsync(d => d.LocationId == locationId, cancellationToken)
        || await context.CalendarEvents.AnyAsync(e => e.LocationId == locationId, cancellationToken)
        || await context.RecurrencePatterns.AnyAsync(p => p.LocationId == locationId, cancellationToken)
        || await context.JournalEntries.AnyAsync(e => e.LocationId == locationId, cancellationToken)
        || await context.Locations.AnyAsync(l => l.MergedIntoLocationId == locationId, cancellationToken);

    /// <summary>
    /// <paramref name="query"/> narrowed to referenced (<paramref name="referenced"/> = true) or unused
    /// places, by the same definition as <see cref="IsReferencedAsync"/>.
    /// </summary>
    public IQueryable<LocationEntity> WhereReferenced(IQueryable<LocationEntity> query, bool referenced) =>
        query.Where(l =>
            (context.Addresses.Any(a => a.LocationId == l.LocationId)
             || context.RealEstateDetails.Any(d => d.LocationId == l.LocationId)
             || context.CalendarEvents.Any(e => e.LocationId == l.LocationId)
             || context.RecurrencePatterns.Any(p => p.LocationId == l.LocationId)
             || context.JournalEntries.Any(e => e.LocationId == l.LocationId)
             || context.Locations.Any(f => f.MergedIntoLocationId == l.LocationId)) == referenced);

    /// <summary>The per-kind record counts for <paramref name="locationIds"/>, one grouped query per kind.</summary>
    public async Task<Dictionary<Guid, Counts>> CountAsync(IReadOnlyCollection<Guid> locationIds, CancellationToken cancellationToken)
    {
        var ids = locationIds.Distinct().ToList();
        if (ids.Count == 0)
        {
            return [];
        }

        var contacts = await GroupCount(context.Addresses.Where(a => ids.Contains(a.LocationId)).Select(a => (Guid?)a.LocationId), cancellationToken);
        var properties = await GroupCount(context.RealEstateDetails.Where(d => d.LocationId != null && ids.Contains(d.LocationId.Value)).Select(d => d.LocationId), cancellationToken);
        var events = await GroupCount(context.CalendarEvents.Where(e => e.LocationId != null && ids.Contains(e.LocationId.Value)).Select(e => e.LocationId), cancellationToken);
        var series = await GroupCount(context.RecurrencePatterns.Where(p => p.LocationId != null && ids.Contains(p.LocationId.Value)).Select(p => p.LocationId), cancellationToken);
        var entries = await GroupCount(context.JournalEntries.Where(e => e.LocationId != null && ids.Contains(e.LocationId.Value)).Select(e => e.LocationId), cancellationToken);

        return ids.ToDictionary(id => id, id => new Counts(
            contacts.GetValueOrDefault(id),
            properties.GetValueOrDefault(id),
            events.GetValueOrDefault(id),
            series.GetValueOrDefault(id),
            entries.GetValueOrDefault(id)));
    }

    /// <summary>
    /// <see cref="ExistingLocation.Usage"/>: each kind filled only when <paramref name="claims"/> may read it
    /// (decision 6). There is no total, on purpose.
    /// </summary>
    public static LocationUsage ToUsage(Counts counts, LocationClaims claims) => new()
    {
        Contacts = claims.ContactsRead ? counts.Contacts : null,
        Properties = claims.PropertiesRead ? counts.Properties : null,
        CalendarEvents = claims.CalendarRead ? counts.CalendarEvents : null,
        RecurringSeries = claims.CalendarRead ? counts.RecurringSeries : null,
        JournalEntries = claims.JournalRead ? counts.JournalEntries : null,
    };

    /// <summary>
    /// The number of OTHER records using the place — the row of kind <paramref name="self"/> excluded —
    /// summed over the kinds <paramref name="claims"/> may read (§5.3). The row itself is always of a kind
    /// the caller reads, since the count is only ever shown beside it.
    /// </summary>
    public static int OtherUses(Counts counts, Kind self, LocationClaims claims)
    {
        var total = (claims.ContactsRead || self == Kind.Contact ? counts.Contacts : 0)
            + (claims.PropertiesRead || self == Kind.Property ? counts.Properties : 0)
            + (claims.CalendarRead ? counts.CalendarEvents + counts.RecurringSeries : 0)
            + (claims.JournalRead ? counts.JournalEntries : 0);
        return Math.Max(0, total - 1);
    }

    private static async Task<Dictionary<Guid, int>> GroupCount(IQueryable<Guid?> keys, CancellationToken cancellationToken) =>
        await keys.GroupBy(id => id)
            .Select(g => new { Id = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Id!.Value, x => x.Count, cancellationToken);
}
