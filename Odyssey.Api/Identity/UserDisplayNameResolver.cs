using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Odyssey.Context;
using Odyssey.Dtos.Authorization;

namespace Odyssey.Api.Identity;

/// <inheritdoc />
public sealed class UserDisplayNameResolver : IUserDisplayNameResolver
{
    /// <summary>The neutral label shown when no name is available and the caller may not see the email.</summary>
    public const string UnknownUser = "Unknown user";

    private readonly OdysseyContext context;

    public UserDisplayNameResolver(OdysseyContext context)
    {
        this.context = context;
    }

    public async Task<IReadOnlyDictionary<string, string>> ResolveAsync(
        ClaimsPrincipal caller,
        IEnumerable<string?> userIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);

        var wanted = userIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (wanted.Count == 0)
        {
            return result;
        }

        // Every wanted id gets a non-null label; a deleted/unresolvable id keeps this default.
        foreach (var id in wanted)
        {
            result[id] = UnknownUser;
        }

        var callerCanSeeEmail = caller.HasClaim(PermissionClaims.Type, PermissionClaims.UsersRead);

        // Single batched WHERE Id IN (...) left-join projecting only the five allowed columns — never
        // BirthDate/Sex/Title/MiddleName/LastName (spec §5/§10).
        var rows = await (
            from user in context.Users.AsNoTracking()
            where wanted.Contains(user.Id)
            join profile in context.UserProfiles.AsNoTracking()
                on user.Id equals profile.UserId into profiles
            from profile in profiles.DefaultIfEmpty()
            select new
            {
                user.Id,
                user.UserName,
                user.Email,
                DisplayName = profile != null ? profile.DisplayName : null,
                FirstName = profile != null ? profile.FirstName : null,
            }).ToListAsync(cancellationToken);

        foreach (var row in rows)
        {
            result[row.Id] = Resolve(row.DisplayName, row.FirstName, row.UserName, row.Email, callerCanSeeEmail);
        }

        return result;
    }

    public async Task<string> ResolveAsync(ClaimsPrincipal caller, string? userId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(userId))
        {
            return UnknownUser;
        }

        var map = await ResolveAsync(caller, [userId], cancellationToken);
        return map.GetValueOrDefault(userId, UnknownUser);
    }

    private static string Resolve(string? displayName, string? firstName, string? userName, string? email, bool callerCanSeeEmail)
    {
        if (!string.IsNullOrWhiteSpace(displayName))
        {
            return displayName.Trim();
        }

        if (!string.IsNullOrWhiteSpace(firstName))
        {
            return firstName.Trim();
        }

        if (callerCanSeeEmail)
        {
            var identifier = email ?? userName;
            if (!string.IsNullOrWhiteSpace(identifier))
            {
                return identifier;
            }
        }

        return UnknownUser;
    }
}
