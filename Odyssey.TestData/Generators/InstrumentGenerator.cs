using Odyssey.Context;
using Odyssey.Dtos.Finance;
using static Odyssey.TestData.DemoDataDefaults;

namespace Odyssey.TestData.Generators;

/// <summary>
/// Demo instruments and their manual daily closes (issue #446): two Oslo Børs stocks, a US-listed ETF (so the
/// watchlist converts) and a Norwegian fund identified by ISIN. Closes are a deterministic random walk over the
/// weekdays of the last <see cref="HistoryDays"/> days before the anchor, entered as manual rows — no source client
/// exists in Phase 1, so nothing is fetched and no binding is seeded.
/// </summary>
public static class InstrumentGenerator
{
    public const int HistoryDays = 120;

    private static readonly (string Symbol, string? Mic, string? Isin, string Name, InstrumentType Type, string Currency, decimal Start, bool Dashboard)[] Catalogue =
    [
        ("EQNR", "XOSL", "NO0010096985", "Equinor ASA", InstrumentType.Stock, Currencies.Nok, 285.40m, true),
        ("DNB", "XOSL", null, "DNB Bank ASA", InstrumentType.Stock, Currencies.Nok, 212.10m, true),
        ("VTI", "ARCX", null, "Vanguard Total Stock Market ETF", InstrumentType.Etf, Currencies.Usd, 268.55m, true),
        ("ODYIDX", null, WithCheckDigit("NO000000123"), "Odyssey Global Index Fund", InstrumentType.Fund, Currencies.Nok, 1843.2071m, false),
    ];

    public static (List<Instrument> Instruments, List<InstrumentPrice> Prices) Build(DateTime anchor)
    {
        var instruments = new List<Instrument>();
        var prices = new List<InstrumentPrice>();
        var random = new Random(RandomizerSeed + 446);
        var last = DateOnly.FromDateTime(anchor).AddDays(-1);

        foreach (var (entry, index) in Catalogue.Select((entry, index) => (entry, index)))
        {
            var id = DeterministicGuid.From("instrument", index);
            instruments.Add(new Instrument
            {
                InstrumentId = id,
                Symbol = entry.Symbol,
                MicCode = entry.Mic,
                Isin = entry.Isin,
                Name = entry.Name,
                Type = entry.Type,
                CurrencyCode = entry.Currency,
                ListingKey = InstrumentIdentifierRules.ListingKey(entry.Symbol, entry.Mic, entry.Isin, entry.Currency),
                ShowOnDashboard = entry.Dashboard,
                CreatedAtUtc = anchor.AddDays(-HistoryDays - 1),
            });

            var close = entry.Start;
            var decimals = entry.Type == InstrumentType.Fund ? 4 : 2;
            for (var day = last.AddDays(-HistoryDays + 1); day <= last; day = day.AddDays(1))
            {
                if (day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
                {
                    continue;
                }

                // ±1.5 % a day, rounded to the listing's tick; a close is always positive.
                close = Math.Max(0.01m, decimal.Round(close * (1m + ((decimal)random.NextDouble() - 0.5m) * 0.03m), decimals));
                prices.Add(new InstrumentPrice
                {
                    InstrumentPriceId = DeterministicGuid.From($"instrument-price:{index}", day.DayNumber),
                    InstrumentId = id,
                    TradingDate = day,
                    Close = close,
                    CreatedAtUtc = day.ToDateTime(new TimeOnly(17, 0), DateTimeKind.Utc),
                });
            }
        }

        return (instruments, prices);
    }

    /// <summary>Completes an 11-character ISIN body with the check digit the shared rule accepts.</summary>
    private static string WithCheckDigit(string body) =>
        Enumerable.Range(0, 10).Select(digit => body + digit).First(InstrumentIdentifierRules.IsValidIsinCheckDigit);
}
