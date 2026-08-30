using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Odyssey.MigrationService.Tests;

/// <summary>
/// The orchestration in <see cref="Worker"/> itself: the order it runs its steps in, and the process
/// exit code a failing step leaves behind (issue #290).
/// </summary>
/// <remarks>
/// Both properties are invisible to the seeders' own tests. A test that calls
/// <c>BootstrapAdminSeeder</c> and then <c>DemoDataSeeder</c> in the order it wants to prove would stay
/// green if the two lines in <see cref="Worker"/> were swapped — so ordering has to be observed on the
/// real <see cref="Worker"/>, with substituted steps recording when they were called. The exit code
/// matters just as much: <c>BackgroundService</c>'s default behaviour leaves it at 0, which would let
/// Compose's <c>service_completed_successfully</c> start the API behind a failed migrations job.
/// </remarks>
[Collection(ProcessExitCodeCollection.Name)]
public class WorkerTests
{
    [Fact]
    public async Task TheStepsRunInTheOrderTheDesignDependsOn()
    {
        var calls = new List<string>();
        var worker = BuildWorker(calls);

        await RunAsync(worker);

        Assert.Equal(
            [
                nameof(OdysseyMigrationService),
                // After the migration creates AspNetRoleClaims, and before anything
                // that reasons about authorization. Claims are reconciled here rather than seeded by
                // the model, so adding one no longer needs a hand-written migration.
                nameof(RoleClaimSeeder),
                // Before the demo seed: keyed on an empty user table, a bootstrap seeder running second
                // would find the demo users already there and ignore configured credentials.
                nameof(BootstrapAdminSeeder),
                nameof(DemoDataSeeder),
                // Last, so it sees whatever either seeder produced.
                nameof(AdministratorAssertion),
            ],
            calls);
    }

    [Fact]
    public async Task ASuccessfulRun_LeavesTheExitCodeAlone_AndStopsTheHost()
    {
        var original = Environment.ExitCode;
        try
        {
            Environment.ExitCode = 0;
            var lifetime = new RecordingLifetime();
            var worker = BuildWorker([], lifetime: lifetime);

            await RunAsync(worker);

            Assert.Equal(0, Environment.ExitCode);
            Assert.True(lifetime.Stopped, "A completed migrations job must stop the host so the container exits.");
        }
        finally
        {
            Environment.ExitCode = original;
        }
    }

    /// <summary>
    /// The regression guard for the gap this issue closed: a throwing step used to leave the exit code
    /// at 0, so Compose would start the API behind a failed job — a failed <em>migration</em> included.
    /// </summary>
    [Theory]
    [InlineData(nameof(OdysseyMigrationService))]
    [InlineData(nameof(BootstrapAdminSeeder))]
    [InlineData(nameof(AdministratorAssertion))]
    public async Task AFailingStep_SetsANonZeroExitCode_AndRethrows(string failingStep)
    {
        var original = Environment.ExitCode;
        try
        {
            Environment.ExitCode = 0;
            var lifetime = new RecordingLifetime();
            var worker = BuildWorker([], failAt: failingStep, lifetime: lifetime);

            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(worker));

            Assert.Equal(failingStep, error.Message);
            Assert.NotEqual(0, Environment.ExitCode);
            Assert.False(lifetime.Stopped, "A failed job must not reach the graceful stop.");
        }
        finally
        {
            Environment.ExitCode = original;
        }
    }

    /// <summary>
    /// A drifted database is the one failure an operator can actually act on, so the repair guidance
    /// has to reach the log rather than only the stack trace (issue #468). Under Aspire the stack trace
    /// lands in a DCP stdout file nobody thinks to open.
    /// </summary>
    [Fact]
    public async Task ADriftedDatabase_LogsTheRepairGuidance_AtCritical()
    {
        var original = Environment.ExitCode;
        try
        {
            Environment.ExitCode = 0;
            var logger = new RecordingLogger();
            var drift = new MigrationDriftException(
                "OdysseyContext", new MigrationDrift("20260828172122_InitialCreate", "table 'Accounts'"));
            var worker = BuildWorker(
                [], failAt: nameof(OdysseyMigrationService), logger: logger, failWith: drift);

            await Assert.ThrowsAsync<MigrationDriftException>(() => RunAsync(worker));

            var critical = Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Critical);
            Assert.Contains("20260828172122_InitialCreate", critical.Message);
            Assert.Contains("docs/migration-history-drift.md", critical.Message);
            Assert.NotEqual(0, Environment.ExitCode);
        }
        finally
        {
            Environment.ExitCode = original;
        }
    }

    /// <summary>
    /// Every other failure still has to say that it was the migrations job that died — the host's own
    /// unhandled-exception trace does not, which is what made the original occurrence read as a slow
    /// boot rather than a dead stack.
    /// </summary>
    [Fact]
    public async Task AnyOtherFailure_IsStillAnnounced_AtCritical()
    {
        var original = Environment.ExitCode;
        try
        {
            Environment.ExitCode = 0;
            var logger = new RecordingLogger();
            var worker = BuildWorker([], failAt: nameof(BootstrapAdminSeeder), logger: logger);

            await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(worker));

            var critical = Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Critical);
            Assert.Contains("Migrations job failed", critical.Message);
        }
        finally
        {
            Environment.ExitCode = original;
        }
    }

    /// <summary>
    /// <see cref="BackgroundService.StartAsync"/> hands back the faulted task when
    /// <c>ExecuteAsync</c> completes synchronously, and <see cref="BackgroundService.ExecuteTask"/>
    /// otherwise — await whichever carries the result.
    /// </summary>
    private static async Task RunAsync(Worker worker)
    {
        await worker.StartAsync(CancellationToken.None);
        if (worker.ExecuteTask is { } running)
        {
            await running;
        }
    }

    private static Worker BuildWorker(
        List<string> calls,
        string? failAt = null,
        RecordingLifetime? lifetime = null,
        RecordingLogger? logger = null,
        Exception? failWith = null)
    {
        StubStep Step(string name) => new(name, calls, failAt, failWith);

        return new Worker(
            Step(nameof(OdysseyMigrationService)),
            Step(nameof(RoleClaimSeeder)),
            Step(nameof(BootstrapAdminSeeder)),
            Step(nameof(DemoDataSeeder)),
            Step(nameof(AdministratorAssertion)),
            lifetime ?? new RecordingLifetime(),
            logger ?? new RecordingLogger());
    }

    /// <summary>
    /// Stands in for any one step: records that it ran, and fails if it is the one under test.
    /// </summary>
    /// <remarks>
    /// One class covers them all because the role interfaces share a single method signature — the
    /// point of depending on them rather than on the concrete types, which would drag a service
    /// provider, an <c>IConfiguration</c> and a logger into every double for no purpose.
    /// </remarks>
    private sealed class StubStep(string name, List<string> calls, string? failAt, Exception? failWith = null)
        : IOdysseyMigrationService,
            IRoleClaimSeeder,
            IBootstrapAdminSeeder,
            IDemoDataSeeder,
            IAdministratorAssertion
    {
        public Task ExecuteAsync(CancellationToken cancellationToken)
        {
            calls.Add(name);
            return failAt == name
                ? Task.FromException(failWith ?? new InvalidOperationException(name))
                : Task.CompletedTask;
        }
    }

    private sealed class RecordingLifetime : IHostApplicationLifetime
    {
        public bool Stopped { get; private set; }

        public CancellationToken ApplicationStarted => CancellationToken.None;

        public CancellationToken ApplicationStopping => CancellationToken.None;

        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void StopApplication() => Stopped = true;
    }

    /// <summary>Captures what the worker logged, so the operator-facing message can be asserted on.</summary>
    private sealed class RecordingLogger : ILogger<Worker>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }
}

/// <summary>
/// <see cref="Environment.ExitCode"/> is process-global, so the tests that assert on it must not run
/// beside anything else that could observe or change it mid-assertion.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public class ProcessExitCodeCollection
{
    public const string Name = "process-exit-code";
}
