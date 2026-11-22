using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Odyssey.Context;
using Odyssey.Core.FileStorage;
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
public sealed partial class PhotoThumbnailJob : IPeriodicJob, IDisposable
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
