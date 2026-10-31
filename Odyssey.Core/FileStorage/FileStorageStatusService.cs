using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Odyssey.Context;
using Odyssey.Dtos.Application;

namespace Odyssey.Core.FileStorage;

/// <summary>
/// <c>GET /api/file-storage/volumes</c> (issue #346 §5, A3.1, A3.2): each volume's state, usage and bindings, as
/// THIS process sees them — the <c>Degraded</c> flag is per process, so a volume degraded only in the worker shows
/// in the worker's log, not here. Never returns a root path, a relative path, a file name or an object id.
/// </summary>
public sealed class FileStorageStatusService(
    OdysseyContext context,
    StorageFeatureCatalogue catalogue,
    FileStorageVolumeRegistry volumes,
    IFileStorageSpaceProbe space,
    IMemoryCache cache,
    TimeProvider time,
    ILogger<FileStorageStatusService> logger)
{
    /// <summary>No completed maintenance pass for longer than this — three intervals — is stale.</summary>
    public static readonly TimeSpan MaintenanceStaleAfter = TimeSpan.FromHours(3);

    /// <summary>
    /// A pending delete still present this long after its erase time is overdue: two maintenance intervals, so a
    /// file that fell due just after a pass began is never counted.
    /// </summary>
    public static readonly TimeSpan OverdueAfter = 2 * FileStorageMaintenanceJob.Tick;

    public async Task<IReadOnlyList<FileStorageVolumeStatus>> GetVolumesAsync(CancellationToken cancellationToken)
    {
        // The no-op rule: the store is not set up, so there is nothing to report.
        if (!volumes.IsActive)
        {
            return [];
        }

        var rows = await context.FileStorageVolumes.AsNoTracking().ToListAsync(cancellationToken);
        var usage = await context.StoredObjects.AsNoTracking()
            .Where(o => o.State != StoredObjectState.Pending)
            .GroupBy(o => new { o.VolumeId, o.FeatureKey, o.State })
            .Select(g => new { g.Key.VolumeId, g.Key.FeatureKey, g.Key.State, Count = g.Count(), Bytes = g.Sum(o => o.SizeBytes) })
            .ToListAsync(cancellationToken);
        var faults = await context.StoredObjects.AsNoTracking()
            .Where(o => o.State == StoredObjectState.Committed && o.IntegrityFaultAtUtc != null)
            .GroupBy(o => o.VolumeId)
            .Select(g => new { VolumeId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.VolumeId, g => g.Count, cancellationToken);
        var lastScrub = await context.StoredObjects.AsNoTracking()
            .Where(o => o.LastVerifiedAtUtc != null)
            .GroupBy(o => o.VolumeId)
            .Select(g => new { VolumeId = g.Key, Last = g.Max(o => o.LastVerifiedAtUtc) })
            .ToDictionaryAsync(g => g.VolumeId, g => g.Last, cancellationToken);
        var overdue = await CountOverdueDeletesAsync(cancellationToken);
        var verdictCutoff = time.GetUtcNow().UtcDateTime - FileStorageWorkerVerification.VerdictTtl;
        var verdicts = await context.FileStorageWorkerVerdicts.AsNoTracking()
            .Where(v => v.LastReportedAtUtc > verdictCutoff)
            .OrderBy(v => v.FailedAtUtc)
            .ToListAsync(cancellationToken);
        var lease = await context.FileStorageMaintenanceLeases.AsNoTracking()
            .SingleOrDefaultAsync(l => l.Id == FileStorageMaintenanceLease.SingletonId, cancellationToken);

        var boundVolumeNames = volumes.Bindings.Values.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var stale = IsStale(lease?.LastCompletedAtUtc, rows.Where(r => boundVolumeNames.Contains(r.Name)));

        var result = new List<FileStorageVolumeStatus>();
        foreach (var row in rows.OrderBy(r => r.Name, StringComparer.Ordinal))
        {
            var active = volumes.Find(row.Id);
            var volumeUsage = usage.Where(u => u.VolumeId == row.Id).ToList();
            var measured = active is null ? null : space.Measure(active.RootPath);
            result.Add(new FileStorageVolumeStatus
            {
                Name = row.Name,
                State = active is null ? FileStorageVolumeState.NotConfigured
                    : volumes.RecheckIfDue(active) ? FileStorageVolumeState.Degraded
                    : FileStorageVolumeState.Available,
                InitializedAtUtc = row.InitializedAtUtc,
                BoundFeatures = [.. volumes.Bindings.Where(b => string.Equals(b.Value, row.Name, StringComparison.OrdinalIgnoreCase))
                    .Select(b => catalogue.Find(b.Key)?.Key ?? b.Key).Order(StringComparer.Ordinal)],
                Features = [.. volumeUsage.GroupBy(u => u.FeatureKey)
                    .Select(g => new FileStorageFeatureUsage
                    {
                        FeatureKey = g.Key,
                        ObjectCount = g.Where(u => u.State == StoredObjectState.Committed).Sum(u => u.Count),
                        CommittedBytes = g.Where(u => u.State == StoredObjectState.Committed).Sum(u => u.Bytes),
                    })
                    .OrderBy(f => f.FeatureKey, StringComparer.Ordinal)],
                PendingDeleteCount = volumeUsage.Where(u => u.State == StoredObjectState.PendingDelete).Sum(u => u.Count),
                OverdueDeleteCount = overdue.GetValueOrDefault(row.Id),
                IntegrityFaultCount = faults.GetValueOrDefault(row.Id),
                FreeBytes = measured?.Free,
                TotalBytes = measured?.Total,
                LastScrubAtUtc = lastScrub.GetValueOrDefault(row.Id),
                QuarantinedCount = row.QuarantinedFileCount,
                LastPassOrphanCount = row.LastPassOrphanCount,
                ReconciliationHalted = row.ReconciliationHaltedAtUtc is not null,
                OrphanedFeatures = [.. volumeUsage.Select(u => u.FeatureKey).Distinct().Where(k => catalogue.Find(k) is null).Order(StringComparer.Ordinal)],
                MaintenanceLastCompletedAtUtc = lease?.LastCompletedAtUtc,
                MaintenanceStale = stale,
                MaintenanceHaltedCheck = verdicts.FirstOrDefault()?.FailedCheck,
                MaintenanceHaltedAtUtc = verdicts.FirstOrDefault()?.FailedAtUtc,
                MaintenanceHaltedWorkerCount = verdicts.Count,
                OverdueAfterHours = (int)OverdueAfter.TotalHours,
                MaintenanceStaleAfterHours = (int)MaintenanceStaleAfter.TotalHours,
                VerificationRetryMinutes = (int)FileStorageWorkerVerification.RetryInterval.TotalMinutes,
            });
        }

        return result;
    }

    /// <summary>
    /// Pending deletes per volume more than <see cref="OverdueAfter"/> past their erase time, by the same grace the
    /// sweeper erases by — an immediate release is due at once.
    /// </summary>
    private async Task<Dictionary<Guid, int>> CountOverdueDeletesAsync(CancellationToken cancellationToken)
    {
        var (graceHours, _) = await FileStorageDeletionGrace.ResolveAsync(context, cache, logger, cancellationToken);
        var immediateCutoff = time.GetUtcNow().UtcDateTime - OverdueAfter;
        var graceCutoff = immediateCutoff - TimeSpan.FromHours(graceHours);
        return await context.StoredObjects.AsNoTracking()
            .Where(o => o.State == StoredObjectState.PendingDelete
                && (o.DeleteRequestedAtUtc <= graceCutoff || (o.DeleteImmediately && o.DeleteRequestedAtUtc <= immediateCutoff)))
            .GroupBy(o => o.VolumeId)
            .Select(g => new { VolumeId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.VolumeId, g => g.Count, cancellationToken);
    }

    /// <summary>
    /// Stale when the last completed pass is older than three hours — or when none has ever completed and the
    /// oldest bound volume was registered more than three hours ago.
    /// </summary>
    private bool IsStale(DateTime? lastCompleted, IEnumerable<FileStorageVolume> boundVolumes)
    {
        var now = time.GetUtcNow().UtcDateTime;
        if (lastCompleted is { } completed)
        {
            return now - completed > MaintenanceStaleAfter;
        }

        var oldest = boundVolumes.Select(v => v.InitializedAtUtc ?? v.LastSeenAtUtc).DefaultIfEmpty(DateTime.MaxValue).Min();
        return oldest != DateTime.MaxValue && now - oldest > MaintenanceStaleAfter;
    }
}
