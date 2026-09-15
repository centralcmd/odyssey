using System.Globalization;
using Odyssey.Dtos.Finance;

namespace Odyssey.Client.Pages.Finance;

/// <summary>
/// The dashboard's money formatting and advisory wording, split out of <c>Home</c> for the same
/// reason <see cref="AllocationLegend"/> was split out of <c>AccountsOverview</c>: a branch that
/// lives as a private method on a component is checkable only by rendering the page, and
/// <c>Home</c> cannot be rendered in a test — its <c>OnInitializedAsync</c> early-returns on
/// <c>!OperatingSystem.IsBrowser()</c> and it exposes no <c>InteractiveCheck</c> seam.
/// </summary>
/// <remarks>
/// These are the branches a source-lint cannot reach. A lint proves the page asks the server for its
/// net worth; only a behavioural test proves the answer is then rendered in the right currency and
/// that the advisory names the right pair of them.
/// </remarks>
internal static class DashboardFigures
{
    /// <summary>Two decimals under a generic "$", for an amount whose currency is not the main one.</summary>
    internal static NumberFormatInfo GenericMoneyFormat() => BaseFormat();

    /// <summary>
    /// The main currency's symbol and minor units. <paramref name="currency"/> is the reference-data
    /// row when one was found. A known code with no usable symbol falls back to the CODE, not to "$":
    /// a wrong sigil misreports the denomination, where the code merely looks unpolished.
    /// </summary>
    internal static NumberFormatInfo MoneyFormat(string? currencyCode, ExistingCurrency? currency)
    {
        var format = BaseFormat();
        if (string.IsNullOrWhiteSpace(currencyCode))
            return format;

        if (currency is not null && !string.IsNullOrWhiteSpace(currency.Symbol))
        {
            format.CurrencySymbol = currency.Symbol;
            format.CurrencyDecimalDigits = currency.MinorUnits;
        }
        else
        {
            format.CurrencySymbol = currencyCode;
        }

        return format;
    }

    /// <summary>
    /// A compact axis label, e.g. "$52k" / "kr 52k" / "CHF 640".
    ///
    /// <para>
    /// The separator is the point. A sigil ("$", "€", "£") sits against its number; an ALPHABETIC
    /// symbol does not, and the app's own default main currency is one — NOK's symbol is "kr", so
    /// the unseparated form reads "kr52k", and CHF's symbol is the code itself. The headline figure
    /// has no such problem because <see cref="NumberFormatInfo"/>'s "C" format supplies the spacing;
    /// this label is hand-composed to get the "k" suffix, so it has to supply its own.
    /// </para>
    /// </summary>
    internal static string AxisLabel(decimal value, string symbol)
    {
        var prefix = Prefix(symbol);
        return value >= 1000 || value <= -1000
            ? $"{prefix}{value / 1000:0}k"
            : $"{prefix}{value:0}";
    }

    /// <summary>The advisory on a party the server could not convert. Order matters: FROM the account's currency, TO the main one.</summary>
    internal static string UnconvertedMessage(string accountCurrencyCode, string mainCurrencyCode) =>
        $"No exchange rate from {accountCurrencyCode} to {mainCurrencyCode}, "
        + "so this account counts as 0 towards net worth.";

    /// <summary>
    /// The chart's empty state has two causes and they are not interchangeable: nothing to chart, or
    /// a figure the server could not give us. Reporting the first for the second tells the reader
    /// their accounts are empty when they are not.
    /// </summary>
    internal static string ChartEmptyLabel(bool hasTotals) => hasTotals
        ? "No account balances to chart yet."
        : "Net worth is unavailable right now.";

    private static string Prefix(string symbol) =>
        symbol.Length > 0 && char.IsLetter(symbol[^1]) ? symbol + " " : symbol;

    private static NumberFormatInfo BaseFormat()
    {
        var format = (NumberFormatInfo)CultureInfo.CurrentCulture.NumberFormat.Clone();
        format.CurrencySymbol = "$";
        format.CurrencyDecimalDigits = 2;
        format.CurrencyNegativePattern = 1; // "-$n" — leading minus, no parentheses
        return format;
    }
}
