using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Odyssey.Context;
using Odyssey.Dtos.Application;

namespace Odyssey.Core.Jobs;

/// <summary>
/// This process's id in the run log (issue #409 §4.3): the key of its heartbeats and the <c>WorkerId</c> of every run
/// row it writes. Drawn once per process, so a restarted container is a new worker and its predecessor's open rows
/// lose their heartbeat.
/// </summary>
public sealed class JobWorkerIdentity
{
    public Guid WorkerId { get; } = Guid.NewGuid();
}

/// <summary>
/// The recording half of the run log (issue #409 §3.2–§3.3), shared by the four jobs: the throttled heartbeat, work
/// bursts, held periods, and claiming and finishing a manual run. A singleton, because the open burst and the open
/// held period of each job are per-process state; the rows are the shared state.
/// </summary>
/// <remarks>
/// <para>
/// <b>Recording never breaks a job.</b> Every method logs and swallows a failure (cancellation aside): a database
/// hiccup costs a row, never a run of the job itself.
/// </para>
/// <para>
/// <b>Every write is conditional on <see cref="JobRun.Generation"/>.</b> When the sweep has written the row first,
/// the save fails on the token and the write is dropped with a Warning, never retried (§3.4); the burst or held
/// period is forgotten and the next tick opens a fresh one.
/// </para>
/// </remarks>
public sealed class JobRunRecorder(
    IServiceScopeFactory scopes,
    JobWorkerIdentity identity,
    TimeProvider time,
    ILogger<JobRunRecorder> logger)
{
    /// <summary>At most one heartbeat per job per process per this interval (§3.3 step 1, §10).</summary>
    public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(60);

    /// <summary>A burst reaching this age is finished and continues in a new row (§3.2).</summary>
    public static readonly TimeSpan MaxBurst = TimeSpan.FromHours(1);

    private readonly ConcurrentDictionary<JobType, DateTime> lastHeartbeat = new();
    private readonly ConcurrentDictionary<JobType, Guid> openBursts = new();
    private readonly ConcurrentDictionary<JobType, OpenHeld> openHelds = new();

    public Guid WorkerId => identity.WorkerId;

    private DateTime Now => time.GetUtcNow().UtcDateTime;

    // ── Heartbeat ─────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Upserts this process's heartbeat for <paramref name="jobType"/>, at most once per <see cref="HeartbeatInterval"/>.</summary>
    public async Task HeartbeatAsync(JobType jobType, CancellationToken cancellationToken)
    {
        var now = Now;
        if (lastHeartbeat.TryGetValue(jobType, out var last) && now - last < HeartbeatInterval)
        {
            return;
        }

        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
            var row = await context.JobHeartbeats
                .FirstOrDefaultAsync(h => h.JobType == jobType && h.WorkerId == identity.WorkerId, cancellationToken);
            if (row is null)
            {
                context.JobHeartbeats.Add(new JobHeartbeat { JobType = jobType, WorkerId = identity.WorkerId, LastTickAtUtc = now });
            }
            else
            {
                row.LastTickAtUtc = now;
            }

            await context.SaveChangesAsync(cancellationToken);
            lastHeartbeat[jobType] = now;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("Writing the {JobType} heartbeat failed ({Failure}).", jobType, ex.GetType().Name);
        }
    }

    // ── Bursts ────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A tick with work (§3.2): adds <paramref name="add"/>'s counts to this process's open burst, or opens one. A
    /// burst that has reached <see cref="MaxBurst"/> is finished first and the work continues in a new row.
    /// </summary>
    public async Task ExtendBurstAsync<TRun>(JobType jobType, Action<TRun> add, CancellationToken cancellationToken)
        where TRun : JobRun, new()
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
            var now = Now;
            if (openBursts.TryGetValue(jobType, out var openId))
            {
                var open = await context.Set<TRun>().FirstOrDefaultAsync(r => r.JobRunId == openId, cancellationToken);
                if (open is { Status: JobRunStatus.Running } && now - open.StartedAtUtc < MaxBurst)
                {
                    add(open);
                    open.LastActivityAtUtc = now;
                    await SaveAsync(context, jobType, cancellationToken);
                    return;
                }

                if (open is { Status: JobRunStatus.Running })
                {
                    var (status, category) = JobRunOutcomes.Of(open);
                    open.Finish(status, category, now);
                    await SaveAsync(context, jobType, cancellationToken);
                    context.ChangeTracker.Clear();
                }

                openBursts.TryRemove(jobType, out _);
            }

            var run = NewRun<TRun>(JobRunTrigger.Scheduled, JobRunStatus.Running, now);
            add(run);
            context.Set<TRun>().Add(run);
            if (await SaveAsync(context, jobType, cancellationToken))
            {
                openBursts[jobType] = run.JobRunId;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            openBursts.TryRemove(jobType, out _);
            logger.LogWarning("Recording a {JobType} burst failed ({Failure}).", jobType, ex.GetType().Name);
        }
    }

    /// <summary>An idle (or held) tick: finishes this process's open burst, if any.</summary>
    public async Task CloseBurstAsync(JobType jobType, CancellationToken cancellationToken)
    {
        if (!openBursts.TryRemove(jobType, out var openId))
        {
            return;
        }

        await FinishAsync<JobRun>(jobType, openId, _ => { }, outcome: null, cancellationToken);
    }

    /// <summary>The id of this process's open burst of <paramref name="jobType"/>, if any.</summary>
    public Guid? OpenBurst(JobType jobType) => openBursts.TryGetValue(jobType, out var id) ? id : null;

    // ── Held periods ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A held tick (§3.3 step 3): opens this process's <c>Held</c> row on the first one, with its reason; a change of
    /// reason closes the old period and opens a new one. Later held ticks write nothing — the heartbeat is what shows
    /// the process alive.
    /// </summary>
    public async Task HoldAsync<TRun>(
        JobType jobType, JobHeldReason reason, Action<TRun>? configure, CancellationToken cancellationToken)
        where TRun : JobRun, new()
    {
        if (openHelds.TryGetValue(jobType, out var held))
        {
            if (held.Reason == reason)
            {
                return;
            }

            await ReleaseHoldAsync(jobType, cancellationToken);
        }

        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
            var run = NewRun<TRun>(JobRunTrigger.Scheduled, JobRunStatus.Held, Now);
            run.HeldReason = reason;
            configure?.Invoke(run);
            context.Set<TRun>().Add(run);
            if (await SaveAsync(context, jobType, cancellationToken))
            {
                openHelds[jobType] = new OpenHeld(run.JobRunId, reason);
                logger.LogInformation("Job {JobType} is held in this worker ({Reason}); run {RunId} records the period.", jobType, reason, run.JobRunId);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("Recording a held {JobType} period failed ({Failure}).", jobType, ex.GetType().Name);
        }
    }

    /// <summary>The first tick no longer held: closes this process's open held period, if any.</summary>
    public async Task ReleaseHoldAsync(JobType jobType, CancellationToken cancellationToken)
    {
        if (!openHelds.TryRemove(jobType, out var held))
        {
            return;
        }

        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
            var row = await context.JobRuns.FirstOrDefaultAsync(r => r.JobRunId == held.Id, cancellationToken);
            if (row is null || !row.IsOpen)
            {
                return;
            }

            row.Finish(JobRunStatus.Held, null, Now);
            await SaveAsync(context, jobType, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("Closing a held {JobType} period failed ({Failure}).", jobType, ex.GetType().Name);
        }
    }

    /// <summary>The reason this process holds <paramref name="jobType"/>, if it does.</summary>
    public JobHeldReason? HeldReason(JobType jobType) => openHelds.TryGetValue(jobType, out var held) ? held.Reason : null;

    // ── Manual runs ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Claims the oldest manual request of <paramref name="jobType"/> no process has taken yet (§3.5 step 1). Every
    /// replica asks; the <see cref="JobRun.Generation"/> token lets exactly one save succeed.
    /// </summary>
    public async Task<Guid?> ClaimManualAsync(JobType jobType, CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
            var cutoff = Now - JobCatalogue.UnclaimedRequestTimeout;
            var run = await context.JobRuns
                .Where(r => r.JobType == jobType && r.Status == JobRunStatus.Running && r.Trigger == JobRunTrigger.Manual
                    && r.ClaimedAtUtc == null && r.StartedAtUtc >= cutoff)
                .OrderBy(r => r.StartedAtUtc)
                .FirstOrDefaultAsync(cancellationToken);
            if (run is null)
            {
                return null;
            }

            var now = Now;
            run.ClaimedAtUtc = now;
            run.WorkerId = identity.WorkerId;
            run.LastActivityAtUtc = now;
            try
            {
                await context.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateConcurrencyException)
            {
                // Another replica claimed it (or the sweep abandoned it) between the read and here.
                return null;
            }

            return run.JobRunId;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("Claiming a manual {JobType} run failed ({Failure}).", jobType, ex.GetType().Name);
            return null;
        }
    }

    /// <summary>A progress write: moves the run's stale clock (A-N1), optionally with counts.</summary>
    public async Task TouchAsync<TRun>(JobType jobType, Guid runId, Action<TRun>? add, CancellationToken cancellationToken)
        where TRun : JobRun
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
            var run = await context.Set<TRun>().FirstOrDefaultAsync(r => r.JobRunId == runId, cancellationToken);
            if (run is not { Status: JobRunStatus.Running })
            {
                return;
            }

            add?.Invoke(run);
            run.LastActivityAtUtc = Now;
            await SaveAsync(context, jobType, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("Recording {JobType} run progress failed ({Failure}).", jobType, ex.GetType().Name);
        }
    }

    /// <summary>
    /// Finishes a <c>Running</c> run: applies <paramref name="apply"/>, then the status and category of
    /// <paramref name="outcome"/>, or — when null — the ones its counters imply (<see cref="JobRunOutcomes"/>).
    /// </summary>
    public async Task FinishAsync<TRun>(
        JobType jobType,
        Guid runId,
        Action<TRun> apply,
        (JobRunStatus Status, JobFailureCategory? Category)? outcome,
        CancellationToken cancellationToken)
        where TRun : JobRun
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
            var run = await context.Set<TRun>().FirstOrDefaultAsync(r => r.JobRunId == runId, CancellationToken.None);
            if (run is not { Status: JobRunStatus.Running })
            {
                logger.LogWarning("{JobType} run {RunId} was finished elsewhere before this worker finished it; its outcome is dropped.", jobType, runId);
                return;
            }

            apply(run);
            var (status, category) = outcome ?? JobRunOutcomes.Of(run);
            run.Finish(status, category, Now);
            await SaveAsync(context, jobType, CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("Finishing {JobType} run {RunId} failed ({Failure}).", jobType, runId, ex.GetType().Name);
        }
    }

    private TRun NewRun<TRun>(JobRunTrigger trigger, JobRunStatus status, DateTime now)
        where TRun : JobRun, new() => new()
    {
        JobRunId = Guid.NewGuid(),
        Trigger = trigger,
        Status = status,
        StartedAtUtc = now,
        ClaimedAtUtc = now,
        LastActivityAtUtc = now,
        WorkerId = identity.WorkerId,
    };

    /// <summary>Saves; a lost <see cref="JobRun.Generation"/> race drops the write with a Warning and returns false.</summary>
    private async Task<bool> SaveAsync(OdysseyContext context, JobType jobType, CancellationToken cancellationToken)
    {
        try
        {
            await context.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            logger.LogWarning("A {JobType} run row was written by another writer first (the stale-run sweep); this write is dropped.", jobType);
            openBursts.TryRemove(jobType, out _);
            context.ChangeTracker.Clear();
            return false;
        }
    }

    private readonly record struct OpenHeld(Guid Id, JobHeldReason Reason);
}

/// <summary>The status and failure category a run's own counters imply, per job (§3.3 step 2).</summary>
public static class JobRunOutcomes
{
    public static (JobRunStatus Status, JobFailureCategory? Category) Of(JobRun run) => run switch
    {
        WeatherJobRun weather => Weather(weather),
        PhotoThumbnailJobRun thumbnails => Thumbnails(thumbnails),
        PhotoOriginalBackfillJobRun move => PhotoOriginalMove(move),
        FileStorageMaintenanceJobRun storage => storage.IntegrityFailures > 0
            ? (JobRunStatus.PartiallySucceeded, JobFailureCategory.Storage)
            : (JobRunStatus.Succeeded, null),
        ExchangeRateSyncJobRun sync => sync.ErrorKind is { } kind
            ? (JobRunStatus.Failed, (JobFailureCategory)kind)
            : (JobRunStatus.Succeeded, null),
        _ => (JobRunStatus.Succeeded, null),
    };

    private static (JobRunStatus, JobFailureCategory?) Weather(WeatherJobRun run)
    {
        if (run.PointsFailed == 0)
        {
            return (JobRunStatus.Succeeded, null);
        }

        var category = WeatherCategory(run.FailureKind);
        return run.PointsFailed >= run.PointsAttempted
            ? (JobRunStatus.Failed, category)
            : (JobRunStatus.PartiallySucceeded, category);
    }

    /// <summary>
    /// A storage or database interruption outranks a parked photo for the category: the outage is what an operator has
    /// to act on, and a parked photo is already listed by code on the status card.
    /// </summary>
    private static (JobRunStatus, JobFailureCategory?) PhotoOriginalMove(PhotoOriginalBackfillJobRun run)
    {
        if (run.Interruptions == 0 && run.PhotosParked == 0)
        {
            return (JobRunStatus.Succeeded, null);
        }

        var category = run.Interruptions > 0 ? JobFailureCategory.Storage : JobFailureCategory.UnsupportedInput;
        return run.PhotosMoved + run.PhotosCopiedShared == 0
            ? (JobRunStatus.Failed, category)
            : (JobRunStatus.PartiallySucceeded, category);
    }

    private static (JobRunStatus, JobFailureCategory?) Thumbnails(PhotoThumbnailJobRun run)
    {
        var failures = run.PhotosFailed + run.Crashes + run.Hangs;
        if (failures == 0)
        {
            return (JobRunStatus.Succeeded, null);
        }

        var category = run.Crashes + run.Hangs > 0 ? JobFailureCategory.Internal : JobFailureCategory.UnsupportedInput;
        return run.PhotosRendered == 0
            ? (JobRunStatus.Failed, category)
            : (JobRunStatus.PartiallySucceeded, category);
    }

    /// <summary>A weather fetch failure in the run log's vocabulary.</summary>
    public static JobFailureCategory WeatherCategory(Dtos.Journal.WeatherFetchFailureKind? kind) => kind switch
    {
        Dtos.Journal.WeatherFetchFailureKind.RateLimited => JobFailureCategory.ProviderRateLimited,
        Dtos.Journal.WeatherFetchFailureKind.MalformedPayload or Dtos.Journal.WeatherFetchFailureKind.ResponseTooLarge
            => JobFailureCategory.ProviderMalformed,
        Dtos.Journal.WeatherFetchFailureKind.ProviderRejected => JobFailureCategory.UnsupportedInput,
        Dtos.Journal.WeatherFetchFailureKind.NotConfigured => JobFailureCategory.Configuration,
        _ => JobFailureCategory.ProviderUnavailable,
    };
}

public static class JobRunServiceCollectionExtensions
{
    /// <summary>
    /// The run log's recording services, shared by every host that registers a job's services (the worker runs the
    /// jobs; the API and the tests construct them). Idempotent, so each job's own registration can call it.
    /// </summary>
    public static IServiceCollection AddJobRunRecording(this IServiceCollection services)
    {
        services.TryAddSingleton<JobWorkerIdentity>();
        services.TryAddSingleton<JobRunRecorder>();
        services.TryAddScoped<JobRunSweeper>();
        return services;
    }
}
