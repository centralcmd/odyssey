using Odyssey.Core.Journal;
using Xunit;

namespace Odyssey.Core.Tests;

/// <summary>
/// A removing calendar import writes its per-batch audit line in parts (PR #524 architect nit 9): one removed series can
/// carry thousands of future rows, and a single entry holding every id would be unbounded.
/// </summary>
public class CalendarIcsAuditChunksTests
{
    [Fact]
    public void Ids_are_split_into_lines_of_at_most_the_limit_series_first_and_none_lost()
    {
        var series = Enumerable.Range(0, 3).Select(_ => Guid.NewGuid()).ToList();
        var events = Enumerable.Range(0, 1200).Select(_ => Guid.NewGuid()).ToList();

        var lines = CalendarIcsService.AuditChunks(series, events, CalendarIcsService.AuditIdsPerLine);

        Assert.Equal(3, lines.Count);
        Assert.All(lines, l => Assert.InRange(l.SeriesIds.Count + l.EventIds.Count, 1, CalendarIcsService.AuditIdsPerLine));
        Assert.Equal(series, lines[0].SeriesIds);
        Assert.Equal(events, lines.SelectMany(l => l.EventIds));
    }

    [Fact]
    public void Nothing_removed_writes_no_line()
    {
        Assert.Empty(CalendarIcsService.AuditChunks([], [], CalendarIcsService.AuditIdsPerLine));
    }
}
