using Microsoft.Extensions.Caching.Memory;
using Odyssey.Context;
using Odyssey.Core.Finance;
using Odyssey.Dtos;

namespace Odyssey.Api.SystemSettings;

/// <summary>
/// Backs <see cref="IContractLimitsLookup"/> (issue #166) on the same 30s <see cref="IMemoryCache"/> TTL
/// as its siblings, evicted by <see cref="SystemSettingsService"/> the moment the cap actually changes.
/// Modelled on <see cref="AccountLimitsLookup"/>, including its read-path clamp to the ceiling and its
/// refusal to cache a degraded result: the value is served by a claim-free endpoint that must fail
/// closed, and a row above the ceiling would break the very filter query the cap exists to keep
/// answerable.
///
/// <para>
/// Its own cache key, not a shared one: <c>SystemSettingDescriptor.CacheKeyToEvict</c> is a single
/// string, so sharing an entry would make one owner's save evict another's.
/// </para>
/// </summary>
public sealed class ContractLimitsLookup(
    OdysseyContext context,
    IMemoryCache cache,
    ILogger<ContractLimitsLookup> logger) : IContractLimitsLookup
{
    internal const string CacheKey = "system-settings:contract-limits";

    private static readonly IntSettingSpec Spec = new(
        SystemSettingsKeys.ContractMaxSmartTagsPerContract,
        SystemSettingsDefaults.ContractMaxSmartTagsPerContract,
        SystemSettingsBounds.ContractMaxSmartTagsPerContractMin,
        SystemSettingsBounds.ContractMaxSmartTagsPerContractMax);

    private readonly IntSettingResolver resolver = new(cache, logger, CacheKey);

    public async Task<ContractLimits> GetAsync(CancellationToken cancellationToken = default)
    {
        if (cache.TryGetValue(CacheKey, out ContractLimits? cached) && cached is not null)
        {
            return cached;
        }

        var (values, readFailed) = await resolver.ReadAsync(context, [Spec.Key], "contract limits", cancellationToken);
        var resolved = resolver.Resolve(Spec, values, readFailed);
        var limits = new ContractLimits(resolved.Value, resolved.IsDegraded);

        if (!resolved.IsDegraded)
        {
            cache.Set(CacheKey, limits, IntSettingResolver.CacheTtl);
        }

        return limits;
    }
}
