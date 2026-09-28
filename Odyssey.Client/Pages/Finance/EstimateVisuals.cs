using System.Globalization;
using Odyssey.Client.Components;
using Odyssey.Dtos.Finance;

namespace Odyssey.Client.Pages.Finance;

/// <summary>
/// How <c>AccountEstimatesSection</c> draws its value chart (Odyssey Design System · AccountEstimates
/// <c>chartStyle</c>): its own estimate hero card, or the TermHistoryChart card the property value
/// history uses, which the account detail takes.
/// </summary>
public enum EstimateChartStyle
{
    Estimate,
    History,
}

/// <summary>
/// Visual + presentation helpers for the account value-estimate surfaces (the "Estimates" section
/// and the New / Edit estimate dialog). Unlike <see cref="TermVisuals"/> an estimate has no
/// unit / billing dimension — it is a single money value — so this registry only carries the
/// recommended-type subset and the compact money formatting the value chart's axis needs. Mirrors
/// the Odyssey Design System (data.js <c>estimateRecommendedTypes</c> + <c>moneyCompact</c>).
/// </summary>
public static class EstimateVisuals
{
    /// <summary>The recommended practical subset for estimates — asset accounts whose worth is not
    /// fully transaction-derived. A UI hint only; every account type is eligible (the API never
    /// blocks estimates on any type). A house or a car is a property record, not an account
    /// (issue #218), so neither retired type appears here.</summary>
    private static readonly HashSet<AccountType> RecommendedTypes =
    [
        AccountType.OtherAsset,
        AccountType.InvestmentAccount,
        AccountType.PensionAccount,
    ];

    /// <summary>Whether the account type is in the recommended subset (used only to orient the user
    /// in the empty state and the dialog hint — never to gate).</summary>
    public static bool IsRecommended(AccountType type) => RecommendedTypes.Contains(type);

    /// <summary>Compact money for the value chart's y-axis, e.g. <c>350k NOK</c> / <c>1.2M USD</c>.
    /// Delegates to <see cref="OdsMoney.Compact"/> — the solution's one money formatter — so the axis
    /// and the full amounts above it cannot drift on the sign slot or the code's placement.</summary>
    public static string MoneyCompact(decimal value, string? currencyCode) =>
        OdsMoney.Compact(value, currencyCode);

    /// <summary>
    /// The value-history chart's y-axis tick: no currency code (the series states it once), compact so
    /// 5.14M fits. Shared by the account and the property value histories, which draw the same card.
    /// </summary>
    public static string CompactTick(decimal v)
    {
        var a = Math.Abs(v);
        var sign = v < 0 ? "−" : "";
        return sign + (a >= 1_000_000m
            ? (a / 1_000_000m).ToString("0.00", CultureInfo.InvariantCulture) + "M"
            : a >= 10_000m
                ? Math.Round(a / 1_000m).ToString("0", CultureInfo.InvariantCulture) + "K"
                : a.ToString("#,##0", CultureInfo.InvariantCulture));
    }
}
