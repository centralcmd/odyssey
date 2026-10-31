using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Odyssey.Context;

namespace Odyssey.Core.FileStorage;

/// <summary>
/// The worker's file-storage startup checks (issue #346 A3.2), owned by the maintenance job rather than the host: a
/// failing verdict stops that job and nothing else, so weather and exchange-rate sync keep running. Each process
/// records its own verdict in <see cref="FileStorageWorkerVerdict"/>, which is how <c>GET /api/file-storage/volumes</c>
/// reports it — the worker container stays up, so its exit can no longer be the signal.
/// </summary>
/// <remarks>
/// The first attempt waits up to <see cref="InitialWait"/> for the API to initialise a new volume. After a failure —
/// a failed check, or the checks unable to run at all (the database unreachable) — they re-run every
/// <see cref="RetryInterval"/> until they pass; the job then runs as usual.
/// </remarks>
public sealed class FileStorageWorkerVerification(
    IServiceProvider services,
    FileStorageVolumeRegistry volumes,
    TimeProvider time,
    ILogger<FileStorageWorkerVerification> logger)
{
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    public static readonly TimeSpan InitialWait = TimeSpan.FromMinutes(2);

    public static readonly TimeSpan RetryInterval = TimeSpan.FromMinutes(10);

    /// <summary>
    /// A verdict not refreshed for three re-checks belongs to a worker that has stopped or recovered: it is ignored and
    /// swept. That is also what bounds a clear that failed to be written.
    /// </summary>
    public static readonly TimeSpan VerdictTtl = 3 * RetryInterval;

    public const int RecordAttempts = 3;

    private volatile bool settled;
    private volatile bool verdictCleared;

    /// <summary>This process's key in <see cref="FileStorageWorkerVerdict"/>.</summary>
    public Guid WorkerId { get; } = Guid.NewGuid();

    /// <summary>
    /// Returns once the checks have passed in this process — at once if they already have, or if this process's
    /// registry was activated by another caller (an API host verifies at its own startup). Until they pass it does
    /// not return, re-checking every <see cref="RetryInterval"/>; only cancellation ends the wait. Once they pass, a
    /// verdict this process could not clear is retried on every later call.
    /// </summary>
    public async Task EnsureVerifiedAsync(CancellationToken cancellationToken)
    {
        if (!settled && volumes.IsActive)
        {
            // Verified by this process's own host; it never recorded a verdict.
            settled = true;
            verdictCleared = true;
        }

        if (settled)
        {
            if (!verdictCleared)
            {
                verdictCleared = await TryClearAsync(cancellationToken);
            }

            return;
        }

        var wait = InitialWait;
        while (true)
        {
            try
            {
                await services.VerifyFileStorageForWorkerAsync(PollInterval, wait, cancellationToken);
                settled = true;
                verdictCleared = await TryClearAsync(cancellationToken);
                return;
            }
            catch (FileStorageMisconfiguredException ex)
            {
                // The verifier has already logged the check, the key and the remedy.
                logger.LogError(
                    "File-storage maintenance is stopped in this worker: startup check {Check} failed. The worker's other jobs keep running; "
                    + "the checks are re-run every {RetryInterval}.",
                    ex.Check, RetryInterval);
                await TryReportAsync(ex.Check, cancellationToken);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                // The checks could not run at all — the database is unreachable, say. There is no verdict to record (the
                // store that would hold it is what failed); the job stays held and the checks are re-run.
                logger.LogError(
                    "The file-storage startup checks could not run in this worker ({Failure}); maintenance waits and they are re-run every {RetryInterval}.",
                    FileStorageLog.Describe(ex), RetryInterval);
            }

            wait = TimeSpan.Zero;
            await Task.Delay(RetryInterval, time, cancellationToken);
        }
    }

    /// <summary>Writes or refreshes this process's failing verdict. False when every attempt failed.</summary>
    internal Task<bool> TryReportAsync(string check, CancellationToken cancellationToken) =>
        WithAttemptsAsync("Recording", async context =>
        {
            var now = time.GetUtcNow().UtcDateTime;
            var verdict = await context.FileStorageWorkerVerdicts.SingleOrDefaultAsync(v => v.WorkerId == WorkerId, cancellationToken);
            if (verdict is null)
            {
                verdict = new FileStorageWorkerVerdict { WorkerId = WorkerId, FailedCheck = check, FailedAtUtc = now };
                context.FileStorageWorkerVerdicts.Add(verdict);
            }
            else if (verdict.FailedCheck != check)
            {
                verdict.FailedCheck = check;
                verdict.FailedAtUtc = now;
            }

            verdict.LastReportedAtUtc = now;
            await context.SaveChangesAsync(cancellationToken);
        }, cancellationToken);

    /// <summary>
    /// Deletes this process's verdict, and any other past <see cref="VerdictTtl"/> (a stopped worker's). False when every
    /// attempt failed; the row then expires on its own within <see cref="VerdictTtl"/>.
    /// </summary>
    internal Task<bool> TryClearAsync(CancellationToken cancellationToken) =>
        WithAttemptsAsync("Clearing", async context =>
        {
            var expired = time.GetUtcNow().UtcDateTime - VerdictTtl;
            var rows = await context.FileStorageWorkerVerdicts
                .Where(v => v.WorkerId == WorkerId || v.LastReportedAtUtc < expired)
                .ToListAsync(cancellationToken);
            if (rows.Count == 0)
            {
                return;
            }

            context.FileStorageWorkerVerdicts.RemoveRange(rows);
            await context.SaveChangesAsync(cancellationToken);
        }, cancellationToken);

    private async Task<bool> WithAttemptsAsync(string what, Func<OdysseyContext, Task> write, CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= RecordAttempts; attempt++)
        {
            try
            {
                await using var scope = services.CreateAsyncScope();
                await write(scope.ServiceProvider.GetRequiredService<OdysseyContext>());
                return true;
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                // A concurrent sweep of the same expired row, or the database briefly away.
                logger.LogWarning("{What} the worker's file-storage verdict failed, attempt {Attempt} of {Attempts} ({Failure}).",
                    what, attempt, RecordAttempts, FileStorageLog.Describe(ex));
            }
        }

        return false;
    }
}
