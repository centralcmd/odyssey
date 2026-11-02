using Odyssey.Context;
using Odyssey.Dtos.Journal;

namespace Odyssey.TestData.Generators;

/// <summary>
/// Deterministic household locations (issue #350): one per source, plus one archived — the shapes the page
/// and the tests need. Written directly as entities, like every other generator, so the values are already
/// in their stored form: names cleaned and coordinates at 5 decimals.
/// </summary>
/// <remarks>
/// Since issue #380 a location is the one address record, so the demo addresses are built here too:
/// <see cref="SharedAddress"/> is listed by two contacts, three properties link their address, one address
/// is used by nothing (<see cref="UnusedAddress"/>), and <see cref="Home"/> is linked from journal entries
/// and an event. The address-derived places carry no coordinates, as a converted one would.
/// </remarks>
public static class LocationGenerator
{
    public const string Home = "home";
    public const string SharedAddress = "shared-address";
    public const string UnusedAddress = "unused-address";
    public const string CabinAddress = "cabin-address";
    public const string ApartmentAddress = "apartment-address";
    public const string PlotAddress = "plot-address";

    public static Guid LocationIdFor(string key) => DeterministicGuid.From($"location::{key}");

    public static IReadOnlyList<Location> Generate(DateTime anchor)
    {
        var ownerId = UserId("Owner");
        var userId = UserId("User");

        return
        [
            Build("home", "Home", "Spare key with the neighbours in 3B.", "Thorvald Meyers gate 41, 0555 OSLO",
                59.92302m, 10.75937m, null, "Europe/Oslo", LocationSource.Kartverket, "0301-13254-41", ownerId, anchor.AddDays(-200), archived: null),
            Build("cabin", "Cabin", "Family cabin. Water is turned off at the main valve under the stairs from October to May.",
                "Fjellveien 12, 2630 RINGEBU", 61.52914m, 10.13802m, 820, "Europe/Oslo", LocationSource.Kartverket, "3439-1020-12", ownerId, anchor.AddDays(-198), archived: null),
            Build("grandma", "Granny's house", null, null,
                38.72509m, -9.14980m, 68, "Europe/Lisbon", LocationSource.OpenMeteo, "2267057", userId, anchor.AddDays(-150), archived: null),
            Build("mooring", "Boat mooring", "Berth 14, pontoon C. Pinned from the marina's chart.", null,
                59.90518m, 10.72604m, null, "Europe/Oslo", LocationSource.Manual, null, userId, anchor.AddDays(-120), archived: null),
            Build("old-flat", "Old flat", null, "Sannergata 6, 0557 OSLO",
                59.92823m, 10.75892m, null, "Europe/Oslo", LocationSource.Kartverket, "0301-15320-6", ownerId, anchor.AddDays(-199), archived: anchor.AddDays(-90)),

            // Issue #380 — addresses. Names are the composed form the resolver would give them.
            Address(SharedAddress, LocationSource.ContactAddress, "Storgata 10", null, "0155", "Oslo", null, "NO", ownerId, anchor.AddDays(-180)),
            Address(UnusedAddress, LocationSource.ContactAddress, "Kirkegata 5", "2nd floor", "0153", "Oslo", null, "NO", ownerId, anchor.AddDays(-170)),
            Address(CabinAddress, LocationSource.PropertyAddress, "Fjellvegen 7", null, "3580", "Geilo", null, "NO", ownerId, anchor.AddDays(-160)),
            Address(ApartmentAddress, LocationSource.PropertyAddress, "Example Street 12, 3rd floor", null, "10115", "Berlin", null, "DE", ownerId, anchor.AddDays(-160)),
            Address(PlotAddress, LocationSource.PropertyAddress, null, null, null, "Hamar", null, "NO", ownerId, anchor.AddDays(-160)),
        ];
    }

    private static Location Address(
        string key, LocationSource source, string? line1, string? line2, string? postalCode, string? city,
        string? region, string? countryCode, string createdBy, DateTime createdAt)
    {
        var composed = string.Join(", ", new[]
        {
            string.Join(' ', new[] { line1, line2 }.Where(part => part is not null)),
            string.Join(' ', new[] { postalCode, city }.Where(part => part is not null)),
            region ?? string.Empty,
            countryCode ?? string.Empty,
        }.Where(group => group.Length > 0));

        return new Location
        {
            LocationId = LocationIdFor(key),
            Name = composed,
            AddressText = composed,
            Line1 = line1,
            Line2 = line2,
            PostalCode = postalCode,
            City = city,
            Region = region,
            CountryCode = countryCode,
            AddressKey = LocationAddressKey.Compute(countryCode, postalCode, city, line1, line2, region),
            Source = source,
            CreatedByUserId = createdBy,
            UpdatedByUserId = createdBy,
            CreatedAt = createdAt,
            UpdatedAt = createdAt,
        };
    }

    private static Location Build(
        string key, string name, string? description, string? addressText, decimal latitude, decimal longitude,
        int? altitude, string timeZoneId, LocationSource source, string? reference, string createdBy, DateTime createdAt,
        DateTime? archived) => new()
    {
        LocationId = LocationIdFor(key),
        Name = name,
        Description = description,
        AddressText = addressText,
        Latitude = latitude,
        Longitude = longitude,
        AltitudeMeters = altitude,
        TimeZoneId = timeZoneId,
        Source = source,
        SourceReference = reference,
        Archived = archived,
        CreatedByUserId = createdBy,
        UpdatedByUserId = createdBy,
        CreatedAt = createdAt,
        UpdatedAt = archived ?? createdAt,
    };

    private static string UserId(string role) => DemoUsers.All.First(user => user.Role == role).Id;
}
