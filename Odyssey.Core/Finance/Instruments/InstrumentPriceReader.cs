using Microsoft.EntityFrameworkCore;
using Odyssey.Context;

namespace Odyssey.Core.Finance.Instruments;

/// <summary>
/// The set-based reads behind every "latest close" and "previous close" (issue #446 §8.3): a grouped
/// <c>MAX(TradingDate)</c> per instrument joined back to the candidate rows of that date (the
/// <c>CurrencyConversionService</c> shape), the winner then chosen in memory by <see cref="InstrumentPriceOrdering"/>.
/// No window function, so the same code runs on MariaDB and EF InMemory with a fixed number of queries.
/// </summary>
internal static class InstrumentPriceReader
{
    public static async Task<Func<Guid, string, int>> PrioritiesAsync(
        OdysseyContext context, IReadOnlyCollection<Guid> instrumentIds, CancellationToken cancellationToken)
    {
        var bindings = await context.InstrumentPriceSourceBindings.AsNoTracking()
            .Where(binding => instrumentIds.Contains(binding.InstrumentId))
            .ToListAsync(cancellationToken);
        return InstrumentPriceOrdering.PriorityLookup(bindings);
    }

    /// <summary>The newest winner per instrument, among <paramref name="instrumentIds"/>.</summary>
    public static async Task<Dictionary<Guid, InstrumentPrice>> LatestAsync(
        OdysseyContext context,
        IReadOnlyCollection<Guid> instrumentIds,
        Func<Guid, string, int> priorities,
        CancellationToken cancellationToken)
    {
        if (instrumentIds.Count == 0)
        {
            return [];
        }

        var prices = context.InstrumentPrices.AsNoTracking().Where(price => instrumentIds.Contains(price.InstrumentId));
        var latest = prices
            .GroupBy(price => price.InstrumentId)
            .Select(group => new { InstrumentId = group.Key, TradingDate = group.Max(price => price.TradingDate) });

        var rows = await (
                from price in prices
                join date in latest
                    on new { price.InstrumentId, price.TradingDate } equals new { date.InstrumentId, date.TradingDate }
                select price)
            .ToListAsync(cancellationToken);

        return rows.GroupBy(row => row.InstrumentId)
            .ToDictionary(group => group.Key, group => InstrumentPriceOrdering.Winner(group, priorities)!);
    }

    /// <summary>The winner of the newest date strictly before each instrument's latest date.</summary>
    public static async Task<Dictionary<Guid, InstrumentPrice>> PreviousAsync(
        OdysseyContext context,
        IReadOnlyDictionary<Guid, InstrumentPrice> latest,
        Func<Guid, string, int> priorities,
        CancellationToken cancellationToken)
    {
        if (latest.Count == 0)
        {
            return [];
        }

        var ids = latest.Keys.ToList();
        var prices = context.InstrumentPrices.AsNoTracking().Where(price => ids.Contains(price.InstrumentId));
        var newest = prices
            .GroupBy(price => price.InstrumentId)
            .Select(group => new { InstrumentId = group.Key, TradingDate = group.Max(price => price.TradingDate) });
        var previous =
            from price in prices
            join date in newest on price.InstrumentId equals date.InstrumentId
            where price.TradingDate < date.TradingDate
            group price by price.InstrumentId into grouped
            select new { InstrumentId = grouped.Key, TradingDate = grouped.Max(price => price.TradingDate) };

        var rows = await (
                from price in prices
                join date in previous
                    on new { price.InstrumentId, price.TradingDate } equals new { date.InstrumentId, date.TradingDate }
                select price)
            .ToListAsync(cancellationToken);

        return rows.GroupBy(row => row.InstrumentId)
            .ToDictionary(group => group.Key, group => InstrumentPriceOrdering.Winner(group, priorities)!);
    }
}

/// <summary>Row locks for the instrument identity rules (issue #446 §8.6).</summary>
internal static class InstrumentLocks
{
    /// <summary>
    /// <c>SELECT … FOR UPDATE</c> on the instrument row inside the caller's open transaction, so an identity change
    /// and a concurrent price or binding insert serialise. A no-op on EF InMemory, which has no locks.
    /// </summary>
    public static async Task LockAsync(OdysseyContext context, Guid instrumentId, CancellationToken cancellationToken)
    {
        if (!context.Database.IsRelational())
        {
            return;
        }

        await context.Database
            .SqlQuery<Guid>($"SELECT `InstrumentId` AS `Value` FROM `Instruments` WHERE `InstrumentId` = {instrumentId} FOR UPDATE")
            .ToListAsync(cancellationToken);
    }

    /// <summary>Runs <paramref name="work"/> in one transaction under the execution strategy (InMemory: directly).</summary>
    public static async Task<T> InTransactionAsync<T>(
        OdysseyContext context, Func<Task<T>> work, CancellationToken cancellationToken)
    {
        if (!context.Database.IsRelational())
        {
            return await work();
        }

        var strategy = context.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
            var result = await work();
            await transaction.CommitAsync(cancellationToken);
            return result;
        });
    }
}
