using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Odyssey.Context;
using Odyssey.Core.Finance.ExchangeRateSync;
using Odyssey.Dtos;

namespace Odyssey.Core.SystemSettings;

/// <summary>
/// Resolves the exchange-rate sync settings (issue #349 §8). The values are read live on every call — the
/// cache holds only the last-known-good watermark — so a save binds on the worker's next tick without an
/// eviction having to cross hosts. A degraded
/// interval resolves LARGER (fewer runs is the conservative direction for load on a public service); an
/// unusable start date resolves to the dynamic default, which is also what an empty one means.
/// </summary>
public sealed class ExchangeRateSyncSettingsLookup(
    OdysseyContext context,
    IMemoryCache cache,
    ILogger<ExchangeRateSyncSettingsLookup> logger) : IExchangeRateSyncSettingsLookup
{
    private const string CachePrefix = "system-settings:exchange-rate-sync";

    private static readonly IntSettingSpec IntervalSpec = new(
        SystemSettingsKeys.ExchangeRateSyncIntervalHours,
        SystemSettingsDefaults.ExchangeRateSyncIntervalHours,
        SystemSettingsBounds.ExchangeRateSyncIntervalHoursMin,
        SystemSettingsBounds.ExchangeRateSyncIntervalHoursMax,
        ConservativeDirection.Larger);

    private readonly IntSettingResolver resolver = new(cache, logger, CachePrefix);

    public async Task<ExchangeRateSyncSettings> GetAsync(CancellationToken cancellationToken = default)
    {
        var (values, readFailed) = await resolver.ReadAsync(
            context,
            [IntervalSpec.Key, SystemSettingsKeys.ExchangeRateHistoryStartDate],
            "exchange-rate sync settings",
            cancellationToken);

        var interval = resolver.Resolve(IntervalSpec, values, readFailed);

        DateOnly? start = null;
        if (values.TryGetValue(SystemSettingsKeys.ExchangeRateHistoryStartDate, out var stored)
            && !string.IsNullOrWhiteSpace(stored))
        {
            if (ExchangeRateHistoryStartDateRule.Validate(stored) is null
                && ExchangeRateHistoryStartDateRule.TryParse(stored, out var parsed))
            {
                start = parsed;
            }
            else
            {
                logger.LogWarning(
                    "The stored system setting '{Key}' is not a usable date; using the dynamic default.",
                    SystemSettingsKeys.ExchangeRateHistoryStartDate);
            }
        }

        return new ExchangeRateSyncSettings(start, interval.Value, readFailed);
    }
}
