using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Odyssey.Client.Auth;

namespace Odyssey.Client.Services;

/// <summary>
/// Picks up claims the server refreshed under a live session (issue #388 §5, #387 §7.2): a <c>403</c> from a
/// user-initiated weather-point call asks <see cref="CookieAuthenticationStateProvider.TryRefreshAsync"/> once, so
/// a session issued before the claim rename redraws its controls without a reload or a sign-in.
/// </summary>
/// <remarks>
/// Bounded on purpose: <b>single-flight</b> (concurrent <c>403</c>s await the one refresh in flight), <b>at most once
/// per signed-in session</b> (the flag is cleared only when the signed-in user changes — a sign-in or a sign-out —
/// never by the refresh's own <c>AuthenticationStateChanged</c>), and therefore <b>never from a call the refresh
/// caused</b>: a claim that is genuinely absent ends at one ordinary <c>403</c> toast, not a loop.
/// </remarks>
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
    private readonly AuthenticationStateProvider auth;
    private readonly Func<Task<bool>> tryRefresh;
    private readonly Lock gate = new();
    private Task<bool>? inFlight;
    private bool used;
    private string? sessionKey;

    public WeatherClaimsRefresher(AuthenticationStateProvider auth)
        : this(auth, auth is CookieAuthenticationStateProvider cookie ? cookie.TryRefreshAsync : static () => Task.FromResult(false))
    {
    }

    internal WeatherClaimsRefresher(AuthenticationStateProvider auth, Func<Task<bool>> tryRefresh)
    {
        this.auth = auth;
        this.tryRefresh = tryRefresh;
        auth.AuthenticationStateChanged += OnAuthenticationStateChanged;
    }

    public async Task<bool> OnResponseAsync(HttpStatusCode status)
    {
        if (status != HttpStatusCode.Forbidden)
            return false;

        Task<bool> task;
        lock (gate)
        {
            if (inFlight is not null)
            {
                task = inFlight;
            }
            else if (used)
            {
                return false;
            }
            else
            {
                if (sessionKey is null)
                {
                    // The session the flag belongs to, captured before the refresh can change the claims.
                    sessionKey = KeyOf(auth.GetAuthenticationStateAsync());
                }

                used = true;
                task = inFlight = RunAsync();
            }
        }

        return await task;
    }

    private async Task<bool> RunAsync()
    {
        try
        {
            return await tryRefresh();
        }
        catch (Exception)
        {
            return false;
        }
        finally
        {
            lock (gate)
            {
                inFlight = null;
            }
        }
    }

    /// <summary>The flag resets only when the signed-in user changes: a sign-in, a sign-out or another account.</summary>
    private async void OnAuthenticationStateChanged(Task<AuthenticationState> state)
    {
        string? key;
        try
        {
            key = Identify((await state).User);
        }
        catch (Exception)
        {
            return;
        }

        lock (gate)
        {
            if (sessionKey is not null && !string.Equals(sessionKey, key, StringComparison.Ordinal))
            {
                used = false;
            }

            sessionKey = key;
        }
    }

    private static string? KeyOf(Task<AuthenticationState> state) =>
        state.IsCompletedSuccessfully ? Identify(state.Result.User) : null;

    private static string Identify(ClaimsPrincipal user) =>
        user.Identity?.IsAuthenticated == true
            ? "in:" + (user.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? user.Identity.Name ?? "")
            : "out";

    public void Dispose() => auth.AuthenticationStateChanged -= OnAuthenticationStateChanged;
}
