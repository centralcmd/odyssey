using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Odyssey.Context;
using Odyssey.Core.Finance;
using Odyssey.Dtos;

namespace Odyssey.Api.SystemSettings;

/// <summary>
/// Backs <see cref="IPropertyLimitsLookup"/> (issue #167) on the same 30s <see cref="IMemoryCache"/> TTL
/// as its siblings, evicted by <see cref="SystemSettingsService"/> the moment the cap actually changes.
/// Modelled on <see cref="AccountLimitsLookup"/>, including its read-path clamp: the value is served by
/// a claim-free endpoint that must fail closed on a degraded read, and a row above the ceiling would
/// break the very transactions query the cap exists to keep answerable.
///
/// <para>
/// Its own cache key, not a shared one: <c>SystemSettingDescriptor.CacheKeyToEvict</c> is a single
/// string, so sharing an entry would make an account or contract save evict this and vice versa.
/// </para>
/// </summary>
public sealed class PropertyLimitsLookup(
    OdysseyContext context,
    IMemoryCache cache,
    ILogger<PropertyLimitsLookup> logger) : IPropertyLimitsLookup
{
    internal const string CacheKey = "system-settings:property-limits";
    private const string LastKnownGoodKey = "system-settings:property-limits:lkg";

    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);

    public async Task<PropertyLimits> GetAsync(CancellationToken cancellationToken = default)
    {
        if (cache.TryGetValue(CacheKey, out PropertyLimits? cached) && cached is not null)
        {
            return cached;
        }

        string? stored;
        try
        {
            stored = await context.SystemSettings.AsNoTracking()
                .Where(row => row.Key == SystemSettingsKeys.PropertyMaxSmartTagsPerProperty)
                .Select(row => row.Value)
                .FirstOrDefaultAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "Reading the property limits failed; falling back conservatively.");
            return Degraded();
        }

        int maxSmartTags;
        if (stored is null)
        {
            // Absent is healthy — the compiled default is the documented answer, not a fault.
            maxSmartTags = SystemSettingsDefaults.PropertyMaxSmartTagsPerProperty;
        }
        else if (int.TryParse(stored, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            // Clamped, not degraded: the row parsed, it is simply outside its pair, so it resolves to the
            // nearer bound — "0" included, which is the below-floor case rather than the unparseable one.
            maxSmartTags = Math.Clamp(
                parsed,
                SystemSettingsBounds.PropertyMaxSmartTagsPerPropertyMin,
                SystemSettingsBounds.PropertyMaxSmartTagsPerPropertyMax);

            if (maxSmartTags != parsed)
            {
                logger.LogWarning(
                    "The stored property smart-tag cap '{Value}' is outside its allowed range; reading the nearer bound {Bound}.",
                    stored,
                    maxSmartTags);
            }
        }
        else
        {
            logger.LogError(
                "The stored property smart-tag cap '{Value}' is not a usable integer; falling back conservatively.",
                stored);
            return Degraded();
        }

        var limits = new PropertyLimits(maxSmartTags, IsDegraded: false);
        cache.Set(LastKnownGoodKey, maxSmartTags);
        cache.Set(CacheKey, limits, CacheTtl);
        return limits;
    }

    private PropertyLimits Degraded()
    {
        var lastKnownGood = cache.TryGetValue(LastKnownGoodKey, out int watermark)
            ? watermark
            : SystemSettingsDefaults.PropertyMaxSmartTagsPerProperty;

        // min: this is a cap, so the conservative direction is smaller.
        var maxSmartTags = Math.Min(lastKnownGood, SystemSettingsDefaults.PropertyMaxSmartTagsPerProperty);

        // Deliberately NOT cached: a degraded answer must not be served for a further 30s after the
        // database recovers.
        return new PropertyLimits(maxSmartTags, IsDegraded: true);
    }
}
