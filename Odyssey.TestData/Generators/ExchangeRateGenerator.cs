using Odyssey.Context;
using static Odyssey.TestData.DemoDataDefaults;

namespace Odyssey.TestData.Generators;

/// <summary>
/// Seeds realistic exchange rates so multi-currency accounts convert without warnings.
/// The conversion service does no inversion or triangulation (a (to,from) rate does NOT serve a
/// (from,to) request), so every directed pair among the demo currencies gets its own rate.
/// Rates are derived from a single USD-value table to stay internally consistent, with a short
/// monthly history so the rates view looks populated; conversions use the latest (AsOf) row.
/// </summary>
public static class ExchangeRateGenerator
{
    // Value of one unit of each currency expressed in USD. Cross rates are derived from these,
    // so the matrix is consistent (e.g. EUR→SEK == EUR→USD × USD→SEK). NOK is included even
    // though no account uses it: it is the default display/main currency, so every account
    // currency must have a rate into it or the totals view flags the account.
    private static readonly (string Code, decimal UsdValue)[] Currencies =
    [
        (DemoDataDefaults.Currencies.Usd, 1.0000m),
        (DemoDataDefaults.Currencies.Eur, 1.0870m),
        (DemoDataDefaults.Currencies.Gbp, 1.2660m),
        (DemoDataDefaults.Currencies.Sek, 0.0952m),
        (DemoDataDefaults.Currencies.Nok, 0.0920m),
    ];

    // (months back, scale) — a little drift in the recent past; the latest point (scale 1.0) is exact.
    private static readonly (int MonthsBack, decimal Scale)[] Snapshots =
    [
        (2, 0.98m),
        (1, 0.99m),
        (0, 1.00m),
    ];

    public static List<ExchangeRate> Build(DateTime anchor)
    {
        var rates = new List<ExchangeRate>();

        foreach (var from in Currencies)
        {
            foreach (var to in Currencies)
            {
                if (string.Equals(from.Code, to.Code, StringComparison.Ordinal))
                {
                    continue;
                }

                var baseRate = from.UsdValue / to.UsdValue;

                foreach (var (monthsBack, scale) in Snapshots)
                {
                    var asOf = anchor.AddMonths(-monthsBack);
                    rates.Add(new ExchangeRate
                    {
                        ExchangeRateId = DeterministicGuid.From($"fx::{from.Code}>{to.Code}@{monthsBack}"),
                        FromCurrencyCode = from.Code,
                        ToCurrencyCode = to.Code,
                        Rate = Math.Round(baseRate * scale, 6, MidpointRounding.AwayFromZero),
                        AsOf = asOf,
                        CreatedAt = asOf,
                    });
                }
            }
        }

        return rates;
    }
}
