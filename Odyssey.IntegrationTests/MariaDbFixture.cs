using DotNet.Testcontainers.Builders;
using Odyssey.Testing;
using Testcontainers.MySql;
using Xunit;

namespace Odyssey.IntegrationTests;

/// <summary>
/// Spins up a real MariaDB container (shared across the test collection) and provisions the
/// Odyssey databases, so tests exercise the actual relational engine — migrations, FK
/// and cascade behaviour, decimal/datetime fidelity — that EF InMemory cannot represent.
/// If no Docker daemon is reachable the fixture degrades gracefully: <see cref="Available"/> is false
/// and tests skip — unless <c>ODYSSEY_REQUIRE_TIER</c> names <c>integration</c>, in which case they fail.
/// Any other startup failure (image pull, container start, provisioning) always fails the tier
/// (issue #257; see <see cref="TestTierGate"/>).
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

    // Built in InitializeAsync, not a field initialiser: .Build() resolves the Docker endpoint and
    // throws DockerUnavailableException when no daemon is reachable, the one failure that may become a skip.
    private MySqlContainer? container;

    public bool Available { get; private set; }

    public string? SkipReason { get; private set; }

    public string OdysseyConnectionString => BuildConnectionString(SharedDatabase);
    public string RelationalConnectionString => BuildConnectionString(RelationalDatabase);

    /// <summary>A connection string for an arbitrary (typically throwaway) database on the same server —
    /// for tests that need a private schema they fully own (the app user has server-wide privileges).</summary>
    public string ConnectionStringFor(string database) => BuildConnectionString(database);

    public async Task InitializeAsync()
    {
        // Parsed first, on every path: a typo in ODYSSEY_REQUIRE_TIER fails here even on a healthy run,
        // rather than silently leaving the tier unrequired for the day Docker is missing.
        TestTierGate.IsRequired(TestTierGate.Integration);

        try
        {
            // The image goes through the constructor, not .WithImage(): Testcontainers 4.14 obsoleted the
            // parameterless MySqlBuilder() so the image is known before any module default is applied.
            container = new MySqlBuilder(Image)
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
        catch (Exception ex) when (ClassifyStartupFailure(ex) == TierProbe.PrerequisiteMissing)
        {
            // No Docker daemon: the one condition that may skip, and only when the tier is not required.
            SkipReason = TestTierGate.Resolve(
                TestTierGate.Integration, TierProbe.PrerequisiteMissing,
                $"MariaDB Testcontainer unavailable, Docker is not reachable: {ex.Message}", ex);
        }
        catch (Exception ex)
        {
            // Docker IS reachable and something else went wrong — an image pull, the container start, the
            // readiness wait, provisioning. That used to become a skip too, so CI went green having run
            // none of this tier (issue #257). It always fails now.
            throw TestTierGate.Fault(
                TestTierGate.Integration, $"MariaDB Testcontainer failed to start or provision: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Only Testcontainers' own "no reachable Docker endpoint" signal — <see cref="DockerUnavailableException"/>,
    /// raised by <c>Build()</c> when endpoint discovery finds no daemon — is a missing prerequisite.
    /// Everything else, including Docker API errors once a daemon was found, is a fault.
    /// </summary>
    internal static TierProbe ClassifyStartupFailure(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is DockerUnavailableException)
            {
                return TierProbe.PrerequisiteMissing;
            }

            if (current is AggregateException aggregate
                && aggregate.InnerExceptions.Any(inner => ClassifyStartupFailure(inner) == TierProbe.PrerequisiteMissing))
            {
                return TierProbe.PrerequisiteMissing;
            }
        }

        return TierProbe.Faulted;
    }

    public async Task DisposeAsync()
    {
        if (container is not null)
        {
            await container.DisposeAsync();
        }
    }

    private string BuildConnectionString(string database) =>
        $"server={container!.Hostname};port={container.GetMappedPublicPort(ContainerPort)};" +
        $"database={database};user={AppUser};password={AppPassword};";
}

[CollectionDefinition(Name)]
public sealed class MariaDbCollection : ICollectionFixture<MariaDbFixture>
{
    public const string Name = "MariaDb";
}
