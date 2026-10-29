using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Odyssey.Context;
using Odyssey.Core.Finance.ExchangeRateSync.Providers;
using Odyssey.Dtos.Finance;

namespace Odyssey.Core.Finance.ExchangeRateSync;

/// <summary>
/// The provider's list of published currencies, stored in <see cref="ExchangeRateProviderSeries"/> (issue
/// #351). The currency list and the sync status read only the stored copy, so neither ever waits on the
/// provider; a sync run, Refresh status and Start sync fetch it and store what they get.
/// </summary>
/// <remarks>
/// <para>
/// Stored, not cached in memory: the API and the worker are separate processes, so a run in the worker
/// could not refresh a cache the API reads. Which currencies the provider publishes almost never changes,
/// so a copy as old as the sync interval reads the same.
/// </para>
/// <para>
/// <b>It never commits a caller's changes.</b> It shares the caller's scoped context, so it stores a fetched
/// list only when that context has nothing pending, and detaches what it wrote. At worst one fetch goes
/// unstored; a half-made edit of the caller's is never saved by it.
/// </para>
/// <para>
/// <b>The refresh window counts attempts, not only successes.</b> A failed fetch stores nothing, so a window
/// read off the stored copy alone would let every Refresh status repeat the full fetch, timeouts and retry
/// included, for as long as the provider is down. The last attempt is held in the host's
/// <see cref="IMemoryCache"/>: the bound is one attempt per window per process, which is what a
/// <c>system-settings.read</c> holder could otherwise multiply.
/// </para>
/// </remarks>
public sealed class ExchangeRateSupportedList(
    OdysseyContext context,
    NorgesBankClient client,
    IMemoryCache cache,
    TimeProvider timeProvider,
    ILogger<ExchangeRateSupportedList> logger)
{
    private const ExchangeRateProvider Provider = ExchangeRateProvider.NorgesBank;

    private const string AttemptCacheKey = "ExchangeRateSync:SupportedListAttempt";

    /// <summary>The stored list and when it was fetched; null until a first fetch has been stored.</summary>
    public async Task<StoredSupportedList?> ReadAsync(CancellationToken cancellationToken = default)
    {
        var rows = await context.ExchangeRateProviderSeries.AsNoTracking()
            .Where(row => row.Provider == Provider)
            .ToListAsync(cancellationToken);
        if (rows.Count == 0)
        {
            return null;
        }

        return new StoredSupportedList(
            [.. rows.OrderBy(row => row.CurrencyCode, StringComparer.Ordinal)
                .Select(row => new ProviderSeries(row.CurrencyCode, row.FirstObservation, row.NewestObservation))],
            rows.Max(row => row.FetchedAtUtc));
    }

    /// <summary>
    /// Fetches the list from the provider now and stores it — for Start sync, which acts on what is
    /// published today. Null when the provider cannot be reached; the stored copy is then kept.
    /// </summary>
    public async Task<IReadOnlyList<ProviderSeries>?> FetchAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<ProviderSeries> fetched;
        try
        {
            fetched = await client.GetSeriesAsync(cancellationToken);
        }
        catch (ExchangeRateProviderException exception)
        {
            logger.LogWarning("The exchange-rate provider's currency list could not be fetched ({ErrorKind}); the stored copy is kept.", exception.Kind);
            return null;
        }

        await StoreAsync(fetched, cancellationToken);
        return fetched;
    }

    /// <summary>
    /// The list as of at most <see cref="ExchangeRateSyncConstants.SupportedListRefreshInterval"/> ago — for a
    /// sync run and Refresh status. A stored copy that recent is used as it is, so neither can make more than
    /// one request in that window; otherwise it fetches. Null when a fetch was needed and failed, or when
    /// one was already attempted in the window without storing a list.
    /// </summary>
    public async Task<IReadOnlyList<ProviderSeries>?> RefreshAsync(CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var window = ExchangeRateSyncConstants.SupportedListRefreshInterval;
        if (await ReadAsync(cancellationToken) is { } stored && now - stored.FetchedAtUtc < window)
        {
            return stored.Series;
        }

        if (cache.TryGetValue(AttemptCacheKey, out DateTime attempted) && now - attempted < window)
        {
            return null;
        }

        // Recorded before the fetch, so concurrent callers in this process do not each start one.
        cache.Set(AttemptCacheKey, now, window);
        var fetched = await FetchAsync(cancellationToken);
        if (fetched is not null)
        {
            // A fetch that succeeded but could not be stored must not hold the next caller off.
            cache.Remove(AttemptCacheKey);
        }

        return fetched;
    }

    /// <summary>Whether a series is offered: published within the last 30 days.</summary>
    public static bool IsFresh(ProviderSeries series, DateOnly todayUtc) =>
        series.NewestObservation >= todayUtc.AddDays(-ExchangeRateSyncConstants.SupportedFreshnessDays);

    /// <summary>Replaces the stored list whole, in one save, unless the caller has changes pending.</summary>
    private async Task StoreAsync(IReadOnlyList<ProviderSeries> series, CancellationToken cancellationToken)
    {
        if (context.ChangeTracker.HasChanges())
        {
            logger.LogDebug("The fetched exchange-rate currency list was not stored: the caller has changes pending.");
            return;
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var old = await context.ExchangeRateProviderSeries.Where(row => row.Provider == Provider).ToListAsync(cancellationToken);
        var fresh = series
            .DistinctBy(value => value.CurrencyCode, StringComparer.Ordinal)
            .Select(value => new ExchangeRateProviderSeries
            {
                Provider = Provider,
                CurrencyCode = value.CurrencyCode,
                FirstObservation = value.FirstObservation,
                NewestObservation = value.NewestObservation,
                FetchedAtUtc = now,
            })
            .ToList();
        context.ExchangeRateProviderSeries.RemoveRange(old);
        context.ExchangeRateProviderSeries.AddRange(fresh);
        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception)
        {
            // Another process stored a list at the same moment; theirs is as fresh as this one.
            logger.LogDebug(exception, "The fetched exchange-rate currency list was stored concurrently elsewhere.");
        }
        finally
        {
            foreach (var entry in context.ChangeTracker.Entries<ExchangeRateProviderSeries>().ToList())
            {
                entry.State = EntityState.Detached;
            }
        }
    }
}

/// <summary>The stored provider list and when it was fetched.</summary>
public sealed record StoredSupportedList(IReadOnlyList<ProviderSeries> Series, DateTime FetchedAtUtc);
