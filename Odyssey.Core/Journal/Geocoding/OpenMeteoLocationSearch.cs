using Odyssey.Dtos.Journal;

namespace Odyssey.Core.Journal.Geocoding;

/// <summary>
/// The rest of the world: Open-Meteo's geocoding API at the fixed host
/// <c>https://geocoding-api.open-meteo.com/</c> (issue #350 §3). A response with no matches omits
/// <c>results</c> entirely, which reads as an empty list.
/// </summary>
public sealed class OpenMeteoLocationSearch(HttpClient http) : ILocationSearchSource
{
    public const string BaseAddress = "https://geocoding-api.open-meteo.com/";

    public LocationSearchRegion Region => LocationSearchRegion.World;

    public async Task<LocationSourceResult> SearchAsync(string query, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(GeocodingHttp.Timeout);

        try
        {
            using var document = await GeocodingHttp.GetJsonAsync(
                http, $"v1/search?name={GeocodingHttp.Encode(query)}&count=10&language=en&format=json", timeout.Token);

            var rows = new List<LocationCandidate>();
            foreach (var result in GeocodingHttp.Array(document.RootElement, "results"))
            {
                var name = GeocodingHttp.String(result, "name");
                if (string.IsNullOrWhiteSpace(name)
                    || GeocodingHttp.Coordinate(result, "latitude", 90) is not { } lat
                    || GeocodingHttp.Coordinate(result, "longitude", 180) is not { } lon)
                {
                    continue;
                }

                var description = string.Join(", ", new[] { GeocodingHttp.String(result, "admin1"), GeocodingHttp.String(result, "country") }
                    .Where(part => !string.IsNullOrWhiteSpace(part)));
                rows.Add(new LocationCandidate
                {
                    Kind = LocationCandidateKind.Locality,
                    Name = name,
                    Description = description.Length == 0 ? null : description,
                    Latitude = lat,
                    Longitude = lon,
                    AltitudeMeters = GeocodingHttp.Metres(result, "elevation"),
                    TimeZoneId = GeocodingHttp.String(result, "timezone"),
                    Source = LocationSource.OpenMeteo,
                    SourceReference = GeocodingHttp.Scalar(result, "id"),
                });
            }

            return new LocationSourceResult(rows, Partial: false);
        }
        catch (GeocodingProviderException)
        {
            throw new GeocodingUnavailableException("Open-Meteo");
        }
    }
}
