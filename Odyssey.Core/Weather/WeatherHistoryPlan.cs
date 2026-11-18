using Odyssey.Dtos.Journal;

namespace Odyssey.Core.Weather;

/// <summary>An inclusive range of point-local dates.</summary>
public readonly record struct WeatherDateRange(DateOnly From, DateOnly To)
{
    public int Days => To.DayNumber - From.DayNumber + 1;

    public bool Contains(DateOnly date) => date >= From && date <= To;
}

/// <summary>A stored window as the planner sees it: point-local dates, <see cref="To"/> null for ongoing.</summary>
public readonly record struct WeatherWindow(DateOnly From, DateOnly? To);

/// <summary>
/// The history-window planner (issue #436 §3.1 item 4) — pure, so the fetcher and the read path answer from one
/// definition and a window's status can never disagree with what the fetcher will do.
/// <para>
/// The <b>effective range</b> is the union of the windows intersected with <c>[firstOwned, lastComplete]</c>. A
/// <b>gap</b> is an effective day not yet covered, with no trailing exclusion: a recent day nothing has checked is an
/// ordinary gap (architect N1).
/// </para>
/// </summary>
public static class WeatherHistoryPlan
{
    /// <summary>Sorts and merges overlapping or adjacent ranges.</summary>
    public static List<WeatherDateRange> Merge(IEnumerable<WeatherDateRange> ranges)
    {
        var sorted = ranges.OrderBy(r => r.From).ToList();
        var merged = new List<WeatherDateRange>(sorted.Count);
        foreach (var range in sorted)
        {
            if (merged.Count > 0 && range.From.DayNumber <= merged[^1].To.DayNumber + 1)
            {
                var last = merged[^1];
                merged[^1] = new WeatherDateRange(last.From, range.To > last.To ? range.To : last.To);
            }
            else
            {
                merged.Add(range);
            }
        }

        return merged;
    }

    /// <summary>Each window clipped to <c>[firstOwned, lastComplete]</c>, oldest first; empty clips dropped.</summary>
    public static List<WeatherDateRange> Effective(IEnumerable<WeatherWindow> windows, DateOnly firstOwned, DateOnly lastComplete) =>
        Merge(windows.Select(w => Clip(w, firstOwned, lastComplete)).OfType<WeatherDateRange>());

    /// <summary>The parts of <paramref name="range"/> inside any window, oldest first.</summary>
    public static List<WeatherDateRange> ClipToWindows(WeatherDateRange range, IEnumerable<WeatherWindow> windows) =>
        Merge(windows.Select(w => Clip(w, range.From, range.To)).OfType<WeatherDateRange>());

    /// <summary>Effective days not yet covered, <b>newest first</b> (the fetcher's order).</summary>
    public static List<WeatherDateRange> Gaps(
        IEnumerable<WeatherWindow> windows, IEnumerable<WeatherDateRange> coverage, DateOnly firstOwned, DateOnly lastComplete)
    {
        var gaps = Subtract(Effective(windows, firstOwned, lastComplete), Merge(coverage));
        gaps.Reverse();
        return gaps;
    }

    /// <summary>
    /// <paramref name="ranges"/> minus <paramref name="covered"/>; both sorted and merged. A two-pointer sweep: linear
    /// in the inputs.
    /// </summary>
    public static List<WeatherDateRange> Subtract(IReadOnlyList<WeatherDateRange> ranges, IReadOnlyList<WeatherDateRange> covered)
    {
        var result = new List<WeatherDateRange>();
        var c = 0;
        foreach (var range in ranges)
        {
            var cursor = range.From;
            while (c < covered.Count && covered[c].To < range.From)
            {
                c++;
            }

            var k = c;
            while (k < covered.Count && covered[k].From <= range.To)
            {
                if (covered[k].From > cursor)
                {
                    result.Add(new WeatherDateRange(cursor, covered[k].From.AddDays(-1)));
                }

                if (covered[k].To >= cursor)
                {
                    cursor = covered[k].To.AddDays(1);
                }

                if (cursor > range.To)
                {
                    break;
                }

                k++;
            }

            if (cursor <= range.To)
            {
                result.Add(new WeatherDateRange(cursor, range.To));
            }
        }

        return result;
    }

    /// <summary>Days of the sorted, merged <paramref name="covered"/> inside <paramref name="range"/>.</summary>
    public static int CoveredDays(WeatherDateRange range, IReadOnlyList<WeatherDateRange> covered)
    {
        var start = 0;
        return CoveredDays(range, covered, ref start);
    }

    /// <summary>
    /// <see cref="CoveredDays(WeatherDateRange, IReadOnlyList{WeatherDateRange})"/> from a cursor that only moves
    /// forward: rows ending before <paramref name="range"/> are skipped for good, so a caller asking for ranges in
    /// ascending start order sweeps <paramref name="covered"/> once rather than once per range (issue #436 AC 23).
    /// </summary>
    private static int CoveredDays(WeatherDateRange range, IReadOnlyList<WeatherDateRange> covered, ref int start)
    {
        while (start < covered.Count && covered[start].To < range.From)
        {
            start++;
        }

        var days = 0;
        for (var i = start; i < covered.Count; i++)
        {
            var c = covered[i];
            if (c.From > range.To)
            {
                break;
            }

            var from = c.From > range.From ? c.From : range.From;
            var to = c.To < range.To ? c.To : range.To;
            days += to.DayNumber - from.DayNumber + 1;
        }

        return days;
    }

    /// <summary>
    /// Each window's status and progress (issue #436 §5.1), oldest first. First match wins: Upcoming · Unavailable ·
    /// Backfilling · Collecting · Complete.
    /// </summary>
    public static List<ExistingWeatherHistoryWindow> Describe(
        IEnumerable<WeatherWindow> windows, IEnumerable<WeatherDateRange> coverage, DateOnly firstOwned, DateOnly lastComplete)
    {
        var covered = Merge(coverage);
        var result = new List<ExistingWeatherHistoryWindow>();
        // Windows go in start order, so their clips do too and one forward cursor serves them all.
        var cursor = 0;
        foreach (var window in windows.OrderBy(w => w.From))
        {
            var clip = Clip(window, firstOwned, lastComplete);
            var fetchable = clip?.Days ?? 0;
            var done = clip is { } c ? CoveredDays(c, covered, ref cursor) : 0;
            var status = window.From > lastComplete ? WeatherHistoryWindowStatus.Upcoming
                : fetchable == 0 ? WeatherHistoryWindowStatus.Unavailable
                : done < fetchable ? WeatherHistoryWindowStatus.Backfilling
                : window.To is null || window.To >= lastComplete ? WeatherHistoryWindowStatus.Collecting
                : WeatherHistoryWindowStatus.Complete;
            result.Add(new ExistingWeatherHistoryWindow
            {
                From = window.From,
                To = window.To,
                Status = status,
                CoveredDays = done,
                FetchableDays = window.To is null ? null : fetchable,
            });
        }

        return result;
    }

    private static WeatherDateRange? Clip(WeatherWindow window, DateOnly from, DateOnly to)
    {
        var start = window.From > from ? window.From : from;
        var end = window.To is { } windowTo && windowTo < to ? windowTo : to;
        return start <= end ? new WeatherDateRange(start, end) : null;
    }
}
