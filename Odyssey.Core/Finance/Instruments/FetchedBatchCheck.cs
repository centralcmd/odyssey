using Odyssey.Core.Finance.Instruments.Sources;
using Odyssey.Dtos;
using Odyssey.Dtos.Application;
using Odyssey.Dtos.Finance;

namespace Odyssey.Core.Finance.Instruments;

/// <summary>
/// The structural check every fetched batch passes before insert (issue #446 §8.5). Any violation rejects the
/// whole batch. Values are not judged — no plausibility rule, by product decision; only data that is broken or
/// belongs to another instrument is refused. An empty batch passes.
/// </summary>
public static class FetchedBatchCheck
{
    /// <summary>Rows a batch may carry beyond its window's day count.</summary>
    public const int RowSlack = 5;

    private static readonly decimal Min = decimal.Parse(MoneyBounds.InstrumentPriceMin, System.Globalization.CultureInfo.InvariantCulture);
    private static readonly decimal Max = decimal.Parse(MoneyBounds.InstrumentPriceMax, System.Globalization.CultureInfo.InvariantCulture);

    /// <summary><c>null</c> when the batch may be stored; otherwise the failure category and the rule number broken.</summary>
    public static (JobFailureCategory Category, int Rule)? Check(
        IReadOnlyList<FetchedClose> batch, SourceFetchRequest request, string instrumentCurrency, DateOnly today)
    {
        var windowDays = request.To.DayNumber - request.From.DayNumber + 1;
        if (batch.Count > windowDays + RowSlack)
        {
            return (JobFailureCategory.ProviderMalformed, 1);
        }

        if (batch.Select(close => close.TradingDate).Distinct().Count() != batch.Count)
        {
            return (JobFailureCategory.ProviderMalformed, 2);
        }

        foreach (var close in batch)
        {
            if (close.TradingDate < request.From || close.TradingDate > request.To || close.TradingDate > today)
            {
                return (JobFailureCategory.ProviderMalformed, 3);
            }

            if (close.Close <= 0 || close.Close < Min || close.Close > Max || !InstrumentPriceRules.HasAtMostDecimals(close.Close))
            {
                return (JobFailureCategory.ProviderMalformed, 4);
            }

            // A minor-unit quote is never scaled: GBX and ILA differ from every major code, and a mixed-case code such
            // as GBp or ZAc (pence, cents) is refused outright rather than read case-insensitively as GBP or ZAC.
            var code = close.CurrencyCode?.Trim();
            if (string.IsNullOrEmpty(code)
                || (code.Any(char.IsUpper) && code.Any(char.IsLower))
                || !string.Equals(code, instrumentCurrency, StringComparison.OrdinalIgnoreCase))
            {
                return (JobFailureCategory.UnsupportedInput, 5);
            }
        }

        return null;
    }
}
