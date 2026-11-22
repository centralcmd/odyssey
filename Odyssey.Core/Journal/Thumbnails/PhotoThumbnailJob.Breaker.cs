using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Odyssey.Context;
using Odyssey.Core.Finance;
using Odyssey.Core.Imaging;
using Odyssey.Core.Jobs;
using Odyssey.Dtos.Application;
using Odyssey.Dtos.Journal;

namespace Odyssey.Core.Journal.Thumbnails;

public sealed partial class PhotoThumbnailJob
{
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
            Kind = PhotoThumbnailIncidentKind.Restart,
            OccurredAtUtc = now,
            WorkerName = identity.Name,
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

    // ── Threads under a watchdog ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The renderer on a dedicated background thread, under the 30 s wall-clock watchdog (§3.6). On expiry the thread
    /// is abandoned — native code cannot be cancelled — and remembered, so claiming pauses while it lives.
    /// </summary>
    private async Task<(bool Finished, ThumbnailRenderResult? Result)> RenderUnderWatchdogAsync(ThumbnailRenderRequest request)
    {
        // The shared guard arms the watchdog before the thread starts (issue #556 arch B4 extracted it).
        var run = await ThumbnailRenderGuard.RunAsync(() => SafeRender(request), PhotoThumbnailTiming.Watchdog, time, ThreadName);
        if (run.Finished)
        {
            return (true, run.Value);
        }

        abandonedDecode = run.Abandoned;
        abandonedAtUtc = Now;
        restartRequested = false;
        return (false, null);
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
        // Armed before the thread starts, as the render watchdog is.
        using var watchdogCancel = new CancellationTokenSource();
        var watchdog = Task.Delay(PhotoThumbnailTiming.OrientationWatchdog, time, watchdogCancel.Token);
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
        if (await Task.WhenAny(read, watchdog) != read)
        {
            return 1;
        }

        await watchdogCancel.CancelAsync();
        var orientation = await read;
        return orientation is >= 1 and <= 8 ? orientation : 1;
    }

    private const string ThreadName = "odyssey-thumbnail";

    private static Task<T> RunOnThread<T>(Func<T> work) => ThumbnailRenderGuard.RunOnThread(work, ThreadName);
}
