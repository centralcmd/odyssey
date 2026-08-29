using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Odyssey.Context;
using Odyssey.Context.Authorization;
using Odyssey.MigrationService;
using Xunit;

namespace Odyssey.IntegrationTests;

/// <summary>
/// Real-engine coverage for the two halves of issue #290 that turn on SQL semantics EF InMemory does
/// not model.
/// </summary>
/// <remarks>
/// <para>
/// <b>The assertion's query.</b> "An enabled admin exists" is expressed as
/// <c>LockoutEnd != DisabledLockoutEnd</c>, and the enabled state is <c>NULL</c>. Under SQL's
/// three-valued logic <c>NULL &lt;&gt; x</c> is unknown, not true — so if EF's C# null semantics ever
/// stopped applying here, the assertion would fail every healthy deployment and take the API down with
/// it. On InMemory the comparison runs in LINQ-to-objects and can never catch that.
/// </para>
/// <para>
/// <b>The seeder's two-step creation.</b> <c>DisabledLockoutEnd</c> is <c>9999-12-31 23:59:59</c>, the
/// maximum a MariaDB <c>datetime(6)</c> holds; the round trip through a real column is what proves the
/// sentinel the seeder clears is the same value the assertion later compares against.
/// </para>
/// </remarks>
[Collection(MariaDbCollection.Name)]
public class BootstrapAdminRelationalTests(MariaDbFixture fixture)
{
    private const string Database = "odyssey_bootstrap_admin";
    private const string Email = "seeded-admin@example.com";
    private const string Password = "Bootstrap!Password1";

    [SkippableFact]
    public async Task TheSeededAdmin_LandsEnabledAndFlagged_AndSatisfiesTheAssertion()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await using var provider = await MigratedProviderAsync();

        await SeederFor(provider).ExecuteAsync(CancellationToken.None);

        using (var scope = provider.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
            var user = await context.Users.AsNoTracking().SingleAsync();

            // Written by CreateAsync (RegistrationRequireAdminApproval is on by migration seed) and
            // cleared by the seeder's follow-up update — read back off the real column, not the tracker.
            Assert.Null(user.LockoutEnd);
            Assert.True(user.LockoutEnabled);
            Assert.True(user.EmailConfirmed);
            Assert.True(user.MustChangePassword);

            var role = Assert.Single(await context.UserRoles.AsNoTracking().ToListAsync());
            Assert.Equal(RoleDefinitions.AdminId, role.RoleId);
        }

        // The query that would silently misread a NULL LockoutEnd as "not enabled".
        await AssertionFor(provider).ExecuteAsync(CancellationToken.None);

        await DropAsync();
    }

    [SkippableFact]
    public async Task TheAssertion_RejectsASoleAdminDisabledBySentinelInARealColumn()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await using var provider = await MigratedProviderAsync();

        await SeederFor(provider).ExecuteAsync(CancellationToken.None);

        using (var scope = provider.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
            var user = await context.Users.SingleAsync();
            user.LockoutEnd = AccountLockout.DisabledLockoutEnd;
            await context.SaveChangesAsync();

            // The sentinel survives datetime(6) exactly; a truncated or overflowed value would make the
            // assertion below pass and let an administrator-less instance boot.
            Assert.Equal(
                AccountLockout.DisabledLockoutEnd,
                (await context.Users.AsNoTracking().SingleAsync()).LockoutEnd);
        }

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => AssertionFor(provider).ExecuteAsync(CancellationToken.None));

        await DropAsync();
    }

    private static BootstrapAdminSeeder SeederFor(ServiceProvider provider)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [BootstrapAdminSeeder.EmailKey] = Email,
                [BootstrapAdminSeeder.PasswordKey] = Password,
            })
            .Build();

        return new BootstrapAdminSeeder(
            provider, configuration, provider.GetRequiredService<ILogger<BootstrapAdminSeeder>>());
    }

    private static AdministratorAssertion AssertionFor(ServiceProvider provider) =>
        new(provider, provider.GetRequiredService<ILogger<AdministratorAssertion>>());

    /// <summary>The migrations job's own service graph, pointed at a freshly migrated real database.</summary>
    private async Task<ServiceProvider> MigratedProviderAsync()
    {
        await DropAsync();

        await using (var admin = new OdysseyContext(OptionsFor(fixture.OdysseyConnectionString)))
        {
            await admin.Database.ExecuteSqlRawAsync($"CREATE DATABASE `{Database}`");
        }

        var connectionString = fixture.ConnectionStringFor(Database);
        await using (var context = new OdysseyContext(OptionsFor(connectionString)))
        {
            await context.Database.MigrateAsync();
        }

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<OdysseyContext>(options =>
            options.UseMySql(connectionString, ServerVersion.AutoDetect(connectionString)));
        // The job's own registration, not a copy of it — see MigrationServiceIdentity.
        services.AddMigrationServiceIdentity();

        return services.BuildServiceProvider();
    }

    private async Task DropAsync()
    {
        await using var admin = new OdysseyContext(OptionsFor(fixture.OdysseyConnectionString));
        await admin.Database.ExecuteSqlRawAsync($"DROP DATABASE IF EXISTS `{Database}`");
    }

    private static DbContextOptions<OdysseyContext> OptionsFor(string connectionString) =>
        new DbContextOptionsBuilder<OdysseyContext>()
            .UseMySql(connectionString, ServerVersion.AutoDetect(connectionString))
            .Options;
}
