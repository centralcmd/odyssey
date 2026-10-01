using Microsoft.Extensions.Caching.Memory;
using Odyssey.Context;
using Odyssey.Core.Finance;
using Odyssey.Dtos;

namespace Odyssey.Api.SystemSettings;

/// <summary>
/// Backs <see cref="IUploadLimitsLookup"/> (issue #421 Wave 4) on the same 30s
/// <see cref="IMemoryCache"/> TTL as its siblings, evicted by <see cref="SystemSettingsService"/> the
/// moment the cap actually changes. Resolution is <see cref="IntSettingResolver"/>'s.
///
/// <para>
/// The cap degrades <b>monotonically</b>: a read fault never yields a cap higher than the last one this
/// instance served, so a database blip cannot widen the upload surface. That is
/// <c>min(last-known-good, compiled default)</c> — <c>min</c> because for an upload cap the
/// conservative direction is smaller. A degraded result is NOT cached, so recovery is immediate.
/// </para>
/// </summary>
public sealed class UploadLimitsLookup(
    OdysseyContext context,
    IMemoryCache cache,
    ILogger<UploadLimitsLookup> logger) : IUploadLimitsLookup
{
    internal const string CacheKey = "system-settings:upload-limits";
    private const long BytesPerMegabyte = 1024 * 1024;

    private static readonly IntSettingSpec Spec = new(
        SystemSettingsKeys.FileStorageMaxUploadMegabytes,
        SystemSettingsDefaults.FileStorageMaxUploadMegabytes,
        SystemSettingsBounds.FileStorageMaxUploadMegabytesMin,
        SystemSettingsBounds.FileStorageMaxUploadMegabytesMax);

    private readonly IntSettingResolver resolver = new(cache, logger, CacheKey);

    public async Task<UploadLimits> GetAsync(CancellationToken cancellationToken = default)
    {
        if (cache.TryGetValue(CacheKey, out UploadLimits? cached) && cached is not null)
        {
            return cached;
        }

        var (values, readFailed) = await resolver.ReadAsync(context, [Spec.Key], "upload cap", cancellationToken);
        var resolved = resolver.Resolve(Spec, values, readFailed);
        var limits = new UploadLimits(resolved.Value * BytesPerMegabyte, resolved.Value, resolved.IsDegraded);

        if (!resolved.IsDegraded)
        {
            cache.Set(CacheKey, limits, IntSettingResolver.CacheTtl);
        }

        return limits;
    }
}
