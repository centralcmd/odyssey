using System.Collections.Frozen;

namespace Odyssey.Core.Finance.Instruments.Sources.Tiingo;

/// <summary>
/// The compiled <c>exchangeCode → ISO 4217</c> map (issue #447 §3.3, amendment A2). Tiingo states no currency in a
/// price row or in the metadata, so the currency is derived from the venue Tiingo states — never from the instrument.
///
/// <para>
/// Lookup is <strong>exact and case-sensitive, with no default</strong>: a code not listed here is
/// <c>UnsupportedInput</c>. Every entry was confirmed against Tiingo's <c>supported_tickers.zip</c> (read 2026-11-11):
/// each listed code quotes in USD for every current ticker, apart from the five named in
/// <see cref="ForeignQuotedTickers"/>. A venue the zip shows quoting in more than one currency is not mapped — which
/// is why <c>SHE</c> and <c>SHG</c> (CNY, but also USD and HKD tickers) are absent. A further venue is a one-line
/// addition, after the same check.
/// </para>
/// </summary>
public static class TiingoVenues
{
    public static readonly FrozenDictionary<string, string> CurrencyByExchangeCode = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["NYSE"] = "USD",
        ["NASDAQ"] = "USD",
        ["NYSE ARCA"] = "USD",
        ["NYSE MKT"] = "USD",
        ["AMEX"] = "USD",
        ["BATS"] = "USD",
        // Tiingo's mutual-fund venue; a fund's close is its NAV.
        ["NMFQS"] = "USD",
    }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>
    /// Tickers <c>supported_tickers.zip</c> (2026-11-11) lists on a mapped US venue but priced in AUD. Mapping them by
    /// venue would store AUD closes as USD, so they are refused like an unmapped venue. Compared case-insensitively,
    /// as Tiingo tickers are.
    /// </summary>
    public static readonly FrozenSet<string> ForeignQuotedTickers =
        new[] { "AYA", "CSF", "FDV", "MPG", "NYM" }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>The ISO code for a stated venue, or <c>null</c> — never a default.</summary>
    public static string? CurrencyOf(string? exchangeCode, string ticker) =>
        exchangeCode is not null && !ForeignQuotedTickers.Contains(ticker)
        && CurrencyByExchangeCode.TryGetValue(exchangeCode, out var currency)
            ? currency
            : null;
}
