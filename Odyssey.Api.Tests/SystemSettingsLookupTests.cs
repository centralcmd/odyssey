using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Odyssey.Api.SystemSettings;
using Odyssey.Context;
using Odyssey.Dtos;
using Xunit;

namespace Odyssey.Api.Tests;

/// <summary>
/// The first coverage <see cref="SystemSettingsLookup"/> has ever had (issue #437 Goal 11). Before this
/// the only references to it in any test project were two hand-written fakes of the interface it backs,
/// so every defect the hardening fixes had been invisible:
///
/// <list type="bullet">
/// <item>the insurance pair went through a throwing <c>int.Parse</c>, so a corrupt
/// <c>InsuranceExpiringSoonWindowDays</c> row was a live <c>500</c> on the insurance summary;</item>
/// <item>the five capped keys ended in <c>Math.Min(parsed, int.MaxValue)</c> — a no-op — so none had a
/// read-path bound at all;</item>
/// <item>a failed query returned <c>[]</c>, making "row absent" (healthy) and "query failed"
/// (degraded) indistinguishable;</item>
/// <item>failures went to <c>Debug.WriteLine</c>, invisible in any deployed configuration.</item>
/// </list>
///
/// <para>
/// A disposed <see cref="OdysseyContext"/> stands in for the settings store being unreachable, the
/// same technique <see cref="ImportExportLimitsLookupTests"/> uses. The resolved-value cache entries are
/// evicted by their own literals between phases — they are <c>internal</c> to
/// <c>SystemSettingsService</c> — so a rename fails this suite loudly rather than letting it pass for
/// the wrong reason.
/// </para>
/// </summary>
public class SystemSettingsLookupTests
{
    private const string InsuranceCacheKey = "system-settings:insurance-policy-settings";
    private const string FinanceCapsCacheKey = "system-settings:finance-request-caps";
    private const string SubscriptionCacheKey = "system-settings:subscription-settings";

    private static OdysseyContext CreateContext(string dbName)
    {
        var options = new DbContextOptionsBuilder<OdysseyContext>().UseInMemoryDatabase(dbName).Options;
        return new OdysseyContext(options);
    }

    private static SystemSettingsLookup CreateLookup(
        OdysseyContext context, IMemoryCache cache, ILogger<SystemSettingsLookup>? logger = null) =>
        new(context, cache, logger ?? NullLogger<SystemSettingsLookup>.Instance);

    private static async Task SetAsync(OdysseyContext context, string key, string value)
    {
        var existing = await context.SystemSettings.FirstOrDefaultAsync(row => row.Key == key);
        if (existing is null)
        {
            context.SystemSettings.Add(new SystemSetting { Key = key, Value = value, UpdatedAt = DateTime.UtcNow });
        }
        else
        {
            existing.Value = value;
        }

        await context.SaveChangesAsync();
    }

    // ── AC 25 — all five stored states, per method ──────────────────────────────────────────────

    /// <summary>
    /// State 1 — <strong>absent is healthy</strong>, not degraded. It resolves to the compiled default
    /// and logs nothing, which is the posture <c>SystemSettingsService</c> takes on reads and the whole
    /// reason the read has to return an explicit failure signal rather than an empty dictionary:
    /// conflating the two would make every unseeded database look degraded.
    /// </summary>
    [Fact]
    public async Task AnAbsentRow_ResolvesToTheCompiledDefault_AndLogsNothing()
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        var logs = new RecordingLogger();
        await using var context = CreateContext(Guid.NewGuid().ToString());

        var subscriptions = await CreateLookup(context, cache, logs).GetSubscriptionSettingsAsync();
        var insurance = await CreateLookup(context, cache, logs).GetInsurancePolicySettingsAsync();
        var caps = await CreateLookup(context, cache, logs).GetRequestCapsAsync();

        Assert.Equal(SystemSettingsDefaults.SubscriptionRenewalWindowDays, subscriptions.RenewalWindowDays);
        Assert.Equal(SystemSettingsDefaults.SubscriptionMaxSummaryRenewals, subscriptions.MaxSummaryRenewals);
        Assert.Equal(SystemSettingsDefaults.SubscriptionMaxSummarySubscriptions, subscriptions.MaxSummarySubscriptions);
        Assert.Equal(SystemSettingsDefaults.InsuranceExpiringSoonWindowDays, insurance.ExpiringSoonWindowDays);
        Assert.Equal(SystemSettingsDefaults.InsuranceMaxSummaryPolicies, insurance.MaxSummaryPolicies);
        Assert.Equal(SystemSettingsDefaults.ContractMaxSummaryContracts, caps.MaxSummaryContracts);

        Assert.Empty(logs.Entries);
    }

    /// <summary>State 2 — a stored value inside the pair is honoured, which is the point of the setting.</summary>
    [Fact]
    public async Task AnInBoundRow_IsHonoured()
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        await using var context = CreateContext(Guid.NewGuid().ToString());
        await SetAsync(context, SystemSettingsKeys.SubscriptionRenewalWindowDays, "90");
        await SetAsync(context, SystemSettingsKeys.InsuranceExpiringSoonWindowDays, "120");
        await SetAsync(context, SystemSettingsKeys.ContractMaxSummaryContracts, "42");

        Assert.Equal(90, (await CreateLookup(context, cache).GetSubscriptionSettingsAsync()).RenewalWindowDays);
        Assert.Equal(120, (await CreateLookup(context, cache).GetInsurancePolicySettingsAsync()).ExpiringSoonWindowDays);
        Assert.Equal(42, (await CreateLookup(context, cache).GetRequestCapsAsync()).MaxSummaryContracts);
    }

    /// <summary>
    /// AC 12, state 3 — a value <strong>outside the pair at either end</strong> is clamped to the nearer
    /// bound: not reverted to the shipped default, and not obeyed.
    ///
    /// <para>
    /// The below-floor direction is asserted, not assumed. It is the one that is load-bearing on a
    /// raise-only key whose floor is the control, and it is also the case a previous draft sent to the
    /// degraded fallback instead — which on the renewals window differs by up to 45 days and is the
    /// OVER-reporting direction.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("2000000000", 100000)]
    [InlineData("0", 1)]
    [InlineData("-5", 1)]
    public async Task AnOutOfBoundRow_IsClampedToTheNearerBound(string stored, int expected)
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        await using var context = CreateContext(Guid.NewGuid().ToString());
        await SetAsync(context, SystemSettingsKeys.SubscriptionMaxSummarySubscriptions, stored);

        var settings = await CreateLookup(context, cache).GetSubscriptionSettingsAsync();

        Assert.Equal(expected, settings.MaxSummarySubscriptions);
    }

    /// <summary>The renewals cap's maximum is 50, not the 100000 its sibling caps carry.</summary>
    [Fact]
    public async Task TheRenewalsCap_IsClampedAtFifty()
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        await using var context = CreateContext(Guid.NewGuid().ToString());
        await SetAsync(context, SystemSettingsKeys.SubscriptionMaxSummaryRenewals, "5000");

        Assert.Equal(50, (await CreateLookup(context, cache).GetSubscriptionSettingsAsync()).MaxSummaryRenewals);
    }

    /// <summary>
    /// The five capped keys had NO real bound before this: <c>Cap()</c> ended in
    /// <c>Math.Min(parsed, int.MaxValue)</c>. Asserted on one of them so the no-op cannot come back.
    /// </summary>
    [Fact]
    public async Task TheFinanceCaps_AreBoundedToo()
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        await using var context = CreateContext(Guid.NewGuid().ToString());
        await SetAsync(context, SystemSettingsKeys.ContractMaxPartiesPerContract, "2000000000");

        Assert.Equal(100000, (await CreateLookup(context, cache).GetRequestCapsAsync()).MaxPartiesPerContract);
    }

    /// <summary>
    /// AC 9, state 4 — an <strong>unparseable</strong> value yields <c>min(last-known-good, shipped
    /// default)</c> rather than an exception, and logs an ERROR. With no prior healthy read the
    /// watermark is the default, so it resolves there.
    ///
    /// <para>
    /// <c>"0"</c> is deliberately NOT this case: it parses, so it is the out-of-bound one above.
    /// </para>
    /// </summary>
    [Fact]
    public async Task AnUnparseableRow_ResolvesConservatively_AndLogsAnError()
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        var logs = new RecordingLogger();
        await using var context = CreateContext(Guid.NewGuid().ToString());
        await SetAsync(context, SystemSettingsKeys.SubscriptionRenewalWindowDays, "abc");

        var settings = await CreateLookup(context, cache, logs).GetSubscriptionSettingsAsync();

        Assert.Equal(SystemSettingsDefaults.SubscriptionRenewalWindowDays, settings.RenewalWindowDays);
        Assert.Contains(logs.Entries, entry => entry.Level == LogLevel.Error);
    }

    /// <summary>
    /// AC 27. The live <c>500</c> the throwing <c>int.Parse</c> on the insurance path caused today: a
    /// corrupt <c>InsuranceExpiringSoonWindowDays</c> row must yield a usable value, not an exception.
    /// </summary>
    [Fact]
    public async Task ACorruptInsuranceWindowRow_YieldsAUsableValue_RatherThanThrowing()
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        await using var context = CreateContext(Guid.NewGuid().ToString());
        await SetAsync(context, SystemSettingsKeys.InsuranceExpiringSoonWindowDays, "not-a-number");

        var settings = await CreateLookup(context, cache).GetInsurancePolicySettingsAsync();

        Assert.Equal(SystemSettingsDefaults.InsuranceExpiringSoonWindowDays, settings.ExpiringSoonWindowDays);
    }

    /// <summary>
    /// State 5 — a <strong>failed query</strong> degrades to <c>min(last-known-good, default)</c> and
    /// logs an error. Distinguishable from "absent", which logs nothing at all: that is the whole
    /// content of the absent-versus-failed goal.
    /// </summary>
    [Fact]
    public async Task AFailedQuery_DegradesAndLogs_UnlikeAnAbsentRow()
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        var logs = new RecordingLogger();
        var context = CreateContext(Guid.NewGuid().ToString());
        await context.DisposeAsync(); // the settings store is unreachable

        var settings = await CreateLookup(context, cache, logs).GetSubscriptionSettingsAsync();

        Assert.Equal(SystemSettingsDefaults.SubscriptionRenewalWindowDays, settings.RenewalWindowDays);
        Assert.Contains(logs.Entries, entry => entry.Level == LogLevel.Error);
    }

    // ── AC 26 — degraded caching, asserted in BOTH directions ───────────────────────────────────

    /// <summary>
    /// AC 26. The two policies are the point of this test, so it fails if either flips.
    ///
    /// <para>
    /// The subscriptions path must NOT cache a degraded answer — one summary read path, so recovery
    /// should be immediate. The other two must, for different reasons: the request caps gate
    /// create/update validation on paths with no limiter in front of them, and the insurance pair is
    /// the highest-traffic settings lookup in the codebase.
    /// </para>
    /// </summary>
    [Fact]
    public async Task ADegradedSubscriptionsRead_IsNotCached_SoRecoveryIsImmediate()
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        var dbName = Guid.NewGuid().ToString();

        var broken = CreateContext(dbName);
        await broken.DisposeAsync();
        await CreateLookup(broken, cache).GetSubscriptionSettingsAsync();

        Assert.False(cache.TryGetValue(SubscriptionCacheKey, out _));

        // …and the very next read, against a healthy store, sees the real value with no TTL wait.
        await using var healthy = CreateContext(dbName);
        await SetAsync(healthy, SystemSettingsKeys.SubscriptionRenewalWindowDays, "120");

        Assert.Equal(120, (await CreateLookup(healthy, cache).GetSubscriptionSettingsAsync()).RenewalWindowDays);
    }

    [Fact]
    public async Task ADegradedRequestCapsRead_IsCached_AgainstAThunderingHerd()
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        var broken = CreateContext(Guid.NewGuid().ToString());
        await broken.DisposeAsync();

        await CreateLookup(broken, cache).GetRequestCapsAsync();

        Assert.True(cache.TryGetValue(FinanceCapsCacheKey, out _));
    }

    [Fact]
    public async Task ADegradedInsuranceRead_IsCached_BecauseItIsTheHighestTrafficLookup()
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        var broken = CreateContext(Guid.NewGuid().ToString());
        await broken.DisposeAsync();

        await CreateLookup(broken, cache).GetInsurancePolicySettingsAsync();

        Assert.True(cache.TryGetValue(InsuranceCacheKey, out _));
    }

    // ── AC 28 — the throttle's unit differs by failure class ────────────────────────────────────

    /// <summary>
    /// AC 28. A corrupt row logs at most one line per window <strong>per settings key</strong>, so a
    /// corrupt insurance row cannot consume the subscriptions fault's line. Without the per-key unit,
    /// one persistently bad row on an endpoint with no rate limiter is one line per request.
    /// </summary>
    [Fact]
    public async Task ACorruptRow_LogsOncePerKeyPerWindow_NotOncePerRead()
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        var logs = new RecordingLogger();
        await using var context = CreateContext(Guid.NewGuid().ToString());
        await SetAsync(context, SystemSettingsKeys.SubscriptionRenewalWindowDays, "abc");
        await SetAsync(context, SystemSettingsKeys.InsuranceExpiringSoonWindowDays, "abc");

        // The subscriptions path does not cache, so three calls really are three reads.
        for (var i = 0; i < 3; i++)
        {
            await CreateLookup(context, cache, logs).GetSubscriptionSettingsAsync();
        }

        Assert.Single(logs.Entries);

        // A different key gets its own line rather than being swallowed by the first key's throttle.
        await CreateLookup(context, cache, logs).GetInsurancePolicySettingsAsync();
        Assert.Equal(2, logs.Entries.Count);
    }

    // ── AC 31 — one log level per condition ─────────────────────────────────────────────────────

    /// <summary>
    /// AC 31. Unparseable is an ERROR and out-of-bound is a WARNING, at this read site — and the read
    /// DTO's projection uses the same two levels for the same two conditions. Before this the two sites
    /// were specified at different levels for the same fault, and this one logged nothing at all.
    /// </summary>
    [Fact]
    public async Task TheTwoConditions_LogAtOneLevelEach()
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        var unparseable = new RecordingLogger();
        var outOfBound = new RecordingLogger();

        await using (var context = CreateContext(Guid.NewGuid().ToString()))
        {
            await SetAsync(context, SystemSettingsKeys.SubscriptionRenewalWindowDays, "abc");
            await CreateLookup(context, cache, unparseable).GetSubscriptionSettingsAsync();
        }

        await using (var context = CreateContext(Guid.NewGuid().ToString()))
        {
            await SetAsync(context, SystemSettingsKeys.SubscriptionRenewalWindowDays, "5000");
            await CreateLookup(context, new MemoryCache(new MemoryCacheOptions()), outOfBound)
                .GetSubscriptionSettingsAsync();
        }

        Assert.Equal(LogLevel.Error, Assert.Single(unparseable.Entries).Level);
        Assert.Equal(LogLevel.Warning, Assert.Single(outOfBound.Entries).Level);
    }

    /// <summary>The stored value is never logged, for the same reason it is never echoed to the caller.</summary>
    [Fact]
    public async Task TheLogLine_NeverCarriesTheStoredValue()
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        var logs = new RecordingLogger();
        await using var context = CreateContext(Guid.NewGuid().ToString());
        await SetAsync(context, SystemSettingsKeys.SubscriptionRenewalWindowDays, "s3cr3t-looking-garbage");

        await CreateLookup(context, cache, logs).GetSubscriptionSettingsAsync();

        Assert.DoesNotContain(
            logs.Entries, entry => entry.Message.Contains("s3cr3t", StringComparison.Ordinal));
    }

    // ── The watermark carries the TTL ───────────────────────────────────────────────────────────

    /// <summary>
    /// A watermark older than the TTL is not "last known good", it is "last known". Written with the
    /// same 30-second expiry as the values themselves, so a degraded read cannot resolve against a
    /// value that has outlived every other bound in the system.
    /// </summary>
    [Fact]
    public async Task TheWatermark_IsWrittenWithAnExpiry()
    {
        var source = await File.ReadAllTextAsync(
            SolutionFile("Odyssey.Api", "SystemSettings", "SystemSettingsLookup.cs"));

        Assert.DoesNotContain(
            "cache.Set(LastKnownGoodPrefix + key, fallback);", source, StringComparison.Ordinal);
        Assert.Contains(
            "cache.Set(LastKnownGoodPrefix + key, fallback, CacheTtl);", source, StringComparison.Ordinal);
        Assert.Contains(
            "cache.Set(LastKnownGoodPrefix + key, clamped, CacheTtl);", source, StringComparison.Ordinal);
    }

    /// <summary>
    /// A degraded read resolves against the watermark when one exists, and <c>min</c> is the direction:
    /// every key on this class is a cap, or (the renewals window) prefers under-reporting.
    /// </summary>
    [Fact]
    public async Task ADegradedRead_NeverExceedsTheShippedDefault_EvenWithAHigherWatermark()
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        var dbName = Guid.NewGuid().ToString();

        await using (var healthy = CreateContext(dbName))
        {
            await SetAsync(healthy, SystemSettingsKeys.SubscriptionRenewalWindowDays, "300");
            Assert.Equal(300, (await CreateLookup(healthy, cache).GetSubscriptionSettingsAsync()).RenewalWindowDays);
        }

        // Evict only the 30s resolved-value entry, so the next read is forced to hit the (broken)
        // context rather than serving the healthy result the phase above cached.
        cache.Remove(SubscriptionCacheKey);

        var broken = CreateContext(dbName);
        await broken.DisposeAsync();

        var degraded = await CreateLookup(broken, cache).GetSubscriptionSettingsAsync();

        // min(last-known-good 300, shipped default 45) — a degraded read must never LOOSEN a bound.
        Assert.Equal(SystemSettingsDefaults.SubscriptionRenewalWindowDays, degraded.RenewalWindowDays);
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────────────

    private static string SolutionFile(params string[] parts)
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(System.IO.Path.Combine(dir, "Odyssey.sln")))
        {
            dir = System.IO.Path.GetDirectoryName(dir.TrimEnd(System.IO.Path.DirectorySeparatorChar));
        }

        Assert.NotNull(dir);
        return System.IO.Path.Combine([dir!, .. parts]);
    }

    /// <summary>Captures level and rendered message, so the level assertions are about the real call.</summary>
    private sealed class RecordingLogger : ILogger<SystemSettingsLookup>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }
}
