namespace Odyssey.Dtos.Finance;

// CS8524 is the "someone cast an out-of-range integer to the enum" case, which is what a discard arm
// would swallow. It is disabled here rather than answered with a `_ =>` arm, because the arm would
// also silence CS8509 — the check that actually matters: the build runs at zero warnings, so a
// NetWorthInterval member added without a case here has to fail the build. An out-of-range cast is
// kept out by [EnumDataType] at the HTTP boundary, and a direct caller passing one gets a
// SwitchExpressionException, which fails closed.
#pragma warning disable CS8524


/// <summary>
/// Calendar arithmetic for a <see cref="NetWorthInterval"/> grid (issue #90 §5.1): where a period
/// starts, where the next one starts, and how many periods a window covers.
/// </summary>
/// <remarks>
/// <para>
/// Everything here counts <b>calendar periods</b>, never days divided by a length. A month is not 30
/// days, a year is not 365, and a leap day must not shift a boundary — the day-arithmetic version of
/// "how many months between these dates" is wrong in exactly the cases a financial series is read at.
/// </para>
/// <para>
/// It lives in <c>Odyssey.Dtos</c> rather than in the service because
/// <see cref="NetWorthHistoryQuery"/> needs it to resolve the default window and count the points it
/// caps, and that project is the one both halves of the stack can reference.
/// </para>
/// </remarks>
public static class NetWorthPeriods
{
    /// <summary>The first day of the calendar period containing <paramref name="date"/>.</summary>
    public static DateOnly StartOfPeriod(DateOnly date, NetWorthInterval interval) => interval switch
    {
        NetWorthInterval.Daily => date,
        // ISO-8601: the week starts on Monday. DayOfWeek numbers Sunday as 0, so Sunday is 6 days in.
        NetWorthInterval.Weekly => date.AddDays(-(((int)date.DayOfWeek + 6) % 7)),
        NetWorthInterval.Monthly => new DateOnly(date.Year, date.Month, 1),
        NetWorthInterval.Quarterly => new DateOnly(date.Year, ((date.Month - 1) / 3 * 3) + 1, 1),
        NetWorthInterval.Yearly => new DateOnly(date.Year, 1, 1),
    };

    /// <summary>
    /// The first day of the period <paramref name="count"/> periods after the one containing
    /// <paramref name="date"/>. Negative counts step backwards.
    /// </summary>
    public static DateOnly AddPeriods(DateOnly date, NetWorthInterval interval, int count)
    {
        var start = StartOfPeriod(date, interval);
        return interval switch
        {
            NetWorthInterval.Daily => start.AddDays(count),
            NetWorthInterval.Weekly => start.AddDays(7 * count),
            NetWorthInterval.Monthly => start.AddMonths(count),
            NetWorthInterval.Quarterly => start.AddMonths(3 * count),
            NetWorthInterval.Yearly => start.AddYears(count),
        };
    }

    /// <summary>
    /// <see cref="AddPeriods"/>, clamped to <see cref="DateOnly.MinValue"/>/<see cref="DateOnly.MaxValue"/>
    /// instead of throwing.
    /// </summary>
    /// <remarks>
    /// Issue #90 V3a. <c>?to=0001-01-01</c> is a well-formed request and the default window steps 23
    /// periods back from it; an <see cref="ArgumentOutOfRangeException"/> there would be a <c>500</c>
    /// on valid input, and an implementer reading that symptom would be pushed towards adding a
    /// rejection the rules do not call for.
    /// </remarks>
    public static DateOnly AddSaturating(DateOnly date, NetWorthInterval interval, int count)
    {
        try
        {
            return AddPeriods(date, interval, count);
        }
        catch (ArgumentOutOfRangeException)
        {
            return count < 0 ? DateOnly.MinValue : DateOnly.MaxValue;
        }
    }

    /// <summary>
    /// The number of periods covered by <c>[from, to]</c> inclusive of both endpoints' periods, or 0
    /// when the window is inverted.
    /// </summary>
    public static int CountPeriods(DateOnly from, DateOnly to, NetWorthInterval interval)
    {
        if (from > to) return 0;

        var start = StartOfPeriod(from, interval);
        var last = StartOfPeriod(to, interval);

        return interval switch
        {
            NetWorthInterval.Daily => last.DayNumber - start.DayNumber + 1,
            NetWorthInterval.Weekly => ((last.DayNumber - start.DayNumber) / 7) + 1,
            NetWorthInterval.Monthly => MonthsBetween(start, last) + 1,
            NetWorthInterval.Quarterly => (MonthsBetween(start, last) / 3) + 1,
            NetWorthInterval.Yearly => last.Year - start.Year + 1,
        };
    }

    private static int MonthsBetween(DateOnly start, DateOnly last) =>
        ((last.Year - start.Year) * 12) + (last.Month - start.Month);
}

#pragma warning restore CS8524
