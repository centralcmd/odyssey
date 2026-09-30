namespace Odyssey.Core.Finance;

/// <summary>
/// The query bounds of a whole-day period (a budget, a tax statement, its settlement window). A
/// period's dates are picked as calendar days and stored at midnight, while transactions carry a time
/// of day, so comparing <c>TimeStamp &lt;= EndDate</c> silently drops everything after 00:00 on the
/// last day (issue #238). The end is therefore EXCLUSIVE at the next midnight.
///
/// <para>
/// Compute both bounds <b>outside</b> the query expression and compare against the resulting locals:
/// <c>t.TimeStamp &gt;= start &amp;&amp; t.TimeStamp &lt; endExclusive</c>. That shape translates on every
/// provider, where a <c>.Date.AddDays(1)</c> inside the lambda would not reliably.
/// </para>
/// </summary>
public static class PeriodBounds
{
    /// <summary>The first instant of the period: midnight of <paramref name="start"/>'s calendar day.</summary>
    public static DateTime InclusiveStart(DateTime start) => start.Date;

    /// <summary>
    /// The first instant AFTER the period: midnight of the day following <paramref name="end"/>'s
    /// calendar day. The last representable day saturates to <see cref="DateTime.MaxValue"/> rather
    /// than throwing.
    /// </summary>
    public static DateTime ExclusiveEnd(DateTime end) =>
        end.Date >= DateTime.MaxValue.Date
            ? DateTime.SpecifyKind(DateTime.MaxValue, end.Kind)
            : end.Date.AddDays(1);

    /// <summary>Both bounds at once, for a <c>&gt;= start &amp;&amp; &lt; endExclusive</c> filter.</summary>
    public static (DateTime Start, DateTime EndExclusive) Of(DateTime start, DateTime end) =>
        (InclusiveStart(start), ExclusiveEnd(end));
}
