using Microsoft.EntityFrameworkCore;
using Odyssey.Context;

namespace Odyssey.Core.Internet.News;

/// <summary>
/// The delegate ran a second time under the execution strategy. The news writes are not safe to replay: their files
/// were written once, before the transaction, and the counters are relative increments. So a retry is turned into a
/// transient skip instead — nothing was committed, and the item is retried on the next fetch.
/// </summary>
public sealed class NewsTransactionRetryException() : Exception("A news transaction was interrupted and is not replayed.");

/// <summary>
/// One explicit transaction for the news writes that span several statements (issue #470 §3.5): inside
/// <c>CreateExecutionStrategy().ExecuteAsync</c> on MariaDB, because <c>EnableRetryOnFailure</c> is configured and a bare
/// <c>BeginTransactionAsync</c> would throw. EF InMemory honours no transaction, so there the work simply runs.
/// </summary>
public static class NewsTransactions
{
    public static async Task<T> RunAsync<T>(OdysseyContext context, Func<Task<T>> work, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(work);
        if (!context.Database.IsRelational())
        {
            return await work();
        }

        var attempts = 0;
        var strategy = context.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            if (++attempts > 1)
            {
                throw new NewsTransactionRetryException();
            }

            await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
            var result = await work();
            await transaction.CommitAsync(cancellationToken);
            return result;
        });
    }

    public static Task RunAsync(OdysseyContext context, Func<Task> work, CancellationToken cancellationToken) =>
        RunAsync(context, async () =>
        {
            await work();
            return true;
        }, cancellationToken);
}
