using System.Reflection;
using Odyssey.Api.SystemSettings;
using Odyssey.Context;
using Xunit;

namespace Odyssey.Api.Tests;

/// <summary>
/// A descriptor's <c>CacheKeyToEvict</c> has to name the cache entry that actually SERVES its key, or
/// an administrator's save returns <c>200</c> and then does nothing until the entry expires on its TTL
/// (issue #28).
///
/// <para>
/// <strong>Registry-wide rather than per-setting, deliberately.</strong> The two caps that prompted
/// this each had round-trip, ceiling, range and claim coverage and still evicted the wrong entry for
/// months, because every one of those tests asks "what does the API return?" and none asks "which
/// entry did the write drop?". The settings recipe is followed by hand for each new key, so the
/// failure worth closing is the next slip, not those two.
/// </para>
///
/// <para>
/// The served sets are <em>reflected off the lookups</em>, never retyped here — a copy would drift and
/// then agree with itself. <see cref="ServedBy"/> pairs each cache key with the field holding its keys;
/// the two single-key lookups read their one key inline, so those are named. The completeness test is
/// what keeps the pairing honest: a new cached setting cannot be added without appearing in it.
/// </para>
/// </summary>
public class SystemSettingsCacheEvictionTests
{
    /// <summary>Which cache entry serves which settings keys — the pairing every descriptor must agree with.</summary>
    private static readonly (string CacheKey, IReadOnlyList<string> Keys)[] ServedBy =
    [
        (SystemSettingsService.FinanceCapsCacheKey, KeysOf<SystemSettingsLookup>("FinanceCapKeys")),
        (SystemSettingsService.ContractSummaryCacheKey, KeysOf<SystemSettingsLookup>("ContractSummaryKeys")),
        (JournalLimitsLookup.CacheKey, KeysOf<JournalLimitsLookup>("Keys")),
        (ImportExportLimitsLookup.CacheKey, KeysOf<ImportExportLimitsLookup>("Keys")),
        (FileAnalysisSettingsLookup.CacheKey, KeysOf<FileAnalysisSettingsLookup>("Keys")),

        // These three resolve a single key inline rather than from an array, so there is nothing to
        // reflect. Naming the constant still fails if the lookup is repointed at a different key.
        (AccountLimitsLookup.CacheKey, [SystemSettingsKeys.AccountMaxSmartTagsPerAccount]),
        (ContractLimitsLookup.CacheKey, [SystemSettingsKeys.ContractMaxSmartTagsPerContract]),
        (PropertyLimitsLookup.CacheKey, [SystemSettingsKeys.PropertyMaxSmartTagsPerProperty]),
        (UploadLimitsLookup.CacheKey, [SystemSettingsKeys.FileStorageMaxUploadMegabytes]),
    ];

    /// <summary>
    /// Keys deliberately read LIVE on every request instead of from a cached snapshot, so no entry
    /// serves them and there is nothing for a save to make stale. <c>FileAnalysisEnabled</c> is the
    /// switch that stops personal data leaving the deployment for a third-party processor, and issue
    /// #439 §5.1 keeps it off <c>GetAsync</c>'s snapshot precisely so a disable binds on the next
    /// request rather than up to the TTL later. Its descriptor still names the file-analysis entry;
    /// that is a harmless no-op eviction, not the defect this file guards, and unpicking it is not
    /// this change's business.
    /// </summary>
    private static readonly string[] ReadLiveNotCached = [SystemSettingsKeys.FileAnalysisEnabled];

    private static string[] KeysOf<TLookup>(string fieldName)
    {
        var field = typeof(TLookup).GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException(
                $"{typeof(TLookup).Name} has no static field '{fieldName}'. If the served set was renamed " +
                "or inlined, update this pairing — do not delete the entry.");

        var keys = (string[]?)field.GetValue(null);
        Assert.NotNull(keys);
        Assert.NotEmpty(keys);
        return keys;
    }

    private static string CacheKeyServing(string settingKey) =>
        ServedBy.SingleOrDefault(entry => entry.Keys.Contains(settingKey)).CacheKey;

    [Fact]
    public void Every_descriptor_evicts_the_cache_entry_that_serves_its_key()
    {
        var wrong = SystemSettingsRegistry.All
            .Where(descriptor => descriptor.CacheKeyToEvict is not null)
            .Select(descriptor => (descriptor.Key, Evicts: descriptor.CacheKeyToEvict, Serves: CacheKeyServing(descriptor.Key)))
            .Where(row => row.Serves is not null && row.Evicts != row.Serves)
            .Select(row => $"{row.Key} evicts '{row.Evicts}' but is served by '{row.Serves}'")
            .ToList();

        Assert.Empty(wrong);
    }

    /// <summary>
    /// The half that makes the test above durable. Without it a new setting served by a lookup nobody
    /// listed here would be silently skipped — passing while carrying the very defect this file exists
    /// to catch.
    /// </summary>
    [Fact]
    public void Every_descriptor_that_evicts_an_entry_is_served_by_a_listed_one()
    {
        var unaccounted = SystemSettingsRegistry.All
            .Where(descriptor => descriptor.CacheKeyToEvict is not null
                                 && CacheKeyServing(descriptor.Key) is null
                                 && !ReadLiveNotCached.Contains(descriptor.Key))
            .Select(descriptor => $"{descriptor.Key} (evicts '{descriptor.CacheKeyToEvict}')")
            .ToList();

        Assert.Empty(unaccounted);
    }

    /// <summary>
    /// The other direction: a key a lookup reads but no descriptor writes is a setting an administrator
    /// cannot change, or one whose write path skips the eviction entirely.
    /// </summary>
    [Fact]
    public void Every_served_key_has_a_descriptor_that_evicts_its_entry()
    {
        var byKey = SystemSettingsRegistry.All.ToDictionary(descriptor => descriptor.Key);

        var missing = ServedBy
            .SelectMany(entry => entry.Keys.Select(key => (key, entry.CacheKey)))
            .Where(pair => !byKey.TryGetValue(pair.key, out var descriptor)
                           || descriptor.CacheKeyToEvict != pair.CacheKey)
            .Select(pair => $"{pair.key} should evict '{pair.CacheKey}'")
            .ToList();

        Assert.Empty(missing);
    }

    /// <summary>
    /// The exemption cannot outlive its reason. If a live-read key is ever folded into a cached
    /// snapshot it starts being able to go stale, so the allow-list entry has to go and the pairing
    /// above has to cover it instead.
    /// </summary>
    [Fact]
    public void A_key_exempted_as_live_read_is_really_served_by_no_cache_entry()
    {
        var nowCached = ReadLiveNotCached
            .Where(key => CacheKeyServing(key) is not null)
            .Select(key => $"{key} is now served by '{CacheKeyServing(key)}' — drop its exemption")
            .ToList();

        Assert.Empty(nowCached);
    }
}
