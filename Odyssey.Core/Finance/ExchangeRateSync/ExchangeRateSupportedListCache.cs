using Odyssey.Core.Finance.ExchangeRateSync.Providers;

namespace Odyssey.Core.Finance.ExchangeRateSync;

/// <summary>
/// The provider's series list, cached for 24 h and single-flight (issue #349 §3 step 3). A failed fetch
/// is cached too, for 5 min, so a provider outage does not turn every settings-page load into an
/// outbound request. A singleton: it holds only state, and the caller supplies the fetch.
/// </summary>
public sealed class ExchangeRateSupportedListCache(TimeProvider timeProvider)
{
    private readonly SemaphoreSlim gate = new(1, 1);

    /// <summary>
    /// The list and its expiry as ONE immutable snapshot behind a volatile reference, so a reader outside
    /// the gate never pairs a new expiry with an old list (or the reverse).
    /// </summary>
    private volatile Snapshot current = new(null, DateTimeOffset.MinValue);

    /// <summary>The cached list, or null while it is unavailable (the last fetch failed).</summary>
    public async Task<IReadOnlyList<ProviderSeries>?> GetAsync(
        Func<CancellationToken, Task<IReadOnlyList<ProviderSeries>>> fetch, CancellationToken cancellationToken)
    {
        var snapshot = current;
        if (timeProvider.GetUtcNow() < snapshot.ExpiresAt)
        {
            return snapshot.Series;
        }

        await gate.WaitAsync(cancellationToken);
        try
        {
            snapshot = current;
            if (timeProvider.GetUtcNow() < snapshot.ExpiresAt)
            {
                return snapshot.Series;
            }

            try
            {
                var fetched = await fetch(cancellationToken);
                current = new Snapshot(fetched, timeProvider.GetUtcNow() + ExchangeRateSyncConstants.SupportedListTtl);
            }
            catch (ExchangeRateProviderException)
            {
                current = new Snapshot(null, timeProvider.GetUtcNow() + ExchangeRateSyncConstants.SupportedListFailureTtl);
            }

            return current.Series;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>The cached list without fetching; null when unknown or unavailable.</summary>
    public IReadOnlyList<ProviderSeries>? Peek()
    {
        var snapshot = current;
        return timeProvider.GetUtcNow() < snapshot.ExpiresAt ? snapshot.Series : null;
    }

    /// <summary>Whether a series is offered: published within the last 30 days.</summary>
    public static bool IsFresh(ProviderSeries series, DateOnly todayUtc) =>
        series.NewestObservation >= todayUtc.AddDays(-ExchangeRateSyncConstants.SupportedFreshnessDays);

    private sealed record Snapshot(IReadOnlyList<ProviderSeries>? Series, DateTimeOffset ExpiresAt);
}
