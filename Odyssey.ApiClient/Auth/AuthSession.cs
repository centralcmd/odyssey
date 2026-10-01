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

public sealed record AuthSession(AuthSessionStatus Status, IReadOnlyList<Claim> Claims)
{
    public static AuthSession Anonymous { get; } = new(AuthSessionStatus.Anonymous, []);

    public static AuthSession Unavailable { get; } = new(AuthSessionStatus.Unavailable, []);
}
