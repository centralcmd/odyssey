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
/// retried briefly, and if it still fails this read is answered with the
/// <see cref="SessionUnavailable"/> principal <em>without</em> caching it, so the next read probes again
/// rather than a network blip signing the user out for the rest of the app's lifetime.
/// </para>
/// <para>
/// <b>An unavailable answer is not an anonymous one</b> (issue #278). The router reads this provider once
/// per load, so answering anonymous sent a signed-in user to <c>/login</c> during an outage — and during a
/// <c>429</c> from the Identity limiter, exactly when signing in is refused too. The sentinel principal is
/// still unauthenticated, so nothing is authorized on it, but <c>App.razor</c> recognises it and renders
/// a retry panel instead of redirecting. Meanwhile a background loop re-probes with backoff (honouring
/// <c>Retry-After</c>) and announces the first definitive answer, so the router re-evaluates without a
/// reload.
/// </para>
/// </remarks>
public sealed class CookieAuthenticationStateProvider : AuthenticationStateProvider
{
    private static readonly AuthenticationState Anonymous = new(new ClaimsPrincipal(new ClaimsIdentity()));

    private static readonly AuthenticationState Unavailable = new(SessionUnavailable.Principal);

    /// <summary>The waits between attempts when the probe has no definitive answer.</summary>
    internal static readonly IReadOnlyList<TimeSpan> DefaultRetryDelays =
        [TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(1)];

    /// <summary>The first background re-probe's wait; each later one doubles, up to <see cref="RecoveryMaxDelay"/>.</summary>
    internal static readonly TimeSpan RecoveryInitialDelay = TimeSpan.FromSeconds(1);

    internal static readonly TimeSpan RecoveryMaxDelay = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The longest <c>Retry-After</c> honoured as given. A longer one is still waited out up to this
    /// bound, so a misconfigured header cannot park the app for an hour; the Retry button is always there.
    /// </summary>
    internal static readonly TimeSpan RetryAfterCeiling = TimeSpan.FromMinutes(5);

    private readonly AuthApiClient authApiClient;
    private readonly IReadOnlyList<TimeSpan> retryDelays;
    private readonly Func<TimeSpan, Task> recoveryDelay;
    private readonly Lock gate = new();
    private Task<Resolution>? current;
    private Task? recovery;

    // Bumped by every definitive resolution, so the recovery loop can tell that a read or a RefreshAsync
    // already got an answer and must not be overwritten by an older probe.
    private int definitiveVersion;

    public CookieAuthenticationStateProvider(AuthApiClient authApiClient)
        : this(authApiClient, DefaultRetryDelays, wait => Task.Delay(wait))
    {
    }

    internal CookieAuthenticationStateProvider(
        AuthApiClient authApiClient, IReadOnlyList<TimeSpan> retryDelays, Func<TimeSpan, Task> recoveryDelay)
    {
        this.authApiClient = authApiClient;
        this.retryDelays = retryDelays;
        this.recoveryDelay = recoveryDelay;
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

            StartRecovery(resolution.RetryAfter);
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
            var resolution = ToResolution(await authApiClient.GetSessionAsync());
            if (resolution.Definitive)
            {
                Interlocked.Increment(ref definitiveVersion);
                return resolution;
            }

            // A Retry-After longer than the remaining in-read retries would only be refused again; hand
            // it to the background loop instead of holding the router up for it.
            if (attempt >= retryDelays.Count || resolution.RetryAfter > retryDelays[attempt])
            {
                return resolution;
            }

            await Task.Delay(retryDelays[attempt]);
        }
    }

    private static Resolution ToResolution(AuthSession session) => session.Status switch
    {
        AuthSessionStatus.Authenticated => new Resolution(
            new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity(session.Claims, "Cookies"))),
            Definitive: true),
        AuthSessionStatus.Anonymous => new Resolution(Anonymous, Definitive: true),
        _ => new Resolution(Unavailable, Definitive: false, session.RetryAfter),
    };

    private void StartRecovery(TimeSpan? retryAfter)
    {
        lock (gate)
        {
            if (recovery is { IsCompleted: false })
            {
                return;
            }

            recovery = RecoverAsync(retryAfter, Volatile.Read(ref definitiveVersion));
        }
    }

    /// <summary>
    /// Re-probes until the API gives a definitive answer, then caches and announces it. One loop at a
    /// time; it ends early, announcing what is cached, if a read or a <see cref="RefreshAsync"/> got a
    /// definitive answer first.
    /// </summary>
    private async Task RecoverAsync(TimeSpan? retryAfter, int startVersion)
    {
        var backoff = RecoveryInitialDelay;
        while (true)
        {
            var wait = retryAfter is { } asked && asked > backoff
                ? (asked < RetryAfterCeiling ? asked : RetryAfterCeiling)
                : backoff;
            await recoveryDelay(wait);

            if (Volatile.Read(ref definitiveVersion) != startVersion)
            {
                AnnounceCurrent();
                return;
            }

            AuthSession session;
            try
            {
                session = await authApiClient.GetSessionAsync();
            }
            catch (Exception)
            {
                session = AuthSession.Unavailable;
            }

            var resolution = ToResolution(session);
            if (resolution.Definitive)
            {
                lock (gate)
                {
                    if (Interlocked.CompareExchange(ref definitiveVersion, startVersion + 1, startVersion) == startVersion)
                    {
                        current = Task.FromResult(resolution);
                    }
                }

                AnnounceCurrent();
                return;
            }

            retryAfter = resolution.RetryAfter;
            backoff = backoff * 2 < RecoveryMaxDelay ? backoff * 2 : RecoveryMaxDelay;
        }
    }

    private void AnnounceCurrent() => NotifyAuthenticationStateChanged(GetAuthenticationStateAsync());

    private sealed record Resolution(AuthenticationState State, bool Definitive, TimeSpan? RetryAfter = null);
}
