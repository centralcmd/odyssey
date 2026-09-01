using System.Globalization;

namespace Odyssey.Client.Pages.Finance;

/// <summary>
/// The two pure figures behind an exchange-rate row: the rate as it reads, and its reciprocal.
///
/// <para>
/// The Inverse column arrived with the design system's flat rates table — the reciprocal used to sit
/// in an expanded detail panel, and now that rows don't expand it is a column of its own. It is also
/// a server sort key (<c>ExchangeRateSortBy.Inverse</c>), which orders by Rate reversed on the
/// grounds that Rate is constrained greater than zero on every write path. That equivalence is the
/// reason the guard below matters: a zero rate cannot arrive through the API, but a reciprocal that
/// divides unguarded would throw rather than degrade if one ever did.
/// </para>
///
/// <para>
/// Statics here rather than private methods on the page, so the guard and the format are assertable
/// — the same extraction the record-card derivations got, for the same reason.
/// </para>
/// </summary>
public static class ExchangeRateFigures
{
    /// <summary>
    /// The reciprocal rate — "1 EUR = x USD" read the other way round. A zero rate yields zero
    /// rather than throwing: the value is unreachable through the validated write paths, so
    /// degrading beats a division fault on a row that should not exist.
    /// </summary>
    public static decimal Inverse(decimal rate) => rate == 0 ? 0 : 1 / rate;

    /// <summary>
    /// A rate as displayed: grouped, always two decimals, up to four. Invariant culture, because the
    /// column is monospaced tabular figures aligned against its neighbours rather than prose.
    /// </summary>
    public static string Format(decimal value) => value.ToString("#,##0.00##", CultureInfo.InvariantCulture);
}
