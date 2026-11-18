using System.Linq.Expressions;
using Odyssey.Core.Finance;
using ContextCalendarEvent = Odyssey.Context.CalendarEvent;

namespace Odyssey.Core.Journal;

/// <summary>
/// The windowed-read contract (issue #519 v3 §A, v4 §1): <c>From</c>/<c>To</c> are instants, a zoned row is matched by
/// its instant and a floating row by its wall-clock against the window translated into the viewer's zone. The filter
/// and the order are both SQL expressions, so paging, <c>TotalCount</c> and the limit clamp stay correct on a mixed set.
/// </summary>
/// <remarks>
/// <para>
/// <b>The filter</b> keeps each branch of the <c>OR</c> sargable on <c>(CalendarId, StartDateTime)</c>:
/// <c>(TimeZoneId IS NOT NULL AND Start &lt; @To AND End &gt; @From) OR (TimeZoneId IS NULL AND Start &lt; @ToWall AND
/// End &gt; @FromWall)</c>. With the default view zone, <c>UTC</c>, the two branches are one, which is exactly the
/// behaviour before #519.
/// </para>
/// <para>
/// <b>The order</b> puts both classes on the instant line: a zoned row sorts by its stored instant, a floating row by
/// its wall-clock minus the view zone's offset in force at that wall-clock. The offsets come from the window's
/// <i>offset segments</i> (at most <see cref="MaxSegments"/>), computed here, so the expression is a <c>CASE</c> over
/// constant bounds with <c>DATE_ADD</c> — no <c>CONVERT_TZ</c> and no tz tables — and runs unchanged on EF InMemory. A
/// wall-clock in an overlap takes the earlier offset and one in a gap the pre-transition offset. A window with more
/// transitions, or no window, orders floating rows as if their wall-clock were UTC, deterministically.
/// </para>
/// </remarks>
internal static class CalendarWindow
{
    public const int MaxSegments = 32;

    /// <summary>The view zone of a request: the validated id, else UTC.</summary>
    public static TimeZoneInfo ViewZone(string? viewTimeZoneId) =>
        CalendarTimeZones.ResolveRequest(viewTimeZoneId, "viewTimeZoneId") ?? TimeZoneInfo.Utc;

    /// <summary>Rows overlapping the window, by the two-branch predicate above.</summary>
    public static IQueryable<ContextCalendarEvent> Overlapping(
        IQueryable<ContextCalendarEvent> rows, DateTime from, DateTime to, TimeZoneInfo view)
    {
        var fromUtc = DateTimeNormalization.NormalizeToUtc(from);
        var toUtc = DateTimeNormalization.NormalizeToUtc(to);
        var fromWall = CalendarTimeZones.ToWall(fromUtc, view);
        var toWall = CalendarTimeZones.ToWall(toUtc, view);
        return rows.Where(e =>
            (e.TimeZoneId != null && e.StartDateTime < toUtc && e.EndDateTime > fromUtc)
            || (e.TimeZoneId == null && e.StartDateTime < toWall && e.EndDateTime > fromWall));
    }

    /// <summary>The instant-line sort key for the window, as an EF-translatable expression.</summary>
    public static Expression<Func<ContextCalendarEvent, DateTime>> SortKey(DateTime? from, DateTime? to, TimeZoneInfo view)
    {
        var segments = from is { } f && to is { } t ? Segments(f, t, view) : null;
        if (segments is null || (segments.Count == 1 && segments[0].OffsetMinutes == 0))
        {
            return e => e.StartDateTime;
        }

        var row = Expression.Parameter(typeof(ContextCalendarEvent), "e");
        var start = Expression.Property(row, nameof(ContextCalendarEvent.StartDateTime));
        var addMinutes = typeof(DateTime).GetMethod(nameof(DateTime.AddMinutes), [typeof(double)])!;

        Expression Shift(int offsetMinutes) =>
            offsetMinutes == 0 ? start : Expression.Call(start, addMinutes, Expression.Constant((double)-offsetMinutes));

        // Built from the last segment backwards: the last one is upper-unbounded, the first lower-unbounded.
        Expression floating = Shift(segments[^1].OffsetMinutes);
        for (var i = segments.Count - 2; i >= 0; i--)
        {
            floating = Expression.Condition(
                Expression.LessThan(start, Expression.Constant(segments[i].WallUpper)),
                Shift(segments[i].OffsetMinutes),
                floating);
        }

        var zoned = Expression.NotEqual(
            Expression.Property(row, nameof(ContextCalendarEvent.TimeZoneId)), Expression.Constant(null, typeof(string)));
        return Expression.Lambda<Func<ContextCalendarEvent, DateTime>>(Expression.Condition(zoned, start, floating), row);
    }

    /// <summary>
    /// The view zone's offset segments over <c>[from, to]</c>: each segment's offset, and the wall-clock below which a
    /// floating row belongs to it — the next transition plus the larger of the two offsets, so an overlap takes the
    /// earlier offset and a gap the pre-transition one. Null past <see cref="MaxSegments"/>.
    /// </summary>
    internal static List<(int OffsetMinutes, DateTime WallUpper)>? Segments(DateTime from, DateTime to, TimeZoneInfo view)
    {
        var fromUtc = DateTimeNormalization.NormalizeToUtc(from);
        var toUtc = DateTimeNormalization.NormalizeToUtc(to);
        var transitions = new List<DateTime>();
        var offset = view.GetUtcOffset(fromUtc);
        for (var probe = fromUtc; probe < toUtc;)
        {
            var next = probe.AddHours(1) < toUtc ? probe.AddHours(1) : toUtc;
            var nextOffset = view.GetUtcOffset(next);
            if (nextOffset != offset)
            {
                // The transition instant, to the minute, between probe and next.
                var low = probe;
                var high = next;
                while ((high - low).TotalMinutes > 1)
                {
                    var mid = low.AddMinutes(Math.Floor((high - low).TotalMinutes / 2));
                    if (view.GetUtcOffset(mid) == offset)
                    {
                        low = mid;
                    }
                    else
                    {
                        high = mid;
                    }
                }

                transitions.Add(high);
                if (transitions.Count >= MaxSegments)
                {
                    return null;
                }

                offset = nextOffset;
            }

            probe = next;
        }

        var segments = new List<(int, DateTime)>();
        var current = view.GetUtcOffset(fromUtc);
        foreach (var transition in transitions)
        {
            var after = view.GetUtcOffset(transition);
            var upper = DateTime.SpecifyKind(transition + (after > current ? after : current), DateTimeKind.Unspecified);
            segments.Add(((int)current.TotalMinutes, upper));
            current = after;
        }

        segments.Add(((int)current.TotalMinutes, DateTime.MaxValue));
        return segments;
    }
}
