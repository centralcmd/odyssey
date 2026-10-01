using System.Security.Claims;

namespace Odyssey.ApiClient.Auth;

/// <summary>What the API answered when asked who the caller is (issue #250).</summary>
public enum AuthSessionStatus
{
    /// <summary>The session is valid; <see cref="AuthSession.Claims"/> holds its claims.</summary>
    Authenticated,

    /// <summary>The API answered <c>401</c>: there is no session.</summary>
    Anonymous,

    /// <summary>
    /// No definitive answer — the request failed, was rate-limited, or the server faulted. Says nothing
    /// about the session, so it must be neither cached nor read as signed out or as zero permissions.
    /// </summary>
    Unavailable,
}

/// <param name="RetryAfter">
/// For <see cref="AuthSessionStatus.Unavailable"/> only: how long the server asked the caller to wait,
/// from a <c>Retry-After</c> header on a <c>429</c> or <c>503</c> (issue #278). <c>null</c> when it named
/// no wait.
/// </param>
public sealed record AuthSession(AuthSessionStatus Status, IReadOnlyList<Claim> Claims, TimeSpan? RetryAfter = null)
{
    public static AuthSession Anonymous { get; } = new(AuthSessionStatus.Anonymous, []);

    public static AuthSession Unavailable { get; } = new(AuthSessionStatus.Unavailable, []);

    public static AuthSession UnavailableFor(HttpResponseMessage response, DateTimeOffset now) =>
        RetryAfterOf(response, now) is { } wait ? new(AuthSessionStatus.Unavailable, [], wait) : Unavailable;

    private static TimeSpan? RetryAfterOf(HttpResponseMessage response, DateTimeOffset now)
    {
        var header = response.Headers.RetryAfter;
        var wait = header?.Delta ?? (header?.Date is { } date ? date - now : null);
        return wait > TimeSpan.Zero ? wait : null;
    }
}
