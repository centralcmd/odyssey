using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Odyssey.Context;
using Odyssey.Core.Finance.ExchangeRateSync.Providers;
using Odyssey.Core.Pagination;
using Odyssey.Dtos;
using Odyssey.Dtos.Finance;

namespace Odyssey.Core.Finance.ExchangeRateSync;

/// <summary>
/// The per-currency sync configuration (issue #349 §5.1, §5.2, §5.9): which currencies are synced, from
/// when, and the purge of one currency's fetched rates. Admin-only through the controller's claims.
/// </summary>
public sealed partial class ExchangeRateSyncCurrencyService(
    OdysseyContext context,
    ExchangeRateSupportedList supportedList,
    IExchangeRateSyncSettingsLookup settingsLookup,
    ExchangeRateSyncService sync,
    TimeProvider timeProvider,
    ILoggerFactory loggerFactory)
{
    /// <summary>
    /// The settings audit category, so a per-currency change lands beside the global settings' lines in
    /// the same format (issue #349 §7.8).
    /// </summary>
    public const string AuditCategory = "Odyssey.Api.SystemSettings.SystemSettingsService";

    private const string Hub = ExchangeRateSyncConstants.BaseCurrencyCode;

    private readonly ILogger audit = loggerFactory.CreateLogger(AuditCategory);

    private DateTime Now => timeProvider.GetUtcNow().UtcDateTime;

    private DateOnly TodayUtc => DateOnly.FromDateTime(Now);

    /// <summary>Every non-archived currency with its sync configuration, left-joined (absent = off).</summary>
    public async Task<PagedResult<ExistingExchangeRateSyncCurrency>> ListAsync(
        ExchangeRateSyncCurrenciesQueryParams query, CancellationToken cancellationToken = default)
    {
        var items = await ProjectAllAsync(cancellationToken);

        IEnumerable<ExistingExchangeRateSyncCurrency> filtered = items;
        if (ListQuery.NormalizeSearch(query.Search) is { } term)
        {
            filtered = filtered.Where(item =>
                item.CurrencyCode.Contains(term, StringComparison.OrdinalIgnoreCase)
                || item.Name.Contains(term, StringComparison.OrdinalIgnoreCase));
        }

        if (query.Enabled is { } enabled)
        {
            filtered = filtered.Where(item => item.Enabled == enabled);
        }

        // supported=true drops only the KNOWN-unsupported, so the degraded state never empties the list.
        filtered = query.Supported switch
        {
            true => filtered.Where(item => item.Supported != false),
            false => filtered.Where(item => item.Supported == false),
            null => filtered,
        };

        var ascending = ListQuery.Ascending(query.SortDir, naturalDefaultAscending: query.SortBy is not ExchangeRateSyncCurrencySortBy.Enabled);
        var ordered = query.SortBy switch
        {
            ExchangeRateSyncCurrencySortBy.Name => ascending
                ? filtered.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                : filtered.OrderByDescending(item => item.Name, StringComparer.OrdinalIgnoreCase),
            ExchangeRateSyncCurrencySortBy.Enabled => ascending
                ? filtered.OrderBy(item => item.Enabled)
                : filtered.OrderByDescending(item => item.Enabled),
            _ => ascending
                ? filtered.OrderBy(item => item.CurrencyCode, StringComparer.Ordinal)
                : filtered.OrderByDescending(item => item.CurrencyCode, StringComparer.Ordinal),
        };
        var all = ordered.ThenBy(item => item.CurrencyCode, StringComparer.Ordinal).ToList();

        var (offset, limit) = ListQuery.ResolveWindow(query.Offset, query.Limit);
        var page = all.Skip(offset).Take(limit).ToList();

        await FillFetchedRowCountsAsync(page, cancellationToken);

        return new PagedResult<ExistingExchangeRateSyncCurrency>
        {
            Items = page,
            Offset = offset,
            Limit = limit,
            TotalCount = all.Count,
        };
    }

    /// <summary>
    /// The sync projection of every non-archived currency, keyed nowhere and unpaged: the currencies list
    /// derives every row's status from it, and attaches it for a caller allowed to see it.
    /// <see cref="ExistingExchangeRateSyncCurrency.FetchedRowCount"/> is left at 0;
    /// <see cref="FillFetchedRowCountsAsync"/> fills it for the rows actually returned.
    /// </summary>
    /// <param name="withExtents">
    /// Whether to read the fetched date extents (the coverage), which scans the fetched rates. The status
    /// needs none of it, so a caller who gets only the status skips it.
    /// </param>
    public async Task<List<ExistingExchangeRateSyncCurrency>> ProjectAllAsync(
        CancellationToken cancellationToken = default, bool withExtents = true)
    {
        var currencies = await context.Currencies.AsNoTracking()
            .Where(currency => currency.Archived == null)
            .Select(currency => new { currency.CurrencyCode, currency.Name })
            .ToListAsync(cancellationToken);
        var configured = await context.ExchangeRateSyncCurrencies.AsNoTracking()
            .ToDictionaryAsync(row => row.CurrencyCode, StringComparer.Ordinal, cancellationToken);

        // Every currency list — every currency picker — derives its statuses from this, so it reads only the
        // stored list and never waits on the provider. Null until a first list is stored: an unknown list,
        // which marks nothing as not published.
        var series = (await supportedList.ReadAsync(cancellationToken))?.Series;
        var settings = await settingsLookup.GetAsync(cancellationToken);
        // Only a currency that was ever configured can have fetched rows.
        var extents = withExtents
            ? await sync.FetchedExtentsAsync(ExchangeRateSyncClock.Oslo, configured.Keys.ToList(), cancellationToken)
            : new Dictionary<string, ExchangeRateSyncService.FetchedExtent>();

        return currencies
            .Select(currency => Project(
                currency.CurrencyCode, currency.Name, configured.GetValueOrDefault(currency.CurrencyCode),
                series, settings, extents))
            .ToList();
    }

    /// <summary>What a purge of each would delete: computed for the given rows only, since it counts rates.</summary>
    public async Task FillFetchedRowCountsAsync(
        IReadOnlyCollection<ExistingExchangeRateSyncCurrency> items, CancellationToken cancellationToken = default)
    {
        var codes = items.Where(item => item.CurrencyCode != Hub).Select(item => item.CurrencyCode).ToList();
        if (codes.Count == 0)
        {
            return;
        }

        var counts = await context.ExchangeRates.AsNoTracking()
            .Where(rate => rate.Provider != null
                && (codes.Contains(rate.FromCurrencyCode) || codes.Contains(rate.ToCurrencyCode)))
            .GroupBy(rate => rate.FromCurrencyCode == Hub ? rate.ToCurrencyCode : rate.FromCurrencyCode)
            .Select(group => new { Code = group.Key, Count = group.Count() })
            .ToListAsync(cancellationToken);
        foreach (var item in items)
        {
            item.FetchedRowCount = counts.FirstOrDefault(count => count.Code == item.CurrencyCode)?.Count ?? 0;
        }
    }

    /// <summary>
    /// The audit line for a sync that archiving its currency switched off — the same line a manual stop
    /// writes, so the settings log records every change to a currency's sync, whatever made it.
    /// </summary>
    public void LogStoppedByArchive(string currencyCode, string? userId) =>
        audit.LogInformation(
            "System settings change by {ActorUserId}: {Field} {OldValue} -> {NewValue}.",
            userId, $"ExchangeRateSync[{NormalizeCode(currencyCode)}].Enabled", true, false);

    /// <summary>
    /// Turns sync on or off for one currency and sets its start-date override (issue #349 §5.2). An
    /// upsert. Enabling needs the provider's list; disabling is never refused for support reasons — it is
    /// the remedy <c>UnsupportedCurrency</c> names — and never deletes fetched rows.
    /// </summary>
    public async Task<ExistingExchangeRateSyncCurrency> UpdateAsync(
        string currencyCode, ExchangeRateSyncCurrencyUpdate update, string? userId, CancellationToken cancellationToken = default)
    {
        var code = NormalizeCode(currencyCode);
        var currency = await context.Currencies.AsNoTracking()
            .FirstOrDefaultAsync(value => value.CurrencyCode == code, cancellationToken)
            ?? throw new DomainNotFoundException($"Currency {code} not found.");

        // The sync never covers an archived currency: archiving switches it off, and it stays off.
        if (currency.Archived is not null)
        {
            throw new DomainUnprocessableException($"{code} is archived. Restore it before syncing it.", "enabled");
        }

        if (code == Hub)
        {
            throw new DomainUnprocessableException(
                $"{Hub} is the base every fetched rate is quoted against; it is synced with any other currency and cannot be changed.",
                "enabled");
        }

        if (update.HistoryStartDate is { } start
            && (start < ExchangeRateHistoryStartDateRule.Floor || start > TodayUtc))
        {
            throw new DomainUnprocessableException(
                $"The history start date must be on or after {ExchangeRateHistoryStartDateRule.Floor.ToString(ExchangeRateHistoryStartDateRule.Format, CultureInfo.InvariantCulture)} and on or before today.",
                "historyStartDate");
        }

        var enable = update.Enabled == true;
        var row = await context.ExchangeRateSyncCurrencies.FirstOrDefaultAsync(value => value.CurrencyCode == code, cancellationToken);
        var oldEnabled = row?.Enabled ?? false;
        var oldStart = row?.HistoryStartDate;
        IReadOnlyList<ProviderSeries>? series = null;
        if (enable && !oldEnabled)
        {
            // Live, not the stored copy: starting a sync acts on what the provider publishes today. Only on
            // the off-to-on transition — editing the start date of a currency already syncing must not
            // depend on the provider being up, and a run reports a series that has since gone.
            series = await supportedList.FetchAsync(cancellationToken)
                ?? throw new ExchangeRateSupportedListUnavailableException();
            if (!series.Any(value => value.CurrencyCode == code && ExchangeRateSupportedList.IsFresh(value, TodayUtc)))
            {
                throw new DomainUnprocessableException(
                    $"Norges Bank does not publish {code}, so it cannot be synced.", "enabled");
            }
        }

        if (row is null)
        {
            row = new ExchangeRateSyncCurrency { CurrencyCode = code };
            context.ExchangeRateSyncCurrencies.Add(row);
        }

        row.Enabled = enable;
        row.HistoryStartDate = update.HistoryStartDate;
        row.UpdatedAtUtc = Now;
        row.UpdatedByUserId = userId;
        await context.SaveChangesAsync(cancellationToken);

        // Unconditional on success, like the settings loop's security lines. The code logged is the one
        // read from the Currencies row, never the route value, so nothing request-supplied reaches the log.
        var loggedCode = currency.CurrencyCode;
        audit.LogInformation(
            "System settings change by {ActorUserId}: {Field} {OldValue} -> {NewValue}.",
            userId, $"ExchangeRateSync[{loggedCode}].Enabled", oldEnabled, enable);
        audit.LogInformation(
            "System settings change by {ActorUserId}: {Field} {OldValue} -> {NewValue}.",
            userId, $"ExchangeRateSync[{loggedCode}].HistoryStartDate", Format(oldStart), Format(update.HistoryStartDate));

        series ??= (await supportedList.ReadAsync(cancellationToken))?.Series;
        var settings = await settingsLookup.GetAsync(cancellationToken);
        var extents = await sync.FetchedExtentsAsync(ExchangeRateSyncClock.Oslo, [code], cancellationToken);
        var item = Project(code, currency.Name, row, series, settings, extents);
        item.FetchedRowCount = await context.ExchangeRates.AsNoTracking()
            .CountAsync(rate => rate.Provider != null && (rate.FromCurrencyCode == code || rate.ToCurrencyCode == code), cancellationToken);
        return item;
    }

    /// <summary>
    /// Removes every fetched row with <paramref name="currencyCode"/> on either side and clears its
    /// watermark (issue #349 §5.9). It takes the run guard through a <c>Purge</c> run row, so it cannot
    /// overlap a sync run, and the deletes and the reset share one transaction: a failure leaves nothing
    /// half-deleted. Manual rows are untouched.
    /// </summary>
    public async Task<ExchangeRatePurgeResult> PurgeAsync(string currencyCode, string? userId, CancellationToken cancellationToken = default)
    {
        var code = NormalizeCode(currencyCode);
        // The stored code, not the route value, is what the audit line below names.
        var loggedCode = await context.Currencies.AsNoTracking()
            .Where(value => value.CurrencyCode == code)
            .Select(value => value.CurrencyCode)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new DomainNotFoundException($"Currency {code} not found.");

        if (code == Hub)
        {
            throw new DomainUnprocessableException(
                $"{Hub} has no fetched rates of its own; purge the other currency instead.", "enabled");
        }

        if (await context.ExchangeRateSyncCurrencies.AnyAsync(value => value.CurrencyCode == code && value.Enabled, cancellationToken))
        {
            throw new DomainUnprocessableException(
                $"{code} is still synced and would be fetched again on the next run. Stop its sync first.", "enabled");
        }

        var run = await sync.TryStartAsync(ExchangeRateSyncTrigger.Purge, userId, cancellationToken)
            ?? throw new DomainConflictException("An exchange-rate sync run or purge is already in progress.");
        var runId = run.ExchangeRateSyncRunId;
        run.CurrencyCodes = code;

        // Read again under the guard: the check above is a fast refusal that leaves no run row, but a sync
        // started between it and the guard would otherwise lose its rates to the purge.
        if (await context.ExchangeRateSyncCurrencies.AsNoTracking().AnyAsync(value => value.CurrencyCode == code && value.Enabled, cancellationToken))
        {
            await sync.FinishAsync(run, ExchangeRateSyncRunStatus.Skipped, null, CancellationToken.None);
            throw new DomainUnprocessableException(
                $"{code} is still synced and would be fetched again on the next run. Stop its sync first.", "enabled");
        }

        var deleted = 0;
        try
        {
            var strategy = context.Database.CreateExecutionStrategy();
            await strategy.ExecuteAsync(async () =>
            {
                deleted = 0;
                context.ChangeTracker.Clear();
                await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

                while (true)
                {
                    var batch = await context.ExchangeRates
                        .Where(rate => rate.Provider != null && (rate.FromCurrencyCode == code || rate.ToCurrencyCode == code))
                        .OrderBy(rate => rate.ExchangeRateId)
                        .Take(ExchangeRateSyncConstants.PurgeBatchSize)
                        .ToListAsync(cancellationToken);
                    if (batch.Count == 0)
                    {
                        break;
                    }

                    context.ExchangeRates.RemoveRange(batch);
                    await context.SaveChangesAsync(cancellationToken);
                    context.ChangeTracker.Clear();
                    deleted += batch.Count;
                }

                var row = await context.ExchangeRateSyncCurrencies.FirstOrDefaultAsync(value => value.CurrencyCode == code, cancellationToken);
                if (row is not null)
                {
                    row.BackfilledFrom = null;
                    row.UpdatedAtUtc = Now;
                    row.UpdatedByUserId = userId;
                    await context.SaveChangesAsync(cancellationToken);
                }

                await transaction.CommitAsync(cancellationToken);
            });
        }
        catch
        {
            context.ChangeTracker.Clear();
            var failed = await context.ExchangeRateSyncRuns.FirstAsync(value => value.ExchangeRateSyncRunId == runId, CancellationToken.None);
            failed.CurrencyCodes = code;
            await sync.FinishAsync(failed, ExchangeRateSyncRunStatus.Failed, null, CancellationToken.None);
            throw;
        }

        context.ChangeTracker.Clear();
        var finished = await context.ExchangeRateSyncRuns.FirstAsync(value => value.ExchangeRateSyncRunId == runId, CancellationToken.None);
        finished.CurrencyCodes = code;
        finished.RowsDeleted = deleted;
        await sync.FinishAsync(finished, ExchangeRateSyncRunStatus.Succeeded, null, CancellationToken.None);

        audit.LogInformation(
            "Exchange-rate purge by {ActorUserId}: {CurrencyCode} {RowsDeleted} fetched rows deleted.",
            userId, loggedCode, deleted);

        return new ExchangeRatePurgeResult { RowsDeleted = deleted };
    }

    /// <summary>Upper-cases and checks the route code (issue #349 §5.2, security L2); anything else is a <c>400</c>.</summary>
    public static string NormalizeCode(string? currencyCode)
    {
        var code = (currencyCode ?? string.Empty).Trim().ToUpperInvariant();
        if (!CodePattern().IsMatch(code))
        {
            throw new DomainValidationException("The currency code must be three letters.", null, "currencyCode");
        }

        return code;
    }

    private ExistingExchangeRateSyncCurrency Project(
        string code,
        string name,
        ExchangeRateSyncCurrency? row,
        IReadOnlyList<ProviderSeries>? series,
        ExchangeRateSyncSettings settings,
        IReadOnlyDictionary<string, ExchangeRateSyncService.FetchedExtent> extents)
    {
        var effective = row?.HistoryStartDate
            ?? settings.HistoryStartDate
            ?? ExchangeRateHistoryStartDateRule.DynamicDefault(TodayUtc);

        if (code == Hub)
        {
            return new ExistingExchangeRateSyncCurrency
            {
                CurrencyCode = code,
                Name = name,
                Supported = series is null ? null : true,
                Enabled = true,
                Locked = true,
                EffectiveHistoryStartDate = effective,
                BackfillComplete = true,
            };
        }

        var published = series?.FirstOrDefault(value => value.CurrencyCode == code);
        var hasExtent = extents.TryGetValue(code, out var extent);
        return new ExistingExchangeRateSyncCurrency
        {
            CurrencyCode = code,
            Name = name,
            Supported = series is null ? null : published is not null && ExchangeRateSupportedList.IsFresh(published, TodayUtc),
            Enabled = row?.Enabled ?? false,
            Locked = false,
            HistoryStartDate = row?.HistoryStartDate,
            EffectiveHistoryStartDate = effective,
            FirstAvailableDate = published?.FirstObservation,
            OldestFetchedDate = hasExtent ? extent.Oldest : null,
            NewestFetchedDate = hasExtent ? extent.Newest : null,
            BackfillComplete = ExchangeRateSyncService.IsBackfillComplete(row?.BackfilledFrom, effective, published?.FirstObservation),
        };
    }

    /// <summary>Composed from the date's integer parts, so the audit line carries no request-supplied text.</summary>
    private static string Format(DateOnly? date) =>
        date is { } value
            ? string.Create(CultureInfo.InvariantCulture, $"{value.Year:D4}-{value.Month:D2}-{value.Day:D2}")
            : "(global)";

    [GeneratedRegex("^[A-Z]{3}$")]
    private static partial Regex CodePattern();
}
