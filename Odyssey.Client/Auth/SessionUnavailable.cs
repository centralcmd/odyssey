using System.Security.Claims;

namespace Odyssey.Client.Auth;

/// <summary>
/// The principal <see cref="CookieAuthenticationStateProvider"/> answers with when the session probe had
/// no definitive answer — a network error, a <c>429</c> or a <c>5xx</c> (issue #278).
/// </summary>
/// <remarks>
/// Unauthenticated on purpose: no authentication type, so <c>IsAuthenticated</c> is false and nothing is
/// authorized on it. What sets it apart from an anonymous visitor is the marker claim, which
/// <c>App.razor</c> checks before <see cref="Pages.Auth.RedirectToAuthorizedFallback"/> so a signed-in user
/// is shown a retry panel rather than sent to <c>/login</c>.
/// </remarks>
public static class SessionUnavailable
{
    /// <summary>A client-only claim type; the server never issues it, so it cannot be spoofed by one.</summary>
    public const string ClaimType = "odyssey:session-unavailable";

    public static ClaimsPrincipal Principal { get; } =
        new(new ClaimsIdentity([new Claim(ClaimType, "true")]));

    public static bool Is(ClaimsPrincipal? user) =>
        user is not null && user.Identity?.IsAuthenticated != true && user.HasClaim(ClaimType, "true");
}
