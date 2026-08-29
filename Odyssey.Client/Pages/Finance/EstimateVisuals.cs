using System.Globalization;
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

    /// <summary>Compact money for the value chart's y-axis, e.g. <c>kr 350k</c> / <c>$ 1.2M</c>.
    /// Negative values use the typographic minus. Mirrors the design-system <c>moneyCompact</c>.</summary>
    public static string MoneyCompact(decimal value, string symbol)
    {
        var sign = value < 0 ? "−" : "";
        var abs = Math.Abs(value);
        string s;
        if (abs >= 1_000_000_000m)
            s = Trim(abs / 1_000_000_000m, abs % 1_000_000_000m != 0 ? 2 : 0) + "B";
        else if (abs >= 1_000_000m)
            s = Trim(abs / 1_000_000m, abs % 1_000_000m != 0 ? 2 : 0) + "M";
        else if (abs >= 1_000m)
            s = Trim(abs / 1_000m, abs % 1_000m != 0 ? 1 : 0) + "k";
        else
            s = Math.Round(abs).ToString("0", CultureInfo.InvariantCulture);

        return $"{sign}{symbol} {s}";
    }

    private static string Trim(decimal value, int decimals) =>
        value.ToString("0." + new string('#', decimals), CultureInfo.InvariantCulture);
}
