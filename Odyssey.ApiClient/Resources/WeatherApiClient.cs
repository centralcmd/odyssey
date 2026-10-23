using Odyssey.Dtos;
using Odyssey.Dtos.Journal;

namespace Odyssey.ApiClient.Resources;

/// <summary>
/// Typed client for weather (issue #348): the reads, the picker and the caller's own place, and the
/// Admin's per-place settings. Every read carries MET Norway's attribution, which the caller must show.
/// </summary>
/// <remarks>
/// Failures callers branch on: <c>409</c> with a <c>code</c> (<c>location-changed</c>,
/// <c>fetch-in-progress</c>, <c>purge-in-progress</c>, …), <c>429</c> on apply, refresh and delete, and
/// <c>503</c> <c>weather-not-configured</c> on refresh. A <c>504</c> from a proxy on refresh or delete
/// means the work may still be running — re-read rather than report a failure.
/// </remarks>
public interface IWeatherApiClient
{
    Task<ApiResult<WeatherCurrentResponse>> CurrentAsync(Guid? locationId = null, CancellationToken ct = default);

    Task<ApiResult<WeatherDailyResponse>> DailyAsync(DateOnly from, DateOnly to, Guid? locationId = null, CancellationToken ct = default);

    Task<ApiResult<WeatherHourlyResponse>> HourlyAsync(DateTime fromUtc, DateTime toUtc, Guid? locationId = null, CancellationToken ct = default);

    Task<ApiResult<WeatherLocationOptions>> OptionsAsync(CancellationToken ct = default);

    Task<ApiResult<WeatherPreferences>> PreferencesAsync(CancellationToken ct = default);

    Task<ApiResult> SetPreferenceAsync(Guid? defaultLocationId, CancellationToken ct = default);

    /// <summary>The Admin list — every place with weather settings.</summary>
    Task<ApiResult<List<ExistingWeatherLocation>>> ListLocationsAsync(CancellationToken ct = default);

    Task<ApiResult<ExistingWeatherLocation>> GetLocationAsync(Guid locationId, CancellationToken ct = default);

    Task<ApiResult<ExistingWeatherLocation>> UpsertLocationAsync(Guid locationId, UpsertWeatherLocation request, CancellationToken ct = default);

    Task<ApiResult<ExistingWeatherLocation>> ApplyLocationAsync(Guid locationId, string expectedLocationFingerprint, CancellationToken ct = default);

    Task<ApiResult<WeatherRefreshResult>> RefreshAsync(Guid locationId, CancellationToken ct = default);

    /// <summary>Deletes every weather row of the place. The location itself is untouched.</summary>
    Task<ApiResult> PurgeAsync(Guid locationId, CancellationToken ct = default);
}

/// <inheritdoc cref="IWeatherApiClient" />
public sealed class WeatherApiClient(IOdysseyApi api) : IWeatherApiClient
{
    private const string Base = "api/weather";

    public Task<ApiResult<WeatherCurrentResponse>> CurrentAsync(Guid? locationId = null, CancellationToken ct = default) =>
        api.GetAsync<WeatherCurrentResponse>(PagedQuery.For($"{Base}/current").Add("locationId", locationId?.ToString()).Build(), ct);

    public Task<ApiResult<WeatherDailyResponse>> DailyAsync(DateOnly from, DateOnly to, Guid? locationId = null, CancellationToken ct = default) =>
        api.GetAsync<WeatherDailyResponse>(PagedQuery.For($"{Base}/daily")
            .Add("from", from.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture))
            .Add("to", to.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture))
            .Add("locationId", locationId?.ToString())
            .Build(), ct);

    public Task<ApiResult<WeatherHourlyResponse>> HourlyAsync(DateTime fromUtc, DateTime toUtc, Guid? locationId = null, CancellationToken ct = default) =>
        api.GetAsync<WeatherHourlyResponse>(PagedQuery.For($"{Base}/hourly")
            .Add("fromUtc", fromUtc.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture))
            .Add("toUtc", toUtc.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture))
            .Add("locationId", locationId?.ToString())
            .Build(), ct);

    public Task<ApiResult<WeatherLocationOptions>> OptionsAsync(CancellationToken ct = default) =>
        api.GetAsync<WeatherLocationOptions>($"{Base}/locations/options", ct);

    public Task<ApiResult<WeatherPreferences>> PreferencesAsync(CancellationToken ct = default) =>
        api.GetAsync<WeatherPreferences>($"{Base}/preferences", ct);

    public Task<ApiResult> SetPreferenceAsync(Guid? defaultLocationId, CancellationToken ct = default) =>
        api.SendAsync(HttpMethod.Put, $"{Base}/preferences", new UpdateWeatherPreferences { DefaultLocationId = defaultLocationId }, ct);

    public Task<ApiResult<List<ExistingWeatherLocation>>> ListLocationsAsync(CancellationToken ct = default) =>
        api.GetAllAsync<ExistingWeatherLocation>(PagedQuery.For($"{Base}/locations").Build(), ct);

    public Task<ApiResult<ExistingWeatherLocation>> GetLocationAsync(Guid locationId, CancellationToken ct = default) =>
        api.GetAsync<ExistingWeatherLocation>($"{Base}/locations/{locationId}", ct);

    public Task<ApiResult<ExistingWeatherLocation>> UpsertLocationAsync(Guid locationId, UpsertWeatherLocation request, CancellationToken ct = default) =>
        api.SendAsync<ExistingWeatherLocation>(HttpMethod.Put, $"{Base}/locations/{locationId}", request, ct);

    public Task<ApiResult<ExistingWeatherLocation>> ApplyLocationAsync(Guid locationId, string expectedLocationFingerprint, CancellationToken ct = default) =>
        api.SendAsync<ExistingWeatherLocation>(HttpMethod.Post, $"{Base}/locations/{locationId}/apply-location",
            new ApplyWeatherLocation { ExpectedLocationFingerprint = expectedLocationFingerprint }, ct);

    public Task<ApiResult<WeatherRefreshResult>> RefreshAsync(Guid locationId, CancellationToken ct = default) =>
        api.SendAsync<WeatherRefreshResult>(HttpMethod.Post, $"{Base}/locations/{locationId}/refresh", null, ct);

    public Task<ApiResult> PurgeAsync(Guid locationId, CancellationToken ct = default) =>
        api.SendAsync(HttpMethod.Delete, $"{Base}/locations/{locationId}", null, ct);
}
