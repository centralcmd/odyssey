using Odyssey.Context;

namespace Odyssey.Core.Finance.Instruments;

/// <summary>
/// The single newest-price rule (issue #446 §8.3), applied in memory to the candidate rows a query returns, so the
/// same code runs on MariaDB and EF InMemory and there is no SQL copy of it. A source-lint fails on a second copy.
/// </summary>
/// <remarks>
/// Within one instrument: the latest <c>TradingDate</c>; on the same date a manual row beats any fetched row (an
/// explicit entry is a correction); among fetched rows the source whose current binding has the higher priority
/// (lower number) wins, and a source with no current binding ranks after every bound one; then the newest
/// <c>UpdatedAtUtc ?? CreatedAtUtc</c>; then the id. This deliberately differs from <c>ExchangeRateOrdering</c>,
/// where source plays no part, because one instrument can carry rows from several sources for a date.
/// </remarks>
public static class InstrumentPriceOrdering
{
    /// <summary>Priority used for a row whose source has no current binding: after every bound source.</summary>
    public const int UnboundPriority = int.MaxValue;

    /// <summary>Orders candidate rows best-first.</summary>
    public static IOrderedEnumerable<InstrumentPrice> BestFirst(
        IEnumerable<InstrumentPrice> rows, Func<Guid, string, int> priorityOf) =>
        rows.OrderByDescending(row => row.TradingDate)
            .ThenBy(row => row.SourceKey is null ? 0 : 1)
            .ThenBy(row => row.SourceKey is null ? 0 : priorityOf(row.InstrumentId, row.SourceKey))
            .ThenByDescending(row => row.UpdatedAtUtc ?? row.CreatedAtUtc)
            .ThenBy(row => row.InstrumentPriceId);

    /// <summary>The winner among rows of one instrument (any dates), or <c>null</c> for none.</summary>
    public static InstrumentPrice? Winner(IEnumerable<InstrumentPrice> rows, Func<Guid, string, int> priorityOf) =>
        BestFirst(rows, priorityOf).FirstOrDefault();

    /// <summary>One winner per <c>(InstrumentId, TradingDate)</c>.</summary>
    public static IReadOnlyList<InstrumentPrice> WinnersPerDate(
        IEnumerable<InstrumentPrice> rows, Func<Guid, string, int> priorityOf) =>
        rows.GroupBy(row => (row.InstrumentId, row.TradingDate))
            .Select(group => Winner(group, priorityOf)!)
            .OrderBy(row => row.InstrumentId)
            .ThenBy(row => row.TradingDate)
            .ToList();

    /// <summary>A priority lookup over a set of bindings.</summary>
    public static Func<Guid, string, int> PriorityLookup(IEnumerable<InstrumentPriceSourceBinding> bindings)
    {
        var map = bindings.ToDictionary(b => (b.InstrumentId, b.SourceKey), b => b.Priority);
        return (instrumentId, sourceKey) => map.TryGetValue((instrumentId, sourceKey), out var priority) ? priority : UnboundPriority;
    }
}
