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

    private IReadOnlyList<ProviderSeries>? series;

    private DateTimeOffset expiresAt = DateTimeOffset.MinValue;

    /// <summary>The cached list, or null while it is unavailable (the last fetch failed).</summary>
    public async Task<IReadOnlyList<ProviderSeries>?> GetAsync(
        Func<CancellationToken, Task<IReadOnlyList<ProviderSeries>>> fetch, CancellationToken cancellationToken)
    {
        if (timeProvider.GetUtcNow() < expiresAt)
        {
            return series;
        }

        await gate.WaitAsync(cancellationToken);
        try
        {
            if (timeProvider.GetUtcNow() < expiresAt)
            {
                return series;
            }

            try
            {
                series = await fetch(cancellationToken);
                expiresAt = timeProvider.GetUtcNow() + ExchangeRateSyncConstants.SupportedListTtl;
            }
            catch (ExchangeRateProviderException)
            {
                series = null;
                expiresAt = timeProvider.GetUtcNow() + ExchangeRateSyncConstants.SupportedListFailureTtl;
            }

            return series;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>The cached list without fetching; null when unknown or unavailable.</summary>
    public IReadOnlyList<ProviderSeries>? Peek() => timeProvider.GetUtcNow() < expiresAt ? series : null;

    /// <summary>Whether a series is offered: published within the last 30 days.</summary>
    public static bool IsFresh(ProviderSeries series, DateOnly todayUtc) =>
        series.NewestObservation >= todayUtc.AddDays(-ExchangeRateSyncConstants.SupportedFreshnessDays);
}
