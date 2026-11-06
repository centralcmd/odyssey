using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Odyssey.Context;
using Odyssey.Core.Jobs;
using Odyssey.Core.Journal;
using Odyssey.Dtos.Application;

namespace Odyssey.Core.FileStorage;

/// <summary>
/// The file store's maintenance, run by <c>Odyssey.Worker</c> (issue #346 Amendment A3.1, lease timings from #362
/// Amendment v7.1 §3): a pass every <see cref="PassInterval"/>, measured from the last completed one, and a manual
/// pass the API requested picked up within one <see cref="Tick"/> (issue #409). Every worker replica runs it; the
/// single <see cref="FileStorageMaintenanceLease"/> row elects one runner per pass, before any run row is written. Erasure depends on this job, so its last completed pass is reported on
/// <c>GET /api/file-storage/volumes</c>.
/// </summary>
/// <remarks>
/// <para>
/// The job runs the worker's file-storage startup checks itself, before its first pass
/// (<see cref="FileStorageWorkerVerification"/>): a failing verdict holds this job alone, so the worker's other
/// jobs keep running.
/// </para>
/// <para>
/// Nothing registers it in the API or in any API test host; tests drive <see cref="RunOnceAsync"/> or the
/// sweeper's per-sweep methods directly.
/// </para>
/// </remarks>
public sealed class FileStorageMaintenanceJob(
    IServiceScopeFactory scopes,
    FileStorageWorkerVerification verification,
    FileStorageVolumeRegistry volumes,
    TimeProvider time,
    ILogger<FileStorageMaintenanceJob> logger) : IPeriodicJob
{
    /// <summary>
    /// The job's tick: the request poll (issue #409 §3.1, A3). A manual pass the API requested is picked up within it,
    /// and a scheduled pass runs once <see cref="PassInterval"/> has passed since the last completed one.
    /// </summary>
    public static readonly TimeSpan Tick = TimeSpan.FromSeconds(30);

    /// <summary>The cadence of scheduled passes, measured from the lease's last completed pass (shared by every replica).</summary>
    public static readonly TimeSpan PassInterval = TimeSpan.FromHours(1);

    /// <summary>How long a claim holds; a crashed holder's lease is taken over within this.</summary>
    public static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(5);

    /// <summary>How often a running pass renews its lease.</summary>
    public static readonly TimeSpan RenewEvery = TimeSpan.FromMinutes(1);

    public string Name => "file-storage-maintenance";

    public TimeSpan Interval => Tick;

    public async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        JobRunRecorder recorder;
        await using (var scope = scopes.CreateAsyncScope())
        {
            recorder = scope.ServiceProvider.GetRequiredService<JobRunRecorder>();
        }

        // The heartbeat first (issue #409 §3.3 step 1), and on every held wait: a held process is alive.
        await recorder.HeartbeatAsync(JobType.FileStorageMaintenance, cancellationToken);
        await verification.EnsureVerifiedAsync(cancellationToken, async (check, ct) =>
        {
            await recorder.HeartbeatAsync(JobType.FileStorageMaintenance, ct);
            await recorder.HoldAsync<FileStorageMaintenanceJobRun>(
                JobType.FileStorageMaintenance, JobHeldReason.StorageCheckFailed, run => run.HeldByCheck = check, ct);
        });
        await recorder.ReleaseHoldAsync(JobType.FileStorageMaintenance, cancellationToken);

        // The no-op rule: no volumes, nothing to maintain.
        if (!volumes.IsActive)
        {
            return;
        }

        var manualPending = await ManualRequestPendingAsync(cancellationToken);
        if (!manualPending && !await PassDueAsync(cancellationToken))
        {
            return;
        }

        // Election order (A12, S9): the lease first; only the winner writes a run row, and a loser writes nothing.
        var holder = Guid.NewGuid();
        if (!await TryClaimAsync(holder, cancellationToken))
        {
            logger.LogDebug("File-storage maintenance is held by another runner; skipping this tick.");
            return;
        }

        var completed = false;
        Guid? runId = null;
        try
        {
            runId = manualPending
                ? await recorder.ClaimManualAsync(JobType.FileStorageMaintenance, cancellationToken)
                : null;
            if (runId is null && !await PassDueAsync(cancellationToken))
            {
                // The request was claimed by another replica (or given up) and no scheduled pass is due.
                return;
            }

            runId ??= await StartScheduledRunAsync(recorder.WorkerId, cancellationToken);

            var lastRenewal = time.GetUtcNow();
            await using var scope = scopes.CreateAsyncScope();
            var sweeper = scope.ServiceProvider.GetRequiredService<FileStorageSweeper>();
            FileStorageSweepResult result;
            try
            {
                result = await sweeper.RunAsync(async ct =>
                {
                    if (time.GetUtcNow() - lastRenewal < RenewEvery)
                    {
                        return;
                    }

                    if (!await TryClaimAsync(holder, ct))
                    {
                        throw new InvalidOperationException("The file-storage maintenance lease was lost; this pass stops.");
                    }

                    lastRenewal = time.GetUtcNow();
                    if (runId is { } id)
                    {
                        await recorder.TouchAsync<FileStorageMaintenanceJobRun>(JobType.FileStorageMaintenance, id, null, ct);
                    }
                }, cancellationToken);
            }
            catch (Exception ex)
            {
                if (runId is { } failedId)
                {
                    var category = ex is OperationCanceledException ? JobFailureCategory.Cancelled : JobFailureCategory.Internal;
                    await recorder.FinishAsync<FileStorageMaintenanceJobRun>(
                        JobType.FileStorageMaintenance, failedId, _ => { }, (JobRunStatus.Failed, category), CancellationToken.None);
                }

                throw;
            }

            completed = true;
            if (runId is { } finishedId)
            {
                await recorder.FinishAsync<FileStorageMaintenanceJobRun>(JobType.FileStorageMaintenance, finishedId, run =>
                {
                    run.FilesErased = result.FilesErased;
                    run.AbandonedWritesSwept = result.AbandonedWritesSwept;
                    run.ObjectsScrubbed = result.ObjectsScrubbed;
                    run.IntegrityFailures = result.IntegrityFailures;
                    run.OrphansFound = result.OrphansFound;
                }, outcome: null, CancellationToken.None);
            }
        }
        finally
        {
            await ReleaseAsync(holder, completed);
        }
    }

    /// <summary>A manual pass the API requested and no worker has taken yet.</summary>
    private async Task<bool> ManualRequestPendingAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        var cutoff = time.GetUtcNow().UtcDateTime - JobCatalogue.UnclaimedRequestTimeout;
        return await context.JobRuns.AnyAsync(r => r.JobType == JobType.FileStorageMaintenance
            && r.Status == JobRunStatus.Running && r.Trigger == JobRunTrigger.Manual
            && r.ClaimedAtUtc == null && r.StartedAtUtc >= cutoff, cancellationToken);
    }

    /// <summary>A scheduled pass is due once <see cref="PassInterval"/> has passed since the last completed one, by any replica.</summary>
    private async Task<bool> PassDueAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        var last = await context.FileStorageMaintenanceLeases.AsNoTracking()
            .Where(l => l.Id == FileStorageMaintenanceLease.SingletonId)
            .Select(l => l.LastCompletedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);
        return last is not { } completed || time.GetUtcNow().UtcDateTime - completed >= PassInterval;
    }

    /// <summary>
    /// The scheduled pass's run row, inserted only after the lease is won. A duplicate key on the exclusive guard means
    /// another runner's row is still open: "not elected", logged at Debug, and the pass runs unrecorded rather than not
    /// at all — the lease, not the row, is what elects the runner.
    /// </summary>
    private async Task<Guid?> StartScheduledRunAsync(Guid workerId, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        var now = time.GetUtcNow().UtcDateTime;
        var run = new FileStorageMaintenanceJobRun
        {
            JobRunId = Guid.NewGuid(),
            Trigger = JobRunTrigger.Scheduled,
            Status = JobRunStatus.Running,
            StartedAtUtc = now,
            ClaimedAtUtc = now,
            LastActivityAtUtc = now,
            WorkerId = workerId,
        };
        context.JobRuns.Add(run);
        try
        {
            await context.SaveChangesAsync(cancellationToken);
            return run.JobRunId;
        }
        catch (DbUpdateException ex) when (DbErrors.IsDuplicateKey(ex))
        {
            logger.LogDebug("A file-storage maintenance run row is still open; this pass is not recorded.");
            return null;
        }
    }

    /// <summary>
    /// Claims or renews the lease for <paramref name="holder"/> with a tracked save on <c>Version</c>. A concurrency
    /// failure, or an unexpired lease held by someone else, means another runner has it.
    /// </summary>
    public async Task<bool> TryClaimAsync(Guid holder, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        var now = time.GetUtcNow().UtcDateTime;
        try
        {
            var lease = await context.FileStorageMaintenanceLeases
                .SingleOrDefaultAsync(l => l.Id == FileStorageMaintenanceLease.SingletonId, cancellationToken);
            if (lease is null)
            {
                // The migration seeds the row; a database created without HasData (EF InMemory) gets it here.
                context.FileStorageMaintenanceLeases.Add(new FileStorageMaintenanceLease
                {
                    Id = FileStorageMaintenanceLease.SingletonId, HolderId = holder, LeaseUntilUtc = now + LeaseDuration, Version = Guid.NewGuid(),
                });
                await context.SaveChangesAsync(cancellationToken);
                return true;
            }

            if (lease.HolderId is { } current && current != holder && lease.LeaseUntilUtc > now)
            {
                return false;
            }

            lease.HolderId = holder;
            lease.LeaseUntilUtc = now + LeaseDuration;
            lease.Version = Guid.NewGuid();
            await context.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException)
        {
            // Concurrency (another claim won) or a duplicate key on the first insert.
            return false;
        }
    }

    /// <summary>Clears the holder; a completed pass also stamps <c>LastCompletedAtUtc</c>.</summary>
    private async Task ReleaseAsync(Guid holder, bool completed)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
            var lease = await context.FileStorageMaintenanceLeases.SingleOrDefaultAsync(l => l.Id == FileStorageMaintenanceLease.SingletonId);
            if (lease is null || lease.HolderId != holder)
            {
                return;
            }

            lease.HolderId = null;
            lease.LeaseUntilUtc = null;
            lease.Version = Guid.NewGuid();
            if (completed)
            {
                lease.LastCompletedAtUtc = time.GetUtcNow().UtcDateTime;
            }

            await context.SaveChangesAsync();
        }
        catch (DbUpdateException ex)
        {
            // The lease expires on its own; the next tick re-claims it.
            logger.LogWarning("Releasing the file-storage maintenance lease failed; it expires on its own ({Failure}).", FileStorageLog.Describe(ex));
        }
    }
}
