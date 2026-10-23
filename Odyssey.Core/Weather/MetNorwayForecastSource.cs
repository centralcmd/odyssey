using System.Globalization;
using System.Text.RegularExpressions;
using Odyssey.Dtos.Journal;

namespace Odyssey.Core.Weather;

/// <summary>
/// MET Norway's Locationforecast 2.0 <c>/complete</c> (issue #348 §3, §8). The base address is a
/// compile-time constant on the named client, redirects are not followed, and every logger is removed from
/// the client — the query string carries the site's coordinates.
/// </summary>
public sealed partial class MetNorwayForecastSource(HttpClient http) : IWeatherForecastSource
{
    public const string BaseAddress = "https://api.met.no/";

    /// <summary>2 MB after decompression (§7.8).</summary>
    public const int MaxResponseBytes = 2 * 1024 * 1024;

    public async Task<ForecastFetchResult> FetchAsync(
        WeatherSiteQuery site, string contact, string? ifModifiedSince, TimeSpan budget, CancellationToken cancellationToken)
    {
        var uri = string.Create(CultureInfo.InvariantCulture,
            $"weatherapi/locationforecast/2.0/complete?lat={site.Latitude:0.###}&lon={site.Longitude:0.###}");
        if (site.AltitudeMeters is { } altitude)
        {
            uri += string.Create(CultureInfo.InvariantCulture, $"&altitude={altitude}");
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        WeatherHttp.SetUserAgent(request, contact);
        if (ifModifiedSince is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Modified-Since", ifModifiedSince);
        }

        using var response = await WeatherHttp.SendAsync(http, request, MaxResponseBytes, budget, cancellationToken);
        if (response.Status == System.Net.HttpStatusCode.NotModified)
        {
            return new ForecastFetchResult(WeatherFetchOutcome.NotModified, ExpiresUtc: response.Expires,
                LastModified: response.LastModified ?? ifModifiedSince);
        }

        if (response.FailureKind is { } failure)
        {
            return new ForecastFetchResult(WeatherFetchOutcome.Failed, FailureKind: failure, RetryAfter: response.RetryAfter);
        }

        var payload = Parse(response.Body!.RootElement);
        return payload is null
            ? new ForecastFetchResult(WeatherFetchOutcome.Failed, FailureKind: WeatherFetchFailureKind.MalformedPayload)
            : new ForecastFetchResult(WeatherFetchOutcome.Ok, payload, response.Expires, response.LastModified);
    }

    /// <summary>
    /// Maps <c>properties.timeseries[]</c> onto steps (§8). Out-of-range values become null; a payload with no
    /// valid step, or without <c>meta.updated_at</c>, is malformed.
    /// </summary>
    internal static ForecastPayload? Parse(System.Text.Json.JsonElement root)
    {
        var properties = WeatherHttp.Object(root, "properties");
        if (properties is null)
        {
            return null;
        }

        var meta = WeatherHttp.Object(properties.Value, "meta");
        if (!DateTime.TryParse(WeatherHttp.String(meta, "updated_at"), CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var updatedAt))
        {
            return null;
        }

        var steps = new List<ForecastStepData>();
        var seen = new HashSet<DateTime>();
        foreach (var entry in WeatherHttp.Array(properties.Value, "timeseries"))
        {
            if (!DateTime.TryParse(WeatherHttp.String(entry, "time"), CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var time)
                || !seen.Add(time))
            {
                continue;
            }

            var data = WeatherHttp.Object(entry, "data");
            if (data is null)
            {
                continue;
            }

            var instant = WeatherHttp.Object(WeatherHttp.Object(data.Value, "instant") ?? default, "details");
            var (periodHours, period) =
                WeatherHttp.Object(data.Value, "next_1_hours") is { } next1 ? ((byte)1, next1)
                : WeatherHttp.Object(data.Value, "next_6_hours") is { } next6 ? ((byte)6, next6)
                : WeatherHttp.Object(data.Value, "next_12_hours") is { } next12 ? ((byte)12, next12)
                : ((byte)0, default);

            var periodDetails = periodHours > 0 ? WeatherHttp.Object(period, "details") : null;
            var symbol = periodHours > 0 ? WeatherHttp.String(WeatherHttp.Object(period, "summary"), "symbol_code") : null;
            var six = WeatherHttp.Object(WeatherHttp.Object(data.Value, "next_6_hours") ?? default, "details");

            var step = new ForecastStepData(
                time,
                periodHours == 0 ? (byte)1 : periodHours,
                Temperature(WeatherHttp.Number(instant, "air_temperature")),
                Percent(WeatherHttp.Number(instant, "relative_humidity")),
                Percent(WeatherHttp.Number(instant, "cloud_area_fraction")),
                WeatherHttp.InRange(WeatherHttp.Number(instant, "air_pressure_at_sea_level"), 800, 1100),
                Wind(WeatherHttp.Number(instant, "wind_speed")),
                Wind(WeatherHttp.Number(instant, "wind_speed_of_gust")),
                WeatherHttp.InRange(WeatherHttp.Number(instant, "wind_from_direction"), 0, 360),
                WeatherHttp.InRange(WeatherHttp.Number(instant, "ultraviolet_index_clear_sky"), 0, 20),
                symbol is not null && SymbolPattern().IsMatch(symbol) ? symbol : null,
                WeatherHttp.InRange(WeatherHttp.Number(periodDetails, "precipitation_amount"), 0, 500),
                Percent(WeatherHttp.Number(periodDetails, "probability_of_precipitation")),
                Temperature(WeatherHttp.Number(six, "air_temperature_min")),
                Temperature(WeatherHttp.Number(six, "air_temperature_max")));

            if (step.AirTemperatureC is not null || step.SymbolCode is not null || step.PrecipitationMm is not null)
            {
                steps.Add(step);
            }
        }

        return steps.Count == 0 ? null : new ForecastPayload(DateTime.SpecifyKind(updatedAt, DateTimeKind.Utc), steps);
    }

    private static decimal? Temperature(double? value) => WeatherHttp.InRange(value, -90, 60);

    private static decimal? Wind(double? value) => WeatherHttp.InRange(value, 0, 120);

    private static decimal? Percent(double? value) => WeatherHttp.InRange(value, 0, 100);

    [GeneratedRegex("^[a-z_]{1,40}$", RegexOptions.CultureInvariant)]
    private static partial Regex SymbolPattern();
}
