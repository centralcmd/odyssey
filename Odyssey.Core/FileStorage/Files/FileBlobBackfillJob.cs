using System.Data.Common;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Odyssey.Context;
using Odyssey.Core.Finance;
using Odyssey.Core.Jobs;
using Odyssey.Dtos.Application;

namespace Odyssey.Core.FileStorage.Files;

/// <summary>What happened to one legacy file.</summary>
public enum FileBackfillOutcome
{
    /// <summary>Moved to the finance area.</summary>
    MovedFinance,

    /// <summary>Moved to the journal area.</summary>
    MovedJournal,

    /// <summary>Moved to the contact-avatar area.</summary>
    MovedContactAvatar,

    /// <summary>Parked for a deterministic reason.</summary>
    Parked,

    /// <summary>Gone, already moved, re-classified or changed under the run: nothing to do this tick.</summary>
    Skipped,
}

/// <summary>
/// Moves every remaining <c>FileBlob</c> onto the file store (issue #448 §3.5). Runs in <c>Odyssey.Worker</c>; every replica
/// runs it and the single <see cref="FileBlobBackfillLease"/> row elects one runner per tick. Each file is classified from
/// its links, written to its area, read back and verified before the database changes; then, inside one transaction
/// whose first statement locks the file row, it is re-classified, pointed at its stored object and its blob removed.
/// The file's id never changes, so no link is rewritten. A file a legacy photo still wraps belongs to the photo move.
/// </summary>
public sealed class FileBlobBackfillJob(
    IServiceScopeFactory scopes,
    FileStorageVolumeRegistry volumes,
    IOptions<FileStorageOptions> fileStorageOptions,
    TimeProvider time,
    ILogger<FileBlobBackfillJob> logger) : IPeriodicJob
{
    public static readonly TimeSpan Tick = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan RunBudget = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(5);
    public const int BatchSize = 25;

    /// <summary>At most this many legacy rows are examined per tick while skipping files of an unhealthy area (§3.5 step 3).</summary>
    public const int ScanLimit = 500;

    private const int ScanPage = 50;

    /// <summary>The back-off after consecutive environmental failures: 1, 5, 15, then 60 minutes.</summary>
    public static TimeSpan Backoff(int consecutiveFailures) => consecutiveFailures switch
    {
        <= 1 => TimeSpan.FromMinutes(1),
        2 => TimeSpan.FromMinutes(5),
        3 => TimeSpan.FromMinutes(15),
        _ => TimeSpan.FromMinutes(60),
    };

    public string Name => "file-blob-backfill";

    public TimeSpan Interval => Tick;

    /// <summary>The counts of the tick in progress; the job's ticks never overlap, so one instance is enough.</summary>
    private FileBackfillTickTally tally = new();

    public async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        var recorder = Recorder();
        // The heartbeat first, before any early return, so an idle process is still seen alive.
        await recorder.HeartbeatAsync(JobType.FileBlobBackfill, cancellationToken);
        tally = new FileBackfillTickTally();
        Guid? manualRunId = null;
        JobHeldReason? held = null;
        JobFailureCategory? failure = null;
        try
        {
            (held, manualRunId) = await RunGatedAsync(recorder, cancellationToken);
        }
        catch (Exception ex)
        {
            failure = ex is OperationCanceledException ? JobFailureCategory.Cancelled : JobFailureCategory.Internal;
            throw;
        }
        finally
        {
            await RecordTickAsync(recorder, held, manualRunId, failure);
        }
    }

    private async Task<(JobHeldReason? Held, Guid? ManualRunId)> RunGatedAsync(JobRunRecorder recorder, CancellationToken cancellationToken)
    {
        await PruneOrphanFailuresAsync(cancellationToken);

        // Idle once done: one read, and no probe and no lease write, when no legacy file is left to move.
        if (!await AnyWorkAsync(cancellationToken))
        {
            return (null, null);
        }

        // The worker's own storage verdict (§3.5 step 2). Per-area health is decided per file, in the scan.
        if (!volumes.IsActive)
        {
            return (JobHeldReason.StorageUnavailable, null);
        }

        // A manual run cuts the back-off short: an operator who fixed the outage should not wait out the hour.
        var manualRunId = await recorder.ClaimManualAsync(JobType.FileBlobBackfill, cancellationToken);
        if (manualRunId is not null)
        {
            await ClearBackoffAsync(cancellationToken);
        }
        else if (await BackingOffAsync(cancellationToken))
        {
            return (JobHeldReason.BackingOff, null);
        }

        var allBlocked = await MoveBatchAsync(cancellationToken);
        return (allBlocked ? JobHeldReason.StorageUnavailable : null, manualRunId);
    }

    /// <summary>One batch under the lease. Returns true when every candidate was skipped for an unhealthy area.</summary>
    private async Task<bool> MoveBatchAsync(CancellationToken cancellationToken)
    {
        var holder = Guid.NewGuid();
        if (!await TryClaimAsync(holder, cancellationToken))
        {
            return false;
        }

        var started = time.GetUtcNow();
        var allBlocked = false;
        try
        {
            var scan = await NextCandidatesAsync(cancellationToken);
            allBlocked = scan.Eligible.Count == 0 && scan.SkippedForHealth > 0;
            foreach (var (fileId, area) in scan.Eligible)
            {
                if (time.GetUtcNow() - started > RunBudget)
                {
                    break;
                }

                FileBackfillOutcome outcome;
                try
                {
                    outcome = await MoveOneAsync(fileId, area, cancellationToken);
                }
                catch (Exception ex) when (!IsEnvironmental(ex) && ex is not OperationCanceledException)
                {
                    // Neither a check this job makes nor an outage. Park it so one file cannot stall every younger one.
                    // Only the exception type is logged: a message could carry a file name.
                    logger.LogWarning("File {FileId} failed with {Failure}; it is parked.", fileId, ex.GetType().Name);
                    outcome = await ParkAsync(fileId, FileBlobBackfillFailureCodes.Error, cancellationToken);
                }

                tally.Add(outcome);
                if (outcome is FileBackfillOutcome.MovedFinance or FileBackfillOutcome.MovedJournal or FileBackfillOutcome.MovedContactAvatar)
                {
                    await RecordSuccessAsync(holder, cancellationToken);
                }
            }
        }
        catch (Exception ex) when (IsEnvironmental(ex) && !cancellationToken.IsCancellationRequested)
        {
            // An outage is not the file's fault: nothing is parked, the tick ends and the job backs off.
            tally.Interruptions++;
            await RecordEnvironmentalFailureAsync(holder, ex, CancellationToken.None);
        }
        finally
        {
            await ReleaseAsync(holder);
        }

        if (tally.Moved > 0)
        {
            logger.LogInformation("Moved {Count} file(s) to the file store.", tally.Moved);
        }

        return allBlocked;
    }

    private JobRunRecorder Recorder()
    {
        using var scope = scopes.CreateScope();
        return scope.ServiceProvider.GetRequiredService<JobRunRecorder>();
    }

    /// <summary>The tick into the run log, as the photo move records it.</summary>
    private async Task RecordTickAsync(JobRunRecorder recorder, JobHeldReason? held, Guid? manualRunId, JobFailureCategory? failure)
    {
        var counts = tally;
        if (manualRunId is { } runId)
        {
            await recorder.FinishAsync<FileBlobBackfillJobRun>(JobType.FileBlobBackfill, runId, counts.AddTo,
                failure is { } category ? (JobRunStatus.Failed, category) : null, CancellationToken.None);
        }
        else if (counts.HasWork)
        {
            await recorder.ExtendBurstAsync<FileBlobBackfillJobRun>(JobType.FileBlobBackfill, counts.AddTo, CancellationToken.None);
        }

        if (held is null && (counts.HasWork || manualRunId is not null))
        {
            await recorder.ReleaseHoldAsync(JobType.FileBlobBackfill, CancellationToken.None);
            return;
        }

        await recorder.CloseBurstAsync(JobType.FileBlobBackfill, CancellationToken.None);
        if (held is { } reason)
        {
            await recorder.HoldAsync<FileBlobBackfillJobRun>(JobType.FileBlobBackfill, reason, null, CancellationToken.None);
        }
        else
        {
            await recorder.ReleaseHoldAsync(JobType.FileBlobBackfill, CancellationToken.None);
        }
    }

    private async Task<bool> BackingOffAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        var now = time.GetUtcNow().UtcDateTime;
        return await context.FileBlobBackfillLeases.AsNoTracking()
            .AnyAsync(l => l.Id == FileBlobBackfillLease.SingletonId && l.NextAttemptAtUtc > now, cancellationToken);
    }

    /// <summary>A manual run ends the back-off; the failure count is kept, so a second outage still backs off longer.</summary>
    private async Task ClearBackoffAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        var lease = await context.FileBlobBackfillLeases
            .FirstOrDefaultAsync(l => l.Id == FileBlobBackfillLease.SingletonId, cancellationToken);
        if (lease?.NextAttemptAtUtc is null)
        {
            return;
        }

        lease.NextAttemptAtUtc = null;
        lease.Version = Guid.NewGuid();
        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Another replica changed the lease; its own claim decides.
        }
    }

    /// <summary>Legacy files not parked and not wrapped by a legacy photo — the job's work, and its manual run's.</summary>
    public static IQueryable<FileMetadata> Movable(OdysseyContext context) =>
        context.FileMetadata.AsNoTracking()
            .Where(f => f.FileBlobId != null)
            .Where(f => !context.FileBlobBackfillFailures.Any(x => x.FileId == f.Id))
            .Where(f => !context.Photos.Any(p => p.FileId == f.Id));

    public async Task<bool> AnyWorkAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        return await Movable(scope.ServiceProvider.GetRequiredService<OdysseyContext>()).AnyAsync(cancellationToken);
    }

    /// <summary>A failure row whose file is gone or no longer legacy needs no retry (the table has no FK, §4.4).</summary>
    private async Task PruneOrphanFailuresAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        var orphans = await context.FileBlobBackfillFailures
            .Where(x => !context.FileMetadata.Any(f => f.Id == x.FileId && f.FileBlobId != null))
            .Take(100)
            .ToListAsync(cancellationToken);
        if (orphans.Count == 0)
        {
            return;
        }

        context.FileBlobBackfillFailures.RemoveRange(orphans);
        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Another replica pruned them first.
        }
    }

    /// <summary>The result of one tick's selection scan.</summary>
    public sealed record CandidateScan(IReadOnlyList<(Guid FileId, StoredFileArea Area)> Eligible, int SkippedForHealth, int Examined);

    /// <summary>
    /// §3.5 step 3: oldest first, classified, and <b>scanning past</b> files whose area's volume is unhealthy this tick, until
    /// <see cref="BatchSize"/> are eligible or <see cref="ScanLimit"/> rows were examined. Each area is probed once per tick.
    /// A full finance disk therefore never hides younger journal or avatar files behind older finance ones.
    /// </summary>
    public async Task<CandidateScan> NextCandidatesAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        var classifier = scope.ServiceProvider.GetRequiredService<FileAreaClassifier>();
        var writer = scope.ServiceProvider.GetRequiredService<StoredFileWriter>();
        var health = new Dictionary<StoredFileArea, bool>();
        var eligible = new List<(Guid, StoredFileArea)>();
        var skipped = 0;
        var examined = 0;

        while (eligible.Count < BatchSize && examined < ScanLimit)
        {
            var page = await Movable(context)
                .OrderBy(f => f.UploadedAtUtc).ThenBy(f => f.Id)
                .Skip(examined)
                .Take(Math.Min(ScanPage, ScanLimit - examined))
                .Select(f => new { f.Id, f.ContentType })
                .ToListAsync(cancellationToken);
            if (page.Count == 0)
            {
                break;
            }

            foreach (var row in page)
            {
                examined++;
                var area = await classifier.ClassifyAsync(row.Id, row.ContentType, cancellationToken);
                if (!health.TryGetValue(area, out var healthy))
                {
                    healthy = await AreaHealthyAsync(writer, area, cancellationToken);
                    health[area] = healthy;
                }

                if (!healthy)
                {
                    skipped++;
                    continue;
                }

                eligible.Add((row.Id, area));
                if (eligible.Count == BatchSize)
                {
                    break;
                }
            }
        }

        return new CandidateScan(eligible, skipped, examined);
    }

    private async Task<bool> AreaHealthyAsync(StoredFileWriter writer, StoredFileArea area, CancellationToken cancellationToken)
    {
        if (!volumes.Bindings.TryGetValue(StoredFileAreas.FeatureKey(area), out var name) || volumes.Find(name) is not { } volume)
        {
            return false;
        }

        if (volume.Degraded && !await volumes.ProbeAsync(volume, cancellationToken))
        {
            return false;
        }

        return writer.CanWrite(area);
    }

    /// <summary>One file (§3.5 steps 5–7). Environmental failures propagate; deterministic ones park the file.</summary>
    public async Task<FileBackfillOutcome> MoveOneAsync(Guid fileId, StoredFileArea area, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        var source = await context.FileMetadata.AsNoTracking()
            .Where(f => f.Id == fileId && f.FileBlobId != null)
            .Select(f => new { f.FileName, f.ContentType, f.SizeBytes, f.Sha256Hash, f.FileBlobId, f.UploadedAtUtc })
            .FirstOrDefaultAsync(cancellationToken);
        if (source is null)
        {
            return FileBackfillOutcome.Skipped;
        }

        if (!StorageNames.IsValidExtension(ContentTypeExtensions.For(source.ContentType)))
        {
            return await ParkAsync(fileId, FileBlobBackfillFailureCodes.Type, cancellationToken);
        }

        // The deploy-time ceiling, never the admin upload cap: lowering the cap must not strand old files.
        var ceiling = fileStorageOptions.Value.MaxFileSizeBytes;
        if (source.SizeBytes > ceiling)
        {
            return await ParkAsync(fileId, FileBlobBackfillFailureCodes.TooLarge, cancellationToken);
        }

        var bytes = await context.FileBlob.AsNoTracking()
            .Where(b => b.Id == source.FileBlobId)
            .Select(b => b.Content)
            .FirstOrDefaultAsync(cancellationToken);
        if (bytes is null || bytes.LongLength != source.SizeBytes)
        {
            return await ParkAsync(fileId, FileBlobBackfillFailureCodes.Read, cancellationToken);
        }

        var expected = source.Sha256Hash.ToLowerInvariant();
        if (!string.Equals(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), expected, StringComparison.Ordinal))
        {
            return await ParkAsync(fileId, FileBlobBackfillFailureCodes.Hash, cancellationToken);
        }

        // Write, then read back what is on disk (§3.5 steps 5–6). A failure here leaves only a reservation, which the
        // abandoned-write sweep removes with its file.
        var writer = scope.ServiceProvider.GetRequiredService<StoredFileWriter>();
        var store = scope.ServiceProvider.GetRequiredService<IObjectStore>();
        StoredObject stored;
        await using (var content = new MemoryStream(bytes, writable: false))
        {
            stored = await writer.PutAsync(area, content, source.FileName, source.ContentType, source.UploadedAtUtc, ceiling, cancellationToken);
        }

        if (!string.Equals(stored.Sha256Hash, expected, StringComparison.Ordinal)
            || !string.Equals(await store.ReadBackHashAsync(stored, cancellationToken), expected, StringComparison.Ordinal))
        {
            return await ParkAsync(fileId, FileBlobBackfillFailureCodes.Hash, cancellationToken);
        }

        bytes = null;
        return await CommitAsync(context, store, scope.ServiceProvider.GetRequiredService<FileAreaClassifier>(), fileId, area, stored, cancellationToken);
    }

    /// <summary>
    /// §3.5 step 7, in one transaction whose FIRST statement locks the file row: a concurrent attach (its FK insert takes
    /// a shared lock on the parent) or delete waits, so the re-classification reads one snapshot with the commit.
    /// </summary>
    private static async Task<FileBackfillOutcome> CommitAsync(
        OdysseyContext context, IObjectStore store, FileAreaClassifier classifier, Guid fileId, StoredFileArea area,
        StoredObject stored, CancellationToken cancellationToken)
    {
        if (!context.Database.IsRelational())
        {
            // The EF InMemory provider (tests) has no locks or transactions; the same steps run unguarded.
            return await ApplyAsync(context, store, classifier, fileId, area, stored, cancellationToken);
        }

        var strategy = context.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
            var locked = await context.Database
                .SqlQuery<Guid>($"SELECT `Id` AS `Value` FROM `FileMetadata` WHERE `Id` = {fileId} FOR UPDATE")
                .ToListAsync(cancellationToken);
            if (locked.Count == 0)
            {
                return FileBackfillOutcome.Skipped;
            }

            var outcome = await ApplyAsync(context, store, classifier, fileId, area, stored, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return outcome;
        });
    }

    private static async Task<FileBackfillOutcome> ApplyAsync(
        OdysseyContext context, IObjectStore store, FileAreaClassifier classifier, Guid fileId, StoredFileArea area,
        StoredObject stored, CancellationToken cancellationToken)
    {
        var file = await context.FileMetadata.FirstOrDefaultAsync(f => f.Id == fileId, cancellationToken);
        if (file is not { FileBlobId: { } blobId })
        {
            // Gone or already moved: the written reservation is swept.
            return FileBackfillOutcome.Skipped;
        }

        // Re-classify under the lock. A link added or removed since the scan changes nothing on disk now: the
        // reservation is released at once and the file waits for the next tick. A detach that commits after this read is
        // not blocked and may leave the file in a root a later read would not choose — benign, placement only.
        if (await classifier.ClassifyAsync(fileId, file.ContentType, cancellationToken) != area)
        {
            store.StageRelease(stored, immediate: true);
            await context.SaveChangesAsync(cancellationToken);
            return FileBackfillOutcome.Skipped;
        }

        // Null the key and remove the blob in ONE save: FileBlob is the principal of a CASCADE to FileMetadata, so
        // removing the blob alone would cascade the file itself away.
        StoredFileAreas.Assign(file, area, stored.Id);
        file.FileBlobId = null;
        var blob = await context.FileBlob.FirstOrDefaultAsync(b => b.Id == blobId, cancellationToken);
        if (blob is not null)
        {
            context.FileBlob.Remove(blob);
        }

        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // The file or the reservation changed under the run; the reservation is swept.
            return FileBackfillOutcome.Skipped;
        }

        return area switch
        {
            StoredFileArea.Journal => FileBackfillOutcome.MovedJournal,
            StoredFileArea.ContactAvatars => FileBackfillOutcome.MovedContactAvatar,
            _ => FileBackfillOutcome.MovedFinance,
        };
    }

    private async Task<FileBackfillOutcome> ParkAsync(Guid fileId, string code, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        if (!await context.FileBlobBackfillFailures.AnyAsync(f => f.FileId == fileId, cancellationToken))
        {
            context.FileBlobBackfillFailures.Add(new FileBlobBackfillFailure
            {
                FileId = fileId,
                Code = code,
                RecordedAtUtc = time.GetUtcNow().UtcDateTime,
            });
            await context.SaveChangesAsync(cancellationToken);
        }

        logger.LogWarning("File {FileId} was not moved to the file store ({Code}); it stays in the database.", fileId, code);
        return FileBackfillOutcome.Parked;
    }

    /// <summary>The lease, honouring the back-off; a concurrency failure or a live lease held by another means no.</summary>
    public async Task<bool> TryClaimAsync(Guid holder, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        var now = time.GetUtcNow().UtcDateTime;
        try
        {
            var lease = await context.FileBlobBackfillLeases
                .SingleOrDefaultAsync(l => l.Id == FileBlobBackfillLease.SingletonId, cancellationToken);
            if (lease is null)
            {
                // The migration seeds the row; a database created without HasData (EF InMemory) gets it here.
                context.FileBlobBackfillLeases.Add(new FileBlobBackfillLease
                {
                    Id = FileBlobBackfillLease.SingletonId, HolderId = holder, LeaseUntilUtc = now + LeaseDuration, Version = Guid.NewGuid(),
                });
                await context.SaveChangesAsync(cancellationToken);
                return true;
            }

            if ((lease.HolderId is { } current && current != holder && lease.LeaseUntilUtc > now)
                || lease.NextAttemptAtUtc > now)
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
            return false;
        }
    }

    private Task RecordSuccessAsync(Guid holder, CancellationToken cancellationToken) =>
        UpdateLeaseAsync(holder, lease =>
        {
            lease.ConsecutiveEnvironmentalFailures = 0;
            lease.NextAttemptAtUtc = null;
        }, cancellationToken);

    private async Task RecordEnvironmentalFailureAsync(Guid holder, Exception ex, CancellationToken cancellationToken)
    {
        DateTime? until = null;
        await UpdateLeaseAsync(holder, lease =>
        {
            lease.ConsecutiveEnvironmentalFailures++;
            until = time.GetUtcNow().UtcDateTime + Backoff(lease.ConsecutiveEnvironmentalFailures);
            lease.NextAttemptAtUtc = until;
        }, cancellationToken);
        logger.LogWarning(
            "The file move stopped this tick after a storage or database error ({Failure}); it resumes at {ResumesAtUtc:O}.",
            ex.GetType().Name, until);
    }

    private async Task ReleaseAsync(Guid holder)
    {
        try
        {
            await UpdateLeaseAsync(holder, lease =>
            {
                lease.HolderId = null;
                lease.LeaseUntilUtc = null;
                lease.LastRunAtUtc = time.GetUtcNow().UtcDateTime;
            }, CancellationToken.None);
        }
        catch (Exception ex) when (IsEnvironmental(ex))
        {
            // The lease simply expires.
        }
    }

    private async Task UpdateLeaseAsync(Guid holder, Action<FileBlobBackfillLease> change, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        var lease = await context.FileBlobBackfillLeases
            .SingleOrDefaultAsync(l => l.Id == FileBlobBackfillLease.SingletonId && l.HolderId == holder, cancellationToken);
        if (lease is null)
        {
            return;
        }

        change(lease);
        lease.Version = Guid.NewGuid();
        await context.SaveChangesAsync(cancellationToken);
    }

    private static bool IsEnvironmental(Exception ex) =>
        ex is FileStorageUnavailableException or FileStorageFullException or IOException or DbException or RetryLimitExceededException
        || ex is DbUpdateException { InnerException: DbException };
}

/// <summary>What one tick of the file move did, added to its run row.</summary>
internal sealed class FileBackfillTickTally
{
    public int MovedFinance { get; set; }

    public int MovedJournal { get; set; }

    public int MovedContactAvatar { get; set; }

    public int Parked { get; set; }

    public int Interruptions { get; set; }

    public int Moved => MovedFinance + MovedJournal + MovedContactAvatar;

    /// <summary>A tick that moved, parked or was cut short; anything else is idle.</summary>
    public bool HasWork => Moved + Parked + Interruptions > 0;

    public void Add(FileBackfillOutcome outcome)
    {
        switch (outcome)
        {
            case FileBackfillOutcome.MovedFinance:
                MovedFinance++;
                break;
            case FileBackfillOutcome.MovedJournal:
                MovedJournal++;
                break;
            case FileBackfillOutcome.MovedContactAvatar:
                MovedContactAvatar++;
                break;
            case FileBackfillOutcome.Parked:
                Parked++;
                break;
        }
    }

    public void AddTo(FileBlobBackfillJobRun run)
    {
        run.FinanceFilesMoved += MovedFinance;
        run.JournalFilesMoved += MovedJournal;
        run.ContactAvatarsMoved += MovedContactAvatar;
        run.FilesParked += Parked;
        run.Interruptions += Interruptions;
    }
}
