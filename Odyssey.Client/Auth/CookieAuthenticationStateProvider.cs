using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;

using Odyssey.ApiClient.Auth;

namespace Odyssey.Client.Auth;

/// <summary>
/// Resolves the cookie session once and serves every later read from that one answer, until
/// <see cref="RefreshAsync"/> replaces it (issue #250).
/// </summary>
/// <remarks>
/// <para>
/// Resolving costs two sequential round-trips (<c>manage/info</c>, then <c>auth/claims</c>), and before
/// this cache every <see cref="GetAuthenticationStateAsync"/> paid them — the layout, the page and each
/// dialog open, before loading any data of their own. The session cannot change underneath the app
/// without one of three things happening: a sign-in or a gate (which call <see cref="RefreshAsync"/>),
/// a sign-out (which reloads the app), or the cookie expiring — which surfaces as a <c>401</c> on the
/// next domain call, and <see cref="UnauthorizedHandler"/> turns that into a reload to the sign-in page.
/// The claims cannot drift either: they are baked into the auth cookie at sign-in, so a fresh probe of
/// the same cookie would return the same set. A cached answer is therefore never stale in a way a
/// fresh probe would have caught.
/// </para>
/// <para>
/// <b>Only a definitive answer is cached.</b> An <see cref="AuthSessionStatus.Unavailable"/> probe is
/// retried briefly, and if it still fails this read is answered anonymous <em>without</em> caching it,
/// so the next read probes again rather than a network blip signing the user out for the rest of the
/// app's lifetime.
/// </para>
/// </remarks>
public sealed class CookieAuthenticationStateProvider : AuthenticationStateProvider
{
    private static readonly AuthenticationState Anonymous = new(new ClaimsPrincipal(new ClaimsIdentity()));

    /// <summary>The waits between attempts when the probe has no definitive answer.</summary>
    internal static readonly IReadOnlyList<TimeSpan> DefaultRetryDelays =
        [TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(1)];

    private readonly AuthApiClient authApiClient;
    private readonly IReadOnlyList<TimeSpan> retryDelays;
    private readonly Lock gate = new();
    private Task<Resolution>? current;

    public CookieAuthenticationStateProvider(AuthApiClient authApiClient)
        : this(authApiClient, DefaultRetryDelays)
    {
    }

    internal CookieAuthenticationStateProvider(AuthApiClient authApiClient, IReadOnlyList<TimeSpan> retryDelays)
    {
        this.authApiClient = authApiClient;
        this.retryDelays = retryDelays;
    }

    public override async Task<AuthenticationState> GetAuthenticationStateAsync()
    {
        Task<Resolution> pending;
        lock (gate)
        {
            pending = current ??= ResolveAsync();
        }

        var resolution = await pending;
        if (!resolution.Definitive)
        {
            lock (gate)
            {
                // Evict only the probe this read awaited: a RefreshAsync that started meanwhile owns the
                // slot now and must not be thrown away.
                if (ReferenceEquals(current, pending))
                {
                    current = null;
                }
            }
        }

        return resolution.State;
    }

    /// <summary>
    /// Discards the cached session and resolves it again — after a sign-in, or a gate that changes the
    /// caller's claims. Completes once the new state is known, so a caller that navigates next does so
    /// with it in place.
    /// </summary>
    public Task RefreshAsync()
    {
        lock (gate)
        {
            current = ResolveAsync();
        }

        var state = GetAuthenticationStateAsync();
        NotifyAuthenticationStateChanged(state);
        return state;
    }

    private async Task<Resolution> ResolveAsync()
    {
        for (var attempt = 0; ; attempt++)
        {
            var session = await authApiClient.GetSessionAsync();
            switch (session.Status)
            {
                case AuthSessionStatus.Authenticated:
                    return new Resolution(
                        new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity(session.Claims, "Cookies"))),
                        Definitive: true);

                case AuthSessionStatus.Anonymous:
                    return new Resolution(Anonymous, Definitive: true);
            }

            if (attempt >= retryDelays.Count)
            {
                return new Resolution(Anonymous, Definitive: false);
            }

            await Task.Delay(retryDelays[attempt]);
        }
    }

    private sealed record Resolution(AuthenticationState State, bool Definitive);
}
