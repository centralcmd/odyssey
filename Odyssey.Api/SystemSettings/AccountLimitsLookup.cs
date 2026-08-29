using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Odyssey.Context;
using Odyssey.Core.Finance;
using Odyssey.Dtos;

namespace Odyssey.Api.SystemSettings;

/// <summary>
/// Backs <see cref="IAccountLimitsLookup"/> (issue #434 key 15) on the same 30s
/// <see cref="IMemoryCache"/> TTL as its siblings, evicted by <see cref="SystemSettingsService"/> the
/// moment the cap actually changes.
///
/// <para>
/// Modelled on <see cref="UploadLimitsLookup"/> rather than on <see cref="JournalLimitsLookup"/>,
/// because this value is also served by a claim-free read endpoint that must fail closed on a
/// degraded read. That needs a last-known-good watermark and an <c>IsDegraded</c> flag, which the
/// upload lookup already has and the journal one did not until this change.
/// </para>
///
/// <para>
/// The watermark lives in <see cref="IMemoryCache"/> rather than a <c>static</c> field. Both have the
/// same lifetime in production (the cache is a singleton), but the cache is container-scoped, so a
/// watermark cannot leak between test classes running in parallel.
/// </para>
/// </summary>
public sealed class AccountLimitsLookup(
    OdysseyContext context,
    IMemoryCache cache,
    ILogger<AccountLimitsLookup> logger) : IAccountLimitsLookup
{
    internal const string CacheKey = "system-settings:account-limits";
    private const string LastKnownGoodKey = "system-settings:account-limits:lkg";

    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);

    public async Task<AccountLimits> GetAsync(CancellationToken cancellationToken = default)
    {
        if (cache.TryGetValue(CacheKey, out AccountLimits? cached) && cached is not null)
        {
            return cached;
        }

        string? stored;
        try
        {
            stored = await context.SystemSettings.AsNoTracking()
                .Where(row => row.Key == SystemSettingsKeys.AccountMaxSmartTagsPerAccount)
                .Select(row => row.Value)
                .FirstOrDefaultAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "Reading the account limits failed; falling back conservatively.");
            return Degraded();
        }

        int maxSmartTags;
        if (stored is null)
        {
            // Absent is healthy — the compiled default is the documented answer, not a fault.
            maxSmartTags = SystemSettingsDefaults.AccountMaxSmartTagsPerAccount;
        }
        else if (int.TryParse(stored, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                 && parsed > 0)
        {
            maxSmartTags = parsed;
        }
        else
        {
            logger.LogError(
                "The stored smart-tag cap '{Value}' is not a usable positive integer; falling back conservatively.",
                stored);
            return Degraded();
        }

        var limits = new AccountLimits(maxSmartTags, IsDegraded: false);
        cache.Set(LastKnownGoodKey, maxSmartTags);
        cache.Set(CacheKey, limits, CacheTtl);
        return limits;
    }

    private AccountLimits Degraded()
    {
        var lastKnownGood = cache.TryGetValue(LastKnownGoodKey, out int watermark)
            ? watermark
            : SystemSettingsDefaults.AccountMaxSmartTagsPerAccount;

        // min: this is a cap, so the conservative direction is smaller.
        var maxSmartTags = Math.Min(lastKnownGood, SystemSettingsDefaults.AccountMaxSmartTagsPerAccount);

        // Deliberately NOT cached: a degraded answer must not be served for a further 30s after the
        // database recovers.
        return new AccountLimits(maxSmartTags, IsDegraded: true);
    }
}
