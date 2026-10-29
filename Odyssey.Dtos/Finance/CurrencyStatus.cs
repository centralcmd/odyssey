namespace Odyssey.Dtos.Finance;

/// <summary>
/// A currency's one status, as a contract has one: derived, never stored, computed by the server on every
/// list read from the currency's archival and its exchange-rate sync projection, and the same for every
/// caller — only the sync's actions and detail are admin-only (issue #351).
/// </summary>
/// <remarks>
/// The ordinals are the READING order, which is also the sort order (synced first, archived last). The
/// derivation's precedence is a separate thing, in <see cref="CurrencyStatuses.Of"/>. Not yet released,
/// so the ordinals are free to be chosen; from the first release on they are a wire contract.
/// </remarks>
public enum CurrencyStatus
{
    /// <summary>Synced and caught up. The provider's base always.</summary>
    Synced,

    /// <summary>Synced, its history not yet back-filled to its start date.</summary>
    FetchingHistory,

    /// <summary>Synced, but the provider stopped publishing it. It stays stoppable.</summary>
    NoLongerPublished,

    /// <summary>
    /// Not synced, and the provider publishes it — or its support is unknown, because the provider's
    /// list is unreachable; that never reads as <see cref="NotPublished"/>. Nobody keeps its rates
    /// current: they are entered by hand, or not at all.
    /// </summary>
    SyncingOff,

    /// <summary>Not synced, and the provider does not publish it.</summary>
    NotPublished,

    /// <summary>Archived. The sync never covers an archived currency.</summary>
    Archived,
}

public static class CurrencyStatuses
{
    /// <summary>
    /// The derivation, by precedence:
    /// <c>Archived &gt; NoLongerPublished &gt; FetchingHistory &gt; Synced &gt; NotPublished &gt; SyncingOff</c>.
    /// The server's list and the client's patch after a sync change call this one rule, so the two cannot
    /// disagree about a row. A non-archived currency with no projection reads <see cref="CurrencyStatus.SyncingOff"/>.
    /// </summary>
    public static CurrencyStatus Of(bool archived, ExistingExchangeRateSyncCurrency? sync)
    {
        if (archived)
            return CurrencyStatus.Archived;
        if (sync is null)
            return CurrencyStatus.SyncingOff;
        if (sync.Enabled || sync.Locked)
        {
            if (!sync.Locked && sync.Supported == false)
                return CurrencyStatus.NoLongerPublished;
            return sync.BackfillComplete ? CurrencyStatus.Synced : CurrencyStatus.FetchingHistory;
        }

        return sync.Supported == false ? CurrencyStatus.NotPublished : CurrencyStatus.SyncingOff;
    }
}
