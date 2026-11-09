using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MySqlConnector;
using Odyssey.Context;

namespace Odyssey.Core.FileStorage.Files;

/// <summary>
/// Takes a slot in a feature's and year's current bucket (issue #448 §3.6), in its own short transaction before the
/// file is written, so a crash can only under-fill a bucket. On MariaDB the row is locked with <c>SELECT … FOR UPDATE</c>,
/// bounded per statement to 5 s: concurrent uploads queue on the lock instead of failing optimistic retries. A wait
/// beyond the bound is a <c>503</c>. The protocol of <c>PhotoOriginalBucketAllocator</c>, keyed by feature as well.
/// </summary>
public sealed class StoredFileBucketAllocator(
    IDbContextFactory<OdysseyContext> contextFactory,
    IOptions<StoredFileOptions> options,
    TimeProvider time)
{
    private const int LockWaitTimeout = 1205;

    /// <summary>The bucket row's lock wait, bounded per statement.</summary>
    public const int LockWaitSeconds = 5;

    public async Task<int> AllocateAsync(string featureKey, int year, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var capacity = Math.Max(1, options.Value.BucketCapacity);
        var now = time.GetUtcNow().UtcDateTime;

        if (!context.Database.IsRelational())
        {
            // The EF InMemory provider (tests) has no locks; the fast tiers run one writer at a time.
            var row = await context.StoredFileBuckets.FirstOrDefaultAsync(b => b.FeatureKey == featureKey && b.Year == year, cancellationToken);
            if (row is null)
            {
                row = new StoredFileBucket { FeatureKey = featureKey, Year = year, CurrentBucket = 1, ObjectsWritten = 0 };
                context.StoredFileBuckets.Add(row);
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

                // Inserting the first row takes the same row lock a later SELECT would; IGNORE makes the loser of a
                // first-use race wait for the winner and then read its row.
                await context.Database.ExecuteSqlInterpolatedAsync(
                    $"SET STATEMENT innodb_lock_wait_timeout = {LockWaitSeconds} FOR INSERT IGNORE INTO `StoredFileBuckets` (`FeatureKey`, `Year`, `CurrentBucket`, `ObjectsWritten`, `UpdatedAtUtc`) VALUES ({featureKey}, {year}, 1, 0, {now})",
                    cancellationToken);

                var rows = await context.StoredFileBuckets
                    .FromSqlInterpolated(
                        $"SET STATEMENT innodb_lock_wait_timeout = {LockWaitSeconds} FOR SELECT * FROM `StoredFileBuckets` WHERE `FeatureKey` = {featureKey} AND `Year` = {year} FOR UPDATE")
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
            throw new FileStorageUnavailableException("File storage is busy. Try again shortly.");
        }
    }

    private static int Take(StoredFileBucket row, int capacity, DateTime now)
    {
        if (row.ObjectsWritten >= capacity)
        {
            row.CurrentBucket++;
            row.ObjectsWritten = 0;
        }

        row.ObjectsWritten++;
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
