using Odyssey.Dtos.Application;
using System.Security.Claims;
using System.Text;
using Odyssey.Api.Email;
using Odyssey.Api.Identity;
using Odyssey.Api.Legal;
using Odyssey.Context;
using Odyssey.Context.Authorization;
using Odyssey.Dtos.Authorization;
using Odyssey.Core.Pagination;
using Odyssey.Dtos;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;

namespace Odyssey.Api.UserAdministration;

public sealed class UserAdministrationService
{
    private static readonly DateTimeOffset DisabledLockoutEnd = AccountLockout.DisabledLockoutEnd;
    private static readonly string[] PreferredRoleOrder =
    [
        RoleDefinitions.Admin,
        RoleDefinitions.Owner,
        RoleDefinitions.User,
        RoleDefinitions.Guest,
    ];

    private readonly OdysseyContext context;
    private readonly UserManager<ApplicationUser> userManager;
    private readonly RoleManager<IdentityRole> roleManager;
    private readonly IUserDisplayNameResolver displayNames;
    private readonly ILegalPseudonymizer pseudonymizer;
    private readonly IEmailSendThrottle emailThrottle;
    private readonly IEmailRecipientHashKey recipientHashKey;
    private readonly IPasswordResetLinkSender resetLinkSender;
    private readonly ILogger<UserAdministrationService> logger;

    public UserAdministrationService(
        OdysseyContext context,
        UserManager<ApplicationUser> userManager,
        RoleManager<IdentityRole> roleManager,
        IUserDisplayNameResolver displayNames,
        ILegalPseudonymizer pseudonymizer,
        IEmailSendThrottle emailThrottle,
        IEmailRecipientHashKey recipientHashKey,
        IPasswordResetLinkSender resetLinkSender,
        ILogger<UserAdministrationService> logger)
    {
        this.context = context;
        this.userManager = userManager;
        this.roleManager = roleManager;
        this.displayNames = displayNames;
        this.pseudonymizer = pseudonymizer;
        this.emailThrottle = emailThrottle;
        this.recipientHashKey = recipientHashKey;
        this.resetLinkSender = resetLinkSender;
        this.logger = logger;
    }

    public async Task<PagedResult<ExistingUser>> SearchAsync(
        ClaimsPrincipal caller,
        UsersQueryParams query,
        CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;

        var q =
            from user in context.Users.AsNoTracking()
            join profile in context.UserProfiles.AsNoTracking() on user.Id equals profile.UserId into profileJoin
            from profile in profileJoin.DefaultIfEmpty()
            select new { User = user, Profile = profile };

        var term = ListQuery.NormalizeSearch(query.Search);
        if (term is not null)
        {
            var pattern = ListQuery.ContainsPattern(term);
            q = q.Where(x =>
                (x.User.UserName != null && EF.Functions.Like(x.User.UserName, pattern))
                || (x.User.Email != null && EF.Functions.Like(x.User.Email, pattern)));
        }

        if (query.Enabled is { } enabled)
        {
            q = enabled
                ? q.Where(x => x.User.LockoutEnd == null || x.User.LockoutEnd <= now)
                : q.Where(x => x.User.LockoutEnd != null && x.User.LockoutEnd > now);
        }

        if (!string.IsNullOrWhiteSpace(query.Role))
        {
            var trimmedRole = query.Role.Trim();
            if (!await roleManager.RoleExistsAsync(trimmedRole))
            {
                throw new UserAdministrationValidationException($"Role '{trimmedRole}' does not exist.");
            }

            q = q.Where(x =>
                context.UserRoles.Any(userRole =>
                    userRole.UserId == x.User.Id
                    && context.Roles.Any(identityRole => identityRole.Id == userRole.RoleId && identityRole.Name == trimmedRole)));
        }

        // `role` is resolved post-materialisation elsewhere; for server sorting it must order in SQL,
        // so we order by the alphabetically-first role name joined from the Identity role tables.
        // 'created' is intentionally not a sort key: ApplicationUser has no creation timestamp, so it
        // would sort by a random GUID. An absent creation column is a follow-up (add ApplicationUser.CreatedAtUtc).
        var ascending = ListQuery.Ascending(query.SortDir, naturalDefaultAscending: true);
        var sorted = query.SortBy switch
        {
            UserSortBy.Email => ascending ? q.OrderBy(x => x.User.Email) : q.OrderByDescending(x => x.User.Email),
            UserSortBy.EmailStatus => ascending ? q.OrderBy(x => x.User.EmailConfirmed) : q.OrderByDescending(x => x.User.EmailConfirmed),
            UserSortBy.Account => ascending
                ? q.OrderBy(x => x.User.LockoutEnd == null || x.User.LockoutEnd <= now)
                : q.OrderByDescending(x => x.User.LockoutEnd == null || x.User.LockoutEnd <= now),
            UserSortBy.Role => ascending
                ? q.OrderBy(x => context.Roles
                    .Where(r => context.UserRoles.Any(ur => ur.UserId == x.User.Id && ur.RoleId == r.Id))
                    .OrderBy(r => r.Name).Select(r => r.Name).FirstOrDefault())
                : q.OrderByDescending(x => context.Roles
                    .Where(r => context.UserRoles.Any(ur => ur.UserId == x.User.Id && ur.RoleId == r.Id))
                    .OrderBy(r => r.Name).Select(r => r.Name).FirstOrDefault()),
            // Sorted "First Middle Last" (mirrors the design system's uaFullName join order); a
            // profile-less user (no completed profile row) sorts as an empty string.
            UserSortBy.FullName => ascending
                ? q.OrderBy(x => x.Profile == null
                    ? string.Empty
                    : ((x.Profile.FirstName ?? string.Empty) + " " + (x.Profile.MiddleName ?? string.Empty) + " " + (x.Profile.LastName ?? string.Empty)).Trim())
                : q.OrderByDescending(x => x.Profile == null
                    ? string.Empty
                    : ((x.Profile.FirstName ?? string.Empty) + " " + (x.Profile.MiddleName ?? string.Empty) + " " + (x.Profile.LastName ?? string.Empty)).Trim()),
            UserSortBy.BirthDate => ascending
                ? q.OrderBy(x => x.Profile == null ? (DateOnly?)null : x.Profile.BirthDate)
                : q.OrderByDescending(x => x.Profile == null ? (DateOnly?)null : x.Profile.BirthDate),
            _ => ascending ? q.OrderBy(x => x.User.UserName) : q.OrderByDescending(x => x.User.UserName),
        };
        var orderedQuery = sorted.ThenBy(x => x.User.Id);

        var totalCount = await orderedQuery.CountAsync(cancellationToken);
        var (safeOffset, safeLimit) = ListQuery.ResolveWindow(query.Offset, query.Limit);

        var rows = await orderedQuery
            .Skip(safeOffset)
            .Take(safeLimit)
            .ToListAsync(cancellationToken);

        var resolvedNames = await displayNames.ResolveAsync(caller, rows.Select(x => (string?)x.User.Id), cancellationToken);
        var items = new List<ExistingUser>(rows.Count);
        foreach (var row in rows)
        {
            items.Add(await MapUserAsync(row.User, row.Profile, resolvedNames.GetValueOrDefault(row.User.Id)));
        }

        return new PagedResult<ExistingUser>
        {
            Items = items,
            Offset = safeOffset,
            Limit = safeLimit,
            TotalCount = totalCount,
        };
    }

    public async Task<ExistingUser?> GetAsync(ClaimsPrincipal caller, string id)
    {
        var user = await userManager.FindByIdAsync(id);
        if (user is null)
        {
            return null;
        }

        var profile = await GetProfileAsync(id);
        return await MapUserAsync(user, profile, await displayNames.ResolveAsync(caller, id, CancellationToken.None));
    }

    /// <summary>
    /// Apply the admin-editable account flags. Disabling additionally revokes the target's live sessions
    /// (issue #442).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The lockout sentinel is only consulted by <c>SignInManager</c> on the sign-in path, so on its own it
    /// bars the next sign-in and leaves every cookie already issued working until it expires — the one thing
    /// "disable this account" is being asked to do. Rotating the security stamp is what closes that:
    /// <c>SecurityStampValidator</c> re-checks the cookie against the stored stamp on the interval configured
    /// in <c>Program.cs</c>, and a rotated stamp fails that check.
    /// </para>
    /// <para>
    /// Revocation is therefore <b>bounded, not instant</b> — it lands within that validation interval (one
    /// minute), not on the next request. That is the existing, deliberate trade-off documented where the
    /// interval is set; the UI copy on the disable toggle says the same thing rather than promising an
    /// immediate sign-out.
    /// </para>
    /// <para>
    /// Only the disable branch rotates. Enabling would be harmless but pointless, and rotating on every
    /// <c>PATCH</c> would sign a user out because an administrator ticked <c>EmailConfirmed</c>. The branch is
    /// taken whenever <c>enabled: false</c> is requested rather than only on an enabled→disabled transition,
    /// which keeps re-disabling an account a working way to end sessions that outlived an earlier disable.
    /// </para>
    /// </remarks>
    public async Task<ExistingUser> UpdateAsync(ClaimsPrincipal caller, string actorUserId, string id, UpdatedUser request)
    {
        ValidateUpdateRequest(request);

        var user = await userManager.FindByIdAsync(id)
            ?? throw new UserAdministrationNotFoundException($"User ID {id} was not found.");

        var changedFields = new List<string>();
        var revokeSessions = false;

        if (request.EmailConfirmed.HasValue && user.EmailConfirmed != request.EmailConfirmed.Value)
        {
            user.EmailConfirmed = request.EmailConfirmed.Value;
            changedFields.Add(nameof(request.EmailConfirmed));
        }

        if (request.Enabled.HasValue)
        {
            if (!request.Enabled.Value && await IsEnabledAdminAsync(user) && await CountEnabledAdminsAsync() <= 1)
            {
                throw new UserAdministrationConflictException("Cannot disable the last enabled Admin user.");
            }

            if (request.Enabled.Value)
            {
                user.LockoutEnabled = true;
                user.LockoutEnd = null;
            }
            else
            {
                user.LockoutEnabled = true;
                user.LockoutEnd = DisabledLockoutEnd;
                revokeSessions = true;
            }

            changedFields.Add(nameof(request.Enabled));
        }

        // Both mutations ride on ONE store write, the same way SendPasswordResetAsync stages its flag:
        // UpdateSecurityStampAsync assigns the new stamp and then updates the user, so the flags staged
        // above on the same tracked entity are persisted by that call. Two writes would leave a failure
        // between them with the account disabled but its sessions still live.
        var result = revokeSessions
            ? await userManager.UpdateSecurityStampAsync(user)
            : await userManager.UpdateAsync(user);

        if (!result.Succeeded)
        {
            throw new UserAdministrationValidationException(FormatIdentityErrors(result));
        }

        logger.LogInformation(
            "User administration update by {ActorUserId} for target {TargetUserId}. Changed fields: {ChangedFields}. Sessions revoked: {SessionsRevoked}. Result: Success.",
            actorUserId,
            id,
            changedFields,
            revokeSessions);

        return await MapUserAsync(user, await GetProfileAsync(id), await displayNames.ResolveAsync(caller, id, CancellationToken.None));
    }

    public async Task<ExistingUser> AssignRoleAsync(ClaimsPrincipal caller, string actorUserId, string id, UpdatedUserRole request)
    {
        var requestedRole = request.Role?.Trim();
        if (string.IsNullOrWhiteSpace(requestedRole))
        {
            throw new UserAdministrationValidationException("Role is required.");
        }

        if (!await roleManager.RoleExistsAsync(requestedRole))
        {
            throw new UserAdministrationValidationException($"Role '{requestedRole}' does not exist.");
        }

        var user = await userManager.FindByIdAsync(id)
            ?? throw new UserAdministrationNotFoundException($"User ID {id} was not found.");

        var existingRoles = await userManager.GetRolesAsync(user);
        if (!string.Equals(requestedRole, RoleDefinitions.Admin, StringComparison.Ordinal)
            && existingRoles.Contains(RoleDefinitions.Admin, StringComparer.Ordinal)
            && IsEnabled(user)
            && await CountEnabledAdminsAsync() <= 1)
        {
            throw new UserAdministrationConflictException("Cannot demote the last enabled Admin user.");
        }

        if (existingRoles.Count > 0)
        {
            var removeResult = await userManager.RemoveFromRolesAsync(user, existingRoles);
            if (!removeResult.Succeeded)
            {
                throw new UserAdministrationValidationException(FormatIdentityErrors(removeResult));
            }
        }

        var addResult = await userManager.AddToRoleAsync(user, requestedRole);
        if (!addResult.Succeeded)
        {
            throw new UserAdministrationValidationException(FormatIdentityErrors(addResult));
        }

        logger.LogInformation(
            "User administration role assignment by {ActorUserId} for target {TargetUserId}. Result: Success.",
            actorUserId,
            id);

        return await MapUserAsync(user, await GetProfileAsync(id), await displayNames.ResolveAsync(caller, id, CancellationToken.None));
    }

    /// <summary>
    /// Mail the target the same reset link a self-service request produces, revoke their live sessions, and
    /// mark the account so a sign-in with the still-valid old password lands in a blocking change-password
    /// gate instead of the application (issue #406 §5.2).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The step order is not interchangeable. The throttle is asked <b>before</b> any work, so a throttled
    /// request returns 429 having mutated nothing rather than silently dropping the mail and reporting
    /// success. <c>UpdateSecurityStampAsync</c> comes <b>before</b> <c>GeneratePasswordResetTokenAsync</c>,
    /// because reset tokens embed the security stamp — generating first would produce a token the rotation
    /// immediately invalidates. The flag and the stamp are staged on the same entity so both land in a
    /// single write.
    /// </para>
    /// <para>
    /// A failed <em>send</em> is not rolled back. The sessions are already revoked and the admin's intent
    /// stands, so the outcome is reported to the caller (as <c>emailDelivered: false</c>) rather than turned
    /// into an error that invites retrying the whole operation as though nothing had happened.
    /// </para>
    /// <para>
    /// No permit is acquired twice: <see cref="IPasswordResetLinkSender.SendResetLinkAsync"/> deliberately
    /// does not consult the throttle, because this method already did.
    /// </para>
    /// </remarks>
    public async Task<PasswordResetLinkDelivery> SendPasswordResetAsync(
        string actorUserId, string id, CancellationToken cancellationToken = default)
    {
        var user = await userManager.FindByIdAsync(id)
            ?? throw new UserAdministrationNotFoundException($"User ID {id} was not found.");

        // Identity's /resetPassword refuses an unconfirmed address — otherwise registering an address you
        // don't control would be a route to taking it over — so a link would be unusable.
        if (string.IsNullOrWhiteSpace(user.Email) || !user.EmailConfirmed)
        {
            throw new UserAdministrationUnprocessableException(
                "This account has no confirmed email address, so a reset link cannot be sent.");
        }

        if (!await TryAcquireSendPermitAsync(user.Email))
        {
            throw new UserAdministrationThrottledException(
                "Too many reset emails have been sent to this address recently. Please try again later.");
        }

        // Both mutations ride on ONE store write. UpdateSecurityStampAsync assigns the new stamp and then
        // updates the user, so staging the flag on the same tracked entity first means a single
        // SaveChanges persists both — atomic by construction, without the explicit transaction
        // DeleteAsync needs for its cross-table sequence. Two separate writes would leave a failure
        // between them with the sessions revoked but the account not gated.
        user.MustChangePassword = true;

        // Revokes existing sessions and invalidates any previously issued reset link for this user.
        var stampResult = await userManager.UpdateSecurityStampAsync(user);
        if (!stampResult.Succeeded)
        {
            throw new UserAdministrationValidationException(FormatIdentityErrors(stampResult));
        }

        // Strictly after the rotation: reset tokens embed the security stamp, so a token minted before it
        // would be invalidated the moment the rotation landed.
        var token = await userManager.GeneratePasswordResetTokenAsync(user);

        // MapIdentityApi Base64Url-encodes the raw token before handing it to the sender, and its
        // /resetPassword Base64Url-DECODES the ResetCode it receives. The admin path generates its own
        // token, so it must apply the same encoding or the one /reset-password page would answer an
        // admin-issued link with InvalidToken.
        var code = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(token));

        var delivery = await resetLinkSender.SendResetLinkAsync(user.Email, code, cancellationToken);

        // Attributable without becoming a new PII store: actor and target ids and the delivery outcome,
        // never the address and never the token.
        logger.LogInformation(
            "Admin-initiated password reset by {ActorUserId} for target {TargetUserId}. Delivery: {Delivery}.",
            actorUserId,
            id,
            delivery);

        return delivery;
    }

    /// <summary>
    /// Fails open, matching <c>SmtpEmailSender</c>'s posture: a throttle that throws is an availability
    /// problem, while refusing the reset would leave the admin unable to help a locked-out user.
    /// </summary>
    private async Task<bool> TryAcquireSendPermitAsync(string email)
    {
        try
        {
            // The limits are database-backed (issue #421 Wave 2) and read here rather than inside the
            // throttle, whose compare-and-increment runs in a lock and so cannot await. Read live, not
            // cached: lowering a limit under active abuse must bind on the very next send.
            var limit = await SystemSettingsReader.GetIntAsync(
                context, SystemSettingsKeys.EmailPerRecipientLimit,
                SystemSettingsDefaults.EmailPerRecipientLimit);
            var windowMinutes = await SystemSettingsReader.GetIntAsync(
                context, SystemSettingsKeys.EmailPerRecipientWindowMinutes,
                SystemSettingsDefaults.EmailPerRecipientWindowMinutes);

            // Raise-only, so the read clamps UPWARD to the shipped floor (issue #434 key 14): the
            // throttle fails open at capacity, which makes max the conservative direction.
            var maxTrackedRecipients = Math.Max(
                await SystemSettingsReader.GetIntAsync(
                    context, SystemSettingsKeys.EmailMaxTrackedRecipients,
                    SystemSettingsDefaults.EmailMaxTrackedRecipients),
                SystemSettingsDefaults.EmailMaxTrackedRecipients);

            // The recipient hash key is read on the same cadence and for the same reason as the limits
            // (issue #445 Wave 3): the throttle's compare-and-increment runs in a lock and cannot await.
            var hashKey = await recipientHashKey.ResolveAsync();

            return emailThrottle.TryAcquire(email, limit, windowMinutes, maxTrackedRecipients, hashKey);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Per-recipient email throttle failed; proceeding with the reset anyway.");
            return true;
        }
    }

    public async Task DeleteAsync(string actorUserId, string id)
    {
        var user = await userManager.FindByIdAsync(id)
            ?? throw new UserAdministrationNotFoundException($"User ID {id} was not found.");

        if (string.Equals(actorUserId, id, StringComparison.Ordinal))
        {
            throw new UserAdministrationConflictException("You cannot delete your own account.");
        }

        if (await IsEnabledAdminAsync(user) && await CountEnabledAdminsAsync() <= 1)
        {
            throw new UserAdministrationConflictException("Cannot delete the last enabled Admin user.");
        }

        // Everything the user owns or is attributed on shares this context now, so the deletion below
        // resolves all of it inside the one transaction and no explicit purge is needed here. Their
        // profile and preferences CASCADE away with the row; the twenty-three user-attribution columns
        // across the domain — who created a journal entry, uploaded a file, attached a document —
        // SET NULL, keeping the shared record and dropping only the name. See OdysseyContext's
        // "User attribution foreign keys" for why nulling rather than cascading is the right direction.
        //
        // This is what the merge of the identity and domain contexts bought: those columns used to be
        // bare strings in another model, left pointing at an account that no longer existed, and no
        // transaction could have spanned the two contexts to fix them. Note EF InMemory enforces no
        // foreign keys at all, so none of it happens on the fast test tiers — the guarantee is proven
        // in Odyssey.IntegrationTests against real MariaDB.
        //
        // Legal acceptance records are the deliberate exception (issue #354 §6, §10.7): they carry no FK
        // and must OUTLIVE the account, so instead of cascading away they are pseudonymized in place —
        // in the same transaction as the deletion, so a live user is never left with silently orphaned
        // compliance history from a half-completed delete, and a future account (including one reusing
        // this id, which DemoDataSeeder's deterministic assignment can produce) can never inherit them.
        await ExecuteAtomicallyAsync(async () =>
        {
            await PseudonymizeLegalAcceptancesAsync(user);

            var result = await userManager.DeleteAsync(user);
            if (!result.Succeeded)
            {
                throw new UserAdministrationValidationException(FormatIdentityErrors(result));
            }
        });

        logger.LogInformation(
            "User administration delete by {ActorUserId} for target {TargetUserId}. Result: Success.",
            actorUserId,
            id);
    }

    /// <summary>
    /// Overwrite the user's id on both acceptance logs with a keyed, individually re-derivable digest of
    /// their email (see <see cref="ILegalPseudonymizer"/>). Rows are updated through the change tracker
    /// rather than <c>ExecuteUpdate</c> so this runs inside the caller's transaction on every provider,
    /// including the InMemory one the fast test tiers use; the row count per user is a handful.
    /// </summary>
    private async Task PseudonymizeLegalAcceptancesAsync(ApplicationUser user)
    {
        var pseudonym = await pseudonymizer.PseudonymizeAsync(user.Email ?? user.UserName ?? user.Id);

        var licenseRows = await context.LicenseAcceptances.Where(row => row.UserId == user.Id).ToListAsync();
        foreach (var row in licenseRows)
        {
            row.UserId = pseudonym;
        }

        var termsRows = await context.TermsOfServiceAcceptances.Where(row => row.UserId == user.Id).ToListAsync();
        foreach (var row in termsRows)
        {
            row.UserId = pseudonym;
        }

        if (licenseRows.Count > 0 || termsRows.Count > 0)
        {
            await context.SaveChangesAsync();
        }
    }

    /// <summary>
    /// Run <paramref name="work"/> in one database transaction. Wrapped in the context's execution
    /// strategy because <c>AddDatabases</c> enables retry-on-failure, and a retrying strategy refuses an
    /// ambient transaction it didn't open itself.
    /// </summary>
    private async Task ExecuteAtomicallyAsync(Func<Task> work)
    {
        var strategy = context.Database.CreateExecutionStrategy();

        await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await context.Database.BeginTransactionAsync();
            await work();
            await transaction.CommitAsync();
        });
    }

    public async Task<IReadOnlyList<ExistingRole>> GetRolesAsync()
    {
        var roles = await roleManager.Roles.AsNoTracking().ToListAsync();
        var existingRoles = new List<ExistingRole>(roles.Count);

        foreach (var role in SortRoles(roles))
        {
            var claims = await roleManager.GetClaimsAsync(role);
            var permissions = claims
                .Where(claim => claim.Type == PermissionClaims.Type && claim.Value is not null)
                .Select(claim => claim.Value)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToList();

            existingRoles.Add(new ExistingRole
            {
                Name = role.Name ?? string.Empty,
                Permissions = permissions,
            });
        }

        return existingRoles;
    }

    public IReadOnlyList<ExistingPermission> GetPermissions()
    {
        return RolePermissions.AllClaims
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .Select(value =>
            {
                var separatorIndex = value.LastIndexOf('.');
                var category = separatorIndex > 0 ? value[..separatorIndex] : value;
                var action = separatorIndex > 0 ? value[(separatorIndex + 1)..] : string.Empty;
                return new ExistingPermission
                {
                    Value = value,
                    Category = category,
                    Action = action,
                };
            })
            .ToList();
    }

    private static void ValidateUpdateRequest(UpdatedUser request)
    {
        if (request.ExtensionData is not null && request.ExtensionData.Count > 0)
        {
            var fields = string.Join(", ", request.ExtensionData.Keys.OrderBy(key => key, StringComparer.Ordinal));
            throw new UserAdministrationValidationException($"Unsupported fields: {fields}.");
        }
    }

    private async Task<ExistingUser> MapUserAsync(ApplicationUser user, UserProfile? profile, string? displayName)
    {
        var roles = await userManager.GetRolesAsync(user);
        return new ExistingUser
        {
            Id = user.Id,
            UserName = user.UserName,
            DisplayName = displayName,
            Email = user.Email,
            EmailConfirmed = user.EmailConfirmed,
            Enabled = IsEnabled(user),
            LockoutEnd = user.LockoutEnd,
            Role = PickDisplayRole(roles),
            CreatedAtUtc = null,
            MustChangePassword = user.MustChangePassword,
            FirstName = profile?.FirstName,
            MiddleName = profile?.MiddleName,
            LastName = profile?.LastName,
            BirthDate = profile?.BirthDate,
            Sex = profile?.Sex,
        };
    }

    private async Task<UserProfile?> GetProfileAsync(string userId) =>
        await context.UserProfiles.AsNoTracking().FirstOrDefaultAsync(profile => profile.UserId == userId);

    private async Task<bool> IsEnabledAdminAsync(ApplicationUser user)
    {
        var roles = await userManager.GetRolesAsync(user);
        return roles.Contains(RoleDefinitions.Admin, StringComparer.Ordinal) && IsEnabled(user);
    }

    private async Task<int> CountEnabledAdminsAsync()
    {
        var adminUsers = await userManager.GetUsersInRoleAsync(RoleDefinitions.Admin);
        return adminUsers.Count(IsEnabled);
    }

    private static bool IsEnabled(ApplicationUser user)
    {
        var now = DateTimeOffset.UtcNow;
        return user.LockoutEnd is null || user.LockoutEnd <= now;
    }

    private static string PickDisplayRole(IEnumerable<string> roles)
    {
        return roles
            .OrderBy(GetRoleSortIndex)
            .ThenBy(role => role, StringComparer.Ordinal)
            .FirstOrDefault() ?? string.Empty;
    }

    private static IEnumerable<IdentityRole> SortRoles(IEnumerable<IdentityRole> roles)
    {
        return roles
            .OrderBy(role => GetRoleSortIndex(role.Name ?? string.Empty))
            .ThenBy(role => role.Name, StringComparer.Ordinal);
    }

    private static int GetRoleSortIndex(string role)
    {
        var index = Array.IndexOf(PreferredRoleOrder, role);
        return index >= 0 ? index : PreferredRoleOrder.Length;
    }

    private static string FormatIdentityErrors(IdentityResult result)
    {
        return string.Join(" ", result.Errors.Select(error => error.Description));
    }
}
