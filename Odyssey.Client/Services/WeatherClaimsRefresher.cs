using System.Net;
using Microsoft.AspNetCore.Components.Authorization;

namespace Odyssey.Client.Services;

/// <summary>
/// The weather points page's claims refresher (issue #388 §5, #387 §7.2). Since issue #509 (v5 §5) it is an adapter
/// over the shared <see cref="IClaimsRefresher"/>, so its callers are unchanged and the bounds — single-flight, at
/// most once per signed-in session — are the shared ones.
/// </summary>
public interface IWeatherClaimsRefresher
{
    /// <summary>
    /// Call with the status of a user-initiated <c>/api/weather/points…</c> call. Returns whether a refresh ran
    /// and the cached session changed; on anything but a <c>403</c> it does nothing.
    /// </summary>
    Task<bool> OnResponseAsync(HttpStatusCode status);
}

/// <inheritdoc cref="IWeatherClaimsRefresher" />
public sealed class WeatherClaimsRefresher : IWeatherClaimsRefresher, IDisposable
{
    private readonly IClaimsRefresher inner;
    private readonly ClaimsRefresher? owned;

    public WeatherClaimsRefresher(IClaimsRefresher inner) => this.inner = inner;

    /// <summary>A refresher over its own <see cref="ClaimsRefresher"/>, for tests.</summary>
    internal WeatherClaimsRefresher(AuthenticationStateProvider auth, Func<Task<bool>> tryRefresh)
    {
        owned = new ClaimsRefresher(auth, tryRefresh);
        inner = owned;
    }

    public Task<bool> OnResponseAsync(HttpStatusCode status) => inner.OnResponseAsync(status);

    public void Dispose() => owned?.Dispose();
}
