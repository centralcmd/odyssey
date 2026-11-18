using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Odyssey.Context;
using Odyssey.Core.FileStorage;
using Odyssey.Dtos.Application;
using Odyssey.Testing.FileStorage;
using Xunit;

namespace Odyssey.Core.Tests.FileStorage;

/// <summary>
/// The maintenance job's rows in the shared run log (issue #409 §3.2–§3.5): one row per pass, elected by the lease
/// first, a heartbeat on every tick, and a manual pass picked up by the 30-second poll with the normal grace.
/// </summary>
public class FileStorageMaintenanceJobRunTests : FileStorageTestBase
{
    [Fact]
    public async Task A_scheduled_pass_writes_one_row_and_a_tick_before_the_next_is_due_writes_none()
    {
        var host = await NewHostAsync();
        var job = host.Services.GetRequiredService<FileStorageMaintenanceJob>();

        await job.RunOnceAsync(default);
        Clock.Advance(FileStorageMaintenanceJob.Tick);
        await job.RunOnceAsync(default);

        var run = (FileStorageMaintenanceJobRun)Assert.Single(await RunsAsync(host));
        Assert.Equal((JobRunTrigger.Scheduled, JobRunStatus.Succeeded), (run.Trigger, run.Status));
        Assert.NotNull(run.WorkerId);
        Assert.Equal(0, run.ItemsFailed);
        Assert.NotEmpty(await InScopeAsync(host, sp => sp.GetRequiredService<OdysseyContext>().JobHeartbeats.Where(h => h.JobType == JobType.FileStorageMaintenance).ToListAsync()));

        Clock.Advance(FileStorageMaintenanceJob.PassInterval);
        await job.RunOnceAsync(default);
        Assert.Equal(2, (await RunsAsync(host)).Count);
    }

    /// <summary>AC 19 and §3.5: a manual request is taken by the poll, not the hourly schedule, and runs one ordinary pass.</summary>
    [Fact]
    public async Task A_manual_request_is_picked_up_by_the_poll_before_the_next_pass_is_due()
    {
        var host = await NewHostAsync();
        var job = host.Services.GetRequiredService<FileStorageMaintenanceJob>();
        await job.RunOnceAsync(default);
        Clock.Advance(TimeSpan.FromMinutes(5));

        var requested = await InScopeAsync(host, async sp =>
        {
            var context = sp.GetRequiredService<OdysseyContext>();
            var now = Clock.GetUtcNow().UtcDateTime;
            var run = new FileStorageMaintenanceJobRun
            {
                JobRunId = Guid.NewGuid(), Trigger = JobRunTrigger.Manual, Status = JobRunStatus.Running,
                StartedAtUtc = now, LastActivityAtUtc = now, RequestedByUserId = null,
            };
            context.JobRuns.Add(run);
            await context.SaveChangesAsync();
            return run.JobRunId;
        });

        await job.RunOnceAsync(default);

        var manual = (await RunsAsync(host)).Single(r => r.JobRunId == requested);
        Assert.Equal(JobRunStatus.Succeeded, manual.Status);
        Assert.NotNull(manual.ClaimedAtUtc);
        Assert.Equal(2, (await RunsAsync(host)).Count);
        var lease = await InScopeAsync(host, sp => sp.GetRequiredService<OdysseyContext>().FileStorageMaintenanceLeases.AsNoTracking().SingleAsync());
        Assert.Equal(Clock.GetUtcNow().UtcDateTime, lease.LastCompletedAtUtc);
    }

    /// <summary>A12, S9: a loser of the lease writes no run row.</summary>
    [Fact]
    public async Task A_tick_that_loses_the_lease_writes_no_row()
    {
        var host = await NewHostAsync();
        var job = host.Services.GetRequiredService<FileStorageMaintenanceJob>();
        Assert.True(await job.TryClaimAsync(Guid.NewGuid(), default));

        await job.RunOnceAsync(default);

        Assert.Empty(await RunsAsync(host));
    }

    private static Task<List<JobRun>> RunsAsync(FileStorageTestHost host) =>
        InScopeAsync(host, sp => sp.GetRequiredService<OdysseyContext>().JobRuns.AsNoTracking().OrderBy(r => r.StartedAtUtc).ToListAsync());
}
