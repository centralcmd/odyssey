using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Odyssey.Context;
using Odyssey.Core.SystemSettings;
using Odyssey.Dtos;

namespace Odyssey.Core.Finance.Instruments;

/// <summary>The resolved instrument-price settings (issue #446 §8.4).</summary>
public sealed record InstrumentPriceSettings(int StaleAfterDays, int SyncIntervalHours, int SyncHistoryDays, bool ReadFailed);

public interface IInstrumentPriceSettingsLookup
{
    Task<InstrumentPriceSettings> GetAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Reads the instrument-price settings live, caching only the last-known-good watermark (the exchange-rate sync's
/// lookup shape), so a save binds on the worker's next tick. Degraded reads resolve in the conservative direction:
/// a larger sync interval and a shallower backfill (fewer provider requests), and a shorter stale window (a price
/// is flagged sooner rather than shown as fresh).
/// </summary>
public sealed class InstrumentPriceSettingsLookup(
    OdysseyContext context,
    IMemoryCache cache,
    ILogger<InstrumentPriceSettingsLookup> logger) : IInstrumentPriceSettingsLookup
{
    private const string CachePrefix = "system-settings:instrument-prices";

    private static readonly IntSettingSpec StaleSpec = new(
        SystemSettingsKeys.InstrumentPriceStaleAfterDays,
        SystemSettingsDefaults.InstrumentPriceStaleAfterDays,
        SystemSettingsBounds.InstrumentPriceStaleAfterDaysMin,
        SystemSettingsBounds.InstrumentPriceStaleAfterDaysMax,
        ConservativeDirection.Smaller);

    private static readonly IntSettingSpec IntervalSpec = new(
        SystemSettingsKeys.InstrumentPriceSyncIntervalHours,
        SystemSettingsDefaults.InstrumentPriceSyncIntervalHours,
        SystemSettingsBounds.InstrumentPriceSyncIntervalHoursMin,
        SystemSettingsBounds.InstrumentPriceSyncIntervalHoursMax,
        ConservativeDirection.Larger);

    private static readonly IntSettingSpec HistorySpec = new(
        SystemSettingsKeys.InstrumentPriceSyncHistoryDays,
        SystemSettingsDefaults.InstrumentPriceSyncHistoryDays,
        SystemSettingsBounds.InstrumentPriceSyncHistoryDaysMin,
        SystemSettingsBounds.InstrumentPriceSyncHistoryDaysMax,
        ConservativeDirection.Smaller);

    private readonly IntSettingResolver resolver = new(cache, logger, CachePrefix);

    public async Task<InstrumentPriceSettings> GetAsync(CancellationToken cancellationToken = default)
    {
        var (values, readFailed) = await resolver.ReadAsync(
            context,
            [StaleSpec.Key, IntervalSpec.Key, HistorySpec.Key],
            "instrument-price settings",
            cancellationToken);

        return new InstrumentPriceSettings(
            resolver.Resolve(StaleSpec, values, readFailed).Value,
            resolver.Resolve(IntervalSpec, values, readFailed).Value,
            resolver.Resolve(HistorySpec, values, readFailed).Value,
            readFailed);
    }
}
