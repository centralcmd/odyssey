using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Odyssey.Context;
using Odyssey.Core.Finance;
using Odyssey.Dtos;

namespace Odyssey.Api.SystemSettings;

/// <summary>
/// Backs <see cref="IContractLimitsLookup"/> (issue #166) on the same 30s <see cref="IMemoryCache"/>
/// TTL as its siblings, evicted by <see cref="SystemSettingsService"/> the moment the cap actually
/// changes.
///
/// <para>
/// Modelled on <see cref="AccountLimitsLookup"/>, because this value is also served by a claim-free
/// read endpoint that must fail closed on a degraded read: that needs a last-known-good watermark and
/// an <c>IsDegraded</c> flag.
/// </para>
///
/// <para>
/// The watermark lives in <see cref="IMemoryCache"/> rather than a <c>static</c> field. Both have the
/// same lifetime in production (the cache is a singleton), but the cache is container-scoped, so a
/// watermark cannot leak between test classes running in parallel.
/// </para>
/// </summary>
public sealed class ContractLimitsLookup(
    OdysseyContext context,
    IMemoryCache cache,
    ILogger<ContractLimitsLookup> logger) : IContractLimitsLookup
{
    internal const string CacheKey = "system-settings:contract-limits";
    private const string LastKnownGoodKey = "system-settings:contract-limits:lkg";

    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);

    public async Task<ContractLimits> GetAsync(CancellationToken cancellationToken = default)
    {
        if (cache.TryGetValue(CacheKey, out ContractLimits? cached) && cached is not null)
        {
            return cached;
        }

        string? stored;
        try
        {
            stored = await context.SystemSettings.AsNoTracking()
                .Where(row => row.Key == SystemSettingsKeys.ContractMaxSmartTagsPerContract)
                .Select(row => row.Value)
                .FirstOrDefaultAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "Reading the contract limits failed; falling back conservatively.");
            return Degraded();
        }

        int maxSmartTags;
        if (stored is null)
        {
            // Absent is healthy — the compiled default is the documented answer, not a fault.
            maxSmartTags = SystemSettingsDefaults.ContractMaxSmartTagsPerContract;
        }
        else if (int.TryParse(stored, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                 && parsed > 0)
        {
            maxSmartTags = parsed;
        }
        else
        {
            logger.LogError(
                "The stored contract smart-tag cap '{Value}' is not a usable positive integer; falling back conservatively.",
                stored);
            return Degraded();
        }

        var limits = new ContractLimits(maxSmartTags, IsDegraded: false);
        cache.Set(LastKnownGoodKey, maxSmartTags);
        cache.Set(CacheKey, limits, CacheTtl);
        return limits;
    }

    private ContractLimits Degraded()
    {
        var lastKnownGood = cache.TryGetValue(LastKnownGoodKey, out int watermark)
            ? watermark
            : SystemSettingsDefaults.ContractMaxSmartTagsPerContract;

        // min: this is a cap, so the conservative direction is smaller.
        var maxSmartTags = Math.Min(lastKnownGood, SystemSettingsDefaults.ContractMaxSmartTagsPerContract);

        // Deliberately NOT cached: a degraded answer must not be served for a further 30s after the
        // database recovers.
        return new ContractLimits(maxSmartTags, IsDegraded: true);
    }
}
