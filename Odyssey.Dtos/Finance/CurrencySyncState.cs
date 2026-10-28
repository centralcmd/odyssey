namespace Odyssey.Dtos.Finance;

/// <summary>
/// What the Currencies page's Sync column and filter show for one currency. Derived from its
/// <see cref="ExistingExchangeRateSyncCurrency"/> by <see cref="CurrencySyncStates.Of"/>, which both the
/// server's filter and the client's chip call, so the two cannot disagree about a row.
/// </summary>
public enum CurrencySyncState
{
    On,
    Off,
    NotPublished,
}

public static class CurrencySyncStates
{
    /// <summary>
    /// A synced currency is <see cref="CurrencySyncState.On"/> even when the provider stopped publishing it,
    /// so stopping it stays reachable; an unsynced one the provider is KNOWN not to publish is
    /// <see cref="CurrencySyncState.NotPublished"/>. Unknown support (the provider list is unavailable)
    /// counts as published, so the degraded state never hides a row.
    /// </summary>
    public static CurrencySyncState Of(ExistingExchangeRateSyncCurrency sync) =>
        sync.Enabled ? CurrencySyncState.On
        : sync.Supported == false ? CurrencySyncState.NotPublished
        : CurrencySyncState.Off;
}
