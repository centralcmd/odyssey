using Odyssey.Client.Components;

namespace Odyssey.Client.Pages.Finance;

/// <summary>
/// The collapsed row's headline figure for a tax statement: what the year settles at, and whether that
/// is a fact or an estimate.
///
/// <para>
/// A DECLARED settlement is stated on the official assessment and reads plainly. Without one, the
/// figure falls back to the reconciliation's estimated outstanding tax and is marked "(est.)" — and an
/// estimate carries the same three readings its declared counterpart would, because the sign means the
/// same thing either way: money owed, money coming back, or nothing left to settle.
/// </para>
///
/// <para>
/// It is a static here so those readings are testable: the wording used to collapse every non-null
/// estimate into "outstanding (est.)", which made an estimated refund read as tax owed.
/// </para>
/// </summary>
public static class TaxSettlementFigure
{
    /// <summary>The caption under the headline figure.</summary>
    public static string Word(decimal? declared, decimal? settle)
    {
        if (declared is { } stated)
            return stated > 0 ? "additional tax to pay" : stated < 0 ? "refund" : "settled";

        return settle switch
        {
            null => "awaiting assessment",
            > 0 => "outstanding (est.)",
            < 0 => "refund (est.)",
            _ => "settled (est.)",
        };
    }

    /// <summary>
    /// The figure takes the finance vocabulary, never the record's accent: tax still to pay is an
    /// expense, a refund is income, and a settled — or not yet assessed — year is neither.
    /// </summary>
    public static OdsRecordFigureTone Tone(decimal? settle) => settle switch
    {
        > 0 => OdsRecordFigureTone.Expense,
        < 0 => OdsRecordFigureTone.Income,
        _ => OdsRecordFigureTone.Neutral,
    };
}
