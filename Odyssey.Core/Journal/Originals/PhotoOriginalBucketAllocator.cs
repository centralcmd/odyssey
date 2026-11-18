using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MySqlConnector;
using Odyssey.Context;
using Odyssey.Core.FileStorage;

namespace Odyssey.Core.Journal.Originals;

/// <summary>
/// Takes a slot in a year's current bucket (issue #428 §3.5), in its own short transaction before the file is
/// written, so a crash can only under-fill a bucket. On MariaDB the year's row is locked with
/// <c>SELECT … FOR UPDATE</c>, bounded per statement to 5 s: concurrent uploads queue on the lock instead of failing
/// optimistic retries. A wait beyond the bound is a <c>503</c>.
/// </summary>
public sealed class PhotoOriginalBucketAllocator(
    IDbContextFactory<OdysseyContext> contextFactory,
    IOptions<PhotoOriginalOptions> options,
    TimeProvider time)
{
    private const int LockWaitTimeout = 1205;

    public async Task<int> AllocateAsync(int year, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var capacity = Math.Max(1, options.Value.BucketCapacity);
        var now = time.GetUtcNow().UtcDateTime;

        if (!context.Database.IsRelational())
        {
            // The EF InMemory provider (tests) has no locks; the fast tiers run one writer at a time.
            var row = await context.PhotoOriginalBuckets.FirstOrDefaultAsync(b => b.Year == year, cancellationToken);
            if (row is null)
            {
                row = new PhotoOriginalBucket { Year = year, CurrentBucket = 1, PhotosWritten = 0 };
                context.PhotoOriginalBuckets.Add(row);
            }

            var bucket = Take(row, capacity, now);
            await context.SaveChangesAsync(cancellationToken);
            return bucket;
        }

        var strategy = context.Database.CreateExecutionStrategy();
        try
        {
            return await strategy.ExecuteAsync(async () =>
            {
                await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

                // Inserting the year's first row takes the same row lock a later SELECT would; IGNORE makes the
                // loser of a first-use race wait for the winner and then read its row.
                await context.Database.ExecuteSqlInterpolatedAsync(
                    $"SET STATEMENT innodb_lock_wait_timeout = {PhotoOriginalLimits.BucketLockWaitSeconds} FOR INSERT IGNORE INTO `PhotoOriginalBuckets` (`Year`, `CurrentBucket`, `PhotosWritten`, `UpdatedAtUtc`) VALUES ({year}, 1, 0, {now})",
                    cancellationToken);

                var rows = await context.PhotoOriginalBuckets
                    .FromSqlInterpolated(
                        $"SET STATEMENT innodb_lock_wait_timeout = {PhotoOriginalLimits.BucketLockWaitSeconds} FOR SELECT * FROM `PhotoOriginalBuckets` WHERE `Year` = {year} FOR UPDATE")
                    .AsTracking()
                    .ToListAsync(cancellationToken);
                var row = rows.Single();
                var bucket = Take(row, capacity, now);
                await context.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return bucket;
            });
        }
        catch (Exception ex) when (IsLockWaitTimeout(ex))
        {
            throw new FileStorageUnavailableException("Photo storage is busy. Try again shortly.");
        }
    }

    private static int Take(PhotoOriginalBucket row, int capacity, DateTime now)
    {
        if (row.PhotosWritten >= capacity)
        {
            row.CurrentBucket++;
            row.PhotosWritten = 0;
        }

        row.PhotosWritten++;
        row.UpdatedAtUtc = now;
        return row.CurrentBucket;
    }

    private static bool IsLockWaitTimeout(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is MySqlException { Number: LockWaitTimeout })
            {
                return true;
            }
        }

        return false;
    }
}
