namespace Odyssey.Dtos.Finance;

/// <summary>
/// The rules of a whole-day period (a budget, a tax statement, its settlement window), declared once
/// here so the services that query and validate a period and the client that lists its transactions
/// cannot drift (the <see cref="TaxSettlementRange"/> precedent). A period's dates are picked as
/// calendar days and stored at midnight, while transactions carry a time of day, so comparing
/// <c>TimeStamp &lt;= EndDate</c> silently drops everything after 00:00 on the last day (issue #238).
/// The end is therefore EXCLUSIVE at the next midnight.
///
/// <para>
/// Compute both bounds <b>outside</b> a query expression and compare against the resulting locals:
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
    /// than throwing. The <see cref="DateTime.Kind"/> is preserved.
    /// </summary>
    public static DateTime ExclusiveEnd(DateTime end) =>
        end.Date >= DateTime.MaxValue.Date
            ? DateTime.SpecifyKind(DateTime.MaxValue, end.Kind)
            : end.Date.AddDays(1);

    /// <summary>
    /// The last instant IN the period, for a filter that can only express an inclusive upper bound —
    /// <c>GET /api/transactions?to=</c> compares <c>TimeStamp &lt;= to</c>. It is one microsecond before
    /// <see cref="ExclusiveEnd"/>, not one tick: the store's <c>datetime(6)</c> keeps microseconds, so a
    /// sub-microsecond value could round up onto the next midnight and let that instant back in.
    /// </summary>
    public static DateTime InclusiveEndInstant(DateTime end) =>
        ExclusiveEnd(end).AddTicks(-TimeSpan.TicksPerMicrosecond);

    /// <summary>Both query bounds at once, for a <c>&gt;= start &amp;&amp; &lt; endExclusive</c> filter.</summary>
    public static (DateTime Start, DateTime EndExclusive) Of(DateTime start, DateTime end) =>
        (InclusiveStart(start), ExclusiveEnd(end));

    /// <summary>
    /// True when the period ends on a calendar day before the one it starts on. Compared by day, the
    /// unit a period is read in: a one-day period whose start time follows its end time still covers
    /// that whole day, so it is not inverted.
    /// </summary>
    public static bool IsInverted(DateTime start, DateTime end) => end.Date < start.Date;
}
