using Odyssey.Core;
using Odyssey.Context;
using Odyssey.Dtos.Finance;
using Odyssey.Core.Pagination;
using Odyssey.Dtos;
using Mapster;
using Microsoft.EntityFrameworkCore;

namespace Odyssey.Core.Finance;

/// <summary>
/// Manages the exchange-rate history. A record's currency pair is its identity and is immutable
/// once created; <see cref="Update"/> only corrects the Rate/AsOf of an existing row in place.
/// <see cref="Create"/> remains available for recording a genuinely new entry, and the newest
/// <see cref="ExchangeRate.AsOf"/> for a pair wins in conversions either way.
/// </summary>
public class ExchangeRateService(OdysseyContext context, TimeProvider? injectedTimeProvider = null)
{
    private readonly TimeProvider timeProvider = injectedTimeProvider ?? TimeProvider.System;

    /// <summary>
    /// Server-side paged list (issue #277): search over currency codes, target-currency and
    /// current/historical filters, and allowlisted sort. "Current" is the newest
    /// (AsOf, UpdatedAt ?? CreatedAt) rate per directed pair — expressed as a SQL
    /// "no newer rate exists" predicate.
    /// </summary>
    public async Task<PagedResult<ExistingExchangeRate>> ListAsync(
        ExchangeRatesQueryParams query,
        CancellationToken cancellationToken = default)
    {
        var q = context.ExchangeRates.AsNoTracking().AsQueryable();

        var term = ListQuery.NormalizeSearch(query.Search);
        if (term is not null)
        {
            var pattern = ListQuery.ContainsPattern(term);
            q = q.Where(r =>
                EF.Functions.Like(r.FromCurrencyCode, pattern) ||
                EF.Functions.Like(r.ToCurrencyCode, pattern));
        }

        var targets = query.ToCurrencies?
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Select(code => code.Trim().ToUpperInvariant())
            .ToList();
        if (targets is { Count: > 0 })
        {
            q = q.Where(r => targets.Contains(r.ToCurrencyCode));
        }

        // A rate is "current" when no other rate for the same directed pair has a newer
        // (AsOf, UpdatedAt ?? CreatedAt) — the tiebreak folds in UpdatedAt so correcting a rate
        // (Update touches AsOf/UpdatedAt, never CreatedAt) can't lose "current" to an unrelated
        // row inserted later.
        q = query.Status switch
        {
            ExchangeRateStatus.Current => q.Where(r => !context.ExchangeRates.Any(x =>
                x.FromCurrencyCode == r.FromCurrencyCode && x.ToCurrencyCode == r.ToCurrencyCode &&
                (x.AsOf > r.AsOf || (x.AsOf == r.AsOf && (x.UpdatedAt ?? x.CreatedAt) > (r.UpdatedAt ?? r.CreatedAt))))),
            ExchangeRateStatus.Historical => q.Where(r => context.ExchangeRates.Any(x =>
                x.FromCurrencyCode == r.FromCurrencyCode && x.ToCurrencyCode == r.ToCurrencyCode &&
                (x.AsOf > r.AsOf || (x.AsOf == r.AsOf && (x.UpdatedAt ?? x.CreatedAt) > (r.UpdatedAt ?? r.CreatedAt))))),
            _ => q,
        };

        var ascending = ListQuery.Ascending(query.SortDir, naturalDefaultAscending: query.SortBy is ExchangeRateSortBy.Pair or ExchangeRateSortBy.Status);
        var sorted = query.SortBy switch
        {
            ExchangeRateSortBy.Pair => ascending
                ? q.OrderBy(r => r.FromCurrencyCode).ThenBy(r => r.ToCurrencyCode)
                : q.OrderByDescending(r => r.FromCurrencyCode).ThenByDescending(r => r.ToCurrencyCode),
            ExchangeRateSortBy.Rate => ascending ? q.OrderBy(r => r.Rate) : q.OrderByDescending(r => r.Rate),
            // Inverse is 1 / Rate, and Rate is > 0 on every write path, so the reciprocal is strictly
            // decreasing in Rate — order by Rate reversed rather than dividing in SQL, which keeps the
            // sort exact (no decimal rounding) and lets it use the same index Rate does.
            ExchangeRateSortBy.Inverse => ascending ? q.OrderByDescending(r => r.Rate) : q.OrderBy(r => r.Rate),
            ExchangeRateSortBy.CreatedAt => ascending ? q.OrderBy(r => r.CreatedAt) : q.OrderByDescending(r => r.CreatedAt),
            // status: current (no newer exists → false) sorts before historical, matching the client (current = 0).
            ExchangeRateSortBy.Status => ascending
                ? q.OrderBy(r => context.ExchangeRates.Any(x =>
                    x.FromCurrencyCode == r.FromCurrencyCode && x.ToCurrencyCode == r.ToCurrencyCode &&
                    (x.AsOf > r.AsOf || (x.AsOf == r.AsOf && (x.UpdatedAt ?? x.CreatedAt) > (r.UpdatedAt ?? r.CreatedAt)))))
                : q.OrderByDescending(r => context.ExchangeRates.Any(x =>
                    x.FromCurrencyCode == r.FromCurrencyCode && x.ToCurrencyCode == r.ToCurrencyCode &&
                    (x.AsOf > r.AsOf || (x.AsOf == r.AsOf && (x.UpdatedAt ?? x.CreatedAt) > (r.UpdatedAt ?? r.CreatedAt))))),
            _ => ascending
                ? q.OrderBy(r => r.AsOf).ThenBy(r => r.UpdatedAt ?? r.CreatedAt)
                : q.OrderByDescending(r => r.AsOf).ThenByDescending(r => r.UpdatedAt ?? r.CreatedAt),
        };
        q = sorted.ThenBy(r => r.ExchangeRateId);

        return await q.ToPagedResultAsync(query.Offset, query.Limit, r => r.Adapt<ExistingExchangeRate>(), cancellationToken);
    }

    public async Task<ExistingExchangeRate?> Get(Guid exchangeRateId, CancellationToken cancellationToken = default)
    {
        var rate = await context.ExchangeRates.FirstOrDefaultAsync(value => value.ExchangeRateId == exchangeRateId, cancellationToken);
        return rate?.Adapt<ExistingExchangeRate>();
    }

    /// <summary>Returns the most recent rate (max <see cref="ExchangeRate.AsOf"/>) for the given pair, or null.</summary>
    public async Task<ExistingExchangeRate?> GetLatest(string fromCurrencyCode, string toCurrencyCode, CancellationToken cancellationToken = default)
    {
        var from = CurrencyValidationService.Normalize(fromCurrencyCode);
        var to = CurrencyValidationService.Normalize(toCurrencyCode);

        var rate = await context.ExchangeRates
            .Where(value => value.FromCurrencyCode == from && value.ToCurrencyCode == to)
            .OrderByDescending(value => value.AsOf)
            .ThenByDescending(value => value.UpdatedAt ?? value.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);

        return rate?.Adapt<ExistingExchangeRate>();
    }

    public async Task<ExistingExchangeRate> Create(NewExchangeRate newExchangeRate, CancellationToken cancellationToken = default)
    {
        var from = CurrencyValidationService.Normalize(newExchangeRate.FromCurrencyCode);
        var to = CurrencyValidationService.Normalize(newExchangeRate.ToCurrencyCode);

        if (string.Equals(from, to, StringComparison.Ordinal))
        {
            throw new DomainValidationException("From and To currencies must differ; a same-currency rate is implicitly 1.");
        }

        if (newExchangeRate.Rate <= 0)
        {
            throw new DomainValidationException("Rate must be greater than zero.");
        }

        // Both ends must reference an existing, non-archived currency.
        await CurrencyValidationService.EnsureSupportedAndActive(context, from, nameof(newExchangeRate.FromCurrencyCode), cancellationToken);
        await CurrencyValidationService.EnsureSupportedAndActive(context, to, nameof(newExchangeRate.ToCurrencyCode), cancellationToken);

        var exchangeRate = new ExchangeRate
        {
            FromCurrencyCode = from,
            ToCurrencyCode = to,
            Rate = newExchangeRate.Rate,
            AsOf = newExchangeRate.AsOf is { } asOf
                ? DateTimeNormalization.NormalizeToUtc(asOf)
                : timeProvider.GetUtcNow().UtcDateTime,
            CreatedAt = timeProvider.GetUtcNow().UtcDateTime,
        };

        context.ExchangeRates.Add(exchangeRate);
        await context.SaveChangesAsync(cancellationToken);

        return exchangeRate.Adapt<ExistingExchangeRate>();
    }

    /// <summary>
    /// Corrects the Rate and AsOf of an existing rate record. The currency pair can't change —
    /// the update DTO doesn't carry From/To — so this never touches a different pair's identity.
    /// Returns null if the id doesn't exist.
    /// </summary>
    public async Task<ExistingExchangeRate?> Update(
        Guid exchangeRateId, UpdateExchangeRate update, CancellationToken cancellationToken = default)
    {
        var rate = await context.ExchangeRates.FirstOrDefaultAsync(value => value.ExchangeRateId == exchangeRateId, cancellationToken);
        if (rate is null)
        {
            return null;
        }

        if (update.Rate <= 0)
        {
            throw new DomainValidationException("Rate must be greater than zero.");
        }

        rate.Rate = update.Rate;
        rate.AsOf = DateTimeNormalization.NormalizeToUtc(update.AsOf);
        rate.UpdatedAt = timeProvider.GetUtcNow().UtcDateTime;
        await context.SaveChangesAsync(cancellationToken);

        return rate.Adapt<ExistingExchangeRate>();
    }

    public async Task Delete(Guid exchangeRateId, CancellationToken cancellationToken = default)
    {
        var rate = await context.ExchangeRates.FirstOrDefaultAsync(value => value.ExchangeRateId == exchangeRateId, cancellationToken);
        if (rate is null)
        {
            return;
        }

        context.ExchangeRates.Remove(rate);
        await context.SaveChangesAsync(cancellationToken);
    }
}
