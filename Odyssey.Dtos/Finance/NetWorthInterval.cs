namespace Odyssey.Dtos.Finance;

/// <summary>
/// The resolution of a net-worth history series (issue #90 §5.1). Every interval is <b>calendar</b>
/// aligned rather than counted in days: a week starts on Monday (ISO-8601), a quarter on a calendar
/// quarter boundary, and a point count is a count of periods rather than of days divided by a length.
/// </summary>
/// <remarks>
/// The values cross the wire as ordinals — there is no <c>JsonStringEnumConverter</c> in the solution
/// — so the numbers are a contract. Append new members; do not renumber.
/// </remarks>
public enum NetWorthInterval
{
    Daily = 0,
    Weekly = 1,
    Monthly = 2,
    Quarterly = 3,
    Yearly = 4,
}
