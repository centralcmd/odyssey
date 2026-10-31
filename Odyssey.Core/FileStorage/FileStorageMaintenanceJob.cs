using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Odyssey.Context;
using Odyssey.Core.Jobs;

namespace Odyssey.Core.FileStorage;

/// <summary>
/// The file store's maintenance, run hourly by <c>Odyssey.Worker</c> (issue #346 Amendment A3.1, lease timings
/// from #362 Amendment v7.1 §3). Every worker replica runs it; the single <see cref="FileStorageMaintenanceLease"/>
/// row elects one runner per tick. Erasure depends on this job, so its last completed pass is reported on
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
    public static readonly TimeSpan Tick = TimeSpan.FromHours(1);

    /// <summary>How long a claim holds; a crashed holder's lease is taken over within this.</summary>
    public static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(5);

    /// <summary>How often a running pass renews its lease.</summary>
    public static readonly TimeSpan RenewEvery = TimeSpan.FromMinutes(1);

    public string Name => "file-storage-maintenance";

    public TimeSpan Interval => Tick;

    public async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        await verification.EnsureVerifiedAsync(cancellationToken);

        // The no-op rule: no volumes, nothing to maintain.
        if (!volumes.IsActive)
        {
            return;
        }

        var holder = Guid.NewGuid();
        if (!await TryClaimAsync(holder, cancellationToken))
        {
            logger.LogDebug("File-storage maintenance is held by another runner; skipping this tick.");
            return;
        }

        var completed = false;
        var lastRenewal = time.GetUtcNow();
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var sweeper = scope.ServiceProvider.GetRequiredService<FileStorageSweeper>();
            await sweeper.RunAsync(async ct =>
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
            }, cancellationToken);
            completed = true;
        }
        finally
        {
            await ReleaseAsync(holder, completed);
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
