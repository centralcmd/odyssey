using System.Data;
using Microsoft.EntityFrameworkCore;
using Odyssey.Context;

namespace Odyssey.Core.Locations;

/// <summary>
/// The one way the location rules run a unit of work atomically (issue #380 §5.3, §5.6, §5.7): inside the
/// execution strategy, because <c>EnableRetryOnFailure</c> makes a bare <c>BeginTransactionAsync</c> throw,
/// and joining an ambient transaction rather than nesting a second <c>BEGIN</c>, which MariaDB does not
/// support. Every attempt starts from a clean change tracker, so a retry re-reads rows instead of replaying
/// against entities a failed attempt mutated. On the EF InMemory provider the work simply runs.
/// </summary>
internal static class LocationTransactions
{
    public static async Task ExecuteAsync(
        OdysseyContext context, Func<Task> work, IsolationLevel isolation, CancellationToken cancellationToken)
    {
        if (!context.Database.IsRelational() || context.Database.CurrentTransaction is not null)
        {
            await work();
            return;
        }

        var strategy = context.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            context.ChangeTracker.Clear();
            await using var transaction = await context.Database.BeginTransactionAsync(isolation, cancellationToken);
            await work();
            await transaction.CommitAsync(cancellationToken);
        });
    }

    /// <summary>
    /// Takes <c>SELECT … FOR UPDATE</c> on the given locations, in ascending id order so two transactions
    /// locking the same pair cannot deadlock (S-R5, A-R11). A no-op off a relational provider.
    /// </summary>
    public static async Task LockAsync(OdysseyContext context, IReadOnlyCollection<Guid> locationIds, CancellationToken cancellationToken)
    {
        if (!context.Database.IsRelational() || locationIds.Count == 0)
        {
            return;
        }

        foreach (var id in locationIds.Distinct().OrderBy(id => id.ToString("D"), StringComparer.Ordinal))
        {
            await context.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT `LocationId` FROM `Locations` WHERE `LocationId` = {id} FOR UPDATE", cancellationToken);
        }
    }
}
