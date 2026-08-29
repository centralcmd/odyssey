using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Odyssey.Context;
using Odyssey.Context.Authorization;

namespace Odyssey.MigrationService;

/// <summary>
/// Creates the initial administrator from configuration (issue #290), replacing the
/// trust-on-first-use branch that used to promote whoever registered first.
/// </summary>
/// <remarks>
/// <para>
/// The trigger is an <b>empty user table</b> and nothing else, which is what keeps this trivially
/// idempotent: from the second run onward at least one user exists, so the seeder is a single
/// <c>AnyAsync</c> and a log line. It has no update path at all — it cannot rewrite a
/// <c>PasswordHash</c>, re-assert a role, or re-arm <see cref="ApplicationUser.MustChangePassword"/>.
/// A redeploy therefore can never revert a password changed in the app, and the configured values are
/// not a way to recover a lost administrator.
/// </para>
/// <para>
/// Runs <b>before</b> <see cref="DemoDataSeeder"/>: keyed on an empty table, a seeder that ran after the
/// demo seed would find rows already present in Development and silently ignore credentials an operator
/// had explicitly configured.
/// </para>
/// </remarks>
public sealed class BootstrapAdminSeeder(
    IServiceProvider serviceProvider,
    IConfiguration configuration,
    ILogger<BootstrapAdminSeeder> logger)
    : IBootstrapAdminSeeder
{
    public const string EmailKey = "Bootstrap:Admin:Email";
    public const string PasswordKey = "Bootstrap:Admin:Password";

    public async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        using var scope = serviceProvider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();

        if (await context.Users.AnyAsync(cancellationToken))
        {
            logger.LogInformation("Users already exist; skipping bootstrap administrator seeding.");
            return;
        }

        var email = Trimmed(configuration[EmailKey]);
        var password = Trimmed(configuration[PasswordKey]);

        if (email is null != password is null)
        {
            throw new InvalidOperationException(
                $"Both '{EmailKey}' and '{PasswordKey}' must be set to create the bootstrap administrator; " +
                $"only '{(email is null ? PasswordKey : EmailKey)}' was supplied.");
        }

        if (email is null)
        {
            // Legitimate in dev: the demo seeder runs next and creates an Admin. If it doesn't, the
            // assertion at the end of the worker is what fails the job.
            logger.LogInformation("No bootstrap administrator configured.");
            return;
        }

        if (!new EmailAddressAttribute().IsValid(email))
        {
            throw new InvalidOperationException($"'{EmailKey}' is not a valid email address.");
        }

        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = new ApplicationUser
        {
            UserName = email,
            Email = email,
            // The operator is verified out of band by possession of the deployment configuration, and a
            // confirmed address is what makes the self-service reset (issue #405) available to them if
            // they lose the one-time password before using it.
            EmailConfirmed = true,
            LockoutEnabled = true,
            TwoFactorEnabled = false,
            // The configured password is a one-time secret: issue #406's gate refuses every authenticated
            // endpoint bar the handful needed to escape until it is changed.
            MustChangePassword = true,
        };

        var created = await userManager.CreateAsync(user, password!);
        if (!created.Succeeded)
        {
            // Codes only — the rejected value is the configured password, which must never reach a log,
            // an exception message or a container's stderr.
            var codes = string.Join(", ", created.Errors.Select(error => error.Code));
            throw new InvalidOperationException(
                $"Failed to create the bootstrap administrator from '{EmailKey}'/'{PasswordKey}': {codes}.");
        }

        // Two steps, not one: with the first-registrant exemption gone, SaveChanges stamps the
        // require-admin-approval lockout on every newly added user — this account included — so
        // CreateAsync has just inserted a *disabled* administrator. Clear it, exactly as DemoDataSeeder
        // does for the demo users. Getting this wrong is not silent: AdministratorAssertion sees no
        // enabled admin and fails the job.
        user.LockoutEnd = null;
        await userManager.UpdateAsync(user);

        // By role id rather than AddToRoleAsync, for the same reason DemoDataSeeder does it: the seeded
        // roles store their NormalizedName lower-cased, which the default upper-invariant normalizer
        // cannot match.
        context.UserRoles.Add(new IdentityUserRole<string>
        {
            UserId = user.Id,
            RoleId = RoleDefinitions.AdminId,
        });
        await context.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Created bootstrap administrator {Email}. This account must change its password at first sign-in.",
            email);
    }

    private static string? Trimmed(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
