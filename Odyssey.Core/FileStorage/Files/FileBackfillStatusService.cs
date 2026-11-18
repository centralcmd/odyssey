using Microsoft.EntityFrameworkCore;
using Odyssey.Context;
using Odyssey.Dtos.Application;

namespace Odyssey.Core.FileStorage.Files;

/// <summary>
/// The progress <c>GET /api/file-storage/file-backfill</c> and the background-jobs overview report (issue #448 §5.4):
/// counts and failure codes only, never an id, a name, a path or a volume. The three counts of legacy files are disjoint
/// — a parked file counts as parked only, a photo-wrapped one as waiting only — so a client subtracts nothing.
/// </summary>
public sealed class FileBackfillStatusService(OdysseyContext context)
{
    public async Task<FileBackfillStatus> GetAsync(CancellationToken cancellationToken)
    {
        var legacy = context.FileMetadata.AsNoTracking().Where(f => f.FileBlobId != null);
        // A failure row is counted only while its file is still legacy, so an orphan row is never counted.
        var parked = legacy.Where(f => context.FileBlobBackfillFailures.Any(x => x.FileId == f.Id));
        var unparked = legacy.Where(f => !context.FileBlobBackfillFailures.Any(x => x.FileId == f.Id));
        var waiting = unparked.Where(f => context.Photos.Any(p => p.FileId == f.Id));
        var toMove = unparked.Where(f => !context.Photos.Any(p => p.FileId == f.Id));

        var byCode = await context.FileBlobBackfillFailures.AsNoTracking()
            .Where(x => legacy.Any(f => f.Id == x.FileId))
            .GroupBy(x => x.Code)
            .Select(g => new { Code = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);
        var failedByCode = FileBlobBackfillFailureCodes.All.ToDictionary(code => code, _ => 0);
        foreach (var row in byCode)
        {
            failedByCode[row.Code] = row.Count;
        }

        var lease = await context.FileBlobBackfillLeases.AsNoTracking()
            .FirstOrDefaultAsync(l => l.Id == FileBlobBackfillLease.SingletonId, cancellationToken);
        var files = context.FileMetadata.AsNoTracking();
        return new FileBackfillStatus
        {
            Remaining = await legacy.CountAsync(cancellationToken),
            ToMove = await toMove.CountAsync(cancellationToken),
            WaitingOnPhotos = await waiting.CountAsync(cancellationToken),
            Failed = await parked.CountAsync(cancellationToken),
            RemainingBytes = await legacy.SumAsync(f => (long?)f.SizeBytes, cancellationToken) ?? 0,
            OnDisk = new FileBackfillOnDisk
            {
                Finance = await files.CountAsync(f => f.FinanceStoredObjectId != null, cancellationToken),
                Journal = await files.CountAsync(f => f.JournalStoredObjectId != null, cancellationToken),
                ContactAvatars = await files.CountAsync(f => f.ContactAvatarStoredObjectId != null, cancellationToken),
            },
            FailedByCode = failedByCode,
            BackingOffUntilUtc = lease?.NextAttemptAtUtc,
            LastRunAtUtc = lease?.LastRunAtUtc,
        };
    }
}
