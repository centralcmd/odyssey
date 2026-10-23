using System.Text.Json;
using Odyssey.Dtos.Journal;

namespace Odyssey.Core.Journal.Geocoding;

/// <summary>
/// Norway: Kartverket's address search (Adresser) and place-name search (Stedsnavn), queried in parallel
/// against the fixed host <c>https://ws.geonorge.no/</c> (issue #350 §3). Addresses come first, then
/// place names, each in provider order. When exactly one of the two services fails, the other's results
/// are returned as <c>Partial</c>; both failing is <see cref="GeocodingUnavailableException"/>.
/// </summary>
/// <remarks>
/// Field names are those of the live services, recorded in <c>Odyssey.Core.Tests/Fixtures/Geocoding</c>:
/// an address is <c>adressetekst</c>/<c>postnummer</c>/<c>poststed</c> with
/// <c>representasjonspunkt.lat</c>/<c>.lon</c> (EPSG:4258); a place name is <c>skrivemåte</c>,
/// <c>navneobjekttype</c>, <c>kommuner[].kommunenavn</c> and <c>representasjonspunkt.nord</c>/<c>.øst</c>.
/// An address has no single object id, so its reference is composed from the parts that identify it in
/// the national register — <c>{kommunenummer}-{adressekode}-{nummer}{bokstav}</c> — which fits the
/// Kartverket reference shape.
/// </remarks>
public sealed class KartverketLocationSearch(HttpClient http) : ILocationSearchSource
{
    public const string BaseAddress = "https://ws.geonorge.no/";

    private const string TimeZone = "Europe/Oslo";

    public LocationSearchRegion Region => LocationSearchRegion.Norway;

    public async Task<LocationSourceResult> SearchAsync(string query, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(GeocodingHttp.Timeout);

        var q = GeocodingHttp.Encode(query);
        var addresses = Half(() => SearchAddressesAsync(q, timeout.Token));
        var places = Half(() => SearchPlaceNamesAsync(q, timeout.Token));
        await Task.WhenAll(addresses, places);

        var (addressRows, addressOk) = addresses.Result;
        var (placeRows, placesOk) = places.Result;
        if (!addressOk && !placesOk)
        {
            throw new GeocodingUnavailableException("Kartverket");
        }

        return new LocationSourceResult([.. addressRows, .. placeRows], Partial: !addressOk || !placesOk);
    }

    private static async Task<(IReadOnlyList<LocationCandidate> Rows, bool Ok)> Half(Func<Task<IReadOnlyList<LocationCandidate>>> call)
    {
        try
        {
            return (await call(), true);
        }
        catch (GeocodingProviderException)
        {
            return ([], false);
        }
    }

    private async Task<IReadOnlyList<LocationCandidate>> SearchAddressesAsync(string q, CancellationToken cancellationToken)
    {
        using var document = await GeocodingHttp.GetJsonAsync(http, $"adresser/v1/sok?sok={q}&treffPerSide=10", cancellationToken);
        var rows = new List<LocationCandidate>();
        foreach (var address in GeocodingHttp.Array(document.RootElement, "adresser"))
        {
            var text = GeocodingHttp.String(address, "adressetekst");
            if (string.IsNullOrWhiteSpace(text) || !TryPoint(address, "lat", "lon", out var lat, out var lon))
            {
                continue;
            }

            var postal = string.Join(' ', new[] { GeocodingHttp.String(address, "postnummer"), GeocodingHttp.String(address, "poststed") }
                .Where(part => !string.IsNullOrWhiteSpace(part)));
            rows.Add(new LocationCandidate
            {
                Kind = LocationCandidateKind.Address,
                Name = text,
                Description = postal.Length == 0 ? null : postal,
                AddressText = postal.Length == 0 ? text : $"{text}, {postal}",
                Latitude = lat,
                Longitude = lon,
                TimeZoneId = TimeZone,
                Source = LocationSource.Kartverket,
                SourceReference = AddressReference(address),
            });
        }

        return rows;
    }

    private async Task<IReadOnlyList<LocationCandidate>> SearchPlaceNamesAsync(string q, CancellationToken cancellationToken)
    {
        using var document = await GeocodingHttp.GetJsonAsync(
            http, $"stedsnavn/v1/navn?sok={q}&fuzzy=true&utkoordsys=4258&treffPerSide=10", cancellationToken);
        var rows = new List<LocationCandidate>();
        foreach (var place in GeocodingHttp.Array(document.RootElement, "navn"))
        {
            var name = GeocodingHttp.String(place, "skrivemåte");
            if (string.IsNullOrWhiteSpace(name) || !TryPoint(place, "nord", "øst", out var lat, out var lon))
            {
                continue;
            }

            var municipality = GeocodingHttp.Array(place, "kommuner")
                .Select(k => GeocodingHttp.String(k, "kommunenavn"))
                .FirstOrDefault(k => !string.IsNullOrWhiteSpace(k));
            var description = string.Join(" — ", new[] { GeocodingHttp.String(place, "navneobjekttype"), municipality }
                .Where(part => !string.IsNullOrWhiteSpace(part)));
            rows.Add(new LocationCandidate
            {
                Kind = LocationCandidateKind.PlaceName,
                Name = name,
                Description = description.Length == 0 ? null : description,
                Latitude = lat,
                Longitude = lon,
                TimeZoneId = TimeZone,
                Source = LocationSource.Kartverket,
                SourceReference = GeocodingHttp.Scalar(place, "stedsnummer"),
            });
        }

        return rows;
    }

    private static bool TryPoint(JsonElement row, string latName, string lonName, out decimal lat, out decimal lon)
    {
        lat = lon = 0;
        if (row.ValueKind != JsonValueKind.Object
            || !row.TryGetProperty("representasjonspunkt", out var point)
            || GeocodingHttp.Coordinate(point, latName, 90) is not { } la
            || GeocodingHttp.Coordinate(point, lonName, 180) is not { } lo)
        {
            return false;
        }

        lat = la;
        lon = lo;
        return true;
    }

    private static string? AddressReference(JsonElement address)
    {
        var municipality = GeocodingHttp.Scalar(address, "kommunenummer");
        var code = GeocodingHttp.Scalar(address, "adressekode");
        if (municipality is null || code is null)
        {
            return null;
        }

        var number = GeocodingHttp.Scalar(address, "nummer") ?? string.Empty;
        var letter = GeocodingHttp.String(address, "bokstav") ?? string.Empty;
        return $"{municipality}-{code}-{number}{letter}".TrimEnd('-');
    }
}
