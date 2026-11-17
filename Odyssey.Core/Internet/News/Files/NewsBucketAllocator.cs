using Microsoft.EntityFrameworkCore;
using MySqlConnector;
using Odyssey.Context;
using Odyssey.Core.FileStorage;
using Odyssey.Dtos;

namespace Odyssey.Core.Internet.News.Files;

/// <summary>
/// Takes an article folder's slot in its feed's current bucket (issue #470 §3.2), in its own short transaction BEFORE
/// the article transaction — the <c>PhotoOriginalBucketAllocator</c> protocol. On MariaDB the feed's
/// <see cref="NewsFeedBucket"/> row is locked <c>FOR UPDATE</c> with a 5 s lock wait, so concurrent ingests of one feed
/// queue on the lock and never overfill a bucket (AC 41); a gap left by an aborted article is harmless. The bucket
/// advances at <see cref="NewsLimits.BucketCapacity"/>, which keeps every folder within the store's 10,000 entries.
/// </summary>
public sealed class NewsBucketAllocator(IDbContextFactory<OdysseyContext> contextFactory, TimeProvider time)
{
    private const int LockWaitTimeout = 1205;

    public const int LockWaitSeconds = 5;

    /// <summary>The capacity; settable only so a test can reach a bucket boundary without 10,000 articles.</summary>
    public int Capacity { get; init; } = NewsLimits.BucketCapacity;

    public async Task<int> AllocateAsync(Guid feedId, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var now = time.GetUtcNow().UtcDateTime;
        var capacity = Math.Max(1, Capacity);

        if (!context.Database.IsRelational())
        {
            // EF InMemory has no locks; the fast tiers run one writer at a time.
            var row = await context.NewsFeedBuckets.FirstOrDefaultAsync(b => b.NewsFeedId == feedId, cancellationToken);
            if (row is null)
            {
                row = new NewsFeedBucket { NewsFeedBucketId = Guid.NewGuid(), NewsFeedId = feedId, CurrentBucket = 1 };
                context.NewsFeedBuckets.Add(row);
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
                context.ChangeTracker.Clear();

                // The first article of a feed inserts the row; IGNORE makes the loser of that race wait for the winner's
                // row lock and then read its row (the unique index is on NewsFeedId).
                var newId = Guid.NewGuid();
                await context.Database.ExecuteSqlInterpolatedAsync(
                    $"SET STATEMENT innodb_lock_wait_timeout = {LockWaitSeconds} FOR INSERT IGNORE INTO `NewsFeedBuckets` (`NewsFeedBucketId`, `NewsFeedId`, `CurrentBucket`, `ArticlesInBucket`, `UpdatedAtUtc`) VALUES ({newId}, {feedId}, 1, 0, {now})",
                    cancellationToken);

                var rows = await context.NewsFeedBuckets
                    .FromSqlInterpolated(
                        $"SET STATEMENT innodb_lock_wait_timeout = {LockWaitSeconds} FOR SELECT * FROM `NewsFeedBuckets` WHERE `NewsFeedId` = {feedId} FOR UPDATE")
                    .AsTracking()
                    .ToListAsync(cancellationToken);
                var bucket = Take(rows.Single(), capacity, now);
                await context.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return bucket;
            });
        }
        catch (Exception ex) when (IsLockWaitTimeout(ex))
        {
            throw new FileStorageUnavailableException("News storage is busy. Try again shortly.");
        }
    }

    private static int Take(NewsFeedBucket row, int capacity, DateTime now)
    {
        if (row.ArticlesInBucket >= capacity)
        {
            row.CurrentBucket++;
            row.ArticlesInBucket = 0;
        }

        row.ArticlesInBucket++;
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
