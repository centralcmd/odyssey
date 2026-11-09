using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Odyssey.Context;
using Odyssey.Core.Finance.Instruments.Sources;
using Odyssey.Core.Pagination;
using Odyssey.Dtos;
using Odyssey.Dtos.Application;
using Odyssey.Dtos.Finance;

namespace Odyssey.Core.Finance.Instruments;

/// <summary>
/// Instrument closing prices (issue #446 §5 endpoints 8–14) — the counterpart of <see cref="ExchangeRateService"/>.
/// A manual row (<c>SourceKey</c> null) can be corrected and deleted; a fetched row is immutable and leaves only
/// through the purge, which holds the sync's run guard. Values are accepted as entered (no plausibility check).
/// </summary>
public sealed class InstrumentPriceService(
    OdysseyContext context,
    InstrumentPriceSourceCatalogue sources,
    TimeProvider? injectedTimeProvider = null,
    ILogger<InstrumentPriceService>? injectedLogger = null)
{
    /// <summary>Batch size of the purge's tracked deletes (the exchange-rate purge's).</summary>
    public const int PurgeBatchSize = 5000;

    /// <summary>The longest series a single request may cover.</summary>
    public const int MaxSeriesDays = 3660;

    private readonly TimeProvider timeProvider = injectedTimeProvider ?? TimeProvider.System;
    private readonly ILogger<InstrumentPriceService> logger = injectedLogger ?? NullLogger<InstrumentPriceService>.Instance;

    private DateTime Now => timeProvider.GetUtcNow().UtcDateTime;

    public async Task<PagedResult<ExistingInstrumentPrice>> ListAsync(
        InstrumentPricesQueryParams query, CancellationToken cancellationToken = default)
    {
        var q = context.InstrumentPrices.AsNoTracking();

        if (query.InstrumentIds is { Length: > 0 } ids)
        {
            var wanted = ids.Distinct().ToList();
            q = q.Where(p => wanted.Contains(p.InstrumentId));
        }

        if (query.Sources is { Length: > 0 } kinds)
        {
            var manual = kinds.Contains(InstrumentPriceSource.Manual);
            var fetched = kinds.Contains(InstrumentPriceSource.Fetched);
            q = q.Where(p => (manual && p.SourceKey == null) || (fetched && p.SourceKey != null));
        }

        if (query.From is { } from)
        {
            q = q.Where(p => p.TradingDate >= from);
        }

        if (query.To is { } to)
        {
            q = q.Where(p => p.TradingDate <= to);
        }

        var term = ListQuery.NormalizeSearch(query.Search);
        if (term is not null)
        {
            var pattern = ListQuery.ContainsPattern(term);
            q = q.Where(p => context.Instruments.Any(i => i.InstrumentId == p.InstrumentId
                && (EF.Functions.Like(i.Symbol, pattern) || EF.Functions.Like(i.Name, pattern))));
        }

        // Status is relative to each instrument's winner. The winners are resolved once, for the instruments the
        // filtered rows belong to, and the filter becomes an id set (the CurrentIdsAsync shape of ExchangeRateService).
        if (query.Status is { Length: > 0 } statuses)
        {
            var wantCurrent = statuses.Contains(InstrumentPriceStatus.Current);
            var wantHistorical = statuses.Contains(InstrumentPriceStatus.Historical);
            if (wantCurrent != wantHistorical)
            {
                var instrumentIds = await q.Select(p => p.InstrumentId).Distinct().ToListAsync(cancellationToken);
                var current = await CurrentIdsAsync(instrumentIds, cancellationToken);
                q = wantCurrent ? q.Where(p => current.Contains(p.InstrumentPriceId)) : q.Where(p => !current.Contains(p.InstrumentPriceId));
            }
            else if (!wantCurrent)
            {
                q = q.Where(_ => false);
            }
        }

        var ascending = ListQuery.Ascending(query.SortDir, naturalDefaultAscending: query.SortBy is InstrumentPriceSortBy.Instrument);
        IOrderedQueryable<InstrumentPrice> sorted = query.SortBy switch
        {
            InstrumentPriceSortBy.Instrument => ascending
                ? q.OrderBy(p => context.Instruments.Where(i => i.InstrumentId == p.InstrumentId).Select(i => i.Symbol).FirstOrDefault())
                : q.OrderByDescending(p => context.Instruments.Where(i => i.InstrumentId == p.InstrumentId).Select(i => i.Symbol).FirstOrDefault()),
            InstrumentPriceSortBy.Close => ascending ? q.OrderBy(p => p.Close) : q.OrderByDescending(p => p.Close),
            InstrumentPriceSortBy.CreatedAt => ascending ? q.OrderBy(p => p.CreatedAtUtc) : q.OrderByDescending(p => p.CreatedAtUtc),
            _ => ascending ? q.OrderBy(p => p.TradingDate) : q.OrderByDescending(p => p.TradingDate),
        };

        var page = await sorted.ThenByDescending(p => p.TradingDate).ThenBy(p => p.InstrumentPriceId)
            .ToPagedResultAsync(query.Offset, query.Limit, p => p, cancellationToken);

        return new PagedResult<ExistingInstrumentPrice>
        {
            Items = await ToDtosAsync(page.Items, cancellationToken),
            Offset = page.Offset,
            Limit = page.Limit,
            TotalCount = page.TotalCount,
        };
    }

    public async Task<ExistingInstrumentPrice?> GetAsync(Guid priceId, CancellationToken cancellationToken = default)
    {
        var price = await context.InstrumentPrices.AsNoTracking().FirstOrDefaultAsync(p => p.InstrumentPriceId == priceId, cancellationToken);
        return price is null ? null : (await ToDtosAsync([price], cancellationToken))[0];
    }

    public async Task<ExistingInstrumentPrice> CreateAsync(NewInstrumentPrice request, string? userId, CancellationToken cancellationToken = default)
    {
        var instrumentId = request.InstrumentId!.Value;
        var tradingDate = request.TradingDate!.Value;
        var close = request.Close!.Value;
        EnsureNotFuture(tradingDate);

        var price = await InstrumentLocks.InTransactionAsync(context, async () =>
        {
            await InstrumentLocks.LockAsync(context, instrumentId, cancellationToken);
            var instrument = await context.Instruments.AsNoTracking().FirstOrDefaultAsync(i => i.InstrumentId == instrumentId, cancellationToken)
                ?? throw new DomainNotFoundException($"Instrument {instrumentId} not found.", nameof(NewInstrumentPrice.InstrumentId));
            if (instrument.Archived is not null)
            {
                throw new DomainUnprocessableException("Prices cannot be added to an archived instrument.", nameof(NewInstrumentPrice.InstrumentId));
            }

            await EnsureNoManualDuplicateAsync(instrumentId, tradingDate, null, cancellationToken);
            var row = new InstrumentPrice
            {
                InstrumentPriceId = Guid.NewGuid(),
                InstrumentId = instrumentId,
                TradingDate = tradingDate,
                Close = close,
                CreatedAtUtc = Now,
                CreatedByUserId = userId,
            };
            context.InstrumentPrices.Add(row);
            await SaveMappingManualDuplicateAsync(instrumentId, tradingDate, cancellationToken);
            return row;
        }, cancellationToken);

        logger.LogInformation("Manual price {PriceId} for instrument {InstrumentId} created by {UserId}.", price.InstrumentPriceId, instrumentId, userId);
        return (await ToDtosAsync([price], cancellationToken))[0];
    }

    public async Task<ExistingInstrumentPrice?> UpdateAsync(
        Guid priceId, UpdateInstrumentPrice request, string? userId, CancellationToken cancellationToken = default)
    {
        var tradingDate = request.TradingDate!.Value;
        var close = request.Close!.Value;
        EnsureNotFuture(tradingDate);

        var price = await context.InstrumentPrices.FirstOrDefaultAsync(p => p.InstrumentPriceId == priceId, cancellationToken);
        if (price is null)
        {
            return null;
        }

        EnsureManual(price);
        if (price.TradingDate != tradingDate)
        {
            await EnsureNoManualDuplicateAsync(price.InstrumentId, tradingDate, priceId, cancellationToken);
        }

        price.TradingDate = tradingDate;
        price.Close = close;
        price.UpdatedAtUtc = Now;
        await SaveMappingManualDuplicateAsync(price.InstrumentId, tradingDate, cancellationToken);
        logger.LogInformation("Manual price {PriceId} updated by {UserId}.", priceId, userId);
        return (await ToDtosAsync([price], cancellationToken))[0];
    }

    public async Task<bool> DeleteAsync(Guid priceId, string? userId, CancellationToken cancellationToken = default)
    {
        var price = await context.InstrumentPrices.FirstOrDefaultAsync(p => p.InstrumentPriceId == priceId, cancellationToken);
        if (price is null)
        {
            return false;
        }

        EnsureManual(price);
        context.InstrumentPrices.Remove(price);
        await context.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Manual price {PriceId} deleted by {UserId}.", priceId, userId);
        return true;
    }

    public async Task<InstrumentPriceSeries?> GetSeriesAsync(
        Guid instrumentId, DateOnly? from, DateOnly? to, CancellationToken cancellationToken = default)
    {
        var instrument = await context.Instruments.AsNoTracking().FirstOrDefaultAsync(i => i.InstrumentId == instrumentId, cancellationToken);
        if (instrument is null)
        {
            return null;
        }

        var end = to ?? OsloDate.Today(timeProvider);
        var start = from ?? end.AddDays(-365);
        if (start > end)
        {
            throw new DomainValidationException("The start date must be on or before the end date.", null, "from");
        }

        if (end.DayNumber - start.DayNumber > MaxSeriesDays)
        {
            throw new DomainValidationException($"A series covers at most {MaxSeriesDays} days.", null, "from");
        }

        var rows = await context.InstrumentPrices.AsNoTracking()
            .Where(p => p.InstrumentId == instrumentId && p.TradingDate >= start && p.TradingDate <= end)
            .ToListAsync(cancellationToken);
        var priorities = await InstrumentPriceReader.PrioritiesAsync(context, [instrumentId], cancellationToken);

        return new InstrumentPriceSeries
        {
            CurrencyCode = instrument.CurrencyCode,
            Points = InstrumentPriceOrdering.WinnersPerDate(rows, priorities)
                .Select(p => new InstrumentPricePoint
                {
                    Date = p.TradingDate,
                    Close = p.Close,
                    Source = p.SourceKey is null ? InstrumentPriceSource.Manual : InstrumentPriceSource.Fetched,
                })
                .ToList(),
        };
    }

    /// <summary>The number of prices a purge of <paramref name="scope"/> would delete; what the client confirms against.</summary>
    public Task<int> CountAsync(Guid instrumentId, InstrumentPricePurgeScope scope, CancellationToken cancellationToken = default) =>
        Scoped(context.InstrumentPrices.AsNoTracking(), instrumentId, scope).CountAsync(cancellationToken);

    /// <summary>
    /// Deletes an instrument's prices in <paramref name="scope"/> (issue #446 §5 endpoint 13). Holds the sync's run
    /// guard through a purge run row, locks the instrument, refuses unless <paramref name="expectedCount"/> equals the
    /// rows it would delete, deletes in tracked batches and resets the bindings' watermarks — all in one transaction.
    /// </summary>
    public async Task<int> PurgeAsync(
        Guid instrumentId, InstrumentPricePurgeScope scope, int expectedCount, string? userId,
        CancellationToken cancellationToken = default)
    {
        if (!await context.Instruments.AnyAsync(i => i.InstrumentId == instrumentId, cancellationToken))
        {
            throw new DomainNotFoundException($"Instrument {instrumentId} not found.");
        }

        var run = await StartPurgeRunAsync(userId, cancellationToken);
        var deleted = 0;
        var completed = false;
        try
        {
            deleted = await InstrumentLocks.InTransactionAsync(context, async () =>
            {
                context.ChangeTracker.Clear();
                await InstrumentLocks.LockAsync(context, instrumentId, cancellationToken);
                var actual = await Scoped(context.InstrumentPrices, instrumentId, scope).CountAsync(cancellationToken);
                if (actual != expectedCount)
                {
                    throw new DomainCodedConflictException(
                        $"The number of prices changed to {actual}. Confirm the new number.",
                        InstrumentProblemCodes.PurgeCountMismatch,
                        "expectedCount",
                        new Dictionary<string, object?> { ["actualCount"] = actual });
                }

                var removed = 0;
                while (true)
                {
                    var batch = await Scoped(context.InstrumentPrices, instrumentId, scope)
                        .OrderBy(p => p.InstrumentPriceId)
                        .Take(PurgeBatchSize)
                        .ToListAsync(cancellationToken);
                    if (batch.Count == 0)
                    {
                        break;
                    }

                    context.InstrumentPrices.RemoveRange(batch);
                    await context.SaveChangesAsync(cancellationToken);
                    context.ChangeTracker.Clear();
                    removed += batch.Count;
                }

                if (scope is InstrumentPricePurgeScope.Fetched or InstrumentPricePurgeScope.All)
                {
                    var bindings = await context.InstrumentPriceSourceBindings
                        .Where(b => b.InstrumentId == instrumentId)
                        .ToListAsync(cancellationToken);
                    foreach (var binding in bindings)
                    {
                        binding.NewestFetched = null;
                        binding.BackfilledFrom = null;
                        binding.UpdatedAtUtc = Now;
                        binding.UpdatedByUserId = userId;
                    }

                    await context.SaveChangesAsync(cancellationToken);
                }

                return removed;
            }, cancellationToken);
            completed = true;
        }
        finally
        {
            await FinishPurgeRunAsync(run.JobRunId, deleted, completed);
        }

        logger.LogInformation("Purged {Count} {Scope} prices of instrument {InstrumentId} by {UserId}.", deleted, scope, instrumentId, userId);
        return deleted;
    }

    private static IQueryable<InstrumentPrice> Scoped(IQueryable<InstrumentPrice> prices, Guid instrumentId, InstrumentPricePurgeScope scope)
    {
        var q = prices.Where(p => p.InstrumentId == instrumentId);
        return scope switch
        {
            InstrumentPricePurgeScope.Manual => q.Where(p => p.SourceKey == null),
            InstrumentPricePurgeScope.Fetched => q.Where(p => p.SourceKey != null),
            _ => q,
        };
    }

    private async Task<InstrumentPriceSyncJobRun> StartPurgeRunAsync(string? userId, CancellationToken cancellationToken)
    {
        // The application's pre-check; on MariaDB the ExclusiveGuard unique index is the real guard.
        if (await context.JobRuns.AnyAsync(r => r.JobType == JobType.InstrumentPriceSync && r.Status == JobRunStatus.Running, cancellationToken))
        {
            throw AlreadyRunning();
        }

        var now = Now;
        var run = new InstrumentPriceSyncJobRun
        {
            JobRunId = Guid.NewGuid(),
            Trigger = JobRunTrigger.Purge,
            Status = JobRunStatus.Running,
            StartedAtUtc = now,
            ClaimedAtUtc = now,
            LastActivityAtUtc = now,
            RequestedByUserId = userId,
        };
        context.JobRuns.Add(run);
        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (InstrumentDbErrors.IsDuplicateKey(exception))
        {
            context.Entry(run).State = EntityState.Detached;
            throw AlreadyRunning();
        }

        context.Entry(run).State = EntityState.Detached;
        return run;
    }

    private async Task FinishPurgeRunAsync(Guid runId, int deleted, bool completed)
    {
        context.ChangeTracker.Clear();
        var run = await context.Set<InstrumentPriceSyncJobRun>().FirstOrDefaultAsync(r => r.JobRunId == runId, CancellationToken.None);
        if (run is not { Status: JobRunStatus.Running })
        {
            return;
        }

        run.PricesDeleted = deleted;
        // A refused purge (count mismatch, cancellation) deleted nothing and is recorded as Skipped.
        run.Finish(completed ? JobRunStatus.Succeeded : JobRunStatus.Skipped, null, Now);
        await context.SaveChangesAsync(CancellationToken.None);
    }

    private static DomainCodedConflictException AlreadyRunning() => new(
        "A price sync is running. Try again in a minute.", JobRunConflictCodes.AlreadyRunning);

    private void EnsureNotFuture(DateOnly tradingDate)
    {
        if (tradingDate > OsloDate.Today(timeProvider))
        {
            throw new DomainUnprocessableException("Prices can only be entered for today or earlier.", nameof(NewInstrumentPrice.TradingDate));
        }
    }

    private static void EnsureManual(InstrumentPrice price)
    {
        if (price.SourceKey is not null)
        {
            throw new DomainUnprocessableException(
                "Fetched prices can't be edited or deleted one at a time; delete them with the instrument's purge.", "source");
        }
    }

    private async Task EnsureNoManualDuplicateAsync(Guid instrumentId, DateOnly tradingDate, Guid? exceptId, CancellationToken cancellationToken)
    {
        var existing = await context.InstrumentPrices.AsNoTracking()
            .Where(p => p.InstrumentId == instrumentId && p.TradingDate == tradingDate && p.SourceKey == null
                && (exceptId == null || p.InstrumentPriceId != exceptId))
            .Select(p => (Guid?)p.InstrumentPriceId)
            .FirstOrDefaultAsync(cancellationToken);
        if (existing is { } id)
        {
            throw ManualDuplicate(id);
        }
    }

    private async Task SaveMappingManualDuplicateAsync(Guid instrumentId, DateOnly tradingDate, CancellationToken cancellationToken)
    {
        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (InstrumentDbErrors.IsDuplicateKey(exception))
        {
            var existing = await context.InstrumentPrices.AsNoTracking()
                .Where(p => p.InstrumentId == instrumentId && p.TradingDate == tradingDate && p.SourceKey == null)
                .Select(p => p.InstrumentPriceId)
                .FirstOrDefaultAsync(CancellationToken.None);
            throw ManualDuplicate(existing);
        }
    }

    private static DomainCodedConflictException ManualDuplicate(Guid existingId) => new(
        "A manual price already exists for this date.",
        InstrumentProblemCodes.PriceDuplicate,
        nameof(NewInstrumentPrice.TradingDate),
        new Dictionary<string, object?> { ["existingInstrumentPriceId"] = existingId });

    private async Task<HashSet<Guid>> CurrentIdsAsync(IReadOnlyCollection<Guid> instrumentIds, CancellationToken cancellationToken)
    {
        var priorities = await InstrumentPriceReader.PrioritiesAsync(context, instrumentIds, cancellationToken);
        var latest = await InstrumentPriceReader.LatestAsync(context, instrumentIds, priorities, cancellationToken);
        return latest.Values.Select(p => p.InstrumentPriceId).ToHashSet();
    }

    private async Task<List<ExistingInstrumentPrice>> ToDtosAsync(IReadOnlyList<InstrumentPrice> rows, CancellationToken cancellationToken)
    {
        if (rows.Count == 0)
        {
            return [];
        }

        var instrumentIds = rows.Select(r => r.InstrumentId).Distinct().ToList();
        var instruments = await context.Instruments.AsNoTracking()
            .Where(i => instrumentIds.Contains(i.InstrumentId))
            .ToDictionaryAsync(i => i.InstrumentId, cancellationToken);
        var current = await CurrentIdsAsync(instrumentIds, cancellationToken);

        return rows.Select(row =>
        {
            var instrument = instruments[row.InstrumentId];
            return new ExistingInstrumentPrice
            {
                InstrumentPriceId = row.InstrumentPriceId,
                InstrumentId = row.InstrumentId,
                Symbol = instrument.Symbol,
                InstrumentName = instrument.Name,
                CurrencyCode = instrument.CurrencyCode,
                TradingDate = row.TradingDate,
                Close = row.Close,
                Source = row.SourceKey is null ? InstrumentPriceSource.Manual : InstrumentPriceSource.Fetched,
                SourceKey = row.SourceKey,
                SourceDisplayName = row.SourceKey is null ? null : sources.DisplayNameOf(row.SourceKey),
                Status = current.Contains(row.InstrumentPriceId) ? InstrumentPriceStatus.Current : InstrumentPriceStatus.Historical,
                CreatedAtUtc = row.CreatedAtUtc,
                UpdatedAtUtc = row.UpdatedAtUtc,
            };
        }).ToList();
    }
}
