namespace Odyssey.Core.Weather;

/// <summary>
/// The one place↔point distance rule (issue #387 §8): great-circle (haversine) between a place's own coordinates
/// and a point's <b>current</b> site, rounded to the nearest whole kilometre away from zero — the station-distance
/// rounding. Computed at read time and never stored, since both ends move independently. Whole kilometres
/// everywhere is deliberate: it bounds what a distance discloses about a point's exact position.
/// </summary>
public static class WeatherDistance
{
    private const double EarthRadiusKm = 6371.0088;

    public static int Km(decimal fromLatitude, decimal fromLongitude, decimal toLatitude, decimal toLongitude)
    {
        var phi1 = Radians((double)fromLatitude);
        var phi2 = Radians((double)toLatitude);
        var dPhi = phi2 - phi1;
        var dLambda = Radians((double)toLongitude - (double)fromLongitude);
        var a = Math.Sin(dPhi / 2) * Math.Sin(dPhi / 2)
            + Math.Cos(phi1) * Math.Cos(phi2) * Math.Sin(dLambda / 2) * Math.Sin(dLambda / 2);
        var c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
        return (int)Math.Round(EarthRadiusKm * c, MidpointRounding.AwayFromZero);
    }

    /// <summary>The distance, or null when either end has no position.</summary>
    public static int? KmOrNull(decimal? fromLatitude, decimal? fromLongitude, decimal? toLatitude, decimal? toLongitude) =>
        fromLatitude is { } a && fromLongitude is { } b && toLatitude is { } c && toLongitude is { } d ? Km(a, b, c, d) : null;

    private static double Radians(double degrees) => degrees * Math.PI / 180;
}
