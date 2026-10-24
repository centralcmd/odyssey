using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Odyssey.Dtos.Journal;

namespace Odyssey.Core.Weather;

/// <summary>
/// MET Norway's Frost observations API (issue #348 §3, §8): the nearest station with daily air temperature
/// within 25 km, then daily values. The client id is a Basic-auth username attached to this client's
/// requests only — it never reaches the forecast host (AC 6).
/// </summary>
public sealed class FrostObservationSource(HttpClient http) : IWeatherObservationSource
{
    public const string BaseAddress = "https://frost.met.no/";

    /// <summary>1 MB after decompression (§7.8).</summary>
    public const int MaxResponseBytes = 1024 * 1024;

    public const double StationMaxDistanceKm = 25;

    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(10);

    private const string TemperatureElement = "mean(air_temperature P1D)";

    private static readonly string[] Elements =
    [
        TemperatureElement,
        "min(air_temperature P1D)",
        "max(air_temperature P1D)",
        "sum(precipitation_amount P1D)",
        "mean(wind_speed P1D)",
        "max(wind_speed_of_gust P1D)",
    ];

    public async Task<StationLookupResult> FindStationAsync(
        WeatherSiteQuery site, string contact, string clientId, CancellationToken cancellationToken)
    {
        var uri = string.Create(CultureInfo.InvariantCulture,
            $"sources/v0.jsonld?types=SensorSystem&elements={Uri.EscapeDataString(TemperatureElement)}&geometry={Uri.EscapeDataString($"nearest(POINT({site.Longitude:0.###} {site.Latitude:0.###}))")}&nearestmaxcount=5&fields=id,distance");

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

        foreach (var source in WeatherHttp.Array(response.Body!.RootElement, "data"))
        {
            var id = WeatherHttp.String(source, "id");
            var distance = WeatherHttp.Number(source, "distance");
            if (id is not null && id.Length <= WeatherRules.StationIdMaxLength && distance is { } km && km <= StationMaxDistanceKm)
            {
                return new StationLookupResult(true, id);
            }
        }

        return new StationLookupResult(true, null);
    }

    public async Task<ObservationFetchResult> FetchDailyAsync(
        string stationId, DateOnly from, DateOnly to, string contact, string clientId, CancellationToken cancellationToken)
    {
        // referencetime is end-exclusive in Frost; timeoffsets PT0H for temperature and wind, PT6H for the
        // 06–06 UTC precipitation day (§8).
        var uri = string.Create(CultureInfo.InvariantCulture,
            $"observations/v0.jsonld?sources={Uri.EscapeDataString(stationId)}&referencetime={from:yyyy-MM-dd}/{to.AddDays(1):yyyy-MM-dd}&elements={Uri.EscapeDataString(string.Join(',', Elements))}&timeoffsets=PT0H,PT6H&levels=default");

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
        var days = new SortedDictionary<DateOnly, Dictionary<string, decimal?>>();
        foreach (var item in WeatherHttp.Array(root, "data"))
        {
            if (!DateTime.TryParse(WeatherHttp.String(item, "referenceTime"), CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var reference))
            {
                continue;
            }

            var date = DateOnly.FromDateTime(reference);
            if (!days.TryGetValue(date, out var values))
            {
                days[date] = values = new Dictionary<string, decimal?>(StringComparer.Ordinal);
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
                day.Key,
                Temp(day.Value, "mean(air_temperature P1D)"),
                Temp(day.Value, "min(air_temperature P1D)"),
                Temp(day.Value, "max(air_temperature P1D)"),
                Wind(day.Value, "mean(wind_speed P1D)"),
                Wind(day.Value, "max(wind_speed_of_gust P1D)"),
                Get(day.Value, "sum(precipitation_amount P1D)", 0, 500)))
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
