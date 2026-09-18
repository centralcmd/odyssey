namespace Odyssey.Context;

/// <summary>
/// The cadence UNIT of a term — paired with an <c>IntervalCount</c> multiplier, so "every 3 months"
/// and "every 2 weeks" are recordable without an enum value for each.
/// </summary>
/// <remarks>
/// Ordinal <b>4</b> is RETIRED and must never be reassigned: it was <c>Quarterly</c>, which the
/// rename migration rewrote to <see cref="Monthly"/> with a count of 3. A row still holding 4 — a
/// hand-edited database, a restore from a pre-migration dump, an interrupted migration — has to fail
/// as an undefined value rather than silently becoming whatever took the slot. That is why
/// <see cref="Weekly"/> takes 7 rather than the vacant 4.
/// </remarks>
public enum Interval
{
    OneTime = 0,
    PerOccurrence = 1,
    Daily = 2,
    Monthly = 3,
    Annually = 5,
    PerUnit = 6,
    Weekly = 7,
}

/// <summary>
/// The one definition of which cadence units are PERIODIC — the units an <c>IntervalCount</c>
/// multiplies. The non-periodic set (<see cref="Interval.OneTime"/>,
/// <see cref="Interval.PerOccurrence"/>, <see cref="Interval.PerUnit"/>) names an occasion rather
/// than a rhythm, so "how many of them between charges" has no meaning there.
/// </summary>
public static class IntervalCadence
{
    public static bool IsPeriodic(this Interval interval) => interval switch
    {
        Interval.Daily => true,
        Interval.Weekly => true,
        Interval.Monthly => true,
        Interval.Annually => true,
        _ => false,
    };
}
