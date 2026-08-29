using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Odyssey.Context;
using Odyssey.Core.Finance;
using Odyssey.Dtos;

namespace Odyssey.Api.SystemSettings;

/// <summary>
/// Backs <see cref="IUploadLimitsLookup"/> (issue #421 Wave 4) on the same 30s
/// <see cref="IMemoryCache"/> TTL as its siblings, evicted by <see cref="SystemSettingsService"/> the
/// moment the cap actually changes.
///
/// <para>
/// The cap degrades <b>monotonically</b>: a read fault never yields a cap higher than the last one this
/// instance served, so a database blip cannot widen the upload surface. That is
/// <c>min(last-known-good, compiled default)</c> — <c>min</c> because for an upload cap the
/// conservative direction is smaller, unlike the auto-link threshold and the mail-throttle window
/// where it is larger.
/// </para>
///
/// <para>
/// The last-known-good value lives in <see cref="IMemoryCache"/> rather than a <c>static</c> field.
/// Both have the same lifetime in production (the cache is a singleton), but the cache is
/// container-scoped, so a watermark cannot leak between test classes running in parallel.
/// </para>
/// </summary>
public sealed class UploadLimitsLookup(
    OdysseyContext context,
    IMemoryCache cache,
    ILogger<UploadLimitsLookup> logger) : IUploadLimitsLookup
{
    internal const string CacheKey = "system-settings:upload-limits";
    private const string LastKnownGoodKey = "system-settings:upload-limits:lkg";
    private const long BytesPerMegabyte = 1024 * 1024;

    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);

    public async Task<UploadLimits> GetAsync(CancellationToken cancellationToken = default)
    {
        if (cache.TryGetValue(CacheKey, out UploadLimits? cached) && cached is not null)
        {
            return cached;
        }

        string? stored;
        try
        {
            stored = await context.SystemSettings.AsNoTracking()
                .Where(row => row.Key == SystemSettingsKeys.FileStorageMaxUploadMegabytes)
                .Select(row => row.Value)
                .FirstOrDefaultAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "Reading the upload cap failed; falling back conservatively.");
            return Degraded();
        }

        int megabytes;
        if (stored is null)
        {
            // Absent is healthy — the compiled default is the documented answer, not a fault.
            megabytes = SystemSettingsDefaults.FileStorageMaxUploadMegabytes;
        }
        else if (int.TryParse(stored, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                 && parsed > 0)
        {
            megabytes = parsed;
        }
        else
        {
            logger.LogError(
                "The stored upload cap '{Value}' is not a usable positive integer; falling back conservatively.",
                stored);
            return Degraded();
        }

        var limits = new UploadLimits(megabytes * BytesPerMegabyte, megabytes, IsDegraded: false);
        cache.Set(LastKnownGoodKey, megabytes);
        cache.Set(CacheKey, limits, CacheTtl);
        return limits;
    }

    private UploadLimits Degraded()
    {
        var lastKnownGood = cache.TryGetValue(LastKnownGoodKey, out int watermark)
            ? watermark
            : SystemSettingsDefaults.FileStorageMaxUploadMegabytes;

        var megabytes = Math.Min(lastKnownGood, SystemSettingsDefaults.FileStorageMaxUploadMegabytes);
        var limits = new UploadLimits(megabytes * BytesPerMegabyte, megabytes, IsDegraded: true);

        // Deliberately NOT cached: a degraded answer must not be served for a further 30s after the
        // database recovers.
        return limits;
    }
}
