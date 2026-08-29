using Odyssey.Context;
using Odyssey.Core.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace Odyssey.Api;

public static class DatabaseExtension
{
    // Pinned MariaDB version (matches docker-compose and the Aspire AppHost). Pinning avoids
    // ServerVersion.AutoDetect, which opens a blocking probe connection per context at startup.
    private static readonly ServerVersion DatabaseVersion = new MariaDbServerVersion(new Version(11, 4));

    public static WebApplicationBuilder AddDatabases(this WebApplicationBuilder builder)
    {
        var useInMemoryDatabase = builder.Configuration.GetValue<bool>("UseInMemoryDatabase") || builder.Environment.IsEnvironment("Testing");
        
        if (useInMemoryDatabase)
        {
            // EF Core InMemory has no real transactions and throws by default rather than silently
            // no-opping; the user-deletion path opens one for atomicity with the acceptance-row
            // pseudonymization (issue #354 §6), a genuine no-op here. The real guarantee is covered by
            // Odyssey.IntegrationTests against MariaDB. Note InMemory also enforces no foreign keys at
            // all, so none of the model's cascades or set-nulls are exercised on this provider.
            builder.Services.AddDbContext<OdysseyContext>(options =>
                options.UseInMemoryDatabase("Odyssey")
                    .ConfigureWarnings(warnings => warnings.Ignore(InMemoryEventId.TransactionIgnoredWarning)));
        }
        else
        {
            // One context, one connection: identity, finance and journal are a single model with real
            // foreign keys between them, so they cannot be pointed at different databases. Resolved
            // eagerly, outside the options lambda, so a missing or blank connection string fails at
            // startup rather than on the first DbContext resolution.
            var odysseyConnectionString = builder.Configuration.GetRequiredConnectionString("OdysseyConnection");
            builder.Services.AddDbContext<OdysseyContext>(options =>
                options.UseMySql(odysseyConnectionString, DatabaseVersion, EnableRetries));
        }

        return builder;
    }

    // Retry transient MariaDB failures (failover, restart, network blips) instead of surfacing them
    // as 500s. A retrying execution strategy rejects an ambient BeginTransaction it didn't open itself,
    // so any service that needs a manual transaction must go through
    // Database.CreateExecutionStrategy().ExecuteAsync(...) — see UserAdministrationService's
    // delete-with-pseudonymization path (issue #354 §6).
    private static void EnableRetries(MySqlDbContextOptionsBuilder mySql) => mySql.EnableRetryOnFailure();
}
