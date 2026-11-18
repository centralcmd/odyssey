using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MySqlConnector;
using Odyssey.Context;

namespace Odyssey.Core.Journal;

/// <summary>
/// The series lock (issue #519 §3 component 3, v4 §2, v5 §2, implementation notes 2–4): every write that touches a
/// series' rows runs in one transaction inside an execution strategy, and takes <c>SELECT … FOR UPDATE</c> on the
/// series row before it reads anything else. On the EF InMemory provider the work simply runs and the lock is a no-op;
/// <c>Odyssey.IntegrationTests</c> owns the race coverage.
/// </summary>
/// <remarks>
/// <para>
/// <b>Locks are taken in ascending id order</b> (the <c>char(36)</c> column's ordinal order), so two writers locking the
/// same set cannot deadlock on the order alone — the import's batch locks follow the same rule.
/// </para>
/// <para>
/// <b>The lock wait is bounded per statement</b>: the locking <c>SELECT</c> is issued as
/// <c>SET STATEMENT innodb_lock_wait_timeout = 5 FOR …</c> through a raw command, never composed with <c>FromSql</c>.
/// The UPDATE and DELETE statements EF itself generates inside the same delegate cannot carry that prefix, so the
/// session value is set to the same 5 s for the transaction and restored to the server default on the same connection
/// before the commit or rollback (MySqlConnector also resets session state when a pooled connection is reused).
/// </para>
/// <para>
/// <b>Every attempt starts clean and rolls back fully</b>: the change tracker is cleared, and a failed attempt rolls the
/// whole transaction back before the strategy retries — a <c>1205</c> otherwise rolls back only its own statement. When
/// the strategy gives up on a <c>1205</c>/<c>1213</c>, or a key-index <c>1062</c> surfaces, the caller receives
/// <c>409 calendar-series-busy</c> rather than a <c>500</c>.
/// </para>
/// </remarks>
internal static class CalendarSeriesTransactions
{
    /// <summary>The per-statement lock wait, in seconds.</summary>
    public const int LockWaitSeconds = 5;

    /// <summary>The unique indexes a race on keys can hit; a <c>1062</c> on either is a busy series.</summary>
    public static readonly string[] KeyIndexes =
    [
        "IX_CalendarEvents_RecurrencePatternId_OccurrenceKey",
        "IX_RecurrenceExclusions_RecurrencePatternId_OccurrenceKey",
    ];

    /// <summary>Runs <paramref name="work"/> as one interactive series transaction.</summary>
    /// <param name="busy">The refusal once the strategy gives up; <c>409 calendar-series-busy</c> unless a link write on
    /// a standalone event names <c>409 calendar-links-busy</c> (issue #521 addendum).</param>
    public static async Task<T> ExecuteAsync<T>(
        OdysseyContext context, Func<Task<T>> work, CancellationToken cancellationToken, ILogger? logger = null,
        Func<DomainException>? busy = null)
    {
        if (!context.Database.IsRelational() || context.Database.CurrentTransaction is not null)
        {
            return await work();
        }

        var strategy = OdysseyDatabaseRetry.InteractiveStrategy(context);
        var started = DateTime.UtcNow;
        try
        {
            return await strategy.ExecuteAsync(() => RunAttemptAsync(context, work, cancellationToken));
        }
        catch (Exception ex) when (IsBusy(ex))
        {
            logger?.LogWarning(
                "A calendar series write gave up after {Seconds:F1} s waiting for another writer.",
                (DateTime.UtcNow - started).TotalSeconds);
            throw busy?.Invoke() ?? CalendarSeriesConflictException.SeriesBusy();
        }
    }

    /// <summary>One attempt: a clean tracker, a transaction with the bounded lock wait, and a full rollback on failure.</summary>
    public static async Task<T> RunAttemptAsync<T>(OdysseyContext context, Func<Task<T>> work, CancellationToken cancellationToken)
    {
        context.ChangeTracker.Clear();
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            await context.Database.ExecuteSqlRawAsync(
                $"SET SESSION innodb_lock_wait_timeout = {LockWaitSeconds}", cancellationToken);
            var result = await work();
            // Restored before the commit, on the transaction's own connection (PR #528 review): after it EF may release
            // the connection, and a restore would then run on whichever pooled connection came next.
            await context.Database.ExecuteSqlRawAsync("SET SESSION innodb_lock_wait_timeout = DEFAULT", cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return result;
        }
        catch
        {
            await RestoreLockWaitAsync(context);
            await transaction.RollbackAsync(CancellationToken.None);
            context.ChangeTracker.Clear();
            throw;
        }
    }

    /// <summary>
    /// Takes the series locks, first statement of the transaction, in ascending id order. A no-op off a relational
    /// provider.
    /// </summary>
    public static async Task LockSeriesAsync(
        OdysseyContext context, IEnumerable<Guid> patternIds, CancellationToken cancellationToken)
    {
        if (!context.Database.IsRelational())
        {
            return;
        }

        foreach (var id in Ascending(patternIds))
        {
            await context.Database.ExecuteSqlInterpolatedAsync(
                $"SET STATEMENT innodb_lock_wait_timeout = 5 FOR SELECT `RecurrencePatternId` FROM `RecurrencePatterns` WHERE `RecurrencePatternId` = {id} FOR UPDATE",
                cancellationToken);
        }
    }

    /// <summary>Locks standalone event rows (the import's second lock class, after the series), ascending.</summary>
    public static async Task LockEventsAsync(
        OdysseyContext context, IEnumerable<Guid> eventIds, CancellationToken cancellationToken)
    {
        if (!context.Database.IsRelational())
        {
            return;
        }

        foreach (var id in Ascending(eventIds))
        {
            await context.Database.ExecuteSqlInterpolatedAsync(
                $"SET STATEMENT innodb_lock_wait_timeout = 5 FOR SELECT `CalendarEventId` FROM `CalendarEvents` WHERE `CalendarEventId` = {id} FOR UPDATE",
                cancellationToken);
        }
    }

    /// <summary>The ids in the order the locks are taken: the <c>char(36)</c> column's ordinal order.</summary>
    public static IEnumerable<Guid> Ascending(IEnumerable<Guid> ids) =>
        ids.Distinct().OrderBy(id => id.ToString("D"), StringComparer.Ordinal);

    /// <summary>A lock wait, a deadlock or a key-index race, anywhere in the exception chain.</summary>
    public static bool IsBusy(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is MySqlException mysql)
            {
                if (mysql.Number is 1205 or 1213)
                {
                    return true;
                }

                if (mysql.Number == 1062 && KeyIndexes.Any(index => mysql.Message.Contains($"{index}'", StringComparison.Ordinal)))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static async Task RestoreLockWaitAsync(OdysseyContext context)
    {
        try
        {
            await context.Database.ExecuteSqlRawAsync("SET SESSION innodb_lock_wait_timeout = DEFAULT", CancellationToken.None);
        }
        catch (Exception ex) when (ex is MySqlException or InvalidOperationException)
        {
            // A broken connection is discarded by the pool, and a pooled one is reset on reuse anyway.
        }
    }
}
