using Odyssey.Dtos.Journal;
using LocationEntity = Odyssey.Context.Location;

namespace Odyssey.Core.Weather;

/// <summary>
/// Weather's coordinate rules applied to the entities (issue #348 §3). The rules themselves live once, in
/// <see cref="WeatherRules"/> (<c>Odyssey.Dtos</c>), shared with the client; this only adapts the entity
/// types, so the snapshot, the "moved" comparison and every outbound request round the same way.
/// </summary>
public static class WeatherCoordinates
{
    public static decimal Round(decimal value) => WeatherRules.RoundCoordinate(value);

    /// <summary>
    /// <c>ExistingLocation.WeatherFingerprint</c> (issue #387 §5 endpoint 11): what a weather point would copy
    /// from the place, or null for a place with no coordinates.
    /// </summary>
    public static string? FingerprintOrNull(LocationEntity location) =>
        Position(location) is { } position
            ? WeatherRules.Fingerprint(position.Latitude, position.Longitude, location.AltitudeMeters, position.TimeZoneId)
            : null;

    /// <summary>
    /// The location's position, or <see langword="null"/> when it has none. The three columns are
    /// all-or-none (<c>CK_Locations_CoordinatesAllOrNone</c>), so a partial set is treated as none.
    /// </summary>
    public static (decimal Latitude, decimal Longitude, string TimeZoneId)? Position(LocationEntity location) =>
        location is { Latitude: { } latitude, Longitude: { } longitude, TimeZoneId: { } timeZoneId }
            ? (latitude, longitude, timeZoneId)
            : null;
}
