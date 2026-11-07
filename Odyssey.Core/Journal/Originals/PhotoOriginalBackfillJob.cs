using System.Data.Common;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Odyssey.Context;
using Odyssey.Core.FileStorage;
using Odyssey.Core.Finance;
using Odyssey.Core.Jobs;
using Odyssey.Dtos.Application;

namespace Odyssey.Core.Journal.Originals;

/// <summary>What happened to one legacy photo.</summary>
public enum PhotoBackfillOutcome
{
    /// <summary>Moved; its file was removed because nothing else used it.</summary>
    Moved,

    /// <summary>Moved; its file stays because something else still uses it.</summary>
    CopiedShared,

    /// <summary>Parked for a deterministic reason.</summary>
    Parked,

    /// <summary>Gone, already moved, or changed under the run: nothing to do.</summary>
    Skipped,
}

/// <summary>
/// Moves the originals of legacy photos out of <c>FileBlob</c> onto the photo store (issue #428 §3.4). Runs in
/// <c>Odyssey.Worker</c>; every replica runs it and the single <see cref="PhotoOriginalBackfillLease"/> row elects one
/// runner per tick. Each photo is written, read back and verified before the database changes, and its file is
/// deleted only when the model-derived reference counter finds no other user — inside one transaction whose first
/// statement locks the file row.
/// </summary>
public sealed class PhotoOriginalBackfillJob(
    IServiceScopeFactory scopes,
    FileStorageVolumeRegistry volumes,
    IOptions<FileStorageOptions> fileStorageOptions,
    TimeProvider time,
    ILogger<PhotoOriginalBackfillJob> logger) : IPeriodicJob
{
    public static readonly TimeSpan Tick = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan RunBudget = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(5);
    public const int BatchSize = 25;

    /// <summary>The back-off after consecutive environmental failures: 1, 5, 15, then 60 minutes.</summary>
    public static TimeSpan Backoff(int consecutiveFailures) => consecutiveFailures switch
    {
        <= 1 => TimeSpan.FromMinutes(1),
        2 => TimeSpan.FromMinutes(5),
        3 => TimeSpan.FromMinutes(15),
        _ => TimeSpan.FromMinutes(60),
    };

    public string Name => "photo-original-backfill";

    public TimeSpan Interval => Tick;

    public async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        // The health gate (§3.4 step 2): the store verified in this process, the volume healthy, the reserve held.
        if (!volumes.IsActive || !await StorageHealthyAsync(cancellationToken))
        {
            return;
        }

        var holder = Guid.NewGuid();
        if (!await TryClaimAsync(holder, cancellationToken))
        {
            return;
        }

        var started = time.GetUtcNow();
        var moved = 0;
        try
        {
            var candidates = await NextCandidatesAsync(cancellationToken);
            foreach (var photoId in candidates)
            {
                if (time.GetUtcNow() - started > RunBudget)
                {
                    break;
                }

                var outcome = await MoveOneAsync(photoId, cancellationToken);
                if (outcome is PhotoBackfillOutcome.Moved or PhotoBackfillOutcome.CopiedShared)
                {
                    moved++;
                    await RecordSuccessAsync(holder, outcome == PhotoBackfillOutcome.CopiedShared, cancellationToken);
                }
            }
        }
        catch (Exception ex) when (IsEnvironmental(ex) && !cancellationToken.IsCancellationRequested)
        {
            // An outage is not the photo's fault (§3.4 step 8): nothing is parked, the tick ends and the job backs off.
            await RecordEnvironmentalFailureAsync(holder, ex, CancellationToken.None);
        }
        finally
        {
            await ReleaseAsync(holder);
        }

        if (moved > 0)
        {
            logger.LogInformation("Moved {Count} photo original(s) to the photo store.", moved);
        }
    }

    /// <summary>Legacy photos not parked and without a running thumbnail job (§3.4 step 3), oldest first.</summary>
    public async Task<IReadOnlyList<Guid>> NextCandidatesAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        return await context.Photos.AsNoTracking()
            .Where(p => p.StoredObjectId == null && p.FileId != null)
            .Where(p => !context.PhotoOriginalBackfillFailures.Any(f => f.PhotoId == p.PhotoId))
            .Where(p => !context.PhotoThumbnailJobs.Any(j => j.PhotoId == p.PhotoId && j.State == PhotoThumbnailJobState.Running))
            .OrderBy(p => p.CreatedAt)
            .ThenBy(p => p.PhotoId)
            .Select(p => p.PhotoId)
            .Take(BatchSize)
            .ToListAsync(cancellationToken);
    }

    /// <summary>One photo (§3.4 steps 4–7). Environmental failures propagate; deterministic ones park the photo.</summary>
    public async Task<PhotoBackfillOutcome> MoveOneAsync(Guid photoId, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        var source = await context.Photos.AsNoTracking()
            .Where(p => p.PhotoId == photoId && p.StoredObjectId == null && p.FileId != null)
            .Join(context.FileMetadata, p => p.FileId, f => (Guid?)f.Id, (p, f) => new
            {
                FileId = f.Id,
                p.CreatedAt,
                Name = p.FileName ?? f.FileName,
                f.ContentType,
                f.SizeBytes,
                f.Sha256Hash,
                f.FileBlobId,
            })
            .FirstOrDefaultAsync(cancellationToken);
        if (source is null)
        {
            return PhotoBackfillOutcome.Skipped;
        }

        var contentType = PhotoOriginalLimits.Normalize(source.ContentType);
        if (!PhotoOriginalLimits.ContentTypes.Contains(contentType) || ContentTypeExtensions.For(contentType) is null)
        {
            return await ParkAsync(photoId, PhotoOriginalBackfillFailureCodes.Read, cancellationToken);
        }

        // The deploy-time ceiling, never the admin upload cap: lowering the cap must not strand old photos (§3.4 step 8).
        var ceiling = fileStorageOptions.Value.MaxFileSizeBytes;
        if (source.SizeBytes > ceiling)
        {
            return await ParkAsync(photoId, PhotoOriginalBackfillFailureCodes.TooLarge, cancellationToken);
        }

        var bytes = await context.FileBlob.AsNoTracking()
            .Where(b => b.Id == source.FileBlobId)
            .Select(b => b.Content)
            .FirstOrDefaultAsync(cancellationToken);
        if (bytes is null || bytes.LongLength != source.SizeBytes)
        {
            return await ParkAsync(photoId, PhotoOriginalBackfillFailureCodes.Read, cancellationToken);
        }

        var expected = source.Sha256Hash.ToLowerInvariant();
        if (!string.Equals(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), expected, StringComparison.Ordinal))
        {
            return await ParkAsync(photoId, PhotoOriginalBackfillFailureCodes.Hash, cancellationToken);
        }

        // Write, then read back what is on disk (§3.4 steps 4–5). A failure here leaves only a reservation, which the
        // abandoned-write sweep removes with its file.
        var writer = scope.ServiceProvider.GetRequiredService<PhotoOriginalWriter>();
        var store = scope.ServiceProvider.GetRequiredService<IObjectStore>();
        StoredObject stored;
        await using (var content = new MemoryStream(bytes, writable: false))
        {
            stored = await writer.PutAsync(content, source.Name, contentType, source.CreatedAt, ceiling, cancellationToken);
        }

        if (!string.Equals(stored.Sha256Hash, expected, StringComparison.Ordinal)
            || !string.Equals(await store.ReadBackHashAsync(stored, cancellationToken), expected, StringComparison.Ordinal))
        {
            return await ParkAsync(photoId, PhotoOriginalBackfillFailureCodes.Hash, cancellationToken);
        }

        bytes = null;
        return await CommitAsync(context, scope.ServiceProvider.GetRequiredService<IFileReferenceCounter>(),
            photoId, source.FileId, stored, cancellationToken);
    }

    /// <summary>
    /// §3.4 step 6, in one transaction whose FIRST statement locks the file row: a concurrent attach (its FK insert
    /// takes a shared lock on the parent) or delete waits, so the reference count and the delete read one snapshot.
    /// </summary>
    private static async Task<PhotoBackfillOutcome> CommitAsync(
        OdysseyContext context, IFileReferenceCounter references, Guid photoId, Guid fileId, StoredObject stored,
        CancellationToken cancellationToken)
    {
        if (!context.Database.IsRelational())
        {
            // The EF InMemory provider (tests) has no locks or transactions; the same steps run unguarded.
            return await ApplyAsync(context, references, photoId, fileId, stored, cancellationToken);
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
                return PhotoBackfillOutcome.Skipped;
            }

            var outcome = await ApplyAsync(context, references, photoId, fileId, stored, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return outcome;
        });
    }

    private static async Task<PhotoBackfillOutcome> ApplyAsync(
        OdysseyContext context, IFileReferenceCounter references, Guid photoId, Guid fileId, StoredObject stored,
        CancellationToken cancellationToken)
    {
        var photo = await context.Photos.FirstOrDefaultAsync(p => p.PhotoId == photoId, cancellationToken);
        var file = await context.FileMetadata.FirstOrDefaultAsync(f => f.Id == fileId, cancellationToken);
        if (photo is not { StoredObjectId: null } || photo.FileId != fileId || file is null)
        {
            return PhotoBackfillOutcome.Skipped;
        }

        photo.StoredObjectId = stored.Id;
        photo.ContentType = PhotoOriginalLimits.Normalize(file.ContentType);
        photo.SizeBytes = stored.SizeBytes;
        photo.Sha256Hash = stored.Sha256Hash;
        // A rename made while the photo was legacy wrote Photo.FileName, and it wins (§5.4).
        photo.FileName ??= file.FileName;
        photo.LegacyFileId = fileId;
        photo.FileId = null;

        var shared = await references.HasOtherReferencesAsync(fileId, cancellationToken);
        if (!shared)
        {
            var blob = await context.FileBlob.FirstOrDefaultAsync(b => b.Id == file.FileBlobId, cancellationToken);
            context.FileMetadata.Remove(file);
            if (blob is not null)
            {
                context.FileBlob.Remove(blob);
            }
        }

        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // The photo, its file or the reservation changed under the run; the reservation is swept.
            return PhotoBackfillOutcome.Skipped;
        }

        return shared ? PhotoBackfillOutcome.CopiedShared : PhotoBackfillOutcome.Moved;
    }

    private async Task<PhotoBackfillOutcome> ParkAsync(Guid photoId, string code, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        if (!await context.PhotoOriginalBackfillFailures.AnyAsync(f => f.PhotoId == photoId, cancellationToken))
        {
            context.PhotoOriginalBackfillFailures.Add(new PhotoOriginalBackfillFailure
            {
                PhotoId = photoId,
                Code = code,
                RecordedAtUtc = time.GetUtcNow().UtcDateTime,
            });
            await context.SaveChangesAsync(cancellationToken);
        }

        logger.LogWarning("Photo {PhotoId} was not moved to the photo store ({Code}); it stays in the database.", photoId, code);
        return PhotoBackfillOutcome.Parked;
    }

    private async Task<bool> StorageHealthyAsync(CancellationToken cancellationToken)
    {
        if (!volumes.Bindings.TryGetValue(PhotoOriginalStorage.FeatureKey, out var name) || volumes.Find(name) is not { } volume)
        {
            return false;
        }

        if (volume.Degraded && !await volumes.ProbeAsync(volume, cancellationToken))
        {
            return false;
        }

        await using var scope = scopes.CreateAsyncScope();
        return scope.ServiceProvider.GetRequiredService<PhotoOriginalWriter>().HasReserve();
    }

    /// <summary>The lease, honouring the back-off; a concurrency failure or a live lease held by another means no.</summary>
    public async Task<bool> TryClaimAsync(Guid holder, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        var now = time.GetUtcNow().UtcDateTime;
        try
        {
            var lease = await context.PhotoOriginalBackfillLeases
                .SingleOrDefaultAsync(l => l.Id == PhotoOriginalBackfillLease.SingletonId, cancellationToken);
            if (lease is null)
            {
                // The migration seeds the row; a database created without HasData (EF InMemory) gets it here.
                context.PhotoOriginalBackfillLeases.Add(new PhotoOriginalBackfillLease
                {
                    Id = PhotoOriginalBackfillLease.SingletonId, HolderId = holder, LeaseUntilUtc = now + LeaseDuration, Version = Guid.NewGuid(),
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

    private Task RecordSuccessAsync(Guid holder, bool copiedShared, CancellationToken cancellationToken) =>
        UpdateLeaseAsync(holder, lease =>
        {
            lease.ConsecutiveEnvironmentalFailures = 0;
            lease.NextAttemptAtUtc = null;
            if (copiedShared)
            {
                lease.CopiedSharedCount++;
            }
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
            "The photo-original backfill stopped this tick after a storage or database error ({Failure}); it resumes at {ResumesAtUtc:O}.",
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

    private async Task UpdateLeaseAsync(Guid holder, Action<PhotoOriginalBackfillLease> change, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        var lease = await context.PhotoOriginalBackfillLeases
            .SingleOrDefaultAsync(l => l.Id == PhotoOriginalBackfillLease.SingletonId && l.HolderId == holder, cancellationToken);
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

    /// <summary>The progress the status endpoint reports (§5.5).</summary>
    public static async Task<PhotoBackfillStatus> GetStatusAsync(OdysseyContext context, CancellationToken cancellationToken)
    {
        var lease = await context.PhotoOriginalBackfillLeases.AsNoTracking()
            .FirstOrDefaultAsync(l => l.Id == PhotoOriginalBackfillLease.SingletonId, cancellationToken);
        var byCode = await context.PhotoOriginalBackfillFailures.AsNoTracking()
            .GroupBy(f => f.Code)
            .Select(g => new { Code = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);
        var failedByCode = new Dictionary<string, int>
        {
            [PhotoOriginalBackfillFailureCodes.Hash] = 0,
            [PhotoOriginalBackfillFailureCodes.Read] = 0,
            [PhotoOriginalBackfillFailureCodes.TooLarge] = 0,
        };
        foreach (var row in byCode)
        {
            failedByCode[row.Code] = row.Count;
        }

        return new PhotoBackfillStatus
        {
            Remaining = await context.Photos.CountAsync(p => p.StoredObjectId == null, cancellationToken),
            CopiedShared = lease?.CopiedSharedCount ?? 0,
            Failed = byCode.Sum(c => c.Count),
            FailedByCode = failedByCode,
            BackingOffUntilUtc = lease?.NextAttemptAtUtc,
            LastRunAtUtc = lease?.LastRunAtUtc,
        };
    }
}
