using System.Security.Claims;

namespace Odyssey.Api.Identity;

/// <summary>
/// Resolves user-ids to human display labels at the API edge, given the caller's claims (issue #316).
/// The single source of truth for "who did this" attribution: it batches one query over
/// <c>AspNetUsers ⋈ UserProfiles</c> and applies the claim-aware rule
/// <c>DisplayName ?? FirstName ?? (caller holds <see cref="Odyssey.Context.Authorization.PermissionClaims.UsersRead"/> ? Email/UserName : "Unknown user")</c>.
/// The caller <see cref="ClaimsPrincipal"/> is a REQUIRED parameter so no call site can bypass the
/// claim check and regress to leaking emails. Only five columns are ever read
/// (<c>Id, UserName, Email, DisplayName, FirstName</c>) — never <c>BirthDate/Sex/Title/MiddleName/LastName</c>.
/// The returned label is always non-null.
/// </summary>
public interface IUserDisplayNameResolver
{
    /// <summary>
    /// Resolve every distinct, non-empty id in <paramref name="userIds"/> to a non-null display label.
    /// The returned map contains an entry for every wanted id (unresolvable/deleted ids map to
    /// "Unknown user"), so callers can index it without a null check.
    /// </summary>
    Task<IReadOnlyDictionary<string, string>> ResolveAsync(
        ClaimsPrincipal caller,
        IEnumerable<string?> userIds,
        CancellationToken cancellationToken);

    /// <summary>Resolve a single id (null/empty ⇒ "Unknown user").</summary>
    Task<string> ResolveAsync(
        ClaimsPrincipal caller,
        string? userId,
        CancellationToken cancellationToken);
}
