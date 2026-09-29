namespace Odyssey.Dtos.Finance;

/// <summary>
/// The settlement-range rules a tax statement's server and editor share, declared once here so the
/// dialog's live preview and the stored default cannot drift (the <see cref="ContractPartyRoleMatrix"/>
/// precedent).
/// </summary>
public static class TaxSettlementRange
{
    /// <summary>
    /// The default settlement range: the statement period shifted +1 year. <see cref="DateTime.AddYears"/>
    /// clamps 29 February to the 28th in a non-leap year. A date in the calendar's last year stays
    /// where it is rather than throwing — this runs on every read of a statement, so a row that
    /// predates the write-side bound (<see cref="MinDate"/>/<see cref="MaxDate"/>) must still project.
    /// </summary>
    public static (DateTime Start, DateTime End) Default(DateTime periodStart, DateTime periodEnd) =>
        (ShiftYear(periodStart), ShiftYear(periodEnd));

    /// <summary>The earliest period or settlement date a statement accepts (the fiscal-year floor).</summary>
    public static readonly DateTime MinDate = new(1900, 1, 1);

    /// <summary>
    /// The latest period or settlement date a statement accepts: the end of the year after the
    /// fiscal-year ceiling, so a 2200 statement's settlement can still land in 2201.
    /// </summary>
    public static readonly DateTime MaxDate = new(2201, 12, 31, 23, 59, 59);

    /// <summary>True when <paramref name="date"/> lies within <see cref="MinDate"/>–<see cref="MaxDate"/>.</summary>
    public static bool InBounds(DateTime date) => date >= MinDate && date <= MaxDate;

    private static DateTime ShiftYear(DateTime date) =>
        date.Year < DateTime.MaxValue.Year ? date.AddYears(1) : date;

    /// <summary>
    /// The tags selected both for tax payment and for settlement. A transaction carrying one would be
    /// counted in advance tax paid and in settlement recorded, so such a selection is refused.
    /// </summary>
    public static IReadOnlyList<T> Overlap<T>(IEnumerable<T> taxTags, IEnumerable<T> settlementTags) =>
        [.. settlementTags.Intersect(taxTags)];
}
