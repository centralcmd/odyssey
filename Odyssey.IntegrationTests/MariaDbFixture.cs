using System.Collections.Concurrent;
using DotNet.Testcontainers.Builders;
using Microsoft.EntityFrameworkCore;
using MySqlConnector;
using Odyssey.Context;
using Odyssey.Testing;
using Testcontainers.MySql;
using Xunit;

namespace Odyssey.IntegrationTests;

/// <summary>
/// Spins up a real MariaDB container and provisions the Odyssey databases, so tests exercise the actual
/// relational engine — migrations, FK and cascade behaviour, decimal/datetime fidelity — that EF InMemory
/// cannot represent. If no Docker daemon is reachable the fixture degrades gracefully: <see cref="Available"/>
/// is false and tests skip — unless <c>ODYSSEY_REQUIRE_TIER</c> names <c>integration</c>, in which case they
/// fail. Any other startup failure (image pull, container start, provisioning) always fails the tier
/// (issue #257; see <see cref="TestTierGate"/>).
/// </summary>
/// <remarks>
/// <para>
/// <b>One container per test assembly, shared by several collections.</b> The tier is split into collections
/// that xUnit runs in parallel (<see cref="MariaDbCollection"/> and its siblings below; the layout is in the
/// README). xUnit builds one fixture instance per collection, so the container, its startup outcome and the
/// template cache are static: the first collection to initialise starts the container, every other one awaits
/// that same start, and a failed start fails every collection the same way. The container is removed when the
/// test process exits (and by the Testcontainers reaper should the process die), not when a collection
/// finishes — the non-parallel <see cref="MariaDbTimingCollection"/> starts only after the others have
/// finished and disposed their fixtures.
/// </para>
/// <para>
/// <b>Tests in different collections run at the same time against the same server</b>, so a database name may
/// belong to one test class only. The shared <c>odyssey</c> and <c>odyssey_relational</c> databases hold data
/// only for classes in <see cref="MariaDbSharedDatabaseCollection"/> (and the timing collection, which runs
/// alone); every other class owns its private databases by name, and <c>DatabaseNameUniquenessTests</c> fails
/// when two source files name the same <c>odyssey_…</c> database. Connecting to <c>odyssey</c> only to create
/// or drop another database, as many classes' admin contexts do, is not a use of it.
/// </para>
/// </remarks>
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

    // Every wait in startup is bounded. Unbounded, a stuck image pull or a readiness probe that never
    // succeeds hangs the whole `dotnet test` run until the CI job's 6-hour ceiling, with nothing in the
    // log to say which (main's CI did exactly that from fa098ef on). A healthy start takes well under a
    // minute with the image cached and a few minutes on a cold pull; these only end a start that will
    // never finish, and the failure then names the stage it stopped in.
    internal static readonly TimeSpan StartupTimeout = TimeSpan.FromMinutes(10);
    internal static readonly TimeSpan ReadinessTimeout = TimeSpan.FromMinutes(3);

    // Built in StartAsync, not a field initialiser: .Build() resolves the Docker endpoint and throws
    // DockerUnavailableException when no daemon is reachable, the one failure that may become a skip.
    // Static: one container for the assembly, whichever collection's fixture starts it (see the remarks).
    private static MySqlContainer? container;

    // The one start, awaited by every collection's fixture. Its result is the skip reason (null when the server is
    // up); a faulted start rethrows the same fault into every collection.
    private static readonly Lazy<Task<string?>> Startup = new(StartAsync, LazyThreadSafetyMode.ExecutionAndPublication);

    public bool Available { get; private set; }

    public string? SkipReason { get; private set; }

    public string OdysseyConnectionString => BuildConnectionString(SharedDatabase);
    public string RelationalConnectionString => BuildConnectionString(RelationalDatabase);

    /// <summary>A connection string for an arbitrary (typically throwaway) database on the same server —
    /// for tests that need a private schema they fully own (the app user has server-wide privileges).</summary>
    public string ConnectionStringFor(string database) => BuildConnectionString(database);

    /// <summary>
    /// Drops <paramref name="database"/> if it exists and recreates it migrated to head, as a copy of a template the
    /// collection migrates once (<see cref="MigratedSchemaTemplate"/>). Returns its connection string.
    /// </summary>
    /// <remarks>Use it wherever a test would otherwise drop, create and <c>MigrateAsync</c> a private database. A test
    /// that needs a database before head (a <see cref="MigrationSeam"/> test) uses
    /// <see cref="CreateDatabaseAtMigrationAsync"/>.</remarks>
    public Task<string> CreateMigratedDatabaseAsync(string database) => CopyAsync(database, migrationId: null);

    /// <summary>
    /// Drops <paramref name="database"/> if it exists and recreates it migrated to exactly the migration whose name
    /// ends with <paramref name="nameSuffix"/>, every later migration left pending — the "before" state of a
    /// <see cref="MigrationSeam"/> test. A copy of a template built once per container and per migration. Returns its
    /// connection string.
    /// </summary>
    /// <remarks>Use it wherever a test would otherwise drop and create a private database, then
    /// <c>MigrationSeam.MigrateToAsync</c> it from empty to its baseline.</remarks>
    public Task<string> CreateDatabaseAtMigrationAsync(string database, string nameSuffix) =>
        CopyAsync(database, ResolveMigrationId(nameSuffix));

    private static string ResolveMigrationId(string nameSuffix)
    {
        // Resolving an id needs the migrations assembly but no connection, so the server version is pinned rather
        // than auto-detected and the connection string is never dialled.
        using var context = new OdysseyContext(new DbContextOptionsBuilder<OdysseyContext>()
            .UseMySql("server=localhost;database=odyssey", new MariaDbServerVersion(new Version(11, 4, 0)))
            .Options);
        return MigrationSeam.IdOf(context, nameSuffix);
    }

    private async Task<string> CopyAsync(string database, string? migrationId)
    {
        var source = await TemplateAsync(migrationId);
        await using var server = new MySqlConnection(OdysseyConnectionString);
        await server.OpenAsync();
        await source.CopyToAsync(server, database);
        return ConnectionStringFor(database);
    }

    // One build per target migration, shared by every caller asking for it: a Lazy per key, so two tests wanting the
    // same template wait on one build while a test wanting a different one builds its own alongside. A failed build
    // stays failed for the container, like any other fixture fault. Static, like the container: the collections run in
    // parallel, and a cache per fixture would race another's DROP/CREATE of the same template database.
    private static readonly ConcurrentDictionary<string, Lazy<Task<MigratedSchemaTemplate>>> templates = new(StringComparer.Ordinal);

    private static Task<MigratedSchemaTemplate> TemplateAsync(string? migrationId) =>
        templates.GetOrAdd(
            migrationId ?? "",
            _ => new Lazy<Task<MigratedSchemaTemplate>>(
                () => MigratedSchemaTemplate.BuildAsync(BuildConnectionString(SharedDatabase), BuildConnectionString, migrationId),
                LazyThreadSafetyMode.ExecutionAndPublication)).Value;

    public async Task InitializeAsync()
    {
        // Parsed first, on every path: a typo in ODYSSEY_REQUIRE_TIER fails here even on a healthy run,
        // rather than silently leaving the tier unrequired for the day Docker is missing.
        TestTierGate.IsRequired(TestTierGate.Integration);

        SkipReason = await Startup.Value;
        Available = SkipReason is null;
    }

    private static async Task<string?> StartAsync()
    {
        try
        {
            // The image goes through the constructor, not .WithImage(): Testcontainers 4.14 obsoleted the
            // parameterless MySqlBuilder() so the image is known before any module default is applied.
            var built = new MySqlBuilder(Image)
                .WithDatabase(SharedDatabase)
                .WithUsername(AppUser)
                .WithPassword(AppPassword)
                .WithEnvironment("MARIADB_ROOT_PASSWORD", RootPassword)
                // The tier migrates a fresh schema ~60 times and InnoDB syncs every DDL statement to
                // disk, so on a slow-fsync host (btrfs on LUKS) the tier took 5 hours with the CPU
                // idle, and 3.5 minutes in memory. The data is discarded with the container anyway.
                .WithTmpfsMount("/var/lib/mysql")
                // Every private database is its own connection-string pool, and a pool keeps its idle connections
                // for three minutes. With most private databases now a template copy, the tier opens them faster
                // than they idle out and passed the default ceiling of 151 ("Too many connections").
                .WithCommand("--max-connections=1000")
                // The MySql module's default readiness probe shells out to the `mysql` client, which the
                // mariadb image no longer ships — it would loop until timeout. Use the image's own
                // healthcheck script instead (same one docker-compose uses).
                .WithWaitStrategy(Wait.ForUnixContainer()
                    .UntilCommandIsCompleted(
                        ["healthcheck.sh", "--connect", "--innodb_initialized"],
                        strategy => strategy.WithTimeout(ReadinessTimeout)))
                .Build();
            container = built;
            AppDomain.CurrentDomain.ProcessExit += (_, _) => built.DisposeAsync().AsTask().GetAwaiter().GetResult();

            // The pull, the create and the readiness wait together; a cancellation here is a Faulted start.
            using (var startup = new CancellationTokenSource(StartupTimeout))
            {
                try
                {
                    await built.StartAsync(startup.Token);
                }
                catch (OperationCanceledException ex) when (startup.IsCancellationRequested)
                {
                    throw new TimeoutException(
                        $"The {Image} container did not start within {StartupTimeout.TotalMinutes:0} minutes " +
                        "(image pull, create, or the healthcheck.sh readiness wait). On CI, a Docker Hub pull " +
                        "rate limit (HTTP 429) is the usual cause of a stalled pull.", ex);
                }
                catch (TimeoutException ex)
                {
                    // The wait strategy's own bound: the container runs, but MariaDB never reported ready.
                    throw new TimeoutException(
                        $"The {Image} container started but healthcheck.sh --connect --innodb_initialized did " +
                        $"not pass within {ReadinessTimeout.TotalMinutes:0} minutes.", ex);
                }
            }

            // The image creates the shared database; add the isolated relational one too.
            using var provisioning = new CancellationTokenSource(ReadinessTimeout);
            var result = await built.ExecAsync(
            [
                "mariadb", "-uroot", $"-p{RootPassword}", "-e",
                $"CREATE DATABASE IF NOT EXISTS {RelationalDatabase}; " +
                $"GRANT ALL PRIVILEGES ON *.* TO '{AppUser}'@'%'; FLUSH PRIVILEGES;",
            ], provisioning.Token);

            if (result.ExitCode != 0)
            {
                throw new InvalidOperationException($"Database provisioning failed: {result.Stderr}");
            }

            return null;
        }
        catch (Exception ex) when (ClassifyStartupFailure(ex) == TierProbe.PrerequisiteMissing)
        {
            // No Docker daemon: the one condition that may skip, and only when the tier is not required.
            return TestTierGate.Resolve(
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

    // Nothing to release per collection: the container outlives every collection (see the remarks).
    public Task DisposeAsync() => Task.CompletedTask;

    private static string BuildConnectionString(string database) =>
        $"server={container!.Hostname};port={container.GetMappedPublicPort(ContainerPort)};" +
        $"database={database};user={AppUser};password={AppPassword};";
}

// The collections below run in parallel with each other (xunit.runner.json), against the one container. Which class
// goes where, and why, is in the README ("Parallel collections"); a new class with a private database of its own
// joins MariaDbCollection.

/// <summary>Classes that use only private databases they own by name — the default collection.</summary>
[CollectionDefinition(Name)]
public sealed class MariaDbCollection : ICollectionFixture<MariaDbFixture>
{
    public const string Name = "MariaDb";
}

/// <summary>More classes that use only private databases they own by name, split out of
/// <see cref="MariaDbCollection"/> so the two run side by side.</summary>
[CollectionDefinition(Name)]
public sealed class MariaDbPrivateDatabaseCollection : ICollectionFixture<MariaDbFixture>
{
    public const string Name = "MariaDb.PrivateDatabases";
}

/// <summary>
/// Every class that keeps data in the shared <c>odyssey</c> or <c>odyssey_relational</c> database. They are not
/// isolated from each other, so they stay together in one collection, which runs them one at a time.
/// </summary>
[CollectionDefinition(Name)]
public sealed class MariaDbSharedDatabaseCollection : ICollectionFixture<MariaDbFixture>
{
    public const string Name = "MariaDb.SharedDatabases";
}

/// <summary>
/// Classes that measure something CPU contention or a concurrent test distorts: elapsed-time budgets (P95, a lock wait
/// bounded in seconds), and process-wide heap and allocation counters (<c>GC.GetTotalMemory</c>,
/// <c>GC.GetTotalAllocatedBytes</c>), which count every thread in the test process. Not parallel: xUnit runs it alone,
/// after every parallel collection has finished.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class MariaDbTimingCollection : ICollectionFixture<MariaDbFixture>
{
    public const string Name = "MariaDb.Timing";
}
