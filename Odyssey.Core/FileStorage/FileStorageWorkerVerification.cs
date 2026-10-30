using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Odyssey.Context;

namespace Odyssey.Core.FileStorage;

/// <summary>
/// The worker's file-storage startup checks (issue #346 A3.2), owned by the maintenance job rather than the host: a
/// failing verdict stops that job and nothing else, so weather and exchange-rate sync keep running. The verdict is
/// recorded on the maintenance lease row, which is how <c>GET /api/file-storage/volumes</c> reports it — the worker
/// container stays up, so its exit can no longer be the signal.
/// </summary>
/// <remarks>
/// The first attempt waits up to <see cref="InitialWait"/> for the API to initialise a new volume. After a failure
/// the checks re-run every <see cref="RetryInterval"/> until they pass; the job then runs as usual.
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

    private volatile bool settled;

    /// <summary>
    /// Returns once the checks have passed in this process — at once if they already have, or if this process's
    /// registry was activated by another caller (an API host verifies at its own startup). Until they pass it does
    /// not return, re-checking every <see cref="RetryInterval"/>; only cancellation ends the wait.
    /// </summary>
    public async Task EnsureVerifiedAsync(CancellationToken cancellationToken)
    {
        if (settled || volumes.IsActive)
        {
            settled = true;
            return;
        }

        var wait = InitialWait;
        while (true)
        {
            try
            {
                await services.VerifyFileStorageForWorkerAsync(PollInterval, wait, cancellationToken);
                settled = true;
                await RecordAsync(null, cancellationToken);
                return;
            }
            catch (FileStorageMisconfiguredException ex)
            {
                // The verifier has already logged the check, the key and the remedy.
                logger.LogError(
                    "File-storage maintenance is stopped in this worker: startup check {Check} failed. The worker's other jobs keep running; "
                    + "the checks are re-run every {RetryInterval}.",
                    ex.Check, RetryInterval);
                await RecordAsync(ex.Check, cancellationToken);
            }

            wait = TimeSpan.Zero;
            await Task.Delay(RetryInterval, time, cancellationToken);
        }
    }

    /// <summary>
    /// Writes the failed check (or clears it) on the lease row. Only those two columns change, never
    /// <see cref="FileStorageMaintenanceLease.Version"/>, so a runner's claim is not disturbed; a concurrent claim
    /// changing the version makes this retry.
    /// </summary>
    private async Task RecordAsync(string? check, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                await using var scope = services.CreateAsyncScope();
                var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
                var lease = await context.FileStorageMaintenanceLeases
                    .SingleOrDefaultAsync(l => l.Id == FileStorageMaintenanceLease.SingletonId, cancellationToken);
                if (lease is null)
                {
                    if (check is null)
                    {
                        return;
                    }

                    // The migration seeds the row; a database created without HasData (EF InMemory) gets it here.
                    lease = new FileStorageMaintenanceLease { Id = FileStorageMaintenanceLease.SingletonId, Version = Guid.NewGuid() };
                    context.FileStorageMaintenanceLeases.Add(lease);
                }
                else if (check is null && lease.VerificationFailedCheck is null)
                {
                    return;
                }

                lease.VerificationFailedCheck = check;
                lease.VerificationFailedAtUtc = check is null ? null : time.GetUtcNow().UtcDateTime;
                await context.SaveChangesAsync(cancellationToken);
                return;
            }
            catch (DbUpdateException ex)
            {
                logger.LogWarning("Recording the worker's file-storage verdict failed ({Failure}).", FileStorageLog.Describe(ex));
            }
        }
    }
}
