using System.Security.Claims;

namespace Odyssey.Dtos.Authorization;

/// <summary>
/// The one permission check both halves of the stack use (issue #287 M4). It lives beside
/// <see cref="PermissionClaims"/> for the same reason the vocabulary does: the API and the WASM client
/// name one symbol, so neither can test a different claim type than the other.
/// </summary>
public static class PermissionPrincipalExtensions
{
    /// <summary>Whether the user holds the given permission claim (<see cref="PermissionClaims.Type"/>).</summary>
    public static bool HasPermission(this ClaimsPrincipal user, string permission) =>
        user.HasClaim(PermissionClaims.Type, permission);
}
