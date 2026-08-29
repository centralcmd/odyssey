using Microsoft.EntityFrameworkCore;
using Odyssey.Context;
using Odyssey.Context.Authorization;

namespace Odyssey.MigrationService;

/// <summary>
/// The last step of the migrations job (issue #290): refuse to let an instance boot with no
/// administrator. One rule in every environment — no environment-specific branch.
/// </summary>
/// <remarks>
/// "Enabled admin" means at least one row in <c>AspNetUserRoles</c> for
/// <see cref="RoleDefinitions.AdminId"/> whose user is not disabled, using the same
/// <see cref="AccountLockout.DisabledLockoutEnd"/> convention as the last-admin guards in
/// <c>UserAdministrationService</c>. Throwing exits the job non-zero, and the API does not start behind
/// a failed migrations job — so there is no window in which an administrator-less instance is publicly
/// reachable.
/// </remarks>
public sealed class AdministratorAssertion(IServiceProvider serviceProvider, ILogger<AdministratorAssertion> logger)
    : IAdministratorAssertion
{
    public async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        using var scope = serviceProvider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();

        var adminUserIds = context.UserRoles
            .Where(userRole => userRole.RoleId == RoleDefinitions.AdminId)
            .Select(userRole => userRole.UserId);

        // A null LockoutEnd is the enabled case and must count — EF Core's C# null semantics make
        // "!= sentinel" true for it, rather than the SQL three-valued unknown.
        var hasEnabledAdmin = await context.Users
            .Where(user => adminUserIds.Contains(user.Id))
            .AnyAsync(user => user.LockoutEnd != AccountLockout.DisabledLockoutEnd, cancellationToken);

        if (hasEnabledAdmin)
        {
            logger.LogInformation("An enabled administrator exists.");
            return;
        }

        var anyUsers = await context.Users.AnyAsync(cancellationToken);
        throw new InvalidOperationException(anyUsers
            // Unreachable through the API — UserAdministrationService refuses to disable, demote or
            // delete the last enabled Admin — so this means the database was edited directly.
            ? "No enabled administrator exists, but the user table is not empty. The API's last-admin "
              + "guards make this state unreachable through the application, so it indicates direct "
              + "database modification; re-enable an administrator account manually before redeploying."
            : $"No enabled administrator exists after seeding. Set {BootstrapAdminSeeder.EmailKey} and "
              + $"{BootstrapAdminSeeder.PasswordKey} (BOOTSTRAP_ADMIN_EMAIL / BOOTSTRAP_ADMIN_PASSWORD) "
              + "on a fresh database and redeploy.");
    }
}
