using Odyssey.Core;
using Odyssey.Context;
using Odyssey.Dtos.Finance;
using Odyssey.Core.Pagination;
using Odyssey.Dtos;
using Mapster;
using Microsoft.EntityFrameworkCore;

namespace Odyssey.Core.Finance;

public class CurrencyService(OdysseyContext context, TimeProvider? injectedTimeProvider = null)
{
    private readonly TimeProvider timeProvider = injectedTimeProvider ?? TimeProvider.System;

    /// <summary>Server-side paged list (issue #277): search over code/name + allowlisted sort.</summary>
    public async Task<PagedResult<ExistingCurrency>> ListAsync(
        CurrenciesQueryParams query,
        CancellationToken cancellationToken = default)
    {
        var q = context.Currencies.AsNoTracking().AsQueryable();

        var term = ListQuery.NormalizeSearch(query.Search);
        if (term is not null)
        {
            var pattern = ListQuery.ContainsPattern(term);
            q = q.Where(c =>
                EF.Functions.Like(c.CurrencyCode, pattern) ||
                EF.Functions.Like(c.Name, pattern));
        }

        q = query.Status switch
        {
            ArchivalStatus.Archived => q.Where(c => c.Archived != null),
            ArchivalStatus.Active => q.Where(c => c.Archived == null),
            _ => q,
        };

        var ascending = ListQuery.Ascending(query.SortDir, naturalDefaultAscending: true);
        var sorted = query.SortBy switch
        {
            CurrencySortBy.Name => ascending ? q.OrderBy(c => c.Name) : q.OrderByDescending(c => c.Name),
            CurrencySortBy.Symbol => ascending ? q.OrderBy(c => c.Symbol) : q.OrderByDescending(c => c.Symbol),
            CurrencySortBy.MinorUnits => ascending ? q.OrderBy(c => c.MinorUnits) : q.OrderByDescending(c => c.MinorUnits),
            // Status sorts on the derived archival flag (active before archived when ascending).
            CurrencySortBy.Status => ascending ? q.OrderBy(c => c.Archived != null) : q.OrderByDescending(c => c.Archived != null),
            _ => ascending ? q.OrderBy(c => c.CurrencyCode) : q.OrderByDescending(c => c.CurrencyCode),
        };
        q = sorted.ThenBy(c => c.CurrencyCode);

        return await q.ToPagedResultAsync(query.Offset, query.Limit, c => c.Adapt<ExistingCurrency>(), cancellationToken);
    }

    public async Task<ExistingCurrency?> Get(string currencyCode, CancellationToken cancellationToken = default)
    {
        var normalizedCode = NormalizeCode(currencyCode);
        var currency = await context.Currencies.FirstOrDefaultAsync(value => value.CurrencyCode == normalizedCode, cancellationToken);
        return currency?.Adapt<ExistingCurrency>();
    }

    public async Task<ExistingCurrency> Create(NewCurrency newCurrency, CancellationToken cancellationToken = default)
    {
        var normalizedCode = NormalizeCode(newCurrency.CurrencyCode);
        Validate(newCurrency, normalizedCode);

        var currency = new Currency
        {
            CurrencyCode = normalizedCode,
            Name = newCurrency.Name.Trim(),
            MinorUnits = newCurrency.MinorUnits,
            Symbol = newCurrency.Symbol.Trim(),
        };

        ApplyArchiveTransition(currency, newCurrency.Archived);

        context.Currencies.Add(currency);
        await context.SaveChangesAsync(cancellationToken);

        return currency.Adapt<ExistingCurrency>();
    }

    public async Task<ExistingCurrency?> Update(string currencyCode, NewCurrency putCurrency, CancellationToken cancellationToken = default)
    {
        var normalizedCode = NormalizeCode(currencyCode);
        if (!string.Equals(normalizedCode, NormalizeCode(putCurrency.CurrencyCode), StringComparison.Ordinal))
        {
            throw new DomainValidationException("Currency code in route and payload must match.");
        }

        var currency = await context.Currencies.FirstOrDefaultAsync(value => value.CurrencyCode == normalizedCode, cancellationToken);
        if (currency is null)
        {
            return null;
        }

        Validate(putCurrency, normalizedCode);

        currency.Name = putCurrency.Name.Trim();
        currency.MinorUnits = putCurrency.MinorUnits;
        currency.Symbol = putCurrency.Symbol.Trim();
        ApplyArchiveTransition(currency, putCurrency.Archived);

        await context.SaveChangesAsync(cancellationToken);

        return currency.Adapt<ExistingCurrency>();
    }

    public async Task Delete(string currencyCode, CancellationToken cancellationToken = default)
    {
        var normalizedCode = NormalizeCode(currencyCode);
        var currency = await context.Currencies.FirstOrDefaultAsync(value => value.CurrencyCode == normalizedCode, cancellationToken);
        if (currency is null)
        {
            return;
        }

        context.Currencies.Remove(currency);
        await context.SaveChangesAsync(cancellationToken);
    }

    private static string NormalizeCode(string currencyCode)
    {
        return currencyCode.Trim().ToUpperInvariant();
    }

    private static void Validate(NewCurrency value, string normalizedCode)
    {
        if (!CurrencyValidationService.IsIsoFormat(normalizedCode))
        {
            throw new DomainValidationException("Currency code must be a 3-letter ISO-4217 code.");
        }

        if (string.IsNullOrWhiteSpace(value.Name) || value.Name.Trim().Length > 64)
        {
            throw new DomainValidationException("Currency name length must be between 1 and 64 characters.");
        }

        if (value.MinorUnits is < 0 or > 12)
        {
            throw new DomainValidationException("MinorUnits must be between 0 and 12.");
        }

        if (string.IsNullOrWhiteSpace(value.Symbol) || value.Symbol.Trim().Length > 8)
        {
            throw new DomainValidationException("Currency symbol length must be between 1 and 8 characters.");
        }
    }

    private void ApplyArchiveTransition(Currency currency, bool requestedArchived)
    {
        var currentArchived = currency.Archived is not null;

        if (!currentArchived && requestedArchived)
        {
            currency.Archived = timeProvider.GetUtcNow().UtcDateTime;
            return;
        }

        if (currentArchived && !requestedArchived)
        {
            currency.Archived = null;
        }
    }
}
