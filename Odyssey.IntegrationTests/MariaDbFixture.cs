using DotNet.Testcontainers.Builders;
using Testcontainers.MySql;
using Xunit;

namespace Odyssey.IntegrationTests;

/// <summary>
/// Spins up a real MariaDB container (shared across the test collection) and provisions the
/// Odyssey databases, so tests exercise the actual relational engine — migrations, FK
/// and cascade behaviour, decimal/datetime fidelity — that EF InMemory cannot represent.
/// If Docker is unavailable the fixture degrades gracefully: <see cref="Available"/> is false
/// and tests skip rather than fail.
/// </summary>
public sealed class MariaDbFixture : IAsyncLifetime
{
    private const string Image = "mariadb:11.4";
    private const int ContainerPort = 3306;
    private const string RootPassword = "root_password";
    private const string AppUser = "odyssey";
    private const string AppPassword = "odyssey_password";

    // The one EF context lives here, mirroring how the app runs under Aspire.
    private const string SharedDatabase = "odyssey";

    // A separate database for destructive relational tests, so they never disturb the
    // seeded dataset the seeder test asserts exact counts against.
    private const string RelationalDatabase = "odyssey_relational";

    // The image goes through the constructor, not .WithImage(): Testcontainers 4.14 obsoleted the
    // parameterless MySqlBuilder() so the image is known before any module default is applied.
    private readonly MySqlContainer container = new MySqlBuilder(Image)
        .WithDatabase(SharedDatabase)
        .WithUsername(AppUser)
        .WithPassword(AppPassword)
        .WithEnvironment("MARIADB_ROOT_PASSWORD", RootPassword)
        // The MySql module's default readiness probe shells out to the `mysql` client, which the
        // mariadb image no longer ships — it would loop until timeout. Use the image's own
        // healthcheck script instead (same one docker-compose uses).
        .WithWaitStrategy(Wait.ForUnixContainer()
            .UntilCommandIsCompleted("healthcheck.sh", "--connect", "--innodb_initialized"))
        .Build();

    public bool Available { get; private set; }

    public string? SkipReason { get; private set; }

    public string OdysseyConnectionString => BuildConnectionString(SharedDatabase);
    public string RelationalConnectionString => BuildConnectionString(RelationalDatabase);

    /// <summary>A connection string for an arbitrary (typically throwaway) database on the same server —
    /// for tests that need a private schema they fully own (the app user has server-wide privileges).</summary>
    public string ConnectionStringFor(string database) => BuildConnectionString(database);

    public async Task InitializeAsync()
    {
        try
        {
            await container.StartAsync();

            // The image creates the shared database; add the isolated relational one too.
            var result = await container.ExecAsync(
            [
                "mariadb", "-uroot", $"-p{RootPassword}", "-e",
                $"CREATE DATABASE IF NOT EXISTS {RelationalDatabase}; " +
                $"GRANT ALL PRIVILEGES ON *.* TO '{AppUser}'@'%'; FLUSH PRIVILEGES;",
            ]);

            if (result.ExitCode != 0)
            {
                throw new InvalidOperationException($"Database provisioning failed: {result.Stderr}");
            }

            Available = true;
        }
        catch (Exception ex)
        {
            // Most commonly: no Docker daemon. Record the reason and let tests skip.
            Available = false;
            SkipReason = $"MariaDB Testcontainer unavailable: {ex.Message}";
        }
    }

    public async Task DisposeAsync()
    {
        if (Available)
        {
            await container.DisposeAsync();
        }
    }

    private string BuildConnectionString(string database) =>
        $"server={container.Hostname};port={container.GetMappedPublicPort(ContainerPort)};" +
        $"database={database};user={AppUser};password={AppPassword};";
}

[CollectionDefinition(Name)]
public sealed class MariaDbCollection : ICollectionFixture<MariaDbFixture>
{
    public const string Name = "MariaDb";
}
