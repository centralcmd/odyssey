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
/// job alone, is recorded for the status endpoint, and clears itself once the checks pass again.
/// </summary>
public class FileStorageWorkerVerificationTests : FileStorageTestBase
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    private IReadOnlyDictionary<string, string> Primary() => new Dictionary<string, string> { ["primary"] = Root("primary") };

    private static IReadOnlyDictionary<string, string> AllBound() => TestStorageFeatures.All.ToDictionary(f => f.Key, _ => "primary");

    [Fact]
    public async Task A_failing_check_holds_the_job_records_the_check_and_clears_once_the_volume_is_back()
    {
        var api = await NewHostAsync();
        var committed = await PutCommittedAsync(api, TestStorageFeatures.Files, Guid.NewGuid());

        // The worker's mount is wrong: an empty directory where the volume should be.
        var aside = PrimaryRoot + "-aside";
        Directory.Move(PrimaryRoot, aside);
        Directory.CreateDirectory(PrimaryRoot);

        var worker = await NewHostAsync(Primary(), AllBound(), verify: false);
        var timers = new FakeTimeProvider(Clock.GetUtcNow());
        var job = NewJob(worker, timers);
        var run = job.RunOnceAsync(default);

        var lease = await WaitForLeaseAsync(worker, l => l.VerificationFailedCheck is not null);
        Assert.Equal("VolumeLost", lease.VerificationFailedCheck);
        Assert.Equal(timers.GetUtcNow().UtcDateTime, lease.VerificationFailedAtUtc);
        Assert.Null(lease.LastCompletedAtUtc);
        Assert.False(run.IsCompleted);
        Assert.False(worker.Registry.IsActive);
        Assert.Contains(worker.Logs.At(LogLevel.Error), m => m.Contains("startup check VolumeLost failed", StringComparison.Ordinal));

        var status = await StatusAsync(api);
        Assert.Equal(("VolumeLost", lease.VerificationFailedAtUtc), (status[0].MaintenanceHaltedCheck, status[0].MaintenanceHaltedAtUtc));

        // The mount is fixed; the next re-check passes and the pass runs.
        Directory.Delete(PrimaryRoot);
        Directory.Move(aside, PrimaryRoot);
        await AdvanceUntilAsync(timers, run);
        await run;

        lease = await LeaseAsync(worker);
        Assert.Null(lease.VerificationFailedCheck);
        Assert.Null(lease.VerificationFailedAtUtc);
        Assert.NotNull(lease.LastCompletedAtUtc);
        Assert.True(worker.Registry.IsActive);
        Assert.True(File.Exists(Path.Combine(PrimaryRoot, committed.RelativePath)));
        Assert.Null((await StatusAsync(api))[0].MaintenanceHaltedCheck);
    }

    [Fact]
    public async Task A_held_job_ends_with_its_cancellation()
    {
        var api = await NewHostAsync();
        await PutCommittedAsync(api, TestStorageFeatures.Files, Guid.NewGuid());
        Directory.Delete(PrimaryRoot, recursive: true);
        Directory.CreateDirectory(PrimaryRoot); // VolumeLost

        var worker = await NewHostAsync(Primary(), AllBound(), verify: false);
        using var stopping = new CancellationTokenSource();
        var run = NewJob(worker, new FakeTimeProvider(Clock.GetUtcNow())).RunOnceAsync(stopping.Token);

        Assert.Equal("VolumeLost", (await WaitForLeaseAsync(worker, l => l.VerificationFailedCheck is not null)).VerificationFailedCheck);
        await stopping.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    [Fact]
    public async Task A_process_whose_store_is_already_verified_runs_at_once_and_records_nothing()
    {
        var host = await NewHostAsync();

        await host.Services.GetRequiredService<FileStorageWorkerVerification>().EnsureVerifiedAsync(default);

        await using var scope = host.Scope();
        Assert.Empty(await scope.ServiceProvider.GetRequiredService<OdysseyContext>().FileStorageMaintenanceLeases.ToListAsync());
    }

    /// <summary>The overdue count uses the sweeper's grace, plus two maintenance intervals; an immediate release has no grace.</summary>
    [Fact]
    public async Task Pending_deletes_are_overdue_two_intervals_after_their_erase_time()
    {
        var host = await NewHostAsync();
        var graced = await PutCommittedAsync(host, TestStorageFeatures.Files, Guid.NewGuid());
        var immediate = await PutCommittedAsync(host, TestStorageFeatures.Secrets, Guid.NewGuid());
        await ObjectStoreTests.ReleaseAsync(host, graced.Id);
        await ObjectStoreTests.ReleaseAsync(host, immediate.Id);
        Assert.Equal(TimeSpan.FromHours(2), FileStorageStatusService.OverdueAfter);

        Clock.Advance(TimeSpan.FromHours(2) - TimeSpan.FromMinutes(1));
        Assert.Equal((2, 0), Counts(await StatusAsync(host)));

        Clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal((2, 1), Counts(await StatusAsync(host)));

        Clock.Advance(TimeSpan.FromHours(Dtos.SystemSettingsDefaults.FileStorageDeletionGraceHours) - TimeSpan.FromMinutes(2));
        Assert.Equal((2, 1), Counts(await StatusAsync(host)));

        Clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal((2, 2), Counts(await StatusAsync(host)));

        static (int Pending, int Overdue) Counts(IReadOnlyList<Dtos.Application.FileStorageVolumeStatus> status) =>
            (status.Single().PendingDeleteCount, status.Single().OverdueDeleteCount);
    }

    private static FileStorageMaintenanceJob NewJob(FileStorageTestHost host, TimeProvider timers)
    {
        var verification = new FileStorageWorkerVerification(
            host.Services, host.Registry, timers, host.Services.GetRequiredService<ILogger<FileStorageWorkerVerification>>());
        return new FileStorageMaintenanceJob(
            host.Services.GetRequiredService<IServiceScopeFactory>(), verification, host.Registry, host.Clock,
            host.Services.GetRequiredService<ILogger<FileStorageMaintenanceJob>>());
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

    private static async Task<FileStorageMaintenanceLease> WaitForLeaseAsync(FileStorageTestHost host, Func<FileStorageMaintenanceLease, bool> until)
    {
        var deadline = DateTime.UtcNow + Patience;
        while (true)
        {
            await using (var scope = host.Scope())
            {
                var lease = await scope.ServiceProvider.GetRequiredService<OdysseyContext>().FileStorageMaintenanceLeases.AsNoTracking().SingleOrDefaultAsync();
                if (lease is not null && until(lease))
                {
                    return lease;
                }
            }

            Assert.True(DateTime.UtcNow < deadline, "The lease never reached the expected state.");
            await Task.Delay(20);
        }
    }

    private static Task<IReadOnlyList<Dtos.Application.FileStorageVolumeStatus>> StatusAsync(FileStorageTestHost host) =>
        InScopeAsync(host, sp => sp.GetRequiredService<FileStorageStatusService>().GetVolumesAsync(default));

    private static async Task<FileStorageMaintenanceLease> LeaseAsync(FileStorageTestHost host)
    {
        await using var scope = host.Scope();
        return await scope.ServiceProvider.GetRequiredService<OdysseyContext>().FileStorageMaintenanceLeases.AsNoTracking().SingleAsync();
    }
}
