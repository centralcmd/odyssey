using Microsoft.AspNetCore.Components.Authorization;
using Odyssey.ApiClient;
using Odyssey.ApiClient.Resources;
using Odyssey.Dtos.Journal;

namespace Odyssey.Client.Services;

/// <summary>
/// The name-sorted weather-point picker list (<c>GET /api/weather/points/options</c>, issue #388 §5), shared by
/// the dashboard card, Preferences and System settings.
/// </summary>
/// <remarks>
/// <para>
/// Only the variant <b>without</b> <c>nearLocationId</c> is cached: the near variant carries distances, which a
/// moved place or point changes, so the link dialog asks <see cref="IWeatherApiClient.OptionsAsync"/> itself on
/// every open.
/// </para>
/// <para>
/// A failure is never cached. Any point write in this session calls <see cref="Invalidate"/>, and every
/// authentication-state change clears it, so the list never outlives the signed-in user who read it.
/// </para>
/// </remarks>
public interface IWeatherPointOptionsCache
{
    Task<ApiResult<WeatherPointOptions>> GetAsync(CancellationToken ct = default);

    void Invalidate();
}

/// <inheritdoc cref="IWeatherPointOptionsCache" />
public sealed class WeatherPointOptionsCache : IWeatherPointOptionsCache, IDisposable
{
    private readonly IWeatherApiClient weather;
    private readonly AuthenticationStateProvider auth;
    private Task<ApiResult<WeatherPointOptions>>? pending;

    public WeatherPointOptionsCache(IWeatherApiClient weather, AuthenticationStateProvider auth)
    {
        this.weather = weather;
        this.auth = auth;
        auth.AuthenticationStateChanged += OnAuthenticationStateChanged;
    }

    public async Task<ApiResult<WeatherPointOptions>> GetAsync(CancellationToken ct = default)
    {
        var task = pending ??= weather.OptionsAsync(ct: CancellationToken.None);
        var result = await task.WaitAsync(ct);
        if (!result.IsSuccess && ReferenceEquals(pending, task))
        {
            pending = null;
        }

        return result;
    }

    public void Invalidate() => pending = null;

    private void OnAuthenticationStateChanged(Task<AuthenticationState> state) => Invalidate();

    public void Dispose() => auth.AuthenticationStateChanged -= OnAuthenticationStateChanged;
}
