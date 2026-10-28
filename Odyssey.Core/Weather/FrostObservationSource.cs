using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Odyssey.Dtos.Journal;

namespace Odyssey.Core.Weather;

/// <summary>
/// MET Norway's Frost observations API (issue #348 §3, §8): for each measure group the nearest station that
/// reports it within that group's reach (<see cref="WeatherRules.StationMaxDistanceKm"/>), then daily values from
/// every chosen station in one request. The client id is a Basic-auth username attached to this client's
/// requests only — it never reaches the forecast host (AC 6).
/// </summary>
public sealed class FrostObservationSource(HttpClient http) : IWeatherObservationSource
{
    public const string BaseAddress = "https://frost.met.no/";

    /// <summary>1 MB after decompression (§7.8).</summary>
    public const int MaxResponseBytes = 1024 * 1024;

    /// <summary>Candidates per lookup: enough that the height rule rarely leaves temperature with none.</summary>
    private const int Candidates = 10;

    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(10);

    /// <summary>The element a station must report to serve a group — the one the lookup filters on.</summary>
    public static string LookupElement(WeatherMeasureGroup group) => group switch
    {
        WeatherMeasureGroup.Precipitation => "sum(precipitation_amount P1D)",
        WeatherMeasureGroup.Wind => "mean(wind_speed P1D)",
        _ => "mean(air_temperature P1D)",
    };

    /// <summary>Every element a group's station is asked for.</summary>
    public static IReadOnlyList<string> Elements(WeatherMeasureGroup group) => group switch
    {
        WeatherMeasureGroup.Precipitation => ["sum(precipitation_amount P1D)"],
        WeatherMeasureGroup.Wind => ["mean(wind_speed P1D)", "max(wind_speed_of_gust P1D)"],
        _ => ["mean(air_temperature P1D)", "min(air_temperature P1D)", "max(air_temperature P1D)"],
    };

    public async Task<StationLookupResult> FindStationAsync(
        WeatherSiteQuery site, WeatherMeasureGroup group, string contact, string clientId, CancellationToken cancellationToken)
    {
        var uri = string.Create(CultureInfo.InvariantCulture,
            $"sources/v0.jsonld?types=SensorSystem&elements={Uri.EscapeDataString(LookupElement(group))}&geometry={Uri.EscapeDataString($"nearest(POINT({site.Longitude:0.###} {site.Latitude:0.###}))")}&nearestmaxcount={Candidates}&fields=id,name,distance,masl");

        using var request = Request(uri, contact, clientId);
        using var response = await WeatherHttp.SendAsync(http, request, MaxResponseBytes, Budget, cancellationToken);

        // Frost answers "nothing matched" with a 404 or 412 rather than an empty list.
        if (response.Status is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.PreconditionFailed)
        {
            return new StationLookupResult(true, null);
        }

        if (response.FailureKind is { } failure)
        {
            return new StationLookupResult(false, null, failure, response.RetryAfter);
        }

        var candidates = WeatherHttp.Array(response.Body!.RootElement, "data").Select(source => new FoundStation(
            WeatherHttp.String(source, "id") ?? "",
            Name(WeatherHttp.String(source, "name")),
            WeatherHttp.Number(source, "distance") ?? double.MaxValue,
            WeatherHttp.Number(source, "masl") is { } masl ? (int)Math.Round(masl) : null));
        return new StationLookupResult(true, Choose(candidates, group, site.AltitudeMeters));
    }

    /// <summary>
    /// The nearest candidate in the group's reach — and, for temperature, within
    /// <see cref="WeatherRules.TemperatureStationMaxHeightDifferenceMeters"/> of the place when both heights are known.
    /// </summary>
    internal static FoundStation? Choose(IEnumerable<FoundStation> candidates, WeatherMeasureGroup group, int? siteAltitude) =>
        candidates
            .Where(c => c.Id.Length is > 0 and <= WeatherRules.StationIdMaxLength && c.DistanceKm <= WeatherRules.StationMaxDistanceKm(group))
            .Where(c => group != WeatherMeasureGroup.Temperature || siteAltitude is not { } height || c.AltitudeMeters is not { } masl
                || Math.Abs(masl - height) <= WeatherRules.TemperatureStationMaxHeightDifferenceMeters)
            .OrderBy(c => c.DistanceKm)
            .FirstOrDefault();

    private static string? Name(string? name) =>
        string.IsNullOrWhiteSpace(name) ? null : name.Trim()[..Math.Min(name.Trim().Length, WeatherRules.StationNameMaxLength)];

    public async Task<ObservationFetchResult> FetchDailyAsync(
        IReadOnlyCollection<string> stationIds, IReadOnlyCollection<string> elements, DateOnly from, DateOnly to,
        string contact, string clientId, CancellationToken cancellationToken)
    {
        // Every station in one request (Frost takes a list of sources), so a place costs one request however many
        // stations its groups use. referencetime is end-exclusive in Frost; timeoffsets PT0H for temperature and
        // wind, PT6H for the 06–06 UTC precipitation day (§8).
        var uri = string.Create(CultureInfo.InvariantCulture,
            $"observations/v0.jsonld?sources={Uri.EscapeDataString(string.Join(',', stationIds))}&referencetime={from:yyyy-MM-dd}/{to.AddDays(1):yyyy-MM-dd}&elements={Uri.EscapeDataString(string.Join(',', elements))}&timeoffsets=PT0H,PT6H&levels=default");

        using var request = Request(uri, contact, clientId);
        using var response = await WeatherHttp.SendAsync(http, request, MaxResponseBytes, Budget, cancellationToken);

        if (response.Status is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.PreconditionFailed)
        {
            return new ObservationFetchResult(true, []);
        }

        if (response.FailureKind is { } failure)
        {
            return new ObservationFetchResult(false, [], failure, response.RetryAfter);
        }

        return new ObservationFetchResult(true, Parse(response.Body!.RootElement));
    }

    internal static IReadOnlyList<ObservationDayData> Parse(JsonElement root)
    {
        var days = new SortedDictionary<(string Station, DateOnly Date), Dictionary<string, decimal?>>();
        foreach (var item in WeatherHttp.Array(root, "data"))
        {
            if (!DateTime.TryParse(WeatherHttp.String(item, "referenceTime"), CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var reference))
            {
                continue;
            }

            // "SN18700:0" — the station and its sensor-system index; the station is what was asked for.
            var station = WeatherHttp.String(item, "sourceId") is { Length: > 0 } source ? source.Split(':')[0] : "";
            var date = DateOnly.FromDateTime(reference);
            if (!days.TryGetValue((station, date), out var values))
            {
                days[(station, date)] = values = new Dictionary<string, decimal?>(StringComparer.Ordinal);
            }

            foreach (var observation in WeatherHttp.Array(item, "observations"))
            {
                var element = WeatherHttp.String(observation, "elementId");
                var offset = WeatherHttp.String(observation, "timeOffset") ?? "PT0H";
                var value = WeatherHttp.Number(observation, "value");
                if (element is null || value is null)
                {
                    continue;
                }

                var wantOffset = element.StartsWith("sum(precipitation_amount", StringComparison.Ordinal) ? "PT6H" : "PT0H";
                if (string.Equals(offset, wantOffset, StringComparison.Ordinal))
                {
                    values[element] = value is { } v ? (decimal)v : null;
                }
            }
        }

        return days
            .Select(day => new ObservationDayData(
                day.Key.Date,
                Temp(day.Value, "mean(air_temperature P1D)"),
                Temp(day.Value, "min(air_temperature P1D)"),
                Temp(day.Value, "max(air_temperature P1D)"),
                Wind(day.Value, "mean(wind_speed P1D)"),
                Wind(day.Value, "max(wind_speed_of_gust P1D)"),
                Get(day.Value, "sum(precipitation_amount P1D)", 0, 500))
            {
                StationId = day.Key.Station,
            })
            .Where(day => day.MeanTempC is not null || day.MinTempC is not null || day.MaxTempC is not null
                || day.PrecipitationMm is not null || day.MeanWindSpeedMps is not null || day.MaxWindGustMps is not null)
            .ToList();
    }

    private static decimal? Temp(Dictionary<string, decimal?> values, string key) => Get(values, key, -90, 60);

    private static decimal? Wind(Dictionary<string, decimal?> values, string key) => Get(values, key, 0, 120);

    private static decimal? Get(Dictionary<string, decimal?> values, string key, double min, double max) =>
        values.TryGetValue(key, out var value) ? WeatherHttp.InRange((double?)value, min, max) : null;

    private static HttpRequestMessage Request(string uri, string contact, string clientId)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        WeatherHttp.SetUserAgent(request, contact);
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes(clientId + ":")));
        return request;
    }
}
