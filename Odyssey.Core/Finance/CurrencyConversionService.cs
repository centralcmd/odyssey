using Odyssey.Context;
using Microsoft.EntityFrameworkCore;

namespace Odyssey.Core.Finance;

/// <summary>
/// Converts amounts between currencies using the latest manually-entered <see cref="ExchangeRate"/>.
/// v1 rules: a same-currency conversion is 1:1; otherwise the newest direct (from, to) rate is used.
/// There is no inversion or triangulation — a (to, from) rate does not satisfy a (from, to) request.
/// A missing rate yields <c>null</c> so callers can flag the amount as unconvertible.
/// </summary>
public class CurrencyConversionService(OdysseyContext context)
{
    /// <summary>
    /// Converts <paramref name="amount"/> from one currency to another using the latest rate.
    /// Returns <c>null</c> when no direct rate exists (and the currencies differ).
    /// </summary>
    public async Task<decimal?> ConvertAsync(decimal amount, string fromCurrencyCode, string toCurrencyCode, CancellationToken cancellationToken = default)
    {
        var from = CurrencyValidationService.Normalize(fromCurrencyCode);
        var to = CurrencyValidationService.Normalize(toCurrencyCode);

        if (string.Equals(from, to, StringComparison.Ordinal))
        {
            return amount;
        }

        var rate = await context.ExchangeRates
            .Where(value => value.FromCurrencyCode == from && value.ToCurrencyCode == to)
            .OrderByDescending(value => value.AsOf)
            .ThenByDescending(value => value.UpdatedAt ?? value.CreatedAt)
            .Select(value => (decimal?)value.Rate)
            .FirstOrDefaultAsync(cancellationToken);

        return rate is null ? null : amount * rate.Value;
    }

    /// <summary>
    /// Resolves the latest rate from each of <paramref name="fromCurrencyCodes"/> into
    /// <paramref name="toCurrencyCode"/> in a single query. The map only contains pairs that
    /// have a rate; the same-currency case is intentionally omitted (callers treat it as 1:1).
    /// Used by the totals computation to avoid one query per account.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, decimal>> GetLatestRatesToAsync(
        string toCurrencyCode, IEnumerable<string> fromCurrencyCodes, CancellationToken cancellationToken = default)
    {
        var to = CurrencyValidationService.Normalize(toCurrencyCode);
        var fromCodes = fromCurrencyCodes
            .Select(CurrencyValidationService.Normalize)
            .Where(code => !string.Equals(code, to, StringComparison.Ordinal))
            .Distinct()
            .ToList();

        if (fromCodes.Count == 0)
        {
            return new Dictionary<string, decimal>();
        }

        // The composite (From, To, AsOf) index serves this Where; the per-pair "latest"
        // pick is done in memory after materializing only the relevant pairs' rows.
        var rates = await context.ExchangeRates
            .Where(value => value.ToCurrencyCode == to && fromCodes.Contains(value.FromCurrencyCode))
            .ToListAsync(cancellationToken);

        return rates
            .GroupBy(value => value.FromCurrencyCode)
            .ToDictionary(
                group => group.Key,
                group => group
                    .OrderByDescending(value => value.AsOf)
                    .ThenByDescending(value => value.UpdatedAt ?? value.CreatedAt)
                    .First().Rate);
    }
}
