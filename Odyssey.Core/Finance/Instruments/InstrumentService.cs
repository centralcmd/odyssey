using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Odyssey.Context;
using Odyssey.Core.Finance.Instruments.Sources;
using Odyssey.Core.Pagination;
using Odyssey.Dtos;
using Odyssey.Dtos.Finance;

namespace Odyssey.Core.Finance.Instruments;

/// <summary>
/// The instrument watchlist (issue #446 §5 endpoints 1–7) — the counterpart of the currency list. Shared
/// single-tenant data: authorization is the controller's claims; this service decides only what the caller's
/// <c>instrument-prices.read</c> lets a read include.
/// </summary>
public sealed class InstrumentService(
    OdysseyContext context,
    InstrumentPriceSourceCatalogue sources,
    TimeProvider? injectedTimeProvider = null,
    ILogger<InstrumentService>? injectedLogger = null)
{
    private readonly TimeProvider timeProvider = injectedTimeProvider ?? TimeProvider.System;
    private readonly ILogger<InstrumentService> logger = injectedLogger ?? NullLogger<InstrumentService>.Instance;

    private DateTime Now => timeProvider.GetUtcNow().UtcDateTime;

    public async Task<PagedResult<ExistingInstrument>> ListAsync(
        InstrumentsQueryParams query, bool canReadPrices, CancellationToken cancellationToken = default)
    {
        if (query.SortBy == InstrumentSortBy.LatestDate && !canReadPrices)
        {
            // The order alone would rank the instruments by price recency (the sortBy=Value precedent).
            throw new DomainForbiddenException("Sorting by the latest price date requires permission to read prices.");
        }

        var q = context.Instruments.AsNoTracking();
        if (!query.IncludeArchived)
        {
            q = q.Where(instrument => instrument.Archived == null);
        }

        var term = ListQuery.NormalizeSearch(query.Search);
        if (term is not null)
        {
            var pattern = ListQuery.ContainsPattern(term);
            q = q.Where(instrument =>
                EF.Functions.Like(instrument.Symbol, pattern)
                || EF.Functions.Like(instrument.Name, pattern)
                || (instrument.Isin != null && EF.Functions.Like(instrument.Isin, pattern)));
        }

        if (query.Types is { Length: > 0 } types)
        {
            var wanted = types.Where(Enum.IsDefined).Distinct().ToList();
            q = q.Where(instrument => wanted.Contains(instrument.Type));
        }

        var currencies = query.CurrencyCodes?
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Select(code => code.Trim().ToUpperInvariant())
            .ToList();
        if (currencies is { Count: > 0 })
        {
            q = q.Where(instrument => currencies.Contains(instrument.CurrencyCode));
        }

        var ascending = ListQuery.Ascending(query.SortDir, naturalDefaultAscending: query.SortBy is not InstrumentSortBy.LatestDate);
        var prices = context.InstrumentPrices;
        IOrderedQueryable<Instrument> sorted = query.SortBy switch
        {
            InstrumentSortBy.Name => ascending ? q.OrderBy(i => i.Name) : q.OrderByDescending(i => i.Name),
            InstrumentSortBy.Type => ascending ? q.OrderBy(i => i.Type) : q.OrderByDescending(i => i.Type),
            InstrumentSortBy.Currency => ascending ? q.OrderBy(i => i.CurrencyCode) : q.OrderByDescending(i => i.CurrencyCode),
            InstrumentSortBy.LatestDate => ascending
                ? q.OrderBy(i => prices.Where(p => p.InstrumentId == i.InstrumentId).Max(p => (DateOnly?)p.TradingDate))
                : q.OrderByDescending(i => prices.Where(p => p.InstrumentId == i.InstrumentId).Max(p => (DateOnly?)p.TradingDate)),
            _ => ascending ? q.OrderBy(i => i.Symbol) : q.OrderByDescending(i => i.Symbol),
        };

        var page = await sorted.ThenBy(i => i.Symbol).ThenBy(i => i.InstrumentId)
            .ToPagedResultAsync(query.Offset, query.Limit, i => i, cancellationToken);

        return new PagedResult<ExistingInstrument>
        {
            Items = await ToDtosAsync(page.Items, canReadPrices, cancellationToken),
            Offset = page.Offset,
            Limit = page.Limit,
            TotalCount = page.TotalCount,
        };
    }

    public async Task<ExistingInstrument?> GetAsync(Guid instrumentId, bool canReadPrices, CancellationToken cancellationToken = default)
    {
        var instrument = await context.Instruments.AsNoTracking()
            .FirstOrDefaultAsync(i => i.InstrumentId == instrumentId, cancellationToken);
        return instrument is null ? null : (await ToDtosAsync([instrument], canReadPrices, cancellationToken))[0];
    }

    public async Task<InstrumentSummary> GetSummaryAsync(bool canReadPrices, CancellationToken cancellationToken = default)
    {
        var all = context.Instruments.AsNoTracking();
        var byType = await all.Where(i => i.Archived == null)
            .GroupBy(i => i.Type)
            .Select(g => new InstrumentTypeCount { Type = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);

        var summary = new InstrumentSummary
        {
            TotalCount = await all.CountAsync(i => i.Archived == null, cancellationToken),
            FollowedCount = await all.CountAsync(i => i.Archived == null && i.ShowOnDashboard, cancellationToken),
            ArchivedCount = await all.CountAsync(i => i.Archived != null, cancellationToken),
            ByType = byType.OrderBy(t => t.Type).ToList(),
        };

        if (canReadPrices)
        {
            summary.NewestPriceDate = await context.InstrumentPrices.AsNoTracking()
                .MaxAsync(p => (DateOnly?)p.TradingDate, cancellationToken);
            var keys = await context.InstrumentPrices.AsNoTracking()
                .Where(p => p.SourceKey != null)
                .Select(p => p.SourceKey!)
                .Distinct()
                .ToListAsync(cancellationToken);
            summary.Attributions = Attributions(keys);
        }

        return summary;
    }

    public async Task<ExistingInstrument> CreateAsync(NewInstrument request, string? userId, CancellationToken cancellationToken = default)
    {
        var fields = await NormalizeAsync(request, null, cancellationToken);
        await EnsureNoDuplicateAsync(fields.ListingKey, null, cancellationToken);

        var now = Now;
        var instrument = new Instrument
        {
            InstrumentId = Guid.NewGuid(),
            Symbol = fields.Symbol,
            MicCode = fields.Mic,
            Isin = fields.Isin,
            Name = request.Name.Trim(),
            Type = fields.Type,
            CurrencyCode = fields.Currency,
            ListingKey = fields.ListingKey,
            ShowOnDashboard = request.ShowOnDashboard,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            CreatedByUserId = userId,
            UpdatedByUserId = userId,
        };
        context.Instruments.Add(instrument);
        await SaveMappingDuplicateAsync(fields.ListingKey, cancellationToken);
        logger.LogInformation("Instrument {InstrumentId} created by {UserId}.", instrument.InstrumentId, userId);
        return (await ToDtosAsync([instrument], canReadPrices: true, cancellationToken))[0];
    }

    public async Task<ExistingInstrument?> UpdateAsync(
        Guid instrumentId, NewInstrument request, string? userId, CancellationToken cancellationToken = default)
    {
        var storedCurrency = await context.Instruments.AsNoTracking()
            .Where(i => i.InstrumentId == instrumentId)
            .Select(i => i.CurrencyCode)
            .FirstOrDefaultAsync(cancellationToken);
        var fields = await NormalizeAsync(request, storedCurrency, cancellationToken);
        var updated = await InstrumentLocks.InTransactionAsync(context, async () =>
        {
            await InstrumentLocks.LockAsync(context, instrumentId, cancellationToken);
            var instrument = await context.Instruments.FirstOrDefaultAsync(i => i.InstrumentId == instrumentId, cancellationToken);
            if (instrument is null)
            {
                return null;
            }

            if (await IsIdentityLockedAsync(instrumentId, cancellationToken))
            {
                EnsureIdentityUnchanged(instrument, fields);
            }

            await EnsureNoDuplicateAsync(fields.ListingKey, instrumentId, cancellationToken);
            instrument.Symbol = fields.Symbol;
            instrument.MicCode = fields.Mic;
            instrument.Isin = fields.Isin;
            instrument.Type = fields.Type;
            instrument.CurrencyCode = fields.Currency;
            instrument.ListingKey = fields.ListingKey;
            instrument.Name = request.Name.Trim();
            instrument.ShowOnDashboard = request.ShowOnDashboard;
            instrument.UpdatedAtUtc = Now;
            instrument.UpdatedByUserId = userId;
            await SaveMappingDuplicateAsync(fields.ListingKey, cancellationToken);
            return instrument;
        }, cancellationToken);

        if (updated is null)
        {
            return null;
        }

        logger.LogInformation("Instrument {InstrumentId} updated by {UserId}.", instrumentId, userId);
        return (await ToDtosAsync([updated], canReadPrices: true, cancellationToken))[0];
    }

    public async Task<bool> SetArchivedAsync(Guid instrumentId, bool archived, string? userId, CancellationToken cancellationToken = default)
    {
        var instrument = await context.Instruments.FirstOrDefaultAsync(i => i.InstrumentId == instrumentId, cancellationToken);
        if (instrument is null)
        {
            return false;
        }

        if (archived != (instrument.Archived is not null))
        {
            var now = Now;
            instrument.Archived = archived ? now : null;
            instrument.UpdatedAtUtc = now;
            instrument.UpdatedByUserId = userId;
            await context.SaveChangesAsync(cancellationToken);
            logger.LogInformation("Instrument {InstrumentId} {Action} by {UserId}.",
                instrumentId, archived ? "archived" : "unarchived", userId);
        }

        return true;
    }

    public async Task<bool> DeleteAsync(Guid instrumentId, string? userId, CancellationToken cancellationToken = default)
    {
        // The count and the remove run under the instrument's row lock — the lock every price insert (manual and
        // fetched) takes first — so a price committed between them cannot reach the RESTRICT key as a raw 500.
        var deleted = await InstrumentLocks.InTransactionAsync(context, async () =>
        {
            var instrument = await context.Instruments.FirstOrDefaultAsync(i => i.InstrumentId == instrumentId, cancellationToken);
            if (instrument is null)
            {
                return false;
            }

            await InstrumentLocks.LockAsync(context, instrumentId, cancellationToken);
            var priceCount = await context.InstrumentPrices.CountAsync(p => p.InstrumentId == instrumentId, cancellationToken);
            if (priceCount > 0)
            {
                // priceCount is safe to return: every instruments.delete holder also reads prices (§7.2 invariant).
                throw new DomainCodedConflictException(
                    $"This instrument has {priceCount} prices. Delete its prices first.",
                    InstrumentProblemCodes.InstrumentHasPrices,
                    values: new Dictionary<string, object?> { ["priceCount"] = priceCount });
            }

            // Bindings cascade in the database; removed here too so the InMemory tiers behave the same.
            var bindings = await context.InstrumentPriceSourceBindings
                .Where(b => b.InstrumentId == instrumentId)
                .ToListAsync(cancellationToken);
            context.InstrumentPriceSourceBindings.RemoveRange(bindings);
            context.Instruments.Remove(instrument);
            await context.SaveChangesAsync(cancellationToken);
            return true;
        }, cancellationToken);

        if (deleted)
        {
            logger.LogInformation("Instrument {InstrumentId} deleted by {UserId}.", instrumentId, userId);
        }

        return deleted;
    }

    /// <summary>Plain-text attributions of the registered sources among <paramref name="sourceKeys"/>.</summary>
    internal List<string> Attributions(IEnumerable<string> sourceKeys) =>
        sourceKeys.Select(sources.Find)
            .Where(source => source is not null)
            .Select(source => source!.Attribution)
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(text => text, StringComparer.Ordinal)
            .ToList();

    private async Task<bool> IsIdentityLockedAsync(Guid instrumentId, CancellationToken cancellationToken) =>
        await context.InstrumentPrices.AnyAsync(p => p.InstrumentId == instrumentId, cancellationToken)
        || await context.InstrumentPriceSourceBindings.AnyAsync(b => b.InstrumentId == instrumentId, cancellationToken);

    private static void EnsureIdentityUnchanged(Instrument instrument, NormalizedFields fields)
    {
        const string Locked = "This field is locked because the instrument has prices or sources.";
        if (!string.Equals(instrument.Symbol, fields.Symbol, StringComparison.Ordinal))
        {
            throw new DomainUnprocessableException(Locked, nameof(NewInstrument.Symbol));
        }

        if (!string.Equals(instrument.MicCode, fields.Mic, StringComparison.Ordinal))
        {
            throw new DomainUnprocessableException(Locked, nameof(NewInstrument.MicCode));
        }

        if (!string.Equals(instrument.Isin, fields.Isin, StringComparison.Ordinal))
        {
            throw new DomainUnprocessableException(Locked, nameof(NewInstrument.Isin));
        }

        if (instrument.Type != fields.Type)
        {
            throw new DomainUnprocessableException(Locked, nameof(NewInstrument.Type));
        }

        if (!string.Equals(instrument.CurrencyCode, fields.Currency, StringComparison.Ordinal))
        {
            throw new DomainUnprocessableException(Locked, nameof(NewInstrument.CurrencyCode));
        }
    }

    private async Task EnsureNoDuplicateAsync(string listingKey, Guid? exceptId, CancellationToken cancellationToken)
    {
        var existing = await context.Instruments.AsNoTracking()
            .Where(i => i.ListingKey == listingKey && (exceptId == null || i.InstrumentId != exceptId))
            .Select(i => (Guid?)i.InstrumentId)
            .FirstOrDefaultAsync(cancellationToken);
        if (existing is { } id)
        {
            throw Duplicate(id);
        }
    }

    private async Task SaveMappingDuplicateAsync(string listingKey, CancellationToken cancellationToken)
    {
        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (InstrumentDbErrors.IsDuplicateKey(exception))
        {
            var existing = await context.Instruments.AsNoTracking()
                .Where(i => i.ListingKey == listingKey)
                .Select(i => i.InstrumentId)
                .FirstOrDefaultAsync(CancellationToken.None);
            throw Duplicate(existing);
        }
    }

    private static DomainCodedConflictException Duplicate(Guid existingId) => new(
        "This instrument already exists.",
        InstrumentProblemCodes.InstrumentDuplicate,
        nameof(NewInstrument.Symbol),
        new Dictionary<string, object?> { ["existingInstrumentId"] = existingId });

    private sealed record NormalizedFields(string Symbol, string? Mic, string? Isin, InstrumentType Type, string Currency, string ListingKey);

    /// <param name="storedCurrency">The instrument's current currency on an update: keeping it is allowed after it was archived.</param>
    private async Task<NormalizedFields> NormalizeAsync(NewInstrument request, string? storedCurrency, CancellationToken cancellationToken)
    {
        var type = request.Type ?? throw new DomainValidationException("Type is required.", null, nameof(NewInstrument.Type));
        var symbol = InstrumentIdentifierRules.Normalize(request.Symbol)
            ?? throw new DomainValidationException("Symbol is required.", null, nameof(NewInstrument.Symbol));
        var mic = InstrumentIdentifierRules.Normalize(request.MicCode);
        var isin = InstrumentIdentifierRules.Normalize(request.Isin);
        var currency = request.CurrencyCode.Trim().ToUpperInvariant();

        if (type is InstrumentType.Stock or InstrumentType.Etf && mic is null)
        {
            throw new DomainUnprocessableException(
                "Enter the 4-character exchange code, e.g. XOSL for Oslo Børs or XNAS for Nasdaq.", nameof(NewInstrument.MicCode));
        }

        if (type == InstrumentType.Fund && isin is null)
        {
            throw new DomainUnprocessableException("A fund needs its ISIN.", nameof(NewInstrument.Isin));
        }

        if (isin is not null && !InstrumentIdentifierRules.IsValidIsinCheckDigit(isin))
        {
            throw new DomainUnprocessableException(
                "This ISIN's check digit doesn't match. Check the last character.", nameof(NewInstrument.Isin));
        }

        if (mic is null && isin is null)
        {
            throw new DomainUnprocessableException(
                "Enter an exchange code or an ISIN so the instrument can be told apart.", nameof(NewInstrument.MicCode));
        }

        var keepsArchivedCurrency = string.Equals(currency, storedCurrency, StringComparison.Ordinal);
        if (!await context.Currencies.AnyAsync(c => c.CurrencyCode == currency && (c.Archived == null || keepsArchivedCurrency), cancellationToken))
        {
            throw new DomainUnprocessableException($"Currency '{currency}' is not available.", nameof(NewInstrument.CurrencyCode));
        }

        return new NormalizedFields(symbol, mic, isin, type, currency,
            InstrumentIdentifierRules.ListingKey(symbol, mic, isin, currency));
    }

    private async Task<List<ExistingInstrument>> ToDtosAsync(
        IReadOnlyList<Instrument> instruments, bool canReadPrices, CancellationToken cancellationToken)
    {
        var ids = instruments.Select(i => i.InstrumentId).ToList();
        if (ids.Count == 0)
        {
            return [];
        }

        var counts = await context.InstrumentPrices.AsNoTracking()
            .Where(p => ids.Contains(p.InstrumentId))
            .GroupBy(p => p.InstrumentId)
            .Select(g => new { InstrumentId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.InstrumentId, g => g.Count, cancellationToken);
        var bindings = await context.InstrumentPriceSourceBindings.AsNoTracking()
            .Where(b => ids.Contains(b.InstrumentId))
            .ToListAsync(cancellationToken);

        Dictionary<Guid, InstrumentPrice> latest = [];
        if (canReadPrices)
        {
            latest = await InstrumentPriceReader.LatestAsync(
                context, ids, InstrumentPriceOrdering.PriorityLookup(bindings), cancellationToken);
        }

        return instruments.Select(instrument =>
        {
            var id = instrument.InstrumentId;
            var count = counts.GetValueOrDefault(id);
            var hasBinding = bindings.Any(b => b.InstrumentId == id);
            latest.TryGetValue(id, out var newest);
            return new ExistingInstrument
            {
                InstrumentId = id,
                Symbol = instrument.Symbol,
                MicCode = instrument.MicCode,
                Isin = instrument.Isin,
                Name = instrument.Name,
                Type = instrument.Type,
                CurrencyCode = instrument.CurrencyCode,
                ShowOnDashboard = instrument.ShowOnDashboard,
                Archived = instrument.Archived,
                LatestClose = canReadPrices ? newest?.Close : null,
                LatestTradingDate = canReadPrices ? newest?.TradingDate : null,
                PriceCount = canReadPrices ? count : null,
                IdentityLocked = count > 0 || hasBinding,
                HasEnabledSource = bindings.Any(b => b.InstrumentId == id && b.Enabled),
                CreatedAtUtc = instrument.CreatedAtUtc,
                UpdatedAtUtc = instrument.UpdatedAtUtc,
            };
        }).ToList();
    }
}
