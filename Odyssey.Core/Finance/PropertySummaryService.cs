using Microsoft.EntityFrameworkCore;
using Odyssey.Context;
using Odyssey.Dtos.Finance;
using DtoPropertyType = Odyssey.Dtos.Finance.PropertyType;

namespace Odyssey.Core.Finance;

/// <summary>
/// The <c>/properties</c> header overview: counts by type and derived status, and what the owned
/// properties are estimated to be worth.
///
/// <para>
/// <b>The value is summed per currency first and converted second.</b> Each currency's row is the raw
/// sum in that currency; only the total is converted, at the latest rate to base. A currency with no
/// rate is named and left out of the total rather than folded in at 1:1 — the same rule the contract
/// run rate follows, because a silent 1:1 reads as a whole figure when it is a partial one.
/// </para>
///
/// <para>
/// <b>Nothing here feeds net worth</b> (issue #167 Non-Goal 6): a property is standalone in v1.
/// </para>
/// </summary>
public class PropertySummaryService
{
    private readonly OdysseyContext context;
    private readonly CurrencyConversionService conversion;
    private readonly TimeProvider timeProvider;

    public PropertySummaryService(
        OdysseyContext context, CurrencyConversionService conversion, TimeProvider? timeProvider = null)
    {
        this.context = context;
        this.conversion = conversion;
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <param name="baseCurrency">
    /// The currency the total is converted to. Blank picks the currency most owned properties are
    /// valued in, tie-broken by code so the pick is deterministic.
    /// </param>
    /// <param name="includeValue">
    /// Whether the caller holds <c>properties.estimates.read</c>; without it <see cref="PropertySummary.Value"/>
    /// is left <c>null</c> and no estimate is read.
    /// </param>
    public async Task<PropertySummary> GetAsync(
        string? baseCurrency, bool includeValue, CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;

        var rows = await context.Properties
            .AsNoTracking()
            .Select(p => new { p.PropertyId, p.Type, p.Archived, p.DisposedDate, p.CurrencyCode })
            .ToListAsync(cancellationToken);

        var statusById = rows.ToDictionary(
            r => r.PropertyId, r => PropertyService.DeriveStatus(r.Archived, r.DisposedDate, now));

        var summary = new PropertySummary
        {
            TotalProperties = rows.Count,
            ByType =
            [
                .. Enum.GetValues<DtoPropertyType>().Select(type => new PropertyTypeCount
                {
                    Type = type,
                    Count = rows.Count(r => r.Archived is null && (DtoPropertyType)r.Type == type),
                }),
            ],
            ByStatus = new PropertyStatusCounts
            {
                Owned = statusById.Values.Count(s => s == PropertyStatus.Owned),
                Disposed = statusById.Values.Count(s => s == PropertyStatus.Disposed),
                Archived = statusById.Values.Count(s => s == PropertyStatus.Archived),
            },
        };

        if (!includeValue)
            return summary;

        var owned = rows.Where(r => statusById[r.PropertyId] == PropertyStatus.Owned).ToList();
        var ownedIds = owned.Select(r => r.PropertyId).ToList();

        var estimates = await context.PropertyEstimates
            .AsNoTracking()
            .Where(e => ownedIds.Contains(e.PropertyId) && e.EffectiveFrom <= now)
            .ToListAsync(cancellationToken);

        var currencyById = owned.ToDictionary(r => r.PropertyId, r => CurrencyValidationService.Normalize(r.CurrencyCode));
        var current = estimates
            .GroupBy(e => e.PropertyId)
            .Select(g => g.MostEffective()!)
            .Select(e => (Currency: currencyById[e.PropertyId], e.Value))
            .ToList();

        var byCurrency = current
            .GroupBy(c => c.Currency, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new PropertyCurrencyValue { CurrencyCode = g.Key, Total = g.Sum(c => c.Value), Count = g.Count() })
            .ToList();

        var baseCode = string.IsNullOrWhiteSpace(baseCurrency)
            ? byCurrency.OrderByDescending(c => c.Count).ThenBy(c => c.CurrencyCode, StringComparer.Ordinal)
                .FirstOrDefault()?.CurrencyCode ?? "USD"
            : CurrencyValidationService.Normalize(baseCurrency);

        var rates = await conversion.GetLatestRatesToAsync(
            baseCode, byCurrency.Select(c => c.CurrencyCode), cancellationToken: cancellationToken);

        decimal? total = null;
        var unconverted = new List<string>();
        foreach (var row in byCurrency)
        {
            decimal rate;
            if (string.Equals(row.CurrencyCode, baseCode, StringComparison.Ordinal))
            {
                rate = 1m;
            }
            else if (!rates.TryGetValue(row.CurrencyCode, out rate))
            {
                unconverted.Add(row.CurrencyCode);
                continue;
            }

            total = (total ?? 0m) + row.Total * rate;
        }

        summary.Value = new PropertyValueSummary
        {
            BaseCurrency = baseCode,
            ByCurrency = byCurrency,
            Total = total is { } t ? Math.Round(t, 2, MidpointRounding.AwayFromZero) : null,
            UnconvertedCurrencies = unconverted,
        };

        return summary;
    }
}
