using Odyssey.Dtos.Journal;

namespace Odyssey.Core.Weather;

/// <summary>The position weather sends: always a frozen site, never the live location (§3).</summary>
public sealed record WeatherSiteQuery(decimal Latitude, decimal Longitude, int? AltitudeMeters)
{
    public override string ToString() => nameof(WeatherSiteQuery) + " { (redacted) }";
}

/// <summary>One parsed Locationforecast time step, already range-checked (§8).</summary>
public sealed record ForecastStepData(
    DateTime TimeUtc,
    byte PeriodHours,
    decimal? AirTemperatureC,
    decimal? RelativeHumidityPct,
    decimal? CloudAreaFractionPct,
    decimal? AirPressureHpa,
    decimal? WindSpeedMps,
    decimal? WindGustMps,
    decimal? WindFromDirectionDeg,
    decimal? UvIndexClearSky,
    string? SymbolCode,
    decimal? PrecipitationMm,
    decimal? PrecipitationProbabilityPct,
    decimal? TempMinC,
    decimal? TempMaxC);

public sealed record ForecastPayload(DateTime UpdatedAtUtc, IReadOnlyList<ForecastStepData> Steps);

public enum WeatherFetchOutcome
{
    Ok,
    NotModified,
    Failed,
}

/// <summary>
/// What a forecast request produced. A failure carries only its kind and, for a 429/503, how long the
/// provider asked us to wait — never provider text (§7.12).
/// </summary>
public sealed record ForecastFetchResult(
    WeatherFetchOutcome Outcome,
    ForecastPayload? Payload = null,
    DateTime? ExpiresUtc = null,
    string? LastModified = null,
    WeatherFetchFailureKind? FailureKind = null,
    TimeSpan? RetryAfter = null);

/// <summary>Locationforecast 2.0 <c>/complete</c> (issue #348 §3).</summary>
public interface IWeatherForecastSource
{
    Task<ForecastFetchResult> FetchAsync(
        WeatherSiteQuery site, string contact, string? ifModifiedSince, TimeSpan budget, CancellationToken cancellationToken);
}

public sealed record ObservationDayData(
    DateOnly Date,
    decimal? MeanTempC,
    decimal? MinTempC,
    decimal? MaxTempC,
    decimal? MeanWindSpeedMps,
    decimal? MaxWindGustMps,
    decimal? PrecipitationMm);

public sealed record StationLookupResult(bool Succeeded, string? StationId, WeatherFetchFailureKind? FailureKind = null, TimeSpan? RetryAfter = null);

public sealed record ObservationFetchResult(
    bool Succeeded,
    IReadOnlyList<ObservationDayData> Days,
    WeatherFetchFailureKind? FailureKind = null,
    TimeSpan? RetryAfter = null);

/// <summary>Frost <c>/sources</c> and <c>/observations</c>. Called by the scheduler only — no read ever reaches it.</summary>
public interface IWeatherObservationSource
{
    /// <summary>The nearest station with daily air temperature within 25 km, or a null id when there is none.</summary>
    Task<StationLookupResult> FindStationAsync(
        WeatherSiteQuery site, string contact, string clientId, CancellationToken cancellationToken);

    /// <summary>Daily values for <c>[from, to]</c>, inclusive. An empty range is a success with no days.</summary>
    Task<ObservationFetchResult> FetchDailyAsync(
        string stationId, DateOnly from, DateOnly to, string contact, string clientId, CancellationToken cancellationToken);
}
