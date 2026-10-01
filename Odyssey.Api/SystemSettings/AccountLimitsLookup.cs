using Microsoft.Extensions.Caching.Memory;
using Odyssey.Context;
using Odyssey.Core.Finance;
using Odyssey.Dtos;

namespace Odyssey.Api.SystemSettings;

/// <summary>
/// Backs <see cref="IAccountLimitsLookup"/> (issue #434 key 15) on the same 30s
/// <see cref="IMemoryCache"/> TTL as its siblings, evicted by <see cref="SystemSettingsService"/> the
/// moment the cap actually changes. Resolution is <see cref="IntSettingResolver"/>'s.
///
/// <para>
/// The ceiling is load-bearing (issue #168): the <c>[Range]</c> runs on the HTTP path only, so a row
/// left above it by a hand edit, a restore, or a save made before the ceiling narrowed would otherwise
/// reach the section verbatim and break the very query the cap exists to keep answerable.
/// </para>
///
/// <para>
/// A degraded result is deliberately NOT cached: the value is also served by a claim-free read endpoint
/// that must fail closed, and a degraded answer must not outlive the fault by a further 30s.
/// </para>
/// </summary>
public sealed class AccountLimitsLookup(
    OdysseyContext context,
    IMemoryCache cache,
    ILogger<AccountLimitsLookup> logger) : IAccountLimitsLookup
{
    internal const string CacheKey = "system-settings:account-limits";

    private static readonly IntSettingSpec Spec = new(
        SystemSettingsKeys.AccountMaxSmartTagsPerAccount,
        SystemSettingsDefaults.AccountMaxSmartTagsPerAccount,
        SystemSettingsBounds.AccountMaxSmartTagsPerAccountMin,
        SystemSettingsBounds.AccountMaxSmartTagsPerAccountMax);

    private readonly IntSettingResolver resolver = new(cache, logger, CacheKey);

    public async Task<AccountLimits> GetAsync(CancellationToken cancellationToken = default)
    {
        if (cache.TryGetValue(CacheKey, out AccountLimits? cached) && cached is not null)
        {
            return cached;
        }

        var (values, readFailed) = await resolver.ReadAsync(context, [Spec.Key], "account limits", cancellationToken);
        var resolved = resolver.Resolve(Spec, values, readFailed);
        var limits = new AccountLimits(resolved.Value, resolved.IsDegraded);

        if (!resolved.IsDegraded)
        {
            cache.Set(CacheKey, limits, IntSettingResolver.CacheTtl);
        }

        return limits;
    }
}
