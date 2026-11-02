using Microsoft.EntityFrameworkCore;
using Odyssey.Context;
using Odyssey.Dtos.Journal;
using LocationEntity = Odyssey.Context.Location;

namespace Odyssey.Core.Locations;

/// <summary>
/// Finds the location holding an address, or stages a new one, so a contact address or a property address
/// can link to it (issue #380). The new row is added to <paramref name="context"/> unsaved, so it commits in
/// the caller's save together with the link.
/// </summary>
/// <remarks>
/// B2: a compile-preserving stand-in for the location resolver (§3, §8.1). It keeps today's contact and
/// property address writes working on the new schema and nothing more — no claim checks
/// (<c>locations.read</c>/<c>.create</c>/<c>.update</c>), no <c>applyTo</c> edit modes, no archived-link
/// rule and no <c>1062</c> re-read. The resolver replaces it.
/// </remarks>
internal static class LocationAddressLinker
{
    public static async Task<LocationEntity> FindOrCreateAsync(
        OdysseyContext context, LocationAddress address, LocationSource source, string? userId, DateTime now,
        CancellationToken cancellationToken)
    {
        if (address.Key is { } key)
        {
            var match = context.Locations.Local.FirstOrDefault(l => l.AddressKey == key)
                ?? await context.Locations.FirstOrDefaultAsync(l => l.AddressKey == key, cancellationToken);
            if (match is not null)
            {
                // A merged-away location forwards to its survivor; chains are at most one hop (§5.6).
                return match.MergedIntoLocationId is { } survivor
                    ? await context.Locations.FirstAsync(l => l.LocationId == survivor, cancellationToken)
                    : match;
            }
        }

        var composed = address.Composed ?? throw new InvalidOperationException("An empty address has no location.");
        var taken = new HashSet<string>(
            await context.Locations.AsNoTracking().Select(l => l.Name).ToListAsync(cancellationToken),
            LocationNaming.CollationComparer);
        taken.UnionWith(context.Locations.Local.Select(l => l.Name));

        var location = new LocationEntity
        {
            Name = LocationNaming.Allocate(composed, taken.Contains),
            AddressText = LocationNaming.Truncate(composed, LocationRules.AddressTextMaxLength),
            Line1 = address.Line1,
            Line2 = address.Line2,
            PostalCode = address.PostalCode,
            City = address.City,
            Region = address.Region,
            CountryCode = address.CountryCode,
            AddressKey = address.Key,
            Source = source,
            CreatedByUserId = userId,
            UpdatedByUserId = userId,
            CreatedAt = now,
            UpdatedAt = now,
        };
        context.Locations.Add(location);
        return location;
    }

    /// <summary>Whether <paramref name="location"/>'s structured fields equal <paramref name="address"/>'s, ordinally.</summary>
    public static bool HasFields(LocationEntity location, LocationAddress address) =>
        LocationAddress.Of(location.Line1, location.Line2, location.PostalCode, location.City, location.Region, location.CountryCode)
            == address;
}
