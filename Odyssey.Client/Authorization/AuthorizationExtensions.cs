using Odyssey.Dtos.Authorization;
using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;

namespace Odyssey.Client.Authorization;

/// <summary>
/// Helpers that collapse the permission-check preamble every page repeated:
/// fetching the current <see cref="ClaimsPrincipal"/> and testing a permission claim.
/// </summary>
public static class AuthorizationExtensions
{
    /// <summary>The signed-in user's principal — shorthand for awaiting the auth state.</summary>
    public static async Task<ClaimsPrincipal> GetUserAsync(this AuthenticationStateProvider provider) =>
        (await provider.GetAuthenticationStateAsync()).User;

    /// <summary>Whether the user holds the given permission claim (<see cref="PermissionClaims.Type"/>).</summary>
    public static bool HasPermission(this ClaimsPrincipal user, string permission) =>
        user.HasClaim(PermissionClaims.Type, permission);

    /// <summary>
    /// The signed-in user's own id, or an empty string when the principal carries neither claim.
    /// </summary>
    /// <remarks>
    /// Both spellings are checked because which one appears depends on the token shape rather than on
    /// anything this app controls: the cookie pipeline emits <see cref="ClaimTypes.NameIdentifier"/>,
    /// while a JWT-shaped principal carries the short <c>sub</c>. Pages need this to tell "me" from
    /// "someone else" — <c>/users</c> warns an admin who is resetting their own password, <c>/account</c>
    /// shows the id — and a hand-rolled copy that checked only one spelling would silently answer "not
    /// me" for every user.
    /// </remarks>
    public static string UserId(this ClaimsPrincipal user) =>
        user.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? user.FindFirst("sub")?.Value ?? string.Empty;

    /// <summary>
    /// Whether the user still owes a License / Terms of Service response (issue #354 §5). The server
    /// emits one <see cref="LegalClaims.PendingAcceptanceType"/> claim per outstanding document and
    /// none at all when compliant, so presence is the whole check — the same signal the API's own gate
    /// middleware reads, which is what keeps the client's redirect and the server's 451 in agreement.
    /// </summary>
    public static bool HasPendingLegalAcceptance(this ClaimsPrincipal user) =>
        user.HasClaim(claim => claim.Type == LegalClaims.PendingAcceptanceType);
}
