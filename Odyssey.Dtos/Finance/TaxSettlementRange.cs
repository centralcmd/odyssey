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
    /// clamps 29 February to the 28th in a non-leap year.
    /// </summary>
    public static (DateTime Start, DateTime End) Default(DateTime periodStart, DateTime periodEnd) =>
        (periodStart.AddYears(1), periodEnd.AddYears(1));

    /// <summary>
    /// The tags selected both for tax payment and for settlement. A transaction carrying one would be
    /// counted in advance tax paid and in settlement recorded, so such a selection is refused.
    /// </summary>
    public static IReadOnlyList<T> Overlap<T>(IEnumerable<T> taxTags, IEnumerable<T> settlementTags) =>
        [.. settlementTags.Intersect(taxTags)];
}
