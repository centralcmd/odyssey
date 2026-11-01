using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Odyssey.Context;
using Odyssey.Core.SystemSettings;
using Odyssey.Dtos;

namespace Odyssey.Core.FileStorage;

/// <summary>
/// Process-wide memory the sweeps keep between runs: which reconciliation pass already logged its
/// non-conforming files, so the Warning is once per pass rather than once per tick.
/// </summary>
public sealed class FileStorageSweepMemory
{
    private readonly ConcurrentDictionary<(Guid VolumeId, int Pass), bool> warned = new();

    internal bool ShouldWarnNonConforming(Guid volumeId, int pass) => warned.TryAdd((volumeId, pass), true);
}

/// <summary>
/// The maintenance sweeps (issue #346 §3.7): abandoned writes, deferred deletes, unreferenced objects, the
/// integrity scrub and the disk reconciliation. Each is a public method — the test seam — and
/// <see cref="RunAsync"/> runs them all in order. Each is batch-bounded and catches and logs per object, and
/// every delete is a tracked <c>Remove</c>, never <c>ExecuteDeleteAsync</c>, so every sweep runs on EF InMemory.
/// </summary>
/// <remarks>
/// No log line carries a file name or a relative path: those are personal data. An object is named by its id.
/// </remarks>
public sealed class FileStorageSweeper(
    OdysseyContext context,
    StorageFeatureCatalogue catalogue,
    FileStorageVolumeRegistry volumes,
    FileStorageSweepMemory memory,
    IMemoryCache cache,
    TimeProvider time,
    ILogger<FileStorageSweeper> logger)
{
    public static readonly TimeSpan AbandonedWriteAge = TimeSpan.FromHours(1);
    public static readonly TimeSpan UnreferencedAge = TimeSpan.FromHours(1);
    public static readonly TimeSpan TempPartAge = TimeSpan.FromHours(24);
    public static readonly TimeSpan QuarantineAfter = TimeSpan.FromHours(24);
    public static readonly TimeSpan QuarantineRetention = TimeSpan.FromDays(7);
    public static readonly TimeSpan ScrubTimeBudget = TimeSpan.FromSeconds(20);
    public static readonly TimeSpan ReconcileTimeBudget = TimeSpan.FromSeconds(10);

    public const int BatchSize = 500;
    public const int ScrubMaxObjects = 200;
    public const long ScrubMaxBytes = 512L * 1024 * 1024;
    public const int ReconcileMaxFolders = 256;
    public const int BreakerFloor = 100;

    /// <inheritdoc cref="FileStorageDeletionGrace.FallbackHours"/>
    public const int GraceFallbackHours = FileStorageDeletionGrace.FallbackHours;

    private Func<CancellationToken, Task> keepAlive = _ => Task.CompletedTask;

    /// <summary>One maintenance pass. <paramref name="keepAlive"/> renews the job's lease; it is called between objects.</summary>
    public async Task RunAsync(Func<CancellationToken, Task>? keepAlive, CancellationToken cancellationToken)
    {
        this.keepAlive = keepAlive ?? (_ => Task.CompletedTask);

        foreach (var volume in volumes.Volumes.Where(v => v.Degraded))
        {
            await volumes.ProbeAsync(volume, cancellationToken);
        }

        await LogUnknownFeaturesAsync(cancellationToken);
        var graceHours = await ResolveGraceHoursAsync(cancellationToken);

        await SweepAbandonedWritesAsync(cancellationToken);
        await SweepDeferredDeletesAsync(graceHours, cancellationToken);
        await SweepUnreferencedAsync(cancellationToken);
        await ScrubAsync(cancellationToken);
        await ReconcileAsync(cancellationToken);
    }

    /// <summary>
    /// The grace in hours. The sweeper maps the resolver's outcome itself: the resolver's degraded value and its
    /// below-minimum clamp (1 h) are both wrong for a retention period, so anything but a healthy read is 168 h.
    /// </summary>
    public async Task<int> ResolveGraceHoursAsync(CancellationToken cancellationToken)
    {
        var (hours, outcome) = await FileStorageDeletionGrace.ResolveAsync(context, cache, logger, cancellationToken);
        if (outcome != IntSettingOutcome.Healthy)
        {
            logger.LogWarning(
                "The file-storage deletion grace resolved {Outcome}; deferred deletes use {Fallback} hours until it is repaired at /settings.",
                outcome, GraceFallbackHours);
        }

        return hours;
    }

    // ── Abandoned writes ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// <c>Pending</c> rows older than an hour: a failed write or a caller whose save never came. The ROW is removed
    /// first (concurrency-checked on <c>State</c>), then the files, so a late caller save fails cleanly instead of
    /// committing a reference to a file this sweep already deleted; a crash between the two leaves an orphan file
    /// that the disk reconciliation finds.
    /// </summary>
    public async Task<int> SweepAbandonedWritesAsync(CancellationToken cancellationToken)
    {
        var cutoff = Now - AbandonedWriteAge;
        var ids = await context.StoredObjects.AsNoTracking()
            .Where(o => o.State == StoredObjectState.Pending && o.CreatedAtUtc < cutoff)
            .OrderBy(o => o.CreatedAtUtc)
            .Select(o => o.Id)
            .Take(BatchSize)
            .ToListAsync(cancellationToken);

        var swept = 0;
        foreach (var id in ids)
        {
            await keepAlive(cancellationToken);
            try
            {
                var row = await context.StoredObjects.SingleOrDefaultAsync(o => o.Id == id && o.State == StoredObjectState.Pending, cancellationToken);
                if (row is null || !TryResolve(row, out var volume, out var fullPath, out var stopAt))
                {
                    continue;
                }

                context.StoredObjects.Remove(row);
                await context.SaveChangesAsync(cancellationToken);

                VolumeFileSystem.TryDelete(Path.Combine(volume.RootPath, StorageNames.TempFolder, $"{id:N}.part"));

                // The path is free in the index from the save above: a new writer may already have reserved it, and
                // its file is not this row's to delete.
                var path = row.RelativePath;
                if (await context.StoredObjects.AsNoTracking().AnyAsync(o => o.VolumeId == row.VolumeId && o.RelativePath == path, cancellationToken))
                {
                    swept++;
                    continue;
                }

                DeleteFileOrThrow(fullPath);
                VolumeFileSystem.RemoveEmptyFolders(Path.GetDirectoryName(fullPath)!, stopAt);
                swept++;
            }
            catch (DbUpdateConcurrencyException)
            {
                // The caller committed meanwhile: it is not abandoned after all.
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DbUpdateException)
            {
                logger.LogError("Sweeping abandoned write {StoredObjectId} failed; the disk reconciliation will find any file left behind ({Failure}).", id, FileStorageLog.Describe(ex));
            }
            finally
            {
                context.ChangeTracker.Clear();
            }
        }

        LogCount("abandoned writes", swept);
        return swept;
    }

    // ── Deferred deletes ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// <c>PendingDelete</c> rows past the grace, or stamped <c>DeleteImmediately</c>, in the §3.5 order: re-check
    /// the reference, delete the file, delete the row, remove empty folders. A failed unlink leaves the row
    /// <c>PendingDelete</c>, so a row always drives the retry.
    /// </summary>
    public async Task<int> SweepDeferredDeletesAsync(int graceHours, CancellationToken cancellationToken)
    {
        var cutoff = Now - TimeSpan.FromHours(graceHours);
        var ids = await context.StoredObjects.AsNoTracking()
            .Where(o => o.State == StoredObjectState.PendingDelete && (o.DeleteImmediately || o.DeleteRequestedAtUtc <= cutoff))
            .OrderBy(o => o.DeleteRequestedAtUtc)
            .Select(o => o.Id)
            .Take(BatchSize)
            .ToListAsync(cancellationToken);

        var deleted = 0;
        foreach (var id in ids)
        {
            await keepAlive(cancellationToken);
            try
            {
                var row = await context.StoredObjects.SingleOrDefaultAsync(o => o.Id == id && o.State == StoredObjectState.PendingDelete, cancellationToken);
                if (row is null)
                {
                    continue;
                }

                var done = catalogue.Find(row.FeatureKey) is { } feature
                    ? await DeleteKnownAsync(row, feature, cancellationToken)
                    : await DeleteUnknownAsync(row, cancellationToken);
                deleted += done ? 1 : 0;
            }
            catch (DbUpdateConcurrencyException)
            {
                // Changed under us; the next tick sees its new state.
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DbUpdateException)
            {
                logger.LogError("Deleting stored object {StoredObjectId} failed; it stays pending and is retried next tick ({Failure}).", id, FileStorageLog.Describe(ex));
            }
            finally
            {
                context.ChangeTracker.Clear();
            }
        }

        LogCount("deferred deletes", deleted);
        return deleted;
    }

    private async Task<bool> DeleteKnownAsync(StoredObject row, StorageFeature feature, CancellationToken cancellationToken)
    {
        if (await feature.Reference.ReferencedIds(context).AnyAsync(referenced => referenced == row.Id, cancellationToken))
        {
            // A consumer released an object it still names: a defect in the consumer. Nothing is deleted.
            row.State = StoredObjectState.Committed;
            row.DeleteRequestedAtUtc = null;
            row.DeleteImmediately = false;
            await context.SaveChangesAsync(cancellationToken);
            logger.LogError(
                "Stored object {StoredObjectId} of feature {Feature} was released while still referenced; it is committed again.",
                row.Id, feature.Key);
            return false;
        }

        if (!TryResolve(row, out _, out var fullPath, out var stopAt))
        {
            return false;
        }

        DeleteFileOrThrow(fullPath);
        context.StoredObjects.Remove(row);
        await context.SaveChangesAsync(cancellationToken);
        VolumeFileSystem.RemoveEmptyFolders(Path.GetDirectoryName(fullPath)!, stopAt);
        return true;
    }

    /// <summary>
    /// A row of a feature this build does not know (B4 — a rollback). Its erasure still completes, but with no
    /// catalogue to name its reference column, the database's own RESTRICT key is the re-check: the row is removed
    /// and saved first, inside a transaction, and the file deleted before the commit (R5-2).
    /// </summary>
    private async Task<bool> DeleteUnknownAsync(StoredObject row, CancellationToken cancellationToken)
    {
        if (!TryResolve(row, out _, out var fullPath, out var stopAt))
        {
            return false;
        }

        if (!context.Database.IsRelational())
        {
            // No keys to guard with on InMemory; the relational path below is covered by Odyssey.IntegrationTests.
            context.StoredObjects.Remove(row);
            await context.SaveChangesAsync(cancellationToken);
            DeleteFileOrThrow(fullPath);
            VolumeFileSystem.RemoveEmptyFolders(Path.GetDirectoryName(fullPath)!, stopAt);
            return true;
        }

        var strategy = context.Database.CreateExecutionStrategy();
        var deleted = await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
            context.StoredObjects.Remove(row);
            try
            {
                await context.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException ex) when (ex is not DbUpdateConcurrencyException)
            {
                await transaction.RollbackAsync(cancellationToken);
                context.ChangeTracker.Clear();
                logger.LogError(
                    "Stored object {StoredObjectId} of unknown feature {Feature} is still referenced; its row and file are kept and retried next tick.",
                    row.Id, row.FeatureKey);
                return false;
            }

            DeleteFileOrThrow(fullPath);
            await transaction.CommitAsync(cancellationToken);
            return true;
        });

        if (deleted)
        {
            VolumeFileSystem.RemoveEmptyFolders(Path.GetDirectoryName(fullPath)!, stopAt);
        }

        return deleted;
    }

    // ── Unreferenced objects ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// <c>Committed</c> rows older than an hour that no row of their feature's reference column names — how a
    /// cascade delete of a consumer row releases its bytes. Flagged rows are included.
    /// </summary>
    public async Task<int> SweepUnreferencedAsync(CancellationToken cancellationToken)
    {
        var cutoff = Now - UnreferencedAge;
        var released = 0;
        foreach (var feature in catalogue.Features)
        {
            var ids = await context.StoredObjects.AsNoTracking()
                .Where(o => o.FeatureKey == feature.Key && o.State == StoredObjectState.Committed && o.CreatedAtUtc < cutoff)
                .Where(o => !feature.Reference.ReferencedIds(context).Contains(o.Id))
                .Select(o => o.Id)
                .Take(BatchSize)
                .ToListAsync(cancellationToken);

            foreach (var id in ids)
            {
                await keepAlive(cancellationToken);
                try
                {
                    var row = await context.StoredObjects.SingleOrDefaultAsync(o => o.Id == id && o.State == StoredObjectState.Committed, cancellationToken);
                    if (row is null)
                    {
                        continue;
                    }

                    row.State = StoredObjectState.PendingDelete;
                    row.DeleteRequestedAtUtc = Now;
                    row.DeleteImmediately = feature.ReleaseMode == StorageReleaseMode.Immediate;
                    await context.SaveChangesAsync(cancellationToken);
                    released++;
                }
                catch (DbUpdateConcurrencyException)
                {
                    // Released by its consumer meanwhile.
                }
                catch (DbUpdateException ex)
                {
                    logger.LogError("Releasing unreferenced stored object {StoredObjectId} failed ({Failure}).", id, FileStorageLog.Describe(ex));
                }
                finally
                {
                    context.ChangeTracker.Clear();
                }
            }
        }

        LogCount("unreferenced objects", released);
        return released;
    }

    // ── Integrity scrub ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Re-hashes committed objects, oldest check first, within a per-tick budget. A mismatch must reproduce on a
    /// second read before <c>IntegrityFaultAtUtc</c> is set, and a flagged row that matches again is cleared. It
    /// writes only <c>IntegrityFaultAtUtc</c> and <c>LastVerifiedAtUtc</c>, never <c>State</c>, so it can never make
    /// a consumer's concurrent release fail.
    /// </summary>
    public async Task<int> ScrubAsync(CancellationToken cancellationToken)
    {
        var started = time.GetTimestamp();
        var knownKeys = catalogue.Features.Select(f => f.Key).ToList();
        var ids = await context.StoredObjects.AsNoTracking()
            .Where(o => o.State == StoredObjectState.Committed && knownKeys.Contains(o.FeatureKey))
            .OrderBy(o => o.LastVerifiedAtUtc).ThenBy(o => o.Id)
            .Select(o => o.Id)
            .Take(ScrubMaxObjects)
            .ToListAsync(cancellationToken);

        long bytes = 0;
        var checkedCount = 0;
        foreach (var id in ids)
        {
            if (bytes >= ScrubMaxBytes || time.GetElapsedTime(started) >= ScrubTimeBudget)
            {
                break;
            }

            await keepAlive(cancellationToken);
            try
            {
                var row = await context.StoredObjects.SingleOrDefaultAsync(o => o.Id == id && o.State == StoredObjectState.Committed, cancellationToken);
                if (row is null || !TryResolve(row, out var volume, out var fullPath, out _))
                {
                    continue;
                }

                var first = await HashAsync(fullPath, cancellationToken);
                if (first is null)
                {
                    continue;
                }

                bytes += first.Value.Size;
                var matches = Matches(row, first.Value);
                if (!matches)
                {
                    // It must reproduce before anything is flagged.
                    var second = await HashAsync(fullPath, cancellationToken);
                    if (second is null)
                    {
                        continue;
                    }

                    matches = Matches(row, second.Value);
                }

                if (!matches && row.IntegrityFaultAtUtc is null)
                {
                    row.IntegrityFaultAtUtc = Now;
                    logger.LogError("Stored object {StoredObjectId} on volume {Volume} failed its integrity check.", row.Id, volume.Name);
                }
                else if (matches && row.IntegrityFaultAtUtc is not null)
                {
                    row.IntegrityFaultAtUtc = null;
                    logger.LogInformation("Stored object {StoredObjectId} on volume {Volume} matches again; its fault is cleared.", row.Id, volume.Name);
                }

                row.LastVerifiedAtUtc = Now;
                await context.SaveChangesAsync(cancellationToken);
                checkedCount++;
            }
            catch (DbUpdateConcurrencyException)
            {
                // Released concurrently: the consumer's change wins, and this check is simply dropped.
            }
            catch (DbUpdateException ex)
            {
                logger.LogError("Recording the integrity check of stored object {StoredObjectId} failed ({Failure}).", id, FileStorageLog.Describe(ex));
            }
            finally
            {
                context.ChangeTracker.Clear();
            }
        }

        LogCount("integrity checks", checkedCount);
        return checkedCount;
    }

    private static bool Matches(StoredObject row, (bool Exists, long Size, string? Hash) actual) =>
        actual.Exists && actual.Size == row.SizeBytes && string.Equals(actual.Hash, row.Sha256Hash, StringComparison.Ordinal);

    /// <summary>Null when the read failed for a reason that is not the object's own (the volume is now degraded).</summary>
    private async Task<(bool Exists, long Size, string? Hash)?> HashAsync(string fullPath, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(fullPath, new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.Read,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
            });
            var hash = await SHA256.HashDataAsync(stream, cancellationToken);
            return (true, stream.Length, Convert.ToHexStringLower(hash));
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return (false, 0, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("Reading a stored object for the integrity scrub failed; it is checked again next tick ({Failure}).", FileStorageLog.Describe(ex));
            return null;
        }
    }

    // ── Disk reconciliation ──────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Per volume: tidies <c>.tmp</c>, purges <c>.quarantine</c>, and walks up to 256 folders of the catalogue's
    /// feature roots from the persisted cursor, recording orphan sightings and quarantining the ones confirmed
    /// across two passes (§3.7 safeguards).
    /// </summary>
    public async Task ReconcileAsync(CancellationToken cancellationToken)
    {
        foreach (var volume in volumes.Volumes)
        {
            if (!IsUsable(volume))
            {
                continue;
            }

            try
            {
                var row = await context.FileStorageVolumes.SingleOrDefaultAsync(v => v.Id == volume.Id, cancellationToken);
                if (row is null)
                {
                    continue;
                }

                await TidyTempAsync(volume, cancellationToken);
                PurgeQuarantine(volume, row);
                await context.SaveChangesAsync(cancellationToken);
                await WalkAsync(volume, row, cancellationToken);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DbUpdateException)
            {
                logger.LogError("Disk reconciliation of volume {Volume} failed; it resumes from its cursor next tick ({Failure}).", volume.Name, FileStorageLog.Describe(ex));
            }
            finally
            {
                context.ChangeTracker.Clear();
            }
        }
    }

    /// <summary><c>.tmp/*.part</c> older than a day with no <c>Pending</c> row.</summary>
    private async Task TidyTempAsync(ActiveVolume volume, CancellationToken cancellationToken)
    {
        var temp = new DirectoryInfo(Path.Combine(volume.RootPath, StorageNames.TempFolder));
        if (!temp.Exists)
        {
            return;
        }

        if (temp.LinkTarget is not null)
        {
            SecurityEvent(volume, "the .tmp folder is a symbolic link");
            return;
        }

        var cutoff = Now - TempPartAge;
        foreach (var part in temp.EnumerateFiles("*.part"))
        {
            if (part.LinkTarget is not null || part.LastWriteTimeUtc >= cutoff
                || !Guid.TryParseExact(Path.GetFileNameWithoutExtension(part.Name), "N", out var id))
            {
                continue;
            }

            if (!await context.StoredObjects.AnyAsync(o => o.Id == id && o.State == StoredObjectState.Pending, cancellationToken))
            {
                VolumeFileSystem.TryDelete(part.FullName);
            }
        }
    }

    /// <summary>Quarantine folders older than seven days, deleted without following links; recounts the rest.</summary>
    private void PurgeQuarantine(ActiveVolume volume, FileStorageVolume row)
    {
        var quarantine = new DirectoryInfo(Path.Combine(volume.RootPath, StorageNames.QuarantineFolder));
        if (!quarantine.Exists)
        {
            row.QuarantinedFileCount = 0;
            return;
        }

        if (quarantine.LinkTarget is not null)
        {
            SecurityEvent(volume, "the .quarantine folder is a symbolic link; nothing is purged");
            return;
        }

        var today = DateOnly.FromDateTime(Now);
        var count = 0;
        foreach (var day in quarantine.EnumerateDirectories())
        {
            if (day.LinkTarget is not null)
            {
                SecurityEvent(volume, "a .quarantine date folder is a symbolic link; it is left alone");
                continue;
            }

            if (DateOnly.TryParseExact(day.Name, "yyyy-MM-dd", out var date) && date.AddDays(QuarantineRetention.Days) <= today)
            {
                VolumeFileSystem.DeleteTreeWithoutFollowingLinks(day.FullName);
                continue;
            }

            count += CountFiles(day);
        }

        row.QuarantinedFileCount = count;
    }

    private static int CountFiles(DirectoryInfo folder) =>
        folder.EnumerateFileSystemInfos().Sum(entry => entry switch
        {
            DirectoryInfo directory when directory.LinkTarget is null => CountFiles(directory),
            _ => 1,
        });

    private async Task WalkAsync(ActiveVolume volume, FileStorageVolume row, CancellationToken cancellationToken)
    {
        if (row.ReconciliationPass < 1)
        {
            row.ReconciliationPass = 1;
        }

        var pass = row.ReconciliationPass;
        var cursor = row.ReconciliationCursor?.Split('/');
        var started = time.GetTimestamp();
        var visited = 0;
        var nonConforming = 0;

        foreach (var folder in Folders(volume, cursor, () => nonConforming++))
        {
            if (visited >= ReconcileMaxFolders || time.GetElapsedTime(started) >= ReconcileTimeBudget)
            {
                WarnNonConforming(volume, pass, nonConforming);
                return;
            }

            await keepAlive(cancellationToken);
            nonConforming += await VisitFolderAsync(volume, row, pass, folder, cancellationToken);
            row.ReconciliationCursor = string.Join('/', folder);
            await context.SaveChangesAsync(cancellationToken);
            visited++;
        }

        WarnNonConforming(volume, pass, nonConforming);
        await CompletePassAsync(volume, row, pass, cancellationToken);
    }

    /// <summary>
    /// Every folder under the catalogue's feature roots, depth-first, siblings compared byte-wise one segment at a
    /// time — never as whole path strings, because <c>-</c> (0x2D) sorts before <c>/</c> (0x2F). Folders up to and
    /// including <paramref name="cursor"/> are skipped by the same segment-wise comparison, so the walk and the
    /// cursor always agree.
    /// </summary>
    private IEnumerable<string[]> Folders(ActiveVolume volume, string[]? cursor, Action countNonConforming)
    {
        foreach (var feature in catalogue.Features)
        {
            var root = new DirectoryInfo(Path.Combine(volume.RootPath, feature.RootFolder));
            if (!root.Exists)
            {
                continue;
            }

            if (root.LinkTarget is not null)
            {
                SecurityEvent(volume, $"the {feature.Key} root folder is a symbolic link; it is not walked");
                continue;
            }

            foreach (var folder in Walk(volume, root, [feature.RootFolder], cursor, countNonConforming))
            {
                yield return folder;
            }
        }
    }

    private IEnumerable<string[]> Walk(ActiveVolume volume, DirectoryInfo directory, string[] segments, string[]? cursor, Action countNonConforming)
    {
        if (cursor is not null && !IsPrefixOf(segments, cursor) && CompareSegments(segments, cursor) < 0)
        {
            // Entirely before the cursor: this whole subtree was visited earlier in the pass.
            yield break;
        }

        if (cursor is null || CompareSegments(segments, cursor) > 0)
        {
            yield return segments;
        }

        // Visiting the folder may have emptied and removed it (a quarantine removes the folders it empties), and
        // the walk must survive a folder vanishing under it for any other reason too.
        foreach (var folder in WalkChildren(volume, directory, segments, cursor, countNonConforming))
        {
            yield return folder;
        }
    }

    private IEnumerable<string[]> WalkChildren(ActiveVolume volume, DirectoryInfo directory, string[] segments, string[]? cursor, Action countNonConforming)
    {
        List<DirectoryInfo> listed;
        try
        {
            directory.Refresh();
            listed = directory.Exists ? [.. directory.EnumerateDirectories()] : [];
        }
        catch (DirectoryNotFoundException)
        {
            listed = [];
        }

        var children = new List<DirectoryInfo>();
        foreach (var child in listed)
        {
            if (child.LinkTarget is not null)
            {
                SecurityEvent(volume, "a folder under a feature root is a symbolic link; it is not walked");
                continue;
            }

            // Depth: the root folder plus at most five layout segments.
            if (segments.Length > StorageNames.MaxSegments || !StorageNames.IsValidSegment(child.Name))
            {
                countNonConforming();
                continue;
            }

            children.Add(child);
        }

        foreach (var child in children.OrderBy(c => c.Name, StringComparer.Ordinal))
        {
            foreach (var folder in Walk(volume, child, [.. segments, child.Name], cursor, countNonConforming))
            {
                yield return folder;
            }
        }
    }

    /// <summary>Segment-wise ordinal order, a prefix before its extensions: exactly depth-first pre-order.</summary>
    internal static int CompareSegments(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        for (var i = 0; i < Math.Min(a.Count, b.Count); i++)
        {
            var c = string.CompareOrdinal(a[i], b[i]);
            if (c != 0)
            {
                return c;
            }
        }

        return a.Count.CompareTo(b.Count);
    }

    private static bool IsPrefixOf(IReadOnlyList<string> prefix, IReadOnlyList<string> path) =>
        prefix.Count <= path.Count && prefix.Select((segment, i) => string.Equals(segment, path[i], StringComparison.Ordinal)).All(same => same);

    /// <summary>One folder's files. Returns how many were non-conforming (left alone and counted).</summary>
    private async Task<int> VisitFolderAsync(
        ActiveVolume volume, FileStorageVolume row, int pass, string[] folder, CancellationToken cancellationToken)
    {
        var directory = new DirectoryInfo(Path.Combine([volume.RootPath, .. folder]));
        if (!directory.Exists)
        {
            return 0;
        }

        var featureRoot = folder[0];
        var prefix = string.Join('/', folder) + "/";
        var nonConforming = 0;
        var files = new Dictionary<string, FileInfo>(StringComparer.Ordinal);
        foreach (var file in directory.EnumerateFiles())
        {
            var relative = prefix + file.Name;
            if (file.LinkTarget is not null || !StorageNames.IsConformingPath(relative, featureRoot))
            {
                nonConforming++;
                continue;
            }

            files[relative] = file;
        }

        if (files.Count == 0)
        {
            return nonConforming;
        }

        // Every row and sighting in this folder, matched as the database's collation matches: a stray file that
        // differs from a live row only by case or accent counts as matched and is never quarantined (R4-4).
        var pattern = StorageNames.EscapeLike(prefix) + "%";
        var known = (await context.StoredObjects.AsNoTracking()
                .Where(o => o.VolumeId == volume.Id && EF.Functions.Like(o.RelativePath, pattern, "\\"))
                .Select(o => o.RelativePath)
                .ToListAsync(cancellationToken))
            .Where(p => p.IndexOf('/', prefix.Length) < 0);
        var knownSet = new HashSet<string>(known, StorageNames.CollationComparer);
        var sightings = (await context.FileStorageOrphanSightings
                .Where(s => s.VolumeId == volume.Id && EF.Functions.Like(s.RelativePath, pattern, "\\"))
                .ToListAsync(cancellationToken))
            .Where(s => s.RelativePath.IndexOf('/', prefix.Length) < 0);
        var sightingByPath = sightings
            .GroupBy(s => s.RelativePath, StorageNames.CollationComparer)
            .ToDictionary(g => g.Key, g => g.First(), StorageNames.CollationComparer);

        var now = Now;
        var halted = row.ReconciliationHaltedAtUtc is not null;
        foreach (var (relative, file) in files)
        {
            sightingByPath.TryGetValue(relative, out var sighting);
            if (knownSet.Contains(relative))
            {
                // The file gained a row: no longer an orphan.
                if (sighting is not null)
                {
                    context.FileStorageOrphanSightings.Remove(sighting);
                }

                continue;
            }

            if (sighting is null)
            {
                var added = new FileStorageOrphanSighting
                {
                    Id = Guid.NewGuid(), VolumeId = volume.Id, RelativePath = relative, FirstSeenAtUtc = now, LastSeenPass = pass,
                };
                context.FileStorageOrphanSightings.Add(added);
                sightingByPath[relative] = added;
                continue;
            }

            if (!halted && sighting.LastSeenPass == pass - 1 && sighting.FirstSeenAtUtc <= now - QuarantineAfter
                && Quarantine(volume, file, relative, sighting.Id))
            {
                context.FileStorageOrphanSightings.Remove(sighting);
                continue;
            }

            sighting.LastSeenPass = pass;
        }

        return nonConforming;
    }

    /// <summary>
    /// An atomic rename into <c>.quarantine/&lt;yyyy-MM-dd&gt;/&lt;sightingId:N&gt;/&lt;relative path&gt;</c> — unique by
    /// construction — under the same symlink and containment checks as every other operation.
    /// </summary>
    private bool Quarantine(ActiveVolume volume, FileInfo file, string relative, Guid sightingId)
    {
        var quarantineRelative = $"{StorageNames.QuarantineFolder}/{Now:yyyy-MM-dd}/{sightingId:N}/{relative}";
        try
        {
            if (VolumeFileSystem.IsLink(Path.Combine(volume.RootPath, StorageNames.QuarantineFolder)))
            {
                SecurityEvent(volume, "the .quarantine folder is a symbolic link; nothing is moved");
                return false;
            }

            var target = VolumeFileSystem.Contain(volume.RootPath, quarantineRelative, StorageNames.QuarantineFolder);
            VolumeFileSystem.EnsureNoLinks(volume.RootPath, Path.GetDirectoryName(target)!);
            VolumeFileSystem.EnsureNoLinks(volume.RootPath, file.FullName);
            VolumeFileSystem.CreateDirectory(Path.GetDirectoryName(target)!);
            VolumeFileSystem.EnsureNoLinks(volume.RootPath, Path.GetDirectoryName(target)!);
            File.Move(file.FullName, target, overwrite: false);
            VolumeFileSystem.RemoveEmptyFolders(file.DirectoryName!, Path.Combine(volume.RootPath, StorageNames.RootOf(relative)));
            logger.LogWarning("Quarantined an orphan file on volume {Volume} (sighting {SightingId}); it is deleted in seven days.", volume.Name, sightingId);
            return true;
        }
        catch (FileStorageIntegrityException)
        {
            SecurityEvent(volume, "a quarantine move crossed a symbolic link or left the quarantine; nothing is moved");
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogError("Quarantining an orphan file on volume {Volume} failed; it is retried next pass ({Failure}).", volume.Name, FileStorageLog.Describe(ex));
            return false;
        }
    }

    /// <summary>
    /// The breaker, per completed pass: more orphans than max(100, 1 % of the live rows) is the signature of a
    /// database-only restore or a wrong database, so the volume is halted and nothing is quarantined until a pass
    /// completes below the threshold.
    /// </summary>
    private async Task CompletePassAsync(ActiveVolume volume, FileStorageVolume row, int pass, CancellationToken cancellationToken)
    {
        var orphans = await context.FileStorageOrphanSightings.CountAsync(s => s.VolumeId == volume.Id && s.LastSeenPass == pass, cancellationToken);
        var live = await context.StoredObjects.CountAsync(o => o.VolumeId == volume.Id && o.State != StoredObjectState.Pending, cancellationToken);
        var threshold = Math.Max(BreakerFloor, live / 100);

        if (orphans > threshold)
        {
            row.ReconciliationHaltedAtUtc ??= Now;
            logger.LogCritical(
                "Disk reconciliation of volume {Volume} found {Orphans} files the database does not know (threshold {Threshold}). "
                + "Quarantine is halted: this is the signature of a database restored without its volume, or the wrong database.",
                volume.Name, orphans, threshold);
        }
        else if (row.ReconciliationHaltedAtUtc is not null)
        {
            row.ReconciliationHaltedAtUtc = null;
            logger.LogInformation("Disk reconciliation of volume {Volume} is below its threshold again; quarantine resumes.", volume.Name);
        }

        // Sightings not seen this pass: the file disappeared.
        var stale = await context.FileStorageOrphanSightings.Where(s => s.VolumeId == volume.Id && s.LastSeenPass < pass).ToListAsync(cancellationToken);
        context.FileStorageOrphanSightings.RemoveRange(stale);

        row.LastPassOrphanCount = orphans;
        row.ReconciliationPass = pass + 1;
        row.ReconciliationCursor = null;
        await context.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Disk reconciliation pass {Pass} of volume {Volume} completed: {Orphans} orphan files.", pass, volume.Name, orphans);
    }

    private void WarnNonConforming(ActiveVolume volume, int pass, int count)
    {
        if (count > 0 && memory.ShouldWarnNonConforming(volume.Id, pass))
        {
            logger.LogWarning(
                "Volume {Volume} holds {Count} files or folders under feature roots that do not match the store's naming rules; they are left alone.",
                volume.Name, count);
        }
    }

    // ── Shared ───────────────────────────────────────────────────────────────────────────────────────────

    private async Task LogUnknownFeaturesAsync(CancellationToken cancellationToken)
    {
        var counts = await context.StoredObjects.AsNoTracking()
            .GroupBy(o => new { o.FeatureKey, o.State })
            .Select(g => new { g.Key.FeatureKey, g.Key.State, Count = g.Count() })
            .ToListAsync(cancellationToken);
        foreach (var feature in counts.Where(c => catalogue.Find(c.FeatureKey) is null).GroupBy(c => c.FeatureKey))
        {
            logger.LogWarning(
                "Stored objects of unknown feature {Feature}: {Committed} committed (kept, not served), {PendingDelete} pending delete, {Pending} pending.",
                feature.Key,
                feature.Where(c => c.State == StoredObjectState.Committed).Sum(c => c.Count),
                feature.Where(c => c.State == StoredObjectState.PendingDelete).Sum(c => c.Count),
                feature.Where(c => c.State == StoredObjectState.Pending).Sum(c => c.Count));
        }
    }

    /// <summary>
    /// The structural, containment and symlink checks for a row, with the catalogue-free variant for a feature
    /// this build does not know. A row that fails is left untouched and logged as a security event.
    /// </summary>
    private bool TryResolve(StoredObject row, out ActiveVolume volume, out string fullPath, out string stopAt)
    {
        volume = null!;
        fullPath = string.Empty;
        stopAt = string.Empty;
        if (volumes.Find(row.VolumeId) is not { } found || !IsUsable(found))
        {
            return false;
        }

        volume = found;
        var feature = catalogue.Find(row.FeatureKey);
        var conforming = feature is not null
            ? StorageNames.IsConformingPath(row.RelativePath, feature.RootFolder)
            : StorageNames.IsConformingPathWithoutCatalogue(row.RelativePath, catalogue);
        if (!conforming)
        {
            SecurityEvent(volume, $"stored object {row.Id} has a path that fails the structural check; it is left untouched");
            return false;
        }

        var top = feature?.RootFolder ?? StorageNames.RootOf(row.RelativePath);
        try
        {
            fullPath = VolumeFileSystem.Contain(volume.RootPath, row.RelativePath, top);
            VolumeFileSystem.EnsureNoLinks(volume.RootPath, fullPath);
        }
        catch (FileStorageIntegrityException)
        {
            SecurityEvent(volume, $"stored object {row.Id} failed the containment or symlink check; it is left untouched");
            return false;
        }

        stopAt = Path.Combine(volume.RootPath, top);
        return true;
    }

    /// <summary>
    /// A volume a sweep may touch: verified, not degraded, and still carrying its marker — an unmounted disk would
    /// otherwise make every file look already deleted.
    /// </summary>
    private bool IsUsable(ActiveVolume volume)
    {
        if (volume.Degraded)
        {
            return false;
        }

        if (File.Exists(Path.Combine(volume.RootPath, StorageNames.MarkerFileName)))
        {
            return true;
        }

        volumes.MarkDegraded(volume, new DirectoryNotFoundException($"The marker of volume '{volume.Name}' is missing."));
        return false;
    }

    /// <summary>An absent file counts as success; anything else leaves the row to retry.</summary>
    private static void DeleteFileOrThrow(string fullPath)
    {
        try
        {
            File.Delete(fullPath);
        }
        catch (DirectoryNotFoundException)
        {
            // Already gone with its folder.
        }
    }

    private void SecurityEvent(ActiveVolume volume, string what) =>
        logger.LogError("Security: file-storage maintenance refused an operation on volume {Volume}: {What}.", volume.Name, what);

    private void LogCount(string what, int count)
    {
        if (count > 0)
        {
            logger.LogInformation("File-storage maintenance: {Count} {What}.", count, what);
        }
    }

    private DateTime Now => time.GetUtcNow().UtcDateTime;
}
