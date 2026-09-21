using Odyssey.Client.Components;
using Odyssey.Dtos.Finance;

namespace Odyssey.Client.Pages.Finance;

/// <summary>
/// Visual + presentation helpers for the account value-estimate surfaces (the "Estimates" section
/// and the New / Edit estimate dialog). Unlike <see cref="TermKindVisuals"/> an estimate has no
/// kind / unit / billing dimension — it is a single money value — so this registry only carries the
/// recommended-type subset and the compact money formatting the value chart's axis needs. Mirrors
/// the Odyssey Design System (data.js <c>estimateRecommendedTypes</c> + <c>moneyCompact</c>).
/// </summary>
public static class EstimateVisuals
{
    /// <summary>The recommended practical subset for estimates — asset accounts whose worth is not
    /// fully transaction-derived. A UI hint only; every account type is eligible (the API never
    /// blocks estimates on any type).</summary>
    private static readonly HashSet<AccountType> RecommendedTypes =
    [
        AccountType.Property,
        AccountType.Vehicle,
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
}
