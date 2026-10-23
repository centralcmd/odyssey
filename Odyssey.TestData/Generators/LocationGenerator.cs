using Odyssey.Context;
using Odyssey.Dtos.Journal;

namespace Odyssey.TestData.Generators;

/// <summary>
/// Deterministic household locations (issue #350): one per source, plus one archived — the shapes the page
/// and the tests need. Written directly as entities, like every other generator, so the values are already
/// in their stored form: names cleaned and coordinates at 5 decimals.
/// </summary>
public static class LocationGenerator
{
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
        ];
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
