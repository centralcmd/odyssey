using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Odyssey.Context;

namespace Odyssey.MigrationService.Tests;

/// <summary>
/// The service graph <c>Odyssey.MigrationService/Program.cs</c> builds, over isolated in-memory
/// databases: the one context plus the job's own Identity registration.
/// </summary>
/// <remarks>
/// The Identity half comes from <see cref="MigrationServiceIdentity.AddMigrationServiceIdentity"/> —
/// the very method <c>Program.cs</c> calls — rather than being hand-reproduced here. That is what makes
/// the password-policy assertions in <c>BootstrapAdminSeederTests</c> bind to production wiring: drop
/// the policy from the shared method and those tests fail, instead of a test-only copy keeping them
/// green while the job silently falls back to Identity's 6-character default (issue #290).
/// </remarks>
internal static class MigrationServiceTestHost
{
    public static ServiceProvider Build()
    {
        var databaseId = Guid.NewGuid().ToString();
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddDbContext<OdysseyContext>(options => options.UseInMemoryDatabase($"odyssey-{databaseId}"));

        services.AddMigrationServiceIdentity();

        var provider = services.BuildServiceProvider();

        // InMemory applies HasData (roles, system settings, currencies) only via EnsureCreated.
        using (var scope = provider.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<OdysseyContext>().Database.EnsureCreated();
        }

        return provider;
    }
}
