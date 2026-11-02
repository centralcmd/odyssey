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
    /// The position fingerprint, or <see cref="NoCoordinatesFingerprint"/> for a place with no coordinates
    /// (issue #380 made them optional, all-or-none). That value never matches a reviewed position, so the
    /// fingerprint check refuses rather than turning weather on for a place it cannot locate.
    /// </summary>
    public static string Fingerprint(LocationEntity location) =>
        Position(location) is { } position
            ? WeatherRules.Fingerprint(position.Latitude, position.Longitude, location.AltitudeMeters, position.TimeZoneId)
            : NoCoordinatesFingerprint;

    /// <summary>A place whose coordinates were removed has moved away from any site weather is using.</summary>
    public static bool Moved(LocationEntity location, Odyssey.Context.WeatherLocationSite site) =>
        Position(location) is not { } position
        || WeatherRules.Moved(position.Latitude, position.Longitude, location.AltitudeMeters, position.TimeZoneId,
            site.Latitude, site.Longitude, site.AltitudeMeters, site.TimeZoneId);

    public const string NoCoordinatesFingerprint = WeatherRules.NoCoordinatesFingerprint;

    /// <summary>
    /// The location's position, or <see langword="null"/> when it has none. The three columns are
    /// all-or-none (<c>CK_Locations_CoordinatesAllOrNone</c>), so a partial set is treated as none.
    /// </summary>
    public static (decimal Latitude, decimal Longitude, string TimeZoneId)? Position(LocationEntity location) =>
        location is { Latitude: { } latitude, Longitude: { } longitude, TimeZoneId: { } timeZoneId }
            ? (latitude, longitude, timeZoneId)
            : null;
}
