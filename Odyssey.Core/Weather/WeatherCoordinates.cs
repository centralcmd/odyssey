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

    public static string Fingerprint(LocationEntity location) =>
        WeatherRules.Fingerprint(location.Latitude, location.Longitude, location.AltitudeMeters, location.TimeZoneId);

    public static bool Moved(LocationEntity location, Odyssey.Context.WeatherLocationSite site) =>
        WeatherRules.Moved(location.Latitude, location.Longitude, location.AltitudeMeters, location.TimeZoneId,
            site.Latitude, site.Longitude, site.AltitudeMeters, site.TimeZoneId);
}
