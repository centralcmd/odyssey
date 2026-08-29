using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Odyssey.Context;
using Odyssey.Context.Authorization;
using Odyssey.TestData;
using Xunit;

namespace Odyssey.MigrationService.Tests;

/// <summary>
/// The migrations job's last step (issue #290): an instance may not boot without an administrator.
/// One rule in every environment.
/// </summary>
public class AdministratorAssertionTests
{
    [Fact]
    public async Task Passes_when_the_only_admin_came_from_the_demo_seeder()
    {
        await using var provider = MigrationServiceTestHost.Build();
        await DemoSeederFor(provider).ExecuteAsync(CancellationToken.None);

        await AssertionFor(provider).ExecuteAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Passes_when_the_only_admin_came_from_the_bootstrap_seeder()
    {
        await using var provider = BootstrapAdminSeederTests.BuildProvider(out var seeder);
        await seeder.ExecuteAsync(CancellationToken.None);

        await AssertionFor(provider).ExecuteAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Throws_on_an_empty_database_and_names_both_configuration_keys()
    {
        await using var provider = MigrationServiceTestHost.Build();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => AssertionFor(provider).ExecuteAsync(CancellationToken.None));

        Assert.Contains(BootstrapAdminSeeder.EmailKey, error.Message, StringComparison.Ordinal);
        Assert.Contains(BootstrapAdminSeeder.PasswordKey, error.Message, StringComparison.Ordinal);
        Assert.Contains("BOOTSTRAP_ADMIN_EMAIL", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Unreachable through the API — <c>UserAdministrationService</c> refuses to disable the last enabled
    /// Admin — so this state means the database was edited directly, and the message says so.
    /// </summary>
    [Fact]
    public async Task Throws_when_the_sole_admin_is_disabled()
    {
        await using var provider = MigrationServiceTestHost.Build();

        using (var scope = provider.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
            var user = new ApplicationUser
            {
                Id = Guid.NewGuid().ToString(),
                UserName = "disabled-admin@example.com",
                Email = "disabled-admin@example.com",
                LockoutEnabled = true,
                LockoutEnd = AccountLockout.DisabledLockoutEnd,
            };
            context.Users.Add(user);
            context.UserRoles.Add(new Microsoft.AspNetCore.Identity.IdentityUserRole<string>
            {
                UserId = user.Id,
                RoleId = RoleDefinitions.AdminId,
            });
            await context.SaveChangesAsync();
        }

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => AssertionFor(provider).ExecuteAsync(CancellationToken.None));

        Assert.Contains("database", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Throws_when_users_exist_but_none_is_an_admin()
    {
        await using var provider = MigrationServiceTestHost.Build();

        using (var scope = provider.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
            context.Users.Add(new ApplicationUser
            {
                UserName = "ordinary@example.com",
                Email = "ordinary@example.com",
            });
            await context.SaveChangesAsync();
        }

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => AssertionFor(provider).ExecuteAsync(CancellationToken.None));
    }

    /// <summary>
    /// Worker ordering (issue #290 §Architecture B): the bootstrap seeder runs FIRST, so on an empty
    /// database with both configured and demo seeding on, the configured admin is created too — and it
    /// is the only account carrying the forced-change flag.
    /// </summary>
    [Fact]
    public async Task Bootstrap_then_demo_leaves_both_present_and_only_the_bootstrap_admin_flagged()
    {
        await using var provider = BootstrapAdminSeederTests.BuildProvider(out var bootstrap);

        await bootstrap.ExecuteAsync(CancellationToken.None);
        await DemoSeederFor(provider).ExecuteAsync(CancellationToken.None);
        await AssertionFor(provider).ExecuteAsync(CancellationToken.None);

        using var scope = provider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();

        Assert.Equal(DemoUsers.All.Count + 1, await context.Users.CountAsync());
        foreach (var demoUser in DemoUsers.All)
        {
            Assert.NotNull(await context.Users.SingleOrDefaultAsync(user => user.Email == demoUser.Email));
        }

        var flagged = await context.Users.Where(user => user.MustChangePassword).ToListAsync();
        Assert.Equal("admin@example.com", Assert.Single(flagged).Email);
    }

    private static AdministratorAssertion AssertionFor(ServiceProvider provider) =>
        new(provider, provider.GetRequiredService<ILogger<AdministratorAssertion>>());

    private static DemoDataSeeder DemoSeederFor(ServiceProvider provider)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Seed:DemoData"] = "true" })
            .Build();

        return new DemoDataSeeder(
            provider, configuration, new TestHostEnvironment(), provider.GetRequiredService<ILogger<DemoDataSeeder>>());
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Testing";
        public string ApplicationName { get; set; } = "Odyssey.MigrationService.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
