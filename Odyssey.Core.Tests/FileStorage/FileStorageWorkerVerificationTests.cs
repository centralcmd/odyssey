using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Odyssey.Context;
using Odyssey.Core.FileStorage;
using Odyssey.Testing.FileStorage;
using Xunit;

namespace Odyssey.Core.Tests.FileStorage;

/// <summary>
/// The worker's file-storage checks run inside the maintenance job (A3.2 as amended): a failing verdict holds that
/// job alone, is recorded per worker for the status endpoint, and clears itself once the checks pass again. Also the
/// status endpoint's overdue-erasure count.
/// </summary>
public class FileStorageWorkerVerificationTests : FileStorageTestBase
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    private IReadOnlyDictionary<string, string> Primary() => new Dictionary<string, string> { ["primary"] = Root("primary") };

    private static IReadOnlyDictionary<string, string> AllBound() => TestStorageFeatures.All.ToDictionary(f => f.Key, _ => "primary");

    // ── The held job ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_failing_check_holds_the_job_records_its_worker_and_clears_once_the_volume_is_back()
    {
        var (api, worker, aside) = await LostMountAsync();
        var timers = new FakeTimeProvider(Clock.GetUtcNow());
        var (job, gate) = NewJob(worker, timers);
        var run = job.RunOnceAsync(default);

        var verdict = await WaitForVerdictAsync(worker, gate.WorkerId);
        Assert.Equal("VolumeLost", verdict.FailedCheck);
        Assert.Equal(timers.GetUtcNow().UtcDateTime, verdict.FailedAtUtc);
        Assert.Null((await LeaseAsync(worker))?.LastCompletedAtUtc);
        Assert.False(run.IsCompleted);
        Assert.False(worker.Registry.IsActive);
        Assert.Contains(worker.Logs.At(LogLevel.Error), m => m.Contains("startup check VolumeLost failed", StringComparison.Ordinal));

        var status = (await StatusAsync(api)).Single();
        Assert.Equal(("VolumeLost", verdict.FailedAtUtc, 1), (status.MaintenanceHaltedCheck, status.MaintenanceHaltedAtUtc, status.MaintenanceHaltedWorkerCount));

        // The mount is fixed; the next re-check passes, the verdict goes and the pass runs.
        RestoreMount(aside);
        await AdvanceUntilAsync(timers, run);
        await run;

        Assert.Empty(await VerdictsAsync(worker));
        Assert.NotNull((await LeaseAsync(worker))!.LastCompletedAtUtc);
        Assert.True(worker.Registry.IsActive);
        status = (await StatusAsync(api)).Single();
        Assert.Equal((null, 0), (status.MaintenanceHaltedCheck, status.MaintenanceHaltedWorkerCount));
    }

    [Fact]
    public async Task A_held_job_ends_with_its_cancellation()
    {
        var (_, worker, _) = await LostMountAsync();
        using var stopping = new CancellationTokenSource();
        var (job, gate) = NewJob(worker, new FakeTimeProvider(Clock.GetUtcNow()));
        var run = job.RunOnceAsync(stopping.Token);

        await WaitForVerdictAsync(worker, gate.WorkerId);
        await stopping.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    [Fact]
    public async Task A_process_whose_store_is_already_verified_runs_at_once_and_records_nothing()
    {
        var host = await NewHostAsync();

        await host.Services.GetRequiredService<FileStorageWorkerVerification>().EnsureVerifiedAsync(default);

        Assert.Empty(await VerdictsAsync(host));
    }

    /// <summary>The checks cannot run at all (the database is away): nothing is recorded, the job waits and re-checks.</summary>
    [Fact]
    public async Task Checks_that_cannot_run_are_retried_without_a_verdict()
    {
        await NewHostAsync();
        var worker = await NewHostAsync(Primary(), AllBound(), verify: false);
        var faults = new FaultingServices(worker.Services) { VerifierFaults = 1 };
        var timers = new FakeTimeProvider(Clock.GetUtcNow());
        var (job, _) = NewJob(worker, timers, faults);

        var run = job.RunOnceAsync(default);
        await WaitUntilAsync(() => worker.Logs.At(LogLevel.Error).Any(m => m.Contains("could not run", StringComparison.Ordinal)));
        Assert.False(run.IsCompleted);
        Assert.Empty(await VerdictsAsync(worker));

        await AdvanceUntilAsync(timers, run);
        await run;
        Assert.True(worker.Registry.IsActive);
        Assert.NotNull((await LeaseAsync(worker))!.LastCompletedAtUtc);
    }

    /// <summary>
    /// The clear after a recovery fails: the job still runs (the checks passed), and the clear is retried on the next
    /// run — never left standing for the panel to report a stopped job that is running.
    /// </summary>
    [Fact]
    public async Task A_clear_that_fails_does_not_hold_the_job_and_is_retried_on_the_next_run()
    {
        var (_, worker, aside) = await LostMountAsync();
        var faults = new FaultingServices(worker.Services);
        var timers = new FakeTimeProvider(Clock.GetUtcNow());
        var (job, gate) = NewJob(worker, timers, faults);
        var run = job.RunOnceAsync(default);
        await WaitForVerdictAsync(worker, gate.WorkerId);

        RestoreMount(aside);
        faults.SaveFaults = FileStorageWorkerVerification.RecordAttempts;
        await AdvanceUntilAsync(timers, run);
        await run;

        Assert.NotNull((await LeaseAsync(worker))!.LastCompletedAtUtc);
        Assert.Single(await VerdictsAsync(worker));
        Assert.Equal(FileStorageWorkerVerification.RecordAttempts,
            worker.Logs.At(LogLevel.Warning).Count(m => m.StartsWith("Clearing the worker's file-storage verdict failed", StringComparison.Ordinal)));

        Clock.Advance(FileStorageMaintenanceJob.Tick);
        await job.RunOnceAsync(default);
        Assert.Empty(await VerdictsAsync(worker));
    }

    /// <summary>Recording gives up after its attempts, logs each, and leaves the job held and re-checking.</summary>
    [Fact]
    public async Task Recording_gives_up_after_its_attempts_and_the_job_stays_held()
    {
        var (_, worker, _) = await LostMountAsync();
        var faults = new FaultingServices(worker.Services) { SaveFaults = FileStorageWorkerVerification.RecordAttempts };
        var (job, _) = NewJob(worker, new FakeTimeProvider(Clock.GetUtcNow()), faults);
        using var stopping = new CancellationTokenSource();

        var run = job.RunOnceAsync(stopping.Token);
        await WaitUntilAsync(() => worker.Logs.At(LogLevel.Warning)
            .Count(m => m.StartsWith("Recording the worker's file-storage verdict failed", StringComparison.Ordinal)) == FileStorageWorkerVerification.RecordAttempts);

        Assert.False(run.IsCompleted);
        Assert.Empty(await VerdictsAsync(worker));
        await stopping.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    // ── One verdict per worker ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_passing_worker_clears_only_its_own_verdict_and_a_stale_one_expires()
    {
        var host = await NewHostAsync();
        var failing = NewGate(host, Clock);
        var other = NewGate(host, Clock);

        Assert.True(await failing.TryReportAsync("VolumeLost", default));
        Assert.True(await other.TryClearAsync(default));

        var status = (await StatusAsync(host)).Single();
        Assert.Equal(("VolumeLost", 1), (status.MaintenanceHaltedCheck, status.MaintenanceHaltedWorkerCount));

        // Refreshed within the window, it stays; the failed-at time is the first failure's.
        var first = Clock.GetUtcNow().UtcDateTime;
        Clock.Advance(FileStorageWorkerVerification.RetryInterval);
        Assert.True(await failing.TryReportAsync("VolumeLost", default));
        Assert.Equal(first, (await StatusAsync(host)).Single().MaintenanceHaltedAtUtc);

        // A worker that stopped reporting: ignored after the window, swept by the next clear.
        Clock.Advance(FileStorageWorkerVerification.VerdictTtl + TimeSpan.FromSeconds(1));
        Assert.Equal(0, (await StatusAsync(host)).Single().MaintenanceHaltedWorkerCount);
        Assert.Single(await VerdictsAsync(host));
        Assert.True(await other.TryClearAsync(default));
        Assert.Empty(await VerdictsAsync(host));
    }

    [Fact]
    public async Task Two_failing_workers_are_counted_and_the_longest_failing_is_named()
    {
        var host = await NewHostAsync();
        Assert.True(await NewGate(host, Clock).TryReportAsync("RootNotFound", default));
        var since = Clock.GetUtcNow().UtcDateTime;
        Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.True(await NewGate(host, Clock).TryReportAsync("VolumeLost", default));

        var status = (await StatusAsync(host)).Single();
        Assert.Equal(("RootNotFound", since, 2), (status.MaintenanceHaltedCheck, status.MaintenanceHaltedAtUtc, status.MaintenanceHaltedWorkerCount));
    }

    [Fact]
    public async Task The_status_carries_the_servers_thresholds_for_the_panels_copy()
    {
        var status = (await StatusAsync(await NewHostAsync())).Single();

        Assert.Equal((2, 3, 10), (status.OverdueAfterHours, status.MaintenanceStaleAfterHours, status.VerificationRetryMinutes));
        Assert.Equal(FileStorageStatusService.OverdueAfter, TimeSpan.FromHours(status.OverdueAfterHours));
        Assert.Equal(FileStorageStatusService.MaintenanceStaleAfter, TimeSpan.FromHours(status.MaintenanceStaleAfterHours));
        Assert.Equal(FileStorageWorkerVerification.RetryInterval, TimeSpan.FromMinutes(status.VerificationRetryMinutes));
    }

    // ── Overdue erasure ────────────────────────────────────────────────────────────────────────────────

    /// <summary>Overdue at exactly <c>OverdueAfter</c> past the erase time; an immediate release has no grace.</summary>
    [Fact]
    public async Task Pending_deletes_are_overdue_from_two_intervals_after_their_erase_time()
    {
        var host = await NewHostAsync();
        await ReleaseBothAsync(host);

        Clock.Advance(FileStorageStatusService.OverdueAfter - TimeSpan.FromSeconds(1));
        Assert.Equal((2, 0), await CountsAsync(host));
        Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal((2, 1), await CountsAsync(host));

        Clock.Advance(TimeSpan.FromHours(Dtos.SystemSettingsDefaults.FileStorageDeletionGraceHours) - TimeSpan.FromSeconds(1));
        Assert.Equal((2, 1), await CountsAsync(host));
        Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal((2, 2), await CountsAsync(host));
    }

    /// <summary>The count follows the configured grace, and a faulted one counts by the sweeper's 168 h, never by a shorter one.</summary>
    [Theory]
    [InlineData("48", 48)]
    [InlineData("abc", FileStorageDeletionGrace.FallbackHours)]
    [InlineData("0", FileStorageDeletionGrace.FallbackHours)]
    public async Task The_graced_overdue_count_follows_the_grace_the_sweeper_erases_by(string stored, int graceHours)
    {
        var host = await NewHostAsync();
        await FileStorageSweeperTests.SetGraceAsync(host, stored);
        await ReleaseBothAsync(host);

        Clock.Advance(TimeSpan.FromHours(graceHours) + FileStorageStatusService.OverdueAfter - TimeSpan.FromSeconds(1));
        Assert.Equal((2, 1), await CountsAsync(host));
        Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal((2, 2), await CountsAsync(host));
    }

    // ── Helpers ────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>An API host with one committed object, then the worker's mount replaced by an empty directory (VolumeLost).</summary>
    private async Task<(FileStorageTestHost Api, FileStorageTestHost Worker, string Aside)> LostMountAsync()
    {
        var api = await NewHostAsync();
        await PutCommittedAsync(api, TestStorageFeatures.Files, Guid.NewGuid());
        var aside = PrimaryRoot + "-aside";
        Directory.Move(PrimaryRoot, aside);
        Directory.CreateDirectory(PrimaryRoot);
        return (api, await NewHostAsync(Primary(), AllBound(), verify: false), aside);
    }

    private void RestoreMount(string aside)
    {
        Directory.Delete(PrimaryRoot);
        Directory.Move(aside, PrimaryRoot);
    }

    private static async Task ReleaseBothAsync(FileStorageTestHost host)
    {
        var graced = await PutCommittedAsync(host, TestStorageFeatures.Files, Guid.NewGuid());
        var immediate = await PutCommittedAsync(host, TestStorageFeatures.Secrets, Guid.NewGuid());
        await ObjectStoreTests.ReleaseAsync(host, graced.Id);
        await ObjectStoreTests.ReleaseAsync(host, immediate.Id);
    }

    private static async Task<(int Pending, int Overdue)> CountsAsync(FileStorageTestHost host)
    {
        var status = (await StatusAsync(host)).Single();
        return (status.PendingDeleteCount, status.OverdueDeleteCount);
    }

    private static FileStorageWorkerVerification NewGate(FileStorageTestHost host, TimeProvider timers, IServiceProvider? services = null) =>
        new(services ?? host.Services, host.Registry, timers, host.Services.GetRequiredService<ILogger<FileStorageWorkerVerification>>());

    private static (FileStorageMaintenanceJob Job, FileStorageWorkerVerification Gate) NewJob(
        FileStorageTestHost host, TimeProvider timers, IServiceProvider? services = null)
    {
        var gate = NewGate(host, timers, services);
        var job = new FileStorageMaintenanceJob(
            host.Services.GetRequiredService<IServiceScopeFactory>(), gate, host.Registry, host.Clock,
            host.Services.GetRequiredService<ILogger<FileStorageMaintenanceJob>>());
        return (job, gate);
    }

    /// <summary>Steps the retry timer until <paramref name="run"/> ends; the delay may be armed just after a step.</summary>
    private static async Task AdvanceUntilAsync(FakeTimeProvider timers, Task run)
    {
        var deadline = DateTime.UtcNow + Patience;
        while (!run.IsCompleted && DateTime.UtcNow < deadline)
        {
            timers.Advance(FileStorageWorkerVerification.RetryInterval);
            await Task.Delay(20);
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Patience;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "The expected state was never reached.");
            await Task.Delay(20);
        }
    }

    private static async Task<FileStorageWorkerVerdict> WaitForVerdictAsync(FileStorageTestHost host, Guid workerId)
    {
        FileStorageWorkerVerdict? verdict = null;
        var deadline = DateTime.UtcNow + Patience;
        while ((verdict = (await VerdictsAsync(host)).SingleOrDefault(v => v.WorkerId == workerId)) is null)
        {
            Assert.True(DateTime.UtcNow < deadline, "The worker's verdict was never recorded.");
            await Task.Delay(20);
        }

        return verdict;
    }

    private static async Task<List<FileStorageWorkerVerdict>> VerdictsAsync(FileStorageTestHost host)
    {
        await using var scope = host.Scope();
        return await scope.ServiceProvider.GetRequiredService<OdysseyContext>().FileStorageWorkerVerdicts.AsNoTracking().ToListAsync();
    }

    private static Task<IReadOnlyList<Dtos.Application.FileStorageVolumeStatus>> StatusAsync(FileStorageTestHost host) =>
        InScopeAsync(host, sp => sp.GetRequiredService<FileStorageStatusService>().GetVolumesAsync(default));

    private static async Task<FileStorageMaintenanceLease?> LeaseAsync(FileStorageTestHost host)
    {
        await using var scope = host.Scope();
        return await scope.ServiceProvider.GetRequiredService<OdysseyContext>().FileStorageMaintenanceLeases.AsNoTracking().SingleOrDefaultAsync();
    }

    /// <summary>
    /// The host's services with faults on demand: the verifier unavailable (the checks cannot run), or a save of a
    /// worker verdict refused (the write fails), each for the given number of times.
    /// </summary>
    private sealed class FaultingServices(IServiceProvider inner) : IServiceProvider, IServiceScopeFactory
    {
        private int verifierFaults;
        private int saveFaults;

        public int VerifierFaults { set => verifierFaults = value; }

        public int SaveFaults { set => saveFaults = value; }

        public object? GetService(Type serviceType) => serviceType == typeof(IServiceScopeFactory) ? this : inner.GetService(serviceType);

        public IServiceScope CreateScope() => new Scope(this, inner.CreateScope());

        private sealed class Scope(FaultingServices owner, IServiceScope scope) : IServiceScope, IServiceProvider, IAsyncDisposable
        {
            public IServiceProvider ServiceProvider => this;

            public object? GetService(Type serviceType)
            {
                if (serviceType == typeof(FileStorageVerifier) && Interlocked.Decrement(ref owner.verifierFaults) >= 0)
                {
                    throw new InvalidOperationException("The database is unreachable.");
                }

                var service = scope.ServiceProvider.GetService(serviceType);
                if (service is OdysseyContext context)
                {
                    context.SavingChanges += (_, _) =>
                    {
                        if (context.ChangeTracker.Entries<FileStorageWorkerVerdict>().Any() && Interlocked.Decrement(ref owner.saveFaults) >= 0)
                        {
                            throw new DbUpdateException("Injected.");
                        }
                    };
                }

                return service;
            }

            public void Dispose() => scope.Dispose();

            public ValueTask DisposeAsync() => scope is IAsyncDisposable async ? async.DisposeAsync() : ValueTask.CompletedTask;
        }
    }
}
