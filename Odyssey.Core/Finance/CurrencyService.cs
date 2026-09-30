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

        var blockers = await CountDeleteBlockers(normalizedCode, cancellationToken);

        if (blockers.Count > 0)
        {
            throw new DomainConflictException(string.Join(" ", blockers));
        }

        context.Currencies.Remove(currency);
        await context.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Every reason the currency cannot be hard-deleted, one explaining clause per blocker class
    /// (issue #241).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Each clause corresponds to one of the <c>RESTRICT</c> foreign keys pointing at
    /// <c>Currencies</c>. Those keys are the backstop on MariaDB, but their violation reaches
    /// <c>GlobalExceptionHandler</c> as a generic 409 naming no surface — and the EF InMemory tiers
    /// enforce no foreign keys at all, so there the delete would simply succeed and strand every
    /// account, transaction, budget and statement recorded in it. Before the keys existed that was
    /// also what happened in production. This pre-check is what makes the refusal explain itself,
    /// and what makes it happen on every tier.
    /// </para>
    /// <para>
    /// Every class is counted before any is reported, so a currency blocked by several names them
    /// all. Adding a currency-code column to the schema means adding its key AND its clause here.
    /// <c>FileAnalysisCandidateTransactions.Currency</c> is deliberately absent: it holds unvetted
    /// extraction output that the import validates before it becomes a transaction, so it carries no
    /// key and does not pin a currency.
    /// </para>
    /// <para>
    /// Each clause names a COUNT and never the blocking records: naming them would reach past
    /// <c>currencies.delete</c>'s own boundary.
    /// </para>
    /// </remarks>
    private async Task<List<string>> CountDeleteBlockers(string code, CancellationToken cancellationToken)
    {
        var blockers = new List<string>();

        void Add(int count, string singular, string plural, string remedy)
        {
            if (count > 0)
            {
                blockers.Add($"This currency is used by {count} {(count == 1 ? singular : plural)}. {remedy}");
            }
        }

        Add(
            await context.Accounts.CountAsync(a => a.CurrencyCode == code, cancellationToken),
            "account", "accounts", "Change or delete those accounts first.");
        Add(
            await context.Transactions.CountAsync(t => t.CurrencyCode == code, cancellationToken),
            "transaction", "transactions", "Change or delete those transactions first.");
        Add(
            await context.Budgets.CountAsync(b => b.BaseCurrencyCode == code, cancellationToken),
            "budget", "budgets", "Change or delete those budgets first.");
        Add(
            await context.TaxStatements.CountAsync(s => s.BaseCurrencyCode == code, cancellationToken),
            "tax statement", "tax statements", "Change or delete those statements first.");
        Add(
            await context.Properties.CountAsync(p => p.CurrencyCode == code, cancellationToken),
            "property", "properties", "Change or delete those properties first.");
        Add(
            await context.AccountEstimates.CountAsync(e => e.CurrencyCode == code, cancellationToken),
            "account estimate", "account estimates", "Delete those estimates first.");
        Add(
            await context.PropertyEstimates.CountAsync(e => e.CurrencyCode == code, cancellationToken),
            "property estimate", "property estimates", "Delete those estimates first.");
        Add(
            await context.Terms.CountAsync(t => t.CurrencyCode == code, cancellationToken),
            "contract term", "contract terms", "Change or delete those terms first.");
        Add(
            await context.ExchangeRates.CountAsync(
                r => r.FromCurrencyCode == code || r.ToCurrencyCode == code, cancellationToken),
            "exchange rate", "exchange rates", "Delete those rates first.");

        return blockers;
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
