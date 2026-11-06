using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Odyssey.Context;
using Odyssey.Core.FileStorage;
using Odyssey.Core.Finance;
using Odyssey.Core.Jobs;
using Odyssey.Dtos.Application;
using Odyssey.Dtos.Journal;

namespace Odyssey.Core.Journal.Thumbnails;

/// <summary>This worker's name in heartbeats and restart incidents (Amendment v7.1 §2): the host name, which a restarted container keeps.</summary>
public sealed record PhotoThumbnailWorkerIdentity(string Name)
{
    public static PhotoThumbnailWorkerIdentity FromMachine() =>
        new(Environment.MachineName.Length <= 64 ? Environment.MachineName : Environment.MachineName[..64]);
}

/// <summary>The one call a test must never make for real: ending the process after a bounded pause (§3.6).</summary>
public sealed class PhotoThumbnailProcessHooks
{
    public Action<int> Exit { get; init; } = Environment.Exit;
}

/// <summary>
/// The thumbnail generator (issue #362 §3.3, §3.6, Amendment v7.1), run every 15 s by <c>Odyssey.Worker</c> and
/// nowhere else. Replicas coordinate only through the rows — the claim, <see cref="PhotoThumbnailJobEntry.RunId"/> and
/// <see cref="PhotoThumbnailJobEntry.Generation"/> — and one decode at a time runs per process, on a dedicated
/// background thread under a wall-clock watchdog.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every write to a running job is the run's own</b>: a tracked load, a check that the row is still
/// <c>Running</c> with this run's id, the change, a save, and on a concurrency failure a reload and a fresh decision —
/// uncapped, because each retry needs another write to have landed first and a rename never changes the run's id
/// or state. Only the shutdown release is capped; if it fails, the lease simply expires.
/// </para>
/// <para>
/// Nothing escapes from the self-test or the first touch of the native library: any failure there is
/// <c>PolicyFailed</c>, the job claims nothing, and the worker's other jobs keep running.
/// </para>
/// </remarks>
public sealed class PhotoThumbnailJob : IPeriodicJob, IDisposable
{
    private readonly IServiceScopeFactory scopes;
    private readonly FileStorageVolumeRegistry volumes;
    private readonly IPhotoThumbnailRenderer renderer;
    private readonly IThumbnailDecoderSelfTest selfTest;
    private readonly IPhotoMetadataExtractor extractor;
    private readonly PhotoThumbnailOptions options;
    private readonly PhotoThumbnailWorkerIdentity identity;
    private readonly IHostApplicationLifetime lifetime;
    private readonly PhotoThumbnailProcessHooks hooks;
    private readonly TimeProvider time;
    private readonly ILogger<PhotoThumbnailJob> logger;
    private readonly CancellationTokenRegistration stoppingRegistration;

    private ThumbnailSelfTestResult? selfTestResult;
    private Task? abandonedDecode;
    private DateTime abandonedAtUtc;
    private bool pausedLogged;
    private bool restartRequested;
    private bool restartRefusedLogged;
    private long claims;
    private long ticks;
    private string? lastUploader;
    private ThumbnailWorkerState lastState = ThumbnailWorkerState.Running;
    private ThumbnailTickTally tally = new();
    private Guid? manualRunId;

    public PhotoThumbnailJob(
        IServiceScopeFactory scopes,
        FileStorageVolumeRegistry volumes,
        IPhotoThumbnailRenderer renderer,
        IThumbnailDecoderSelfTest selfTest,
        IPhotoMetadataExtractor extractor,
        IOptions<PhotoThumbnailOptions> options,
        PhotoThumbnailWorkerIdentity identity,
        IHostApplicationLifetime lifetime,
        PhotoThumbnailProcessHooks hooks,
        TimeProvider time,
        ILogger<PhotoThumbnailJob> logger)
    {
        this.scopes = scopes;
        this.volumes = volumes;
        this.renderer = renderer;
        this.selfTest = selfTest;
        this.extractor = extractor;
        this.options = options.Value;
        this.identity = identity;
        this.lifetime = lifetime;
        this.hooks = hooks;
        this.time = time;
        this.logger = logger;

        // A graceful stop removes this worker's heartbeat, so its last state is not reported for 3 minutes (v7.1 §2).
        stoppingRegistration = lifetime.ApplicationStopping.Register(() =>
        {
            try
            {
                DeleteOwnHeartbeatAsync().Wait(TimeSpan.FromSeconds(5));
            }
            catch (Exception ex)
            {
                logger.LogWarning("Removing the thumbnail worker's heartbeat on shutdown failed ({Failure}).", ex.GetType().Name);
            }
        });
    }

    public string Name => "photo-thumbnails";

    public TimeSpan Interval => PhotoThumbnailTiming.Interval;

    /// <summary>The state this worker last reported.</summary>
    public ThumbnailWorkerState State => lastState;

    public void Dispose() => stoppingRegistration.Dispose();

    public async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        ticks++;
        var recorder = Recorder();
        // The run log's heartbeat first (issue #409 §3.3 step 1): keyed by the per-process WorkerId the run rows carry,
        // while PhotoThumbnailWorkerHeartbeats keeps its worker-state role (S-N2).
        await recorder.HeartbeatAsync(JobType.PhotoThumbnails, cancellationToken);
        if (selfTestResult is null)
        {
            selfTestResult = RunSelfTest();
            await RemoveDeadWorkersAsync(cancellationToken);
        }

        var state = PresentState();
        await HeartbeatAsync(state, cancellationToken);
        tally = new ThumbnailTickTally();
        manualRunId = null;
        JobFailureCategory? failure = null;
        try
        {
            state = await RunGatedAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            // A run cut short by shutdown is Cancelled, as the weather and file-storage runs record it — a deploy
            // restart is not an internal error (architect review on PR #418).
            failure = ex is OperationCanceledException ? JobFailureCategory.Cancelled : JobFailureCategory.Internal;
            throw;
        }
        finally
        {
            lastState = state;
            await HeartbeatAsync(state, CancellationToken.None);
            await RecordTickAsync(recorder, state, failure);
            if (ticks % PhotoThumbnailTiming.CleanupEveryNthTick == 0)
            {
                await CleanUpAsync();
            }
        }
    }

    private JobRunRecorder Recorder()
    {
        using var scope = scopes.CreateScope();
        return scope.ServiceProvider.GetRequiredService<JobRunRecorder>();
    }

    /// <summary>
    /// The tick into the run log (issue #409 §3.2–§3.3): a held state opens (or keeps) this process's held period with
    /// its reason; otherwise a manual run is finished with the tick's counts, a tick that did work extends the burst,
    /// and an idle tick closes it.
    /// </summary>
    private async Task RecordTickAsync(JobRunRecorder recorder, ThumbnailWorkerState state, JobFailureCategory? failure)
    {
        var counts = tally;
        if (manualRunId is { } runId)
        {
            await recorder.FinishAsync<PhotoThumbnailJobRun>(JobType.PhotoThumbnails, runId, counts.AddTo,
                failure is { } category ? (JobRunStatus.Failed, category) : null, CancellationToken.None);
        }
        else if (counts.HasWork)
        {
            // Work done before a gate held the tick (a reclaimed crash, say) still lands in the burst it belongs to.
            await recorder.ExtendBurstAsync<PhotoThumbnailJobRun>(JobType.PhotoThumbnails, counts.AddTo, CancellationToken.None);
        }

        var held = HeldReasonOf(state);
        if (held is null && (counts.HasWork || manualRunId is not null))
        {
            await recorder.ReleaseHoldAsync(JobType.PhotoThumbnails, CancellationToken.None);
            return;
        }

        await recorder.CloseBurstAsync(JobType.PhotoThumbnails, CancellationToken.None);
        if (held is { } reason)
        {
            await recorder.HoldAsync<PhotoThumbnailJobRun>(JobType.PhotoThumbnails, reason, null, CancellationToken.None);
        }
        else
        {
            await recorder.ReleaseHoldAsync(JobType.PhotoThumbnails, CancellationToken.None);
        }
    }

    /// <summary>The held reason of a worker state (A6); <c>null</c> for a running worker.</summary>
    public static JobHeldReason? HeldReasonOf(ThumbnailWorkerState state) => state switch
    {
        ThumbnailWorkerState.Paused => JobHeldReason.DecoderPaused,
        ThumbnailWorkerState.Tripped => JobHeldReason.BreakerTripped,
        ThumbnailWorkerState.PolicyFailed => JobHeldReason.DecoderPolicyFailed,
        ThumbnailWorkerState.StorageUnavailable => JobHeldReason.StorageUnavailable,
        _ => null,
    };

    private async Task<ThumbnailWorkerState> RunGatedAsync(CancellationToken cancellationToken)
    {
        if (selfTestResult is { Ok: false })
        {
            return ThumbnailWorkerState.PolicyFailed;
        }

        // The store is not verified in this process (yet, or at all): nothing can be written.
        if (!volumes.IsActive)
        {
            return ThumbnailWorkerState.StorageUnavailable;
        }

        await DiscoverAsync(cancellationToken);
        await ReclaimExpiredAsync(cancellationToken);

        if (!await StorageHealthyAsync(cancellationToken))
        {
            return ThumbnailWorkerState.StorageUnavailable;
        }

        if (await BreakerTrippedAsync(cancellationToken))
        {
            return ThumbnailWorkerState.Tripped;
        }

        if (DecodeStillAbandoned())
        {
            await ConsiderRestartAsync(cancellationToken);
            return ThumbnailWorkerState.Paused;
        }

        // A manual run the API requested (issue #409 §3.5) is claimed only by a worker that is not held: it drains
        // what is due into its own row, then re-queues each Failed job once.
        manualRunId = await Recorder().ClaimManualAsync(JobType.PhotoThumbnails, cancellationToken);

        var started = time.GetUtcNow();
        while (!cancellationToken.IsCancellationRequested && time.GetUtcNow() - started < PhotoThumbnailTiming.RunBudget)
        {
            if (await ClaimAsync(cancellationToken) is not { } claimed)
            {
                break;
            }

            await RunJobAsync(claimed, cancellationToken);
            if (DecodeStillAbandoned())
            {
                return ThumbnailWorkerState.Paused;
            }
        }

        if (manualRunId is not null)
        {
            tally.Requeued += await RequeueFailedAsync(cancellationToken);
        }

        return ThumbnailWorkerState.Running;
    }

    /// <summary>
    /// The manual run's re-queue (issue #409 §3.5, A-N2, S-N1): every <c>Failed</c> job never re-queued by hand goes
    /// back to <c>Pending</c> with its attempts reset, once — <see cref="PhotoThumbnailJobEntry.ManualRequeues"/> is
    /// never reset, and an <c>Unsupported</c> job (a crash or a timeout included) is never touched. One conditional
    /// save per row on its <see cref="PhotoThumbnailJobEntry.Generation"/>, so it cannot race a reclaim or a rename.
    /// </summary>
    internal async Task<int> RequeueFailedAsync(CancellationToken cancellationToken)
    {
        List<Guid> ids;
        await using (var scope = scopes.CreateAsyncScope())
        {
            ids = await scope.ServiceProvider.GetRequiredService<OdysseyContext>().PhotoThumbnailJobs
                .Where(j => j.State == PhotoThumbnailJobState.Failed && j.ManualRequeues == 0)
                .OrderBy(j => j.UpdatedAtUtc)
                .Select(j => j.PhotoId)
                .Take(PhotoThumbnailTiming.DiscoveryBatch)
                .ToListAsync(cancellationToken);
        }

        var requeued = 0;
        foreach (var photoId in ids)
        {
            await using var scope = scopes.CreateAsyncScope();
            var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
            var job = await context.PhotoThumbnailJobs.FirstOrDefaultAsync(j => j.PhotoId == photoId, cancellationToken);
            if (job is not { State: PhotoThumbnailJobState.Failed, ManualRequeues: 0 })
            {
                continue;
            }

            var now = Now;
            job.State = PhotoThumbnailJobState.Pending;
            job.Attempts = 0;
            job.ManualRequeues = 1;
            job.FailureCode = null;
            job.NextAttemptAtUtc = now;
            Touch(job, now);
            try
            {
                await context.SaveChangesAsync(cancellationToken);
                requeued++;
            }
            catch (DbUpdateConcurrencyException)
            {
                // Another write moved it first; it is left as that write made it.
            }
        }

        if (requeued > 0)
        {
            logger.LogInformation("A manual thumbnail run re-queued {Count} failed jobs.", requeued);
        }

        return requeued;
    }

    private ThumbnailWorkerState PresentState() =>
        selfTestResult is { Ok: false } ? ThumbnailWorkerState.PolicyFailed
        : DecodeStillAbandoned() ? ThumbnailWorkerState.Paused
        : lastState;

    // ── Self-test ─────────────────────────────────────────────────────────────────────────────────────

    private ThumbnailSelfTestResult RunSelfTest()
    {
        try
        {
            var result = selfTest.Run();
            if (!result.Ok)
            {
                logger.LogCritical(
                    "The thumbnail decoder failed its security self-test at probe {Probe}; no thumbnails are made until the worker is redeployed from a correct image.",
                    result.FailedProbe);
            }

            return result;
        }
        catch (Exception ex)
        {
            // A missing native library (DllNotFoundException, TypeInitializationException) lands here too.
            logger.LogCritical("The thumbnail decoder could not start ({Failure}); no thumbnails are made.", ex.GetType().Name);
            return new ThumbnailSelfTestResult(ex.GetType().Name);
        }
    }

    // ── Discovery and recipe sweep ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The backfill and the recipe sweep (§3.3 step 7): a job for every photo without one, and a reset for every
    /// finished job attempted under an older recipe — except a file that crashed or hung the decoder.
    /// </summary>
    internal async Task DiscoverAsync(CancellationToken cancellationToken)
    {
        var now = Now;
        await using (var scope = scopes.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
            var missing = await context.Photos
                .Where(p => !context.PhotoThumbnailJobs.Any(j => j.PhotoId == p.PhotoId))
                .OrderBy(p => p.CreatedAt)
                .Select(p => p.PhotoId)
                .Take(PhotoThumbnailTiming.DiscoveryBatch)
                .ToListAsync(cancellationToken);
            if (missing.Count > 0)
            {
                context.PhotoThumbnailJobs.AddRange(missing.Select(id => NewBackgroundJob(id, now)));
                try
                {
                    await context.SaveChangesAsync(cancellationToken);
                }
                catch (DbUpdateException)
                {
                    // Another replica won some of them (v7.1 §1): insert one at a time, each conflict a success.
                    await InsertOneByOneAsync(missing, now, cancellationToken);
                }
            }
        }

        await using (var scope = scopes.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
            var stale = await context.PhotoThumbnailJobs
                .Where(j => (j.State == PhotoThumbnailJobState.Done
                        || j.State == PhotoThumbnailJobState.Failed
                        || (j.State == PhotoThumbnailJobState.Unsupported
                            && j.FailureCode != PhotoThumbnailFailure.Crash
                            && j.FailureCode != PhotoThumbnailFailure.Timeout))
                    && j.AttemptedRecipeVersion < PhotoThumbnailRecipe.Version)
                .Take(PhotoThumbnailTiming.DiscoveryBatch)
                .ToListAsync(cancellationToken);
            foreach (var job in stale)
            {
                job.State = PhotoThumbnailJobState.Pending;
                job.Priority = PhotoThumbnailJobPriority.Background;
                job.Attempts = 0;
                job.FailureCode = null;
                job.NextAttemptAtUtc = now;
                Touch(job, now);
            }

            if (stale.Count > 0)
            {
                try
                {
                    await context.SaveChangesAsync(cancellationToken);
                }
                catch (DbUpdateConcurrencyException)
                {
                    // A concurrent write moved one of them; the next tick sees its new state.
                }
            }
        }
    }

    private async Task InsertOneByOneAsync(IReadOnlyList<Guid> photoIds, DateTime now, CancellationToken cancellationToken)
    {
        foreach (var photoId in photoIds)
        {
            await using var scope = scopes.CreateAsyncScope();
            var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
            context.PhotoThumbnailJobs.Add(NewBackgroundJob(photoId, now));
            try
            {
                await context.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException)
            {
                // The job exists (another replica), or the photo has gone: either way, nothing to do.
            }
        }
    }

    private static PhotoThumbnailJobEntry NewBackgroundJob(Guid photoId, DateTime now) => new()
    {
        PhotoId = photoId,
        State = PhotoThumbnailJobState.Pending,
        Priority = PhotoThumbnailJobPriority.Background,
        NextAttemptAtUtc = now,
        UpdatedAtUtc = now,
    };

    // ── Lease reclaim ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A running job whose lease expired most likely lost its process (§3.3 step 6): one more expiry, a <c>Crash</c>
    /// incident, and the job back to <c>Pending</c> — or, on its second expiry, <c>Unsupported (crash)</c>.
    /// </summary>
    internal async Task ReclaimExpiredAsync(CancellationToken cancellationToken)
    {
        List<Guid> expired;
        await using (var scope = scopes.CreateAsyncScope())
        {
            var now = Now;
            expired = await scope.ServiceProvider.GetRequiredService<OdysseyContext>().PhotoThumbnailJobs
                .Where(j => j.State == PhotoThumbnailJobState.Running && j.LeaseUntilUtc < now)
                .Select(j => j.PhotoId)
                .Take(100)
                .ToListAsync(cancellationToken);
        }

        foreach (var photoId in expired)
        {
            await ReclaimAsync(photoId, cancellationToken);
        }
    }

    private async Task ReclaimAsync(Guid photoId, CancellationToken cancellationToken)
    {
        while (true)
        {
            await using var scope = scopes.CreateAsyncScope();
            var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
            var now = Now;
            var job = await context.PhotoThumbnailJobs.FirstOrDefaultAsync(j => j.PhotoId == photoId, cancellationToken);
            if (job is not { State: PhotoThumbnailJobState.Running } || job.LeaseUntilUtc >= now)
            {
                return;
            }

            // The incident first, so its id can be stored on the job for the one proof-of-life give-back. If the job
            // write then loses a race, the incident is taken back before deciding again.
            var incident = new PhotoThumbnailIncident { Kind = PhotoThumbnailIncidentKind.Crash, OccurredAtUtc = now };
            context.PhotoThumbnailIncidents.Add(incident);
            await context.SaveChangesAsync(cancellationToken);

            job.LeaseExpiries++;
            job.ReclaimedRunId = job.RunId;
            job.ReclaimIncidentId = incident.Id;
            job.RunId = null;
            job.RerunRequested = false;
            job.LeaseUntilUtc = null;
            if (job.LeaseExpiries >= PhotoThumbnailTiming.MaxLeaseExpiries)
            {
                job.State = PhotoThumbnailJobState.Unsupported;
                job.FailureCode = PhotoThumbnailFailure.Crash;
            }
            else
            {
                job.State = PhotoThumbnailJobState.Pending;
                job.NextAttemptAtUtc = now;
            }

            Touch(job, now);
            try
            {
                await context.SaveChangesAsync(cancellationToken);
                tally.Crashes++;
                logger.LogWarning(
                    "Thumbnail job for photo {PhotoId} lost its lease (expiry {Expiries}); its worker most likely died mid-decode.",
                    photoId, job.LeaseExpiries);
                return;
            }
            catch (DbUpdateConcurrencyException)
            {
                context.ChangeTracker.Clear();
                context.PhotoThumbnailIncidents.Remove(incident);
                await context.SaveChangesAsync(cancellationToken);
                await JitterAsync(cancellationToken);
            }
        }
    }

    // ── Gates ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The health gate (§3.3 step 2.3): the bound volume must be <c>Available</c>. A degraded one is probed first, so
    /// claiming resumes within one tick of it answering again rather than waiting for the hourly maintenance probe.
    /// </summary>
    private async Task<bool> StorageHealthyAsync(CancellationToken cancellationToken)
    {
        if (!volumes.Bindings.TryGetValue(PhotoThumbnailStorage.FeatureKey, out var name) || volumes.Find(name) is not { } volume)
        {
            return false;
        }

        return !volume.Degraded || await volumes.ProbeAsync(volume, cancellationToken);
    }

    /// <summary>
    /// The global breaker (§3.6): 3 or more crashes or hangs within an hour stop claiming for an hour from the latest
    /// of them. It counts incidents, not jobs, and the incidents carry no photo id, so a delete cannot reset it.
    /// </summary>
    internal async Task<bool> BreakerTrippedAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var now = Now;
        if (await PhotoThumbnailBreaker.ResumesAtAsync(scope.ServiceProvider.GetRequiredService<OdysseyContext>(), now, cancellationToken) is null)
        {
            return false;
        }

        logger.LogCritical(
            "Thumbnail creation is paused: {Threshold} or more decoder crashes or hangs within an hour. It resumes an hour after the latest.",
            PhotoThumbnailTiming.BreakerThreshold);
        return true;
    }

    private bool DecodeStillAbandoned()
    {
        if (abandonedDecode is null)
        {
            return false;
        }

        if (!abandonedDecode.IsCompleted)
        {
            return true;
        }

        logger.LogInformation("The abandoned thumbnail decode has ended; claiming resumes.");
        abandonedDecode = null;
        pausedLogged = false;
        restartRefusedLogged = false;
        return false;
    }

    /// <summary>
    /// The bounded pause (§3.6 item 4): a decode thread alive after 10 minutes restarts the worker — at most once an
    /// hour per worker, counted by host name so the restart it caused cannot reset the count (v7.1 §2).
    /// </summary>
    private async Task ConsiderRestartAsync(CancellationToken cancellationToken)
    {
        if (!pausedLogged)
        {
            logger.LogCritical("A thumbnail decode hung and was abandoned; claiming is paused until its thread ends.");
            pausedLogged = true;
        }

        var now = Now;
        if (restartRequested || now - abandonedAtUtc < PhotoThumbnailTiming.BoundedPause)
        {
            return;
        }

        await using var scope = scopes.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        var since = now - PhotoThumbnailTiming.RestartWindow;
        if (await context.PhotoThumbnailIncidents.AnyAsync(
                i => i.Kind == PhotoThumbnailIncidentKind.Restart && i.WorkerName == identity.Name && i.OccurredAtUtc > since,
                cancellationToken))
        {
            if (!restartRefusedLogged)
            {
                logger.LogCritical(
                    "A thumbnail decode is still hung after {Pause}, but this worker already restarted within the hour; it stays paused until the thread ends or it is restarted by hand.",
                    PhotoThumbnailTiming.BoundedPause);
                restartRefusedLogged = true;
            }

            return;
        }

        context.PhotoThumbnailIncidents.Add(new PhotoThumbnailIncident
        {
            Kind = PhotoThumbnailIncidentKind.Restart, OccurredAtUtc = now, WorkerName = identity.Name,
        });
        await context.SaveChangesAsync(cancellationToken);
        restartRequested = true;
        lastState = ThumbnailWorkerState.Paused;
        await HeartbeatAsync(ThumbnailWorkerState.Paused, cancellationToken);
        logger.LogCritical(
            "A thumbnail decode is still hung after {Pause}; restarting the worker to free it. The API is not affected.",
            PhotoThumbnailTiming.BoundedPause);
        lifetime.StopApplication();

        // The decode thread is a background thread and cannot hold the process open; if the host has not stopped
        // within the grace, the process ends anyway.
        _ = Task.Delay(PhotoThumbnailTiming.ExitGrace, time).ContinueWith(_ => hooks.Exit(70), TaskScheduler.Default);
    }

    // ── Claim ─────────────────────────────────────────────────────────────────────────────────────────

    internal sealed record Claimed(Guid PhotoId, Guid RunId);

    /// <summary>
    /// Takes one due job (§3.3 step 3): every 4th claim from the background lane when it has one, otherwise the
    /// interactive lane round-robin across uploaders. A claim another replica won moves on to the next candidate.
    /// </summary>
    internal async Task<Claimed?> ClaimAsync(CancellationToken cancellationToken)
    {
        claims++;
        var backgroundTurn = claims % PhotoThumbnailTiming.BackgroundEveryNthClaim == 0;
        var excluded = new HashSet<Guid>();
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var candidate = backgroundTurn
                ? await NextBackgroundAsync(excluded, cancellationToken) ?? await NextInteractiveAsync(excluded, cancellationToken)
                : await NextInteractiveAsync(excluded, cancellationToken) ?? await NextBackgroundAsync(excluded, cancellationToken);
            if (candidate is not { } photoId)
            {
                return null;
            }

            if (await TryClaimAsync(photoId, cancellationToken) is { } claimed)
            {
                return claimed;
            }

            excluded.Add(photoId);
        }

        return null;
    }

    private async Task<Guid?> NextBackgroundAsync(HashSet<Guid> excluded, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var now = Now;
        return await scope.ServiceProvider.GetRequiredService<OdysseyContext>().PhotoThumbnailJobs
            .Where(j => j.State == PhotoThumbnailJobState.Pending
                && j.Priority == PhotoThumbnailJobPriority.Background
                && j.NextAttemptAtUtc <= now
                && !excluded.Contains(j.PhotoId))
            .OrderBy(j => j.NextAttemptAtUtc)
            .ThenBy(j => j.PhotoId)
            .Select(j => (Guid?)j.PhotoId)
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <summary>Each uploader's oldest due interactive job, then round-robin after the uploader claimed last.</summary>
    private async Task<Guid?> NextInteractiveAsync(HashSet<Guid> excluded, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        var now = Now;
        var due = context.PhotoThumbnailJobs
            .Where(j => j.State == PhotoThumbnailJobState.Pending
                && j.Priority == PhotoThumbnailJobPriority.Interactive
                && j.NextAttemptAtUtc <= now
                && !excluded.Contains(j.PhotoId))
            .Join(context.Photos, j => j.PhotoId, p => p.PhotoId, (j, p) => new { j.PhotoId, j.NextAttemptAtUtc, Uploader = p.CreatedByUserId ?? string.Empty });

        var uploaders = await due
            .GroupBy(r => r.Uploader)
            .Select(g => new { Uploader = g.Key, Oldest = g.Min(r => r.NextAttemptAtUtc) })
            .OrderBy(g => g.Oldest)
            .Take(PhotoThumbnailTiming.MaxUploadersConsidered)
            .ToListAsync(cancellationToken);
        if (uploaders.Count == 0)
        {
            return null;
        }

        var ordered = uploaders.Select(u => u.Uploader).OrderBy(u => u, StringComparer.Ordinal).ToList();
        var next = lastUploader is null
            ? ordered[0]
            : ordered.FirstOrDefault(u => string.CompareOrdinal(u, lastUploader) > 0) ?? ordered[0];
        lastUploader = next;

        return await due
            .Where(r => r.Uploader == next)
            .OrderBy(r => r.NextAttemptAtUtc)
            .ThenBy(r => r.PhotoId)
            .Select(r => (Guid?)r.PhotoId)
            .FirstOrDefaultAsync(cancellationToken);
    }

    private async Task<Claimed?> TryClaimAsync(Guid photoId, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        var now = Now;
        var job = await context.PhotoThumbnailJobs.FirstOrDefaultAsync(j => j.PhotoId == photoId, cancellationToken);
        if (job is not { State: PhotoThumbnailJobState.Pending } || job.NextAttemptAtUtc > now)
        {
            return null;
        }

        var runId = Guid.NewGuid();
        job.State = PhotoThumbnailJobState.Running;
        job.RunId = runId;
        job.LeaseUntilUtc = now + PhotoThumbnailTiming.Lease;
        job.Attempts++;
        job.AttemptedRecipeVersion = PhotoThumbnailRecipe.Version;
        job.LastClaimedAtUtc = now;
        Touch(job, now);
        try
        {
            await context.SaveChangesAsync(cancellationToken);
            return new Claimed(photoId, runId);
        }
        catch (DbUpdateConcurrencyException)
        {
            return null;
        }
    }

    // ── One run ───────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Renders and stores one claimed job (§3.3 step 4) and writes its outcome (§9). The per-job deadline bounds the
    /// read and the writes, never the decode, which has its own watchdog.
    /// </summary>
    internal async Task RunJobAsync(Claimed claimed, CancellationToken cancellationToken)
    {
        using var deadline = new CancellationTokenSource(PhotoThumbnailTiming.JobDeadline, time);
        using var renewal = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var renewing = RenewLeaseAsync(claimed, renewal.Token);
        try
        {
            await RenderAndStoreAsync(claimed, deadline.Token, renewal, renewing, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await ReleaseAsync(claimed, PhotoThumbnailTiming.ShutdownReleaseAttempts, "the worker is stopping");
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            logger.LogWarning("Thumbnail job for photo {PhotoId} passed its {Deadline} deadline outside the decode; it is released.",
                claimed.PhotoId, PhotoThumbnailTiming.JobDeadline);
            await ReleaseAsync(claimed, maxAttempts: null, "the per-job deadline passed");
        }
        catch (Exception ex) when (IsOutage(ex))
        {
            logger.LogWarning("Thumbnail job for photo {PhotoId} hit an outage ({Failure}); it is retried.", claimed.PhotoId, ex.GetType().Name);
            await OutageAsync(claimed);
        }
        catch (Exception ex)
        {
            logger.LogWarning("Thumbnail job for photo {PhotoId} failed ({Failure}).", claimed.PhotoId, ex.GetType().Name);
            await CountedFailureAsync(claimed);
        }
        finally
        {
            await renewal.CancelAsync();
            await SwallowAsync(renewing);
        }
    }

    private async Task RenderAndStoreAsync(
        Claimed claimed, CancellationToken deadline, CancellationTokenSource renewal, Task renewing, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(deadline, cancellationToken);

        var source = await context.Photos
            .Where(p => p.PhotoId == claimed.PhotoId)
            .Join(context.FileMetadata, p => p.FileId, f => f.Id, (p, f) => new { p.FileId, f.ContentType, f.FileName })
            .FirstOrDefaultAsync(bounded.Token);
        if (source is null)
        {
            // SourceMissing: the photo or its file has gone, and the job cascades with it.
            return;
        }

        if (PhotoThumbnailLimits.Pin(source.ContentType) is not { } format)
        {
            await UnsupportedAsync(claimed, PhotoThumbnailFailure.Type);
            return;
        }

        var reader = scope.ServiceProvider.GetRequiredService<IImageContentReader>();
        var content = await reader.ReadAllAsync(source.FileId, PhotoThumbnailRecipe.MaxSourceBytes, bounded.Token).WaitAsync(bounded.Token);
        if (content is null)
        {
            return;
        }

        if (content.TooLarge)
        {
            await UnsupportedAsync(claimed, PhotoThumbnailFailure.TooLarge);
            return;
        }

        var orientation = await ReadOrientationAsync(content.Bytes);
        var prepared = PhotoThumbnailPreStrip.Prepare(content.Bytes, format);
        content = null;
        if (prepared.FailureCode is { } preFailure)
        {
            await UnsupportedAsync(claimed, preFailure);
            return;
        }

        var (finished, result) = await RenderUnderWatchdogAsync(new ThumbnailRenderRequest(prepared.Bytes, format, orientation));
        if (!finished)
        {
            await TimedOutAsync(claimed);
            return;
        }

        if (result!.FailureCode is { } renderFailure || result.Medium is null || result.Small is null)
        {
            await UnsupportedAsync(claimed, result.FailureCode ?? PhotoThumbnailFailure.Decode);
            return;
        }

        if (await AllocateBucketAsync(bounded.Token) is not { } bucket)
        {
            await ReleaseAsync(claimed, maxAttempts: null, "the bucket counter stayed contended");
            return;
        }

        var store = scope.ServiceProvider.GetRequiredService<IObjectStore>();
        var medium = await PutAsync(store, result.Medium, ThumbnailSize.Medium, bucket, source.FileName, bounded.Token);
        var small = await PutAsync(store, result.Small, ThumbnailSize.Small, bucket, source.FileName, bounded.Token);

        // The renewal stops before the final save, so the two never race on the run's own row.
        await renewal.CancelAsync();
        await SwallowAsync(renewing);
        await FinishAsync(context, store, claimed, [(ThumbnailSize.Medium, medium, result.Medium), (ThumbnailSize.Small, small, result.Small)], bounded.Token);
    }

    private static async Task<StoredObject> PutAsync(
        IObjectStore store, ThumbnailRendition rendition, ThumbnailSize size, int bucket, string fileName, CancellationToken cancellationToken)
    {
        using var stream = new MemoryStream(rendition.WebP, writable: false);
        return await store.PutAsync(
                PhotoThumbnailStorage.FeatureKey, new PhotoThumbnailLayout(size, bucket), fileName, null, "image/webp",
                stream, PhotoThumbnailRecipe.MaxOutputBytes, cancellationToken)
            .WaitAsync(cancellationToken);
    }

    /// <summary>
    /// The final save (§3.3 step 4.6): both rows upserted, the replaced objects released, the job <c>Done</c> — or,
    /// if a rename landed meanwhile, the run superseded; if the run was reclaimed, only the give-back. Either way a
    /// run that does not finish releases its own new objects. Retried, uncapped, on any conflict but a swept object.
    /// </summary>
    private async Task FinishAsync(
        OdysseyContext context, IObjectStore store, Claimed claimed,
        IReadOnlyList<(ThumbnailSize Size, StoredObject Object, ThumbnailRendition Rendition)> outputs,
        CancellationToken cancellationToken)
    {
        var ownObjectIds = outputs.Select(o => o.Object.Id).ToHashSet();
        while (true)
        {
            var now = Now;
            var job = await context.PhotoThumbnailJobs.FirstOrDefaultAsync(j => j.PhotoId == claimed.PhotoId, cancellationToken);
            if (job is null)
            {
                // The photo was deleted mid-run: its job cascaded away. The new objects go with it.
                ReleaseOwn(store, outputs);
            }
            else if (job.State != PhotoThumbnailJobState.Running || job.RunId != claimed.RunId)
            {
                ReleaseOwn(store, outputs);
                await context.SaveChangesAsync(cancellationToken);
                await GiveBackExpiryAsync(claimed, cancellationToken);
                return;
            }
            else if (job.RerunRequested)
            {
                // Superseded by a rename: the attempt is given back and the re-run waits out the cooldown.
                ReleaseOwn(store, outputs);
                job.State = PhotoThumbnailJobState.Pending;
                job.Priority = PhotoThumbnailJobPriority.Background;
                job.NextAttemptAtUtc = PhotoThumbnailService.CooldownFrom(job, now);
                job.Attempts = Math.Max(0, job.Attempts - 1);
                EndRun(job, now);
            }
            else
            {
                var existing = await context.PhotoThumbnails.Where(t => t.PhotoId == claimed.PhotoId).ToListAsync(cancellationToken);
                foreach (var (size, storedObject, rendition) in outputs)
                {
                    var row = existing.FirstOrDefault(t => t.Size == size);
                    if (row is null)
                    {
                        context.PhotoThumbnails.Add(new PhotoThumbnail
                        {
                            PhotoThumbnailId = Guid.NewGuid(),
                            PhotoId = claimed.PhotoId,
                            Size = size,
                            StoredObjectId = storedObject.Id,
                            PixelWidth = rendition.Width,
                            PixelHeight = rendition.Height,
                            RecipeVersion = PhotoThumbnailRecipe.Version,
                            CreatedAtUtc = now,
                        });
                        continue;
                    }

                    // Updated in place: a delete and re-insert of (PhotoId, Size) in one save is not ordered by EF.
                    if (row.StoredObjectId != storedObject.Id
                        && await context.StoredObjects.FirstOrDefaultAsync(o => o.Id == row.StoredObjectId, cancellationToken) is { State: StoredObjectState.Committed } replaced)
                    {
                        store.StageRelease(replaced, immediate: true);
                    }

                    row.StoredObjectId = storedObject.Id;
                    row.PixelWidth = rendition.Width;
                    row.PixelHeight = rendition.Height;
                    row.RecipeVersion = PhotoThumbnailRecipe.Version;
                    row.CreatedAtUtc = now;
                }

                job.State = PhotoThumbnailJobState.Done;
                job.FailureCode = null;
                EndRun(job, now);
            }

            try
            {
                await context.SaveChangesAsync(cancellationToken);
                if (job is { State: PhotoThumbnailJobState.Done })
                {
                    tally.Rendered++;
                }

                return;
            }
            catch (DbUpdateConcurrencyException ex)
            {
                if (ex.Entries.Any(e => e.Entity is StoredObject o && ownObjectIds.Contains(o.Id)))
                {
                    // Our own reservation was swept: the files are gone. An outage for this job, not the image's fault.
                    throw new FileStorageUnavailableException("A thumbnail reservation was swept before it was saved.");
                }

                await ResetForRetryAsync(context, ownObjectIds, cancellationToken);
                await JitterAsync(cancellationToken);
            }
            catch (DbUpdateException) when (job is null)
            {
                // The photo went between the load and the save (an FK failure): SourceMissing.
                return;
            }
        }
    }

    /// <summary>Discards everything staged but our own new objects, so the next attempt decides from the database.</summary>
    private static async Task ResetForRetryAsync(OdysseyContext context, HashSet<Guid> ownObjectIds, CancellationToken cancellationToken)
    {
        foreach (var entry in context.ChangeTracker.Entries().ToList())
        {
            if (entry.Entity is StoredObject o && ownObjectIds.Contains(o.Id))
            {
                continue;
            }

            if (entry.State == EntityState.Added)
            {
                entry.State = EntityState.Detached;
            }
            else if (entry.State is EntityState.Modified or EntityState.Deleted or EntityState.Unchanged)
            {
                await entry.ReloadAsync(cancellationToken);
            }
        }
    }

    private static void ReleaseOwn(IObjectStore store, IEnumerable<(ThumbnailSize Size, StoredObject Object, ThumbnailRendition Rendition)> outputs)
    {
        foreach (var (_, storedObject, _) in outputs)
        {
            if (storedObject.State == StoredObjectState.Committed)
            {
                store.StageRelease(storedObject, immediate: true);
            }
        }
    }

    /// <summary>
    /// Proof of life (§3.3 step 6): a run that was reclaimed while its worker was alive all along gives the expiry
    /// back, once — the reclaim recorded which run it took over, and a crashed process never comes back to say so.
    /// </summary>
    private async Task GiveBackExpiryAsync(Claimed claimed, CancellationToken cancellationToken)
    {
        while (true)
        {
            await using var scope = scopes.CreateAsyncScope();
            var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
            var job = await context.PhotoThumbnailJobs.FirstOrDefaultAsync(j => j.PhotoId == claimed.PhotoId, cancellationToken);
            if (job is null || job.ReclaimedRunId != claimed.RunId)
            {
                return;
            }

            // The count only: a job the reclaim already ended Unsupported (crash) stays so — the give-back must never
            // re-arm a file that crashes the decoder.
            job.LeaseExpiries = Math.Max(0, job.LeaseExpiries - 1);

            if (job.ReclaimIncidentId is { } incidentId
                && await context.PhotoThumbnailIncidents.FirstOrDefaultAsync(i => i.Id == incidentId, cancellationToken) is { } incident)
            {
                context.PhotoThumbnailIncidents.Remove(incident);
            }

            job.ReclaimedRunId = null;
            job.ReclaimIncidentId = null;
            Touch(job, Now);
            try
            {
                await context.SaveChangesAsync(cancellationToken);
                logger.LogInformation("Thumbnail job for photo {PhotoId} was reclaimed while its worker was alive; the expiry is given back.", claimed.PhotoId);
                return;
            }
            catch (DbUpdateConcurrencyException)
            {
                await JitterAsync(cancellationToken);
            }
        }
    }

    // ── Run-scoped outcome writes ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Applies <paramref name="apply"/> while the job is still this run's, reloading and deciding again on a conflict.
    /// Returns false when the run is no longer ours, in which case only the give-back is attempted.
    /// </summary>
    private async Task<bool> WriteRunAsync(Claimed claimed, Action<PhotoThumbnailJobEntry, DateTime> apply, int? maxAttempts = null)
    {
        for (var attempt = 1; ; attempt++)
        {
            await using var scope = scopes.CreateAsyncScope();
            var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
            var job = await context.PhotoThumbnailJobs.FirstOrDefaultAsync(j => j.PhotoId == claimed.PhotoId);
            if (job is null)
            {
                return false;
            }

            if (job.State != PhotoThumbnailJobState.Running || job.RunId != claimed.RunId)
            {
                await GiveBackExpiryAsync(claimed, CancellationToken.None);
                return false;
            }

            var now = Now;
            apply(job, now);
            Touch(job, now);
            try
            {
                await context.SaveChangesAsync();
                return true;
            }
            catch (DbUpdateConcurrencyException) when (maxAttempts is null || attempt < maxAttempts)
            {
                await JitterAsync(CancellationToken.None);
            }
        }
    }

    private async Task<bool> UnsupportedAsync(Claimed claimed, string code)
    {
        logger.LogWarning("Thumbnail job for photo {PhotoId} is unsupported ({Code}).", claimed.PhotoId, code);
        var landed = await WriteRunAsync(claimed, (job, now) =>
        {
            job.State = PhotoThumbnailJobState.Unsupported;
            job.FailureCode = code;
            EndRun(job, now);
        });
        if (landed)
        {
            tally.Failed++;
        }

        return landed;
    }

    /// <summary>A storage or database outage is not the image's fault: the attempt is given back (§3.3 step 5).</summary>
    private Task<bool> OutageAsync(Claimed claimed) => WriteRunAsync(claimed, (job, now) =>
    {
        job.State = PhotoThumbnailJobState.Pending;
        job.Attempts = Math.Max(0, job.Attempts - 1);
        job.NextAttemptAtUtc = now + PhotoThumbnailTiming.OutageBackoff;
        EndRun(job, now);
    });

    private async Task<bool> CountedFailureAsync(Claimed claimed)
    {
        var landed = await CountedFailureWriteAsync(claimed);
        if (landed)
        {
            tally.Failed++;
        }

        return landed;
    }

    private Task<bool> CountedFailureWriteAsync(Claimed claimed) => WriteRunAsync(claimed, (job, now) =>
    {
        if (job.Attempts >= PhotoThumbnailTiming.MaxCountedAttempts)
        {
            job.State = PhotoThumbnailJobState.Failed;
            job.FailureCode = PhotoThumbnailFailure.Exhausted;
        }
        else
        {
            job.State = PhotoThumbnailJobState.Pending;
            job.NextAttemptAtUtc = now + PhotoThumbnailTiming.Backoff(job.Attempts);
        }

        EndRun(job, now);
    });

    /// <summary>A shutdown, a deadline or a contended bucket: back to <c>Pending</c>, the attempt given back, no expiry.</summary>
    private async Task ReleaseAsync(Claimed claimed, int? maxAttempts, string why)
    {
        try
        {
            await WriteRunAsync(claimed, (job, now) =>
            {
                job.State = PhotoThumbnailJobState.Pending;
                job.Attempts = Math.Max(0, job.Attempts - 1);
                job.NextAttemptAtUtc = now;
                EndRun(job, now);
            }, maxAttempts);
        }
        catch (Exception ex) when (maxAttempts is not null)
        {
            logger.LogWarning("Releasing thumbnail job for photo {PhotoId} failed ({Failure}); its lease expires on its own.", claimed.PhotoId, ex.GetType().Name);
            return;
        }

        logger.LogInformation("Thumbnail job for photo {PhotoId} was released because {Reason}.", claimed.PhotoId, why);
    }

    /// <summary>
    /// The watchdog fired (§3.6): the job is <c>Unsupported (timeout)</c> first, with its incident only if the write
    /// landed on this run; then the thread is abandoned and claiming pauses while it lives.
    /// </summary>
    private async Task TimedOutAsync(Claimed claimed)
    {
        var landed = await WriteRunAsync(claimed, (job, now) =>
        {
            job.State = PhotoThumbnailJobState.Unsupported;
            job.FailureCode = PhotoThumbnailFailure.Timeout;
            EndRun(job, now);
        });
        if (landed)
        {
            tally.Hangs++;
            await using var scope = scopes.CreateAsyncScope();
            var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
            context.PhotoThumbnailIncidents.Add(new PhotoThumbnailIncident { Kind = PhotoThumbnailIncidentKind.Timeout, OccurredAtUtc = Now });
            await context.SaveChangesAsync();
        }

        logger.LogCritical("The thumbnail decode for photo {PhotoId} exceeded {Watchdog} and was abandoned (timeout).",
            claimed.PhotoId, PhotoThumbnailTiming.Watchdog);
    }

    private static void EndRun(PhotoThumbnailJobEntry job, DateTime now)
    {
        job.RunId = null;
        job.RerunRequested = false;
        job.LeaseUntilUtc = null;
    }

    // ── Lease renewal and heartbeat ───────────────────────────────────────────────────────────────────

    /// <summary>Every 60 s while a job runs: the lease moves 5 minutes ahead, and the heartbeat is refreshed (v7.1 §2).</summary>
    private async Task RenewLeaseAsync(Claimed claimed, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(PhotoThumbnailTiming.RenewEvery, time);
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            try
            {
                await WriteRunAsync(claimed, (job, now) => job.LeaseUntilUtc = now + PhotoThumbnailTiming.Lease);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The database is away; the lease may expire and be reclaimed, and the give-back covers it.
                logger.LogWarning("Renewing the thumbnail lease for photo {PhotoId} failed ({Failure}).", claimed.PhotoId, ex.GetType().Name);
            }

            await HeartbeatAsync(PresentState(), CancellationToken.None);
        }
    }

    /// <summary>Upserts this worker's row (§3.6, v7.1 §2). Best-effort: a failed heartbeat is only logged.</summary>
    internal async Task HeartbeatAsync(ThumbnailWorkerState state, CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
            var row = await context.PhotoThumbnailWorkerHeartbeats.FirstOrDefaultAsync(h => h.WorkerName == identity.Name, cancellationToken);
            if (row is null)
            {
                row = new PhotoThumbnailWorkerHeartbeat { WorkerName = identity.Name };
                context.PhotoThumbnailWorkerHeartbeats.Add(row);
            }

            row.State = state;
            row.UpdatedAtUtc = Now;
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("Writing the thumbnail worker's heartbeat failed ({Failure}).", ex.GetType().Name);
        }
    }

    /// <summary>On a first run: rows of other workers not heard from in 3 minutes belong to replaced containers.</summary>
    private async Task RemoveDeadWorkersAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
            var stale = Now - PhotoThumbnailTiming.HeartbeatFresh;
            context.PhotoThumbnailWorkerHeartbeats.RemoveRange(await context.PhotoThumbnailWorkerHeartbeats
                .Where(h => h.WorkerName != identity.Name && h.UpdatedAtUtc < stale)
                .ToListAsync(cancellationToken));
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("Removing stale thumbnail worker heartbeats failed ({Failure}).", ex.GetType().Name);
        }
    }

    private async Task DeleteOwnHeartbeatAsync()
    {
        await using var scope = scopes.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        context.PhotoThumbnailWorkerHeartbeats.RemoveRange(
            await context.PhotoThumbnailWorkerHeartbeats.Where(h => h.WorkerName == identity.Name).ToListAsync());
        await context.SaveChangesAsync();
    }

    /// <summary>Incidents and heartbeats older than 24 hours, about once an hour (v7.1 §2). Best-effort.</summary>
    private async Task CleanUpAsync()
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
            var cutoff = Now - PhotoThumbnailTiming.RowRetention;
            context.PhotoThumbnailIncidents.RemoveRange(await context.PhotoThumbnailIncidents.Where(i => i.OccurredAtUtc < cutoff).ToListAsync());
            context.PhotoThumbnailWorkerHeartbeats.RemoveRange(await context.PhotoThumbnailWorkerHeartbeats.Where(h => h.UpdatedAtUtc < cutoff).ToListAsync());
            await context.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            logger.LogWarning("Cleaning up old thumbnail incidents and heartbeats failed ({Failure}).", ex.GetType().Name);
        }
    }

    // ── Bucket ────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Takes a slot in the current bucket (§3.2.1), in its own save before the files are written, so a crash can only
    /// under-fill a bucket. A conflict reloads and retries up to 3 times (v7.1 §1); null after that.
    /// </summary>
    internal async Task<int?> AllocateBucketAsync(CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < PhotoThumbnailTiming.BucketAttempts; attempt++)
        {
            await using var scope = scopes.CreateAsyncScope();
            var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
            var bucket = await context.PhotoThumbnailBuckets.FirstOrDefaultAsync(b => b.Id == PhotoThumbnailBucket.SingletonId, cancellationToken);
            if (bucket is null)
            {
                // The migration seeds the row; a database created without HasData (EF InMemory) gets it here.
                bucket = new PhotoThumbnailBucket { Id = PhotoThumbnailBucket.SingletonId, CurrentBucket = 1 };
                context.PhotoThumbnailBuckets.Add(bucket);
            }

            if (bucket.PhotosWritten >= options.BucketCapacity)
            {
                bucket.CurrentBucket++;
                bucket.PhotosWritten = 0;
            }

            bucket.PhotosWritten++;
            bucket.UpdatedAtUtc = Now;
            try
            {
                await context.SaveChangesAsync(cancellationToken);
                return bucket.CurrentBucket;
            }
            catch (DbUpdateException)
            {
                // A concurrent allocation (or the first insert, raced): reload and try again.
            }
        }

        return null;
    }

    // ── Threads under a watchdog ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The renderer on a dedicated background thread, under the 30 s wall-clock watchdog (§3.6). On expiry the thread
    /// is abandoned — native code cannot be cancelled — and remembered, so claiming pauses while it lives.
    /// </summary>
    private async Task<(bool Finished, ThumbnailRenderResult? Result)> RenderUnderWatchdogAsync(ThumbnailRenderRequest request)
    {
        var decode = RunOnThread(() => SafeRender(request));
        try
        {
            return (true, await decode.WaitAsync(PhotoThumbnailTiming.Watchdog, time));
        }
        catch (TimeoutException)
        {
            abandonedDecode = decode;
            abandonedAtUtc = Now;
            restartRequested = false;
            return (false, null);
        }
    }

    /// <summary>A decoder exception is <c>decode</c>; only its type is ever logged, never its message (§7.8).</summary>
    private ThumbnailRenderResult SafeRender(ThumbnailRenderRequest request)
    {
        try
        {
            return renderer.Render(request);
        }
        catch (Exception ex)
        {
            logger.LogWarning("The thumbnail decoder raised {Failure}.", ex.GetType().Name);
            return ThumbnailRenderResult.Failed(PhotoThumbnailFailure.Decode);
        }
    }

    /// <summary>
    /// The EXIF orientation from the full original (§3.5 step 2), on its own thread under a 5 s watchdog. Any failure,
    /// timeout or value outside 1–8 is orientation 1.
    /// </summary>
    private async Task<int> ReadOrientationAsync(byte[] original)
    {
        var read = RunOnThread(() =>
        {
            try
            {
                return extractor.Extract(original).Orientation ?? 1;
            }
            catch
            {
                return 1;
            }
        });
        try
        {
            var orientation = await read.WaitAsync(PhotoThumbnailTiming.OrientationWatchdog, time);
            return orientation is >= 1 and <= 8 ? orientation : 1;
        }
        catch (TimeoutException)
        {
            return 1;
        }
    }

    private static Task<T> RunOnThread<T>(Func<T> work)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                completion.SetResult(work());
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
        })
        {
            IsBackground = true,
            Name = "odyssey-thumbnail",
        };
        thread.Start();
        return completion.Task;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────────────────────────────

    private DateTime Now => time.GetUtcNow().UtcDateTime;

    private static void Touch(PhotoThumbnailJobEntry job, DateTime now)
    {
        job.Generation++;
        job.UpdatedAtUtc = now;
    }

    /// <summary>A storage or database outage (§9): counted against nobody.</summary>
    private static bool IsOutage(Exception ex) =>
        ex is FileStorageUnavailableException or FileStorageFullException or DbException or RetryLimitExceededException
        || ex is DbUpdateException { InnerException: DbException };

    private static async Task SwallowAsync(Task task)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>50–500 ms between conflicting writes: real time, so a test clock never stalls a retry.</summary>
    private static Task JitterAsync(CancellationToken cancellationToken) =>
        Task.Delay(Random.Shared.Next(50, 501), cancellationToken);
}

/// <summary>One tick's thumbnail counts, folded into its run row (issue #409 §4.2). Counts only — never a photo id.</summary>
internal sealed class ThumbnailTickTally
{
    public int Rendered { get; set; }

    public int Failed { get; set; }

    public int Requeued { get; set; }

    public int Crashes { get; set; }

    public int Hangs { get; set; }

    /// <summary>A tick that rendered, failed, re-queued or reclaimed something; anything else is idle.</summary>
    public bool HasWork => Rendered + Failed + Requeued + Crashes + Hangs > 0;

    public void AddTo(PhotoThumbnailJobRun run)
    {
        run.PhotosRendered += Rendered;
        run.PhotosFailed += Failed;
        run.PhotosRequeued += Requeued;
        run.Crashes += Crashes;
        run.Hangs += Hangs;
    }
}
