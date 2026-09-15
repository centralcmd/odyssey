using Odyssey.Dtos.Finance;

namespace Odyssey.Client.Pages.Finance;

/// <summary>
/// The amount each allocation-donut legend row prints on the Accounts page. Split out of
/// <c>AccountsOverview</c> for the same reason <see cref="TermKindVisuals.FormatValue"/> is a static
/// helper: a sign decision that lives as a private method on a component is checkable only by reading
/// the rendered page, and this one already went wrong once.
/// </summary>
/// <remarks>
/// Assets print as stored — the allocation filter already makes them positive. Liabilities print as
/// MAGNITUDES, because the panel's own labels ("What you owe", "Total owed") supply the direction: a
/// minus sign in front of a number that text calls owed negates the sentence and reads as a credit.
/// The sign stays on <c>AccountAllocation.Value</c> and <c>AccountSummary.TotalLiabilities</c>
/// themselves, so the accounts table and the per-account pages are unaffected — only this one ring
/// drops it.
/// </remarks>
public static class AllocationLegend
{
    /// <summary>One account's row in the asset ring, in that account's own currency.</summary>
    public static string AssetRow(AccountAllocation allocation, Func<decimal, string?, string> money) =>
        money(allocation.Value, allocation.CurrencyCode);

    /// <summary>The asset ring's closing row — a naive cross-currency sum, so a null currency code.</summary>
    public static string AssetTotal(decimal totalAssets, Func<decimal, string?, string> money) =>
        money(totalAssets, null);

    /// <summary>One account's row in the liability ring: its magnitude, in its own currency.</summary>
    public static string LiabilityRow(AccountAllocation allocation, Func<decimal, string?, string> money) =>
        money(Math.Abs(allocation.Value), allocation.CurrencyCode);

    /// <summary>The liability ring's closing "Total owed" row: the magnitude of a negative aggregate.</summary>
    public static string LiabilityTotal(decimal totalLiabilities, Func<decimal, string?, string> money) =>
        money(Math.Abs(totalLiabilities), null);
}
