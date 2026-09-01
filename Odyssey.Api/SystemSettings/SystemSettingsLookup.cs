using System.Globalization;
using Microsoft.EntityFrameworkCore;
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
/// — a no-op — so none of the five keys using it had a read-path bound at all. The insurance pair did
/// not even use it: both went through a throwing <c>int.Parse</c>, so a corrupt
/// <c>InsuranceExpiringSoonWindowDays</c> row was a live <c>500</c>.
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
/// <see cref="GetInsurancePolicySettingsAsync"/> <strong>caches</strong> too, but for a different
/// reason: these two keys gate no validation at all. It is frequency — five call sites across list,
/// get, update, delete and summary make this the highest-traffic settings lookup in the codebase, so
/// an uncached degraded path is the largest amplification available.
/// </item>
/// <item>
/// <see cref="GetSubscriptionSettingsAsync"/> does <strong>not</strong> cache, matching
/// <see cref="AccountLimitsLookup"/>: a degraded answer must not outlive the fault by 30 seconds. One
/// summary READ path, so recovery should be immediate and the extra query is bounded — a three-row
/// primary-key lookup on a request that already fetches up to a thousand rows plus a currency-rate
/// batch.
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
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);

    private const string LastKnownGoodPrefix = "system-settings:lookup:lkg:";

    private static readonly string[] InsuranceKeys =
    [
        SystemSettingsKeys.InsuranceExpiringSoonWindowDays,
        SystemSettingsKeys.InsuranceMaxSummaryPolicies,
    ];

    private static readonly string[] FinanceCapKeys =
    [
        SystemSettingsKeys.ContractMaxPartiesPerContract,
        SystemSettingsKeys.ContractMaxFilesPerContract,
        SystemSettingsKeys.ContractMaxSummaryContracts,
        SystemSettingsKeys.InsuranceMaxRenewalsPerPolicy,
        SystemSettingsKeys.InsuranceMaxFilesPerParent,
        SystemSettingsKeys.InsuranceMaxLinksPerPolicy,
    ];

    private static readonly string[] SubscriptionKeys =
    [
        SystemSettingsKeys.SubscriptionRenewalWindowDays,
        SystemSettingsKeys.SubscriptionMaxSummaryRenewals,
        SystemSettingsKeys.SubscriptionMaxSummarySubscriptions,
    ];

    public async Task<InsurancePolicySettings> GetInsurancePolicySettingsAsync(CancellationToken cancellationToken = default)
    {
        if (cache.TryGetValue(SystemSettingsService.InsuranceCacheKey, out InsurancePolicySettings? cached)
            && cached is not null)
        {
            return cached;
        }

        var (values, readFailed) = await ReadAsync(InsuranceKeys, "insurance policy settings", cancellationToken);

        var settings = new InsurancePolicySettings(
            // min for both, but for opposite reasons — recorded so a later change does not "harmonise"
            // them on the assumption that one rationale covers both. The window resolving DOWN means
            // UNDER-warning about an expiring policy, a cost accepted for conservatism; the cap
            // resolving down is the ordinary less-work direction.
            Resolve(values, readFailed, SystemSettingsKeys.InsuranceExpiringSoonWindowDays,
                SystemSettingsDefaults.InsuranceExpiringSoonWindowDays,
                SystemSettingsBounds.InsuranceExpiringSoonWindowDaysMin,
                SystemSettingsBounds.InsuranceExpiringSoonWindowDaysMax),
            Resolve(values, readFailed, SystemSettingsKeys.InsuranceMaxSummaryPolicies,
                SystemSettingsDefaults.InsuranceMaxSummaryPolicies,
                SystemSettingsBounds.InsuranceMaxSummaryPoliciesMin,
                SystemSettingsBounds.InsuranceMaxSummaryPoliciesMax));

        cache.Set(SystemSettingsService.InsuranceCacheKey, settings, CacheTtl);
        return settings;
    }

    /// <summary>
    /// The finance-side per-request caps (issue #421 Wave 3), under their own cache key so a contracts
    /// change does not evict the insurance entry or vice versa.
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

        var (values, readFailed) = await ReadAsync(FinanceCapKeys, "finance request caps", cancellationToken);

        var caps = new FinanceRequestCaps(
            Resolve(values, readFailed, SystemSettingsKeys.ContractMaxPartiesPerContract,
                SystemSettingsDefaults.ContractMaxPartiesPerContract,
                SystemSettingsBounds.ContractMaxPartiesPerContractMin,
                SystemSettingsBounds.ContractMaxPartiesPerContractMax),
            Resolve(values, readFailed, SystemSettingsKeys.ContractMaxFilesPerContract,
                SystemSettingsDefaults.ContractMaxFilesPerContract,
                SystemSettingsBounds.ContractMaxFilesPerContractMin,
                SystemSettingsBounds.ContractMaxFilesPerContractMax),
            Resolve(values, readFailed, SystemSettingsKeys.ContractMaxSummaryContracts,
                SystemSettingsDefaults.ContractMaxSummaryContracts,
                SystemSettingsBounds.ContractMaxSummaryContractsMin,
                SystemSettingsBounds.ContractMaxSummaryContractsMax),
            Resolve(values, readFailed, SystemSettingsKeys.InsuranceMaxRenewalsPerPolicy,
                SystemSettingsDefaults.InsuranceMaxRenewalsPerPolicy,
                SystemSettingsBounds.InsuranceMaxRenewalsPerPolicyMin,
                SystemSettingsBounds.InsuranceMaxRenewalsPerPolicyMax),
            Resolve(values, readFailed, SystemSettingsKeys.InsuranceMaxFilesPerParent,
                SystemSettingsDefaults.InsuranceMaxFilesPerParent,
                SystemSettingsBounds.InsuranceMaxFilesPerParentMin,
                SystemSettingsBounds.InsuranceMaxFilesPerParentMax),
            Resolve(values, readFailed, SystemSettingsKeys.InsuranceMaxLinksPerPolicy,
                SystemSettingsDefaults.InsuranceMaxLinksPerPolicy,
                SystemSettingsBounds.InsuranceMaxLinksPerPolicyMin,
                SystemSettingsBounds.InsuranceMaxLinksPerPolicyMax));

        cache.Set(SystemSettingsService.FinanceCapsCacheKey, caps, CacheTtl);
        return caps;
    }

    /// <summary>
    /// The Subscriptions summary limits (issue #437). A third method on this interface rather than a
    /// fourth Finance lookup — the precedent is <see cref="GetRequestCapsAsync"/> — but on its own
    /// cache key, which is forced: <c>CacheKeyToEvict</c> is a single string per descriptor.
    ///
    /// <para>
    /// <strong>A degraded result is deliberately not cached here.</strong> This is the one read path
    /// of the three, so recovery should be immediate rather than lingering for the TTL, and the extra
    /// query while degraded is well under 1x of the request's existing cost.
    /// </para>
    /// </summary>
    public async Task<SubscriptionSettings> GetSubscriptionSettingsAsync(CancellationToken cancellationToken = default)
    {
        if (cache.TryGetValue(SystemSettingsService.SubscriptionCacheKey, out SubscriptionSettings? cached)
            && cached is not null)
        {
            return cached;
        }

        var (values, readFailed) = await ReadAsync(SubscriptionKeys, "subscription summary settings", cancellationToken);

        var settings = new SubscriptionSettings(
            // min on a degraded read, but for a CORRECTNESS reason rather than a load one: the window
            // drives no work at all — BuildRenewals iterates an already-fetched list and the window only
            // decides which iterations continue — so the preference is to under-report renewals rather
            // than over-report them. Unlike the other two, this key has no availability dimension.
            Resolve(values, readFailed, SystemSettingsKeys.SubscriptionRenewalWindowDays,
                SystemSettingsDefaults.SubscriptionRenewalWindowDays,
                SystemSettingsBounds.SubscriptionRenewalWindowDaysMin,
                SystemSettingsBounds.SubscriptionRenewalWindowDaysMax),
            Resolve(values, readFailed, SystemSettingsKeys.SubscriptionMaxSummaryRenewals,
                SystemSettingsDefaults.SubscriptionMaxSummaryRenewals,
                SystemSettingsBounds.SubscriptionMaxSummaryRenewalsMin,
                SystemSettingsBounds.SubscriptionMaxSummaryRenewalsMax),
            Resolve(values, readFailed, SystemSettingsKeys.SubscriptionMaxSummarySubscriptions,
                SystemSettingsDefaults.SubscriptionMaxSummarySubscriptions,
                SystemSettingsBounds.SubscriptionMaxSummarySubscriptionsMin,
                SystemSettingsBounds.SubscriptionMaxSummarySubscriptionsMax));

        if (!readFailed)
        {
            cache.Set(SystemSettingsService.SubscriptionCacheKey, settings, CacheTtl);
        }

        return settings;
    }

    /// <summary>
    /// Reads the rows for one key set, returning an explicit <c>readFailed</c> signal alongside them.
    /// An empty dictionary from a SUCCESSFUL query means "absent", which is healthy; the flag is the
    /// only thing that can say "degraded", which is why it is returned rather than inferred.
    ///
    /// <para>
    /// A query failure is throttled per <em>read</em>, not per key: there is no per-key signal to
    /// attribute it to. A corrupt row is throttled per key instead, in <see cref="Resolve"/>.
    /// </para>
    /// </summary>
    private async Task<(Dictionary<string, string> Values, bool ReadFailed)> ReadAsync(
        string[] keys, string what, CancellationToken cancellationToken)
    {
        try
        {
            var values = await context.SystemSettings.AsNoTracking()
                .Where(row => keys.Contains(row.Key))
                .ToDictionaryAsync(row => row.Key, row => row.Value, cancellationToken);
            return (values, false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Degrade rather than throw: these sit on ordinary read and write paths, and a settings
            // read fault must not turn a user's save into a 500.
            logger.LogError(exception, "Reading the {What} failed; falling back conservatively.", what);
            return ([], true);
        }
    }

    /// <summary>
    /// The five stored states of the read-path contract, resolved for one key. They are mutually
    /// exclusive and exhaustive:
    ///
    /// <list type="table">
    /// <item><term>absent</term><description>the shipped default — <strong>healthy</strong>, not degraded</description></item>
    /// <item><term>parses, within the pair</term><description>the stored value</description></item>
    /// <item><term>parses, outside the pair</term><description>clamped to the nearer bound — reported, not degraded</description></item>
    /// <item><term>does not parse</term><description><c>min(last-known-good, shipped default)</c> — degraded</description></item>
    /// <item><term>query failed</term><description>the same — degraded</description></item>
    /// </list>
    ///
    /// <para>
    /// <strong>A parseable value is CLAMPED, not replaced by the default.</strong> A raise inside the
    /// bound is honoured — the whole point of an admin-editable setting — while a hand-edited row
    /// outside it is clamped rather than obeyed or silently reverted. That includes <c>"0"</c>, which
    /// <em>parses</em>: it is the below-floor case, so it resolves to the floor. The old <c>Cap()</c>
    /// sent it to the fallback instead, conflating "unusable" with "below the floor" — and the
    /// below-floor direction is precisely the one that is load-bearing on a raise-only key whose floor
    /// is the control.
    /// </para>
    ///
    /// <para>
    /// The watermark is written on every clean read and carries the same 30-second TTL as the values
    /// themselves.
    /// </para>
    /// </summary>
    private int Resolve(
        IReadOnlyDictionary<string, string> values, bool readFailed, string key, int fallback, int min, int max)
    {
        if (!readFailed)
        {
            if (!values.TryGetValue(key, out var stored))
            {
                // Absent is healthy — the compiled default is the documented answer, not a fault, and
                // it is what makes "absent" distinguishable from "query failed" at all.
                cache.Set(LastKnownGoodPrefix + key, fallback, CacheTtl);
                return fallback;
            }

            if (int.TryParse(stored, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            {
                var clamped = Math.Clamp(parsed, min, max);
                if (clamped != parsed)
                {
                    // A warning, not an error, matching the read DTO's projection: one level per
                    // condition across both read sites.
                    LogThrottled(key, LogLevel.Warning,
                        "The stored system setting '{Key}' is outside its allowed range; reading the nearer bound.");
                }

                cache.Set(LastKnownGoodPrefix + key, clamped, CacheTtl);
                return clamped;
            }

            LogThrottled(key, LogLevel.Error,
                "The stored system setting '{Key}' could not be parsed; falling back conservatively.");
        }

        var lastKnownGood = cache.TryGetValue(LastKnownGoodPrefix + key, out int watermark) ? watermark : fallback;

        // min for every key on this class: all seven existing ones are caps, and of the three new ones
        // two are caps and the third prefers under-reporting. A shared Math.Min helper would silently
        // invert the direction for a future key whose conservative direction is max (the mail-throttle
        // window, the AI auto-link threshold), so this is stated per method above rather than assumed.
        return Math.Min(lastKnownGood, fallback);
    }

    /// <summary>
    /// One line per faulted key per TTL window. The endpoint in front of these paths has no rate
    /// limiter, so an unthrottled line would be one per request for as long as the row stays corrupt —
    /// and a corrupt insurance row must not consume the subscriptions fault's line.
    /// </summary>
    private void LogThrottled(string key, LogLevel level, string message)
    {
        var marker = LastKnownGoodPrefix + "logged:" + key;
        if (cache.TryGetValue(marker, out _))
        {
            return;
        }

        cache.Set(marker, true, CacheTtl);

        // The stored VALUE is deliberately not logged: the same no-echo rule the caller-facing advisory
        // follows.
#pragma warning disable CA2254 // Template is a compile-time constant at each call site.
        logger.Log(level, message, key);
#pragma warning restore CA2254
    }
}
