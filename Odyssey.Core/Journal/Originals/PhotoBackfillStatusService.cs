using Microsoft.EntityFrameworkCore;
using Odyssey.Context;
using Odyssey.Dtos.Application;

namespace Odyssey.Core.Journal.Originals;

/// <summary>
/// The progress <c>GET /api/file-storage/photo-backfill</c> reports (issue #428 §5.5): counts and failure codes only,
/// never an id, a name or a path.
/// </summary>
public sealed class PhotoBackfillStatusService(OdysseyContext context)
{
    /// <summary>Every failure code, so a code with no parked photo still reads as zero rather than missing.</summary>
    private static readonly string[] Codes =
    [
        PhotoOriginalBackfillFailureCodes.Hash,
        PhotoOriginalBackfillFailureCodes.Read,
        PhotoOriginalBackfillFailureCodes.TooLarge,
        PhotoOriginalBackfillFailureCodes.Error,
    ];

    public async Task<PhotoBackfillStatus> GetAsync(CancellationToken cancellationToken)
    {
        var lease = await context.PhotoOriginalBackfillLeases.AsNoTracking()
            .FirstOrDefaultAsync(l => l.Id == PhotoOriginalBackfillLease.SingletonId, cancellationToken);
        var byCode = await context.PhotoOriginalBackfillFailures.AsNoTracking()
            .GroupBy(f => f.Code)
            .Select(g => new { Code = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);
        var failedByCode = Codes.ToDictionary(code => code, _ => 0);
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
