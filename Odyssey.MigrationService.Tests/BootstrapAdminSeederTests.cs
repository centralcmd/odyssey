using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Odyssey.Context;
using Odyssey.Context.Authorization;
using Xunit;

namespace Odyssey.MigrationService.Tests;

/// <summary>
/// The out-of-band initial administrator (issue #290), which replaced the first-registrant privilege
/// branch. Keyed on an empty user table, so the whole idempotency story is "on the second run there is
/// a user, therefore nothing happens".
/// </summary>
public class BootstrapAdminSeederTests
{
    private const string Email = "admin@example.com";
    private const string Password = "Bootstrap!Password1";

    [Fact]
    public async Task Creates_one_enabled_confirmed_admin_that_must_change_its_password()
    {
        await using var provider = BuildProvider(out var seeder);

        await seeder.ExecuteAsync(CancellationToken.None);

        using var scope = provider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        var user = Assert.Single(await context.Users.ToListAsync());

        Assert.Equal(Email, user.Email);
        Assert.True(user.EmailConfirmed);
        Assert.True(user.MustChangePassword);
        Assert.False(user.TwoFactorEnabled);
        // Not merely "not the sentinel": null is the enabled state, and asserting it explicitly is the
        // regression guard for the two-step creation. RegistrationRequireAdminApproval is on (the
        // migration-seeded default), so CreateAsync inserts a DISABLED user and the seeder has to clear
        // the lockout afterwards — while leaving brute-force lockout protection itself armed.
        Assert.Null(user.LockoutEnd);
        Assert.True(user.LockoutEnabled);

        var role = Assert.Single(await context.UserRoles.Where(row => row.UserId == user.Id).ToListAsync());
        Assert.Equal(RoleDefinitions.AdminId, role.RoleId);

        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        Assert.True(await users.CheckPasswordAsync(user, Password));
    }

    [Fact]
    public async Task Running_twice_adds_no_second_user_and_rewrites_no_password()
    {
        await using var provider = BuildProvider(out var seeder);

        await seeder.ExecuteAsync(CancellationToken.None);
        var hashAfterFirstRun = await SinglePasswordHashAsync(provider);

        await seeder.ExecuteAsync(CancellationToken.None);

        using var scope = provider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        Assert.Equal(1, await context.Users.CountAsync());
        Assert.Equal(hashAfterFirstRun, await SinglePasswordHashAsync(provider));
    }

    /// <summary>
    /// The property that makes the configured value safe to leave in <c>.env.prod</c>: once the admin has
    /// changed its password (and issue #406 has cleared the flag), redeploying must not resurrect either.
    /// </summary>
    [Fact]
    public async Task A_later_run_leaves_a_changed_password_and_a_cleared_flag_intact()
    {
        await using var provider = BuildProvider(out var seeder);
        await seeder.ExecuteAsync(CancellationToken.None);

        string changedHash;
        using (var scope = provider.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = await users.FindByEmailAsync(Email);
            var changed = await users.ChangePasswordAsync(user!, Password, "Something!Entirely2New");
            Assert.True(changed.Succeeded);

            // OdysseyUserManager (the API's subclass) is what clears the flag in production; the
            // migrations job registers the stock UserManager, so clear it here to model the same state.
            user!.MustChangePassword = false;
            await users.UpdateAsync(user);
            changedHash = user.PasswordHash!;
        }

        await seeder.ExecuteAsync(CancellationToken.None);

        using var after = provider.CreateScope();
        var context = after.ServiceProvider.GetRequiredService<OdysseyContext>();
        var seeded = await context.Users.SingleAsync();
        Assert.Equal(changedHash, seeded.PasswordHash);
        Assert.False(seeded.MustChangePassword);
    }

    [Fact]
    public async Task A_non_empty_user_table_is_left_completely_alone()
    {
        await using var provider = BuildProvider(out var seeder);

        using (var scope = provider.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
            context.Users.Add(new ApplicationUser
            {
                UserName = "someone-else@example.com",
                Email = "someone-else@example.com",
                NormalizedEmail = "someone-else@example.com",
            });
            await context.SaveChangesAsync();
        }

        await seeder.ExecuteAsync(CancellationToken.None);

        using var after = provider.CreateScope();
        var users = after.ServiceProvider.GetRequiredService<OdysseyContext>();
        Assert.Equal(1, await users.Users.CountAsync());
        Assert.Null(await users.Users.SingleOrDefaultAsync(user => user.Email == Email));
    }

    [Theory]
    [InlineData(Email, null)]
    [InlineData(null, Password)]
    [InlineData(Email, "   ")]
    public async Task Only_one_of_the_two_values_is_a_hard_configuration_error(string? email, string? password)
    {
        await using var provider = BuildProvider(out var seeder, email, password);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => seeder.ExecuteAsync(CancellationToken.None));

        Assert.Contains(BootstrapAdminSeeder.EmailKey, error.Message, StringComparison.Ordinal);
        Assert.Contains(BootstrapAdminSeeder.PasswordKey, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Neither_value_is_not_an_error_because_the_demo_seed_may_still_run()
    {
        await using var provider = BuildProvider(out var seeder, email: null, password: null);

        await seeder.ExecuteAsync(CancellationToken.None);

        using var scope = provider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        Assert.Equal(0, await context.Users.CountAsync());
    }

    [Fact]
    public async Task A_password_below_the_policy_throws_without_echoing_it()
    {
        const string weak = "Short1!x";
        await using var provider = BuildProvider(out var seeder, Email, weak);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => seeder.ExecuteAsync(CancellationToken.None));

        // Codes, never the value: this message reaches container logs and CI output.
        Assert.Contains("PasswordTooShort", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(weak, error.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// The accepting side of the boundary. A rejection test alone passes just as happily against a
    /// policy that is accidentally stricter than the API's — an operator would then be locked out by a
    /// password the app itself considers valid, and the deploy would fail with no way to satisfy it.
    /// </summary>
    [Fact]
    public async Task A_password_of_exactly_the_required_length_is_accepted()
    {
        var exactly16 = "Abcdefghijklm1!x";
        Assert.Equal(PasswordPolicy.RequiredLength, exactly16.Length);

        await using var provider = BuildProvider(out var seeder, Email, exactly16);

        await seeder.ExecuteAsync(CancellationToken.None);

        using var scope = provider.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = await users.FindByEmailAsync(Email);
        Assert.NotNull(user);
        Assert.True(await users.CheckPasswordAsync(user!, exactly16));
    }

    [Fact]
    public async Task One_character_below_the_required_length_is_rejected()
    {
        var fifteen = "Abcdefghijkl1!x";
        Assert.Equal(PasswordPolicy.RequiredLength - 1, fifteen.Length);

        await using var provider = BuildProvider(out var seeder, Email, fifteen);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => seeder.ExecuteAsync(CancellationToken.None));

        Assert.Contains("PasswordTooShort", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_malformed_email_throws()
    {
        await using var provider = BuildProvider(out var seeder, "not-an-email", Password);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => seeder.ExecuteAsync(CancellationToken.None));

        Assert.Contains(BootstrapAdminSeeder.EmailKey, error.Message, StringComparison.Ordinal);
    }

    private static async Task<string> SinglePasswordHashAsync(ServiceProvider provider)
    {
        using var scope = provider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        return (await context.Users.AsNoTracking().SingleAsync()).PasswordHash!;
    }

    internal static ServiceProvider BuildProvider(
        out BootstrapAdminSeeder seeder,
        string? email = Email,
        string? password = Password)
    {
        var provider = MigrationServiceTestHost.Build();

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [BootstrapAdminSeeder.EmailKey] = email,
                [BootstrapAdminSeeder.PasswordKey] = password,
            })
            .Build();

        seeder = new BootstrapAdminSeeder(
            provider, configuration, provider.GetRequiredService<ILogger<BootstrapAdminSeeder>>());
        return provider;
    }
}
