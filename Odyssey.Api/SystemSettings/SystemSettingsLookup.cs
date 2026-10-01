using Microsoft.Extensions.Caching.Memory;
using Odyssey.Context;
using Odyssey.Core.Finance;
using Odyssey.Dtos;

namespace Odyssey.Api.SystemSettings;

/// <summary>
/// Backs <see cref="ISystemSettingsLookup"/> for the finance domain's hot read paths (issue #349,
/// extended by issue #421 Wave 3 and issue #437): a 30s <see cref="IMemoryCache"/> TTL bounds
/// cross-instance staleness without a measurable regression versus the cached-forever options reads it
/// replaced. <see cref="SystemSettingsService.UpdateAsync"/> evicts the matching cache entry
/// synchronously on the writing instance the moment a field actually changes.
///
/// <para>
/// <strong>Issue #437 hardened all three read paths, and each part closes a live defect.</strong>
/// </para>
///
/// <list type="number">
/// <item>
/// <strong>A real logger.</strong> <c>ReadAsync</c> wrote failures to
/// <c>System.Diagnostics.Debug.WriteLine</c>, which is invisible in any deployed configuration.
/// </item>
/// <item>
/// <strong>Absent is distinguished from failed.</strong> The read returned <c>[]</c> on failure, so
/// "row absent" (healthy — resolves to the compiled default) and "query failed" (degraded) were
/// indistinguishable, and the two cannot both hold under one signal.
/// </item>
/// <item>
/// <strong>Real bounds.</strong> The old <c>Cap()</c> ended in <c>Math.Min(parsed, int.MaxValue)</c>
/// — a no-op — so none of the five keys using it had a read-path bound at all. Some keys did not
/// even use it: they went through a throwing <c>int.Parse</c>, so a corrupt row was a live
/// <c>500</c>.
/// </item>
/// <item>
/// <strong>A last-known-good watermark carrying the TTL.</strong> Written <em>with</em> the 30-second
/// expiry, not without: a watermark older than the TTL is not "last known good", it is "last known",
/// and letting one outlive every other bound in the system is how a degraded read stops being bounded
/// at all. This TTL is also what bounds the disclosed divergence between this site and the read DTO's
/// projection to a single window.
/// </item>
/// </list>
///
/// <para>
/// Parsing, clamping, the watermark and the throttled logging are <see cref="IntSettingResolver"/>'s,
/// shared with every other settings lookup (issue #287 H1) so the copies cannot drift again.
/// </para>
///
/// <para>
/// <strong>Degraded caching is resolved per METHOD, not per class</strong>, because the codebase's
/// disagreement about it is principled rather than accidental:
/// </para>
///
/// <list type="bullet">
/// <item>
/// <see cref="GetRequestCapsAsync"/> <strong>caches</strong> a degraded result. Its values gate
/// create/update validation on paths with no limiter in front of them, so re-querying per request
/// while the database is already unhealthy is a thundering herd at the worst possible moment —
/// <see cref="JournalLimitsLookup"/>'s rationale.
/// </item>
/// <item>
/// <see cref="GetContractSummarySettingsAsync"/> caches a healthy result but does <strong>not</strong>
/// cache a degraded one, matching <see cref="AccountLimitsLookup"/>: a degraded answer must not
/// outlive the fault by 30 seconds. One summary READ path, so recovery should be immediate and the
/// extra query is bounded — a three-row primary-key lookup on a request that already fetches many
/// rows plus a currency-rate batch.
/// </item>
/// </list>
///
/// <para>
/// The watermarks live in <see cref="IMemoryCache"/> rather than <c>static</c> fields. Both have the
/// same lifetime in production (the cache is a singleton), but the cache is container-scoped, so a
/// watermark cannot leak between test classes running in parallel.
/// </para>
/// </summary>
public sealed class SystemSettingsLookup(
    OdysseyContext context,
    IMemoryCache cache,
    ILogger<SystemSettingsLookup> logger) : ISystemSettingsLookup
{
    private static readonly TimeSpan CacheTtl = IntSettingResolver.CacheTtl;

    private readonly IntSettingResolver resolver = new(cache, logger, "system-settings:lookup");

    private static readonly string[] FinanceCapKeys =
    [
        SystemSettingsKeys.ContractMaxPartiesPerContract,
        SystemSettingsKeys.ContractMaxFilesPerContract,
        SystemSettingsKeys.ContractMaxTermsPerContract,
        SystemSettingsKeys.ContractMaxSummaryContracts,
    ];

    private static readonly string[] ContractSummaryKeys =
    [
        SystemSettingsKeys.ContractEndingWindowDays,
        SystemSettingsKeys.ContractChargeWindowDays,
        SystemSettingsKeys.ContractMaxSummaryCharges,
    ];

    /// <summary>
    /// The finance-side per-request caps (issue #421 Wave 3), under their own cache key so a
    /// per-request-cap change does not evict the Contracts summary entry or vice versa.
    ///
    /// <para>
    /// Every one of these is a cap, so the conservative direction on a degraded read is <c>min</c> —
    /// the opposite of the AI auto-link threshold, whose safe direction is upward. Getting that
    /// per-setting rather than via a shared helper is deliberate; issue #421 §5 tabulates it.
    /// </para>
    /// </summary>
    public async Task<FinanceRequestCaps> GetRequestCapsAsync(CancellationToken cancellationToken = default)
    {
        if (cache.TryGetValue(SystemSettingsService.FinanceCapsCacheKey, out FinanceRequestCaps? cached)
            && cached is not null)
        {
            return cached;
        }

        var (values, readFailed) = await resolver.ReadAsync(context, FinanceCapKeys, "finance request caps", cancellationToken);

        var caps = new FinanceRequestCaps(
            Resolve(values, readFailed, SystemSettingsKeys.ContractMaxPartiesPerContract,
                SystemSettingsDefaults.ContractMaxPartiesPerContract,
                SystemSettingsBounds.ContractMaxPartiesPerContractMin,
                SystemSettingsBounds.ContractMaxPartiesPerContractMax),
            Resolve(values, readFailed, SystemSettingsKeys.ContractMaxFilesPerContract,
                SystemSettingsDefaults.ContractMaxFilesPerContract,
                SystemSettingsBounds.ContractMaxFilesPerContractMin,
                SystemSettingsBounds.ContractMaxFilesPerContractMax),
            Resolve(values, readFailed, SystemSettingsKeys.ContractMaxTermsPerContract,
                SystemSettingsDefaults.ContractMaxTermsPerContract,
                SystemSettingsBounds.ContractMaxTermsPerContractMin,
                SystemSettingsBounds.ContractMaxTermsPerContractMax),
            Resolve(values, readFailed, SystemSettingsKeys.ContractMaxSummaryContracts,
                SystemSettingsDefaults.ContractMaxSummaryContracts,
                SystemSettingsBounds.ContractMaxSummaryContractsMin,
                SystemSettingsBounds.ContractMaxSummaryContractsMax));

        cache.Set(SystemSettingsService.FinanceCapsCacheKey, caps, CacheTtl);
        return caps;
    }

    /// <summary>
    /// The Contracts summary windows, on their own cache key rather than sharing the caps entry:
    /// <c>SystemSettingDescriptor.CacheKeyToEvict</c> is a single string per descriptor, so a shared
    /// entry would cross-evict. A degraded result is <strong>not</strong> cached: one summary read
    /// path, so recovery should be immediate.
    /// </summary>
    public async Task<ContractSummarySettings> GetContractSummarySettingsAsync(CancellationToken cancellationToken = default)
    {
        if (cache.TryGetValue(SystemSettingsService.ContractSummaryCacheKey, out ContractSummarySettings? cached)
            && cached is not null)
        {
            return cached;
        }

        var (values, readFailed) = await resolver.ReadAsync(context, ContractSummaryKeys, "contract summary settings", cancellationToken);

        var settings = new ContractSummarySettings(
            // min on both windows, for a CORRECTNESS reason rather than a load one: neither drives any
            // work — both only decide which already-fetched rows are surfaced — so the preference is to
            // under-report a cliff or a charge rather than to invent one.
            Resolve(values, readFailed, SystemSettingsKeys.ContractEndingWindowDays,
                SystemSettingsDefaults.ContractEndingWindowDays,
                SystemSettingsBounds.ContractEndingWindowDaysMin,
                SystemSettingsBounds.ContractEndingWindowDaysMax),
            Resolve(values, readFailed, SystemSettingsKeys.ContractChargeWindowDays,
                SystemSettingsDefaults.ContractChargeWindowDays,
                SystemSettingsBounds.ContractChargeWindowDaysMin,
                SystemSettingsBounds.ContractChargeWindowDaysMax),
            Resolve(values, readFailed, SystemSettingsKeys.ContractMaxSummaryCharges,
                SystemSettingsDefaults.ContractMaxSummaryCharges,
                SystemSettingsBounds.ContractMaxSummaryChargesMin,
                SystemSettingsBounds.ContractMaxSummaryChargesMax));

        if (!readFailed)
        {
            cache.Set(SystemSettingsService.ContractSummaryCacheKey, settings, CacheTtl);
        }

        return settings;
    }

    private int Resolve(
        IReadOnlyDictionary<string, string> values, bool readFailed, string key, int fallback, int min, int max) =>
        // min for every key on this class: all of them are caps, or (the look-ahead windows) prefer
        // under-reporting. The direction is stated per spec rather than assumed, so a future key whose
        // conservative direction is max says so here.
        resolver.Resolve(new IntSettingSpec(key, fallback, min, max, ConservativeDirection.Smaller), values, readFailed).Value;
}
