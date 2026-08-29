using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Odyssey.Context;

namespace Odyssey.MigrationService;

/// <summary>
/// The Identity registration the migrations job needs so its seeders can create users through
/// <see cref="UserManager{TUser}"/> — correct password hashing, normalization and security stamps.
/// Roles already exist via migrations.
/// </summary>
/// <remarks>
/// One method rather than four lines repeated per host, because the test harnesses
/// (<c>MigrationServiceTestHost</c>, <c>BootstrapAdminRelationalTests</c>) have to build the same graph
/// and a hand-copied version can drift from what <c>Program.cs</c> actually registers. The
/// <see cref="PasswordPolicy"/> line is the one that matters most: drop it and the job silently falls
/// back to Identity's 6-character default, so it would seed a bootstrap administrator the API itself
/// would refuse (issue #290). Sharing the registration is what makes
/// <c>BootstrapAdminSeederTests.A_password_below_the_policy_throws_without_echoing_it</c> a real guard
/// on production wiring rather than on a test-only copy of it.
/// </remarks>
public static class MigrationServiceIdentity
{
    public static IServiceCollection AddMigrationServiceIdentity(this IServiceCollection services)
    {
        services.AddIdentityCore<ApplicationUser>()
            .AddRoles<IdentityRole>()
            .AddEntityFrameworkStores<OdysseyContext>();

        // The same policy Odyssey.Api applies, so a password the API would reject can never be seeded
        // here — and one it would accept is never refused.
        services.Configure<IdentityOptions>(options => PasswordPolicy.Apply(options.Password));

        return services;
    }
}
