using Odyssey.Core.Finance.ExchangeRateSync.Providers;

namespace Odyssey.Core.Finance.ExchangeRateSync;

/// <summary>
/// The provider's series list, cached for 24 h and single-flight (issue #349 §3 step 3). A failed fetch
/// is cached too, for 5 min, so a provider outage does not turn every settings-page load into an
/// outbound request. A singleton: it holds only state, and the caller supplies the fetch.
/// </summary>
/// <remarks>
/// Two ways to read it. <see cref="GetAsync"/> waits for a fresh list when the entry has expired — for
/// a caller that must act on what the provider publishes NOW: starting a sync, a sync run. And
/// <see cref="GetWithoutWaiting"/> never waits — for the currency list, which every currency picker
/// requests and which only derives statuses from the list: it answers from the last list fetched and
/// refreshes in the background. Which currencies the provider publishes almost never changes, so a
/// copy a day or two old reads the same.
/// </remarks>
public sealed class ExchangeRateSupportedListCache(TimeProvider timeProvider)
{
    private readonly SemaphoreSlim gate = new(1, 1);

    /// <summary>
    /// The list and its expiry as ONE immutable snapshot behind a volatile reference, so a reader outside
    /// the gate never pairs a new expiry with an old list (or the reverse).
    /// </summary>
    private volatile Snapshot current = new(null, DateTimeOffset.MinValue);

    /// <summary>The last list fetched successfully, kept past its expiry and through a failure.</summary>
    private volatile IReadOnlyList<ProviderSeries>? lastGood;

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

            await RefreshAsync(fetch, cancellationToken);
            return current.Series;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// The last list fetched, at once, never waiting on the provider: null only until a first fetch
    /// succeeds. An expired entry starts ONE background refresh — none while a refresh or a
    /// <see cref="GetAsync"/> fetch is already running — and an outage keeps answering the last good list
    /// while the 5-minute failure entry spaces out the retries.
    /// </summary>
    /// <param name="fetch">
    /// Runs after the request that passed it has finished, so it must not hold anything request-scoped
    /// (the provider client is created per use from the HTTP client factory, which is safe).
    /// </param>
    public IReadOnlyList<ProviderSeries>? GetWithoutWaiting(
        Func<CancellationToken, Task<IReadOnlyList<ProviderSeries>>> fetch)
    {
        if (timeProvider.GetUtcNow() >= current.ExpiresAt && gate.Wait(0))
        {
            _ = RefreshInBackgroundAsync(fetch);
        }

        return lastGood;
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

    /// <summary>The fetch itself. The caller holds the gate.</summary>
    private async Task RefreshAsync(
        Func<CancellationToken, Task<IReadOnlyList<ProviderSeries>>> fetch, CancellationToken cancellationToken)
    {
        try
        {
            var fetched = await fetch(cancellationToken);
            lastGood = fetched;
            current = new Snapshot(fetched, timeProvider.GetUtcNow() + ExchangeRateSyncConstants.SupportedListTtl);
        }
        catch (ExchangeRateProviderException)
        {
            current = new Snapshot(null, timeProvider.GetUtcNow() + ExchangeRateSyncConstants.SupportedListFailureTtl);
        }
    }

    /// <summary>
    /// Owns the gate its caller took. Yields first, so the reader that started it is answered before the
    /// fetch begins, and swallows every failure: nothing awaits this task, so an exception escaping it
    /// would go unobserved — and an unexpected one is cached as a failure, to space out the retries.
    /// </summary>
    private async Task RefreshInBackgroundAsync(Func<CancellationToken, Task<IReadOnlyList<ProviderSeries>>> fetch)
    {
        try
        {
            await Task.Yield();
            if (timeProvider.GetUtcNow() < current.ExpiresAt)
            {
                return;
            }

            await RefreshAsync(fetch, CancellationToken.None);
        }
        catch (Exception)
        {
            current = new Snapshot(null, timeProvider.GetUtcNow() + ExchangeRateSyncConstants.SupportedListFailureTtl);
        }
        finally
        {
            gate.Release();
        }
    }

    private sealed record Snapshot(IReadOnlyList<ProviderSeries>? Series, DateTimeOffset ExpiresAt);
}
