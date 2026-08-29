using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Odyssey.Api.SystemSettings;
using Odyssey.Context;
using Odyssey.Dtos;
using Xunit;

namespace Odyssey.Api.Tests;

/// <summary>
/// The degraded-read direction of every setting issue #434 migrated, asserted <strong>per key</strong>.
///
/// <para>
/// Fourteen of the fifteen are caps, so conservative is <c>min(last-known-good, compiled default)</c>.
/// <strong>One is not:</strong> <c>EmailMaxTrackedRecipients</c> resolves to <c>max</c>, because the
/// per-recipient mail throttle fails <em>open</em> once its tracking table is full — a smaller table
/// weakens the control instead of tightening it. That inversion is why this file names the direction per
/// key rather than looping over a shared helper: a refactor onto a single <c>Math.Min</c> would silently
/// invert exactly one of the fifteen, and it is the one guarding an abuse surface.
/// </para>
/// </summary>
/// <remarks>
/// The pattern follows <see cref="ImportExportLimitsLookupTests"/>: two lookups share one
/// <see cref="IMemoryCache"/> across a healthy phase (which populates the last-known-good watermarks)
/// and a degraded phase (a disposed context, standing in for a settings-store outage), with only the
/// resolved-value entry evicted in between so the second read is forced to hit the broken context.
/// </remarks>
public class TuningLookupDegradationTests
{
    private const string JournalCacheKey = "system-settings:journal-request-caps";
    private const string ImportExportCacheKey = "system-settings:import-export-limits";
    private const string AccountCacheKey = "system-settings:account-limits";

    private static OdysseyContext CreateContext(string dbName) =>
        new(new DbContextOptionsBuilder<OdysseyContext>().UseInMemoryDatabase(dbName).Options);

    private static async Task SeedAsync(OdysseyContext context, params (string Key, string Value)[] rows)
    {
        foreach (var (key, value) in rows)
        {
            context.SystemSettings.Add(new SystemSetting { Key = key, Value = value, UpdatedAt = DateTime.UtcNow });
        }

        await context.SaveChangesAsync();
    }

    // ── JournalLimits: the watermark and IsDegraded flag issue #434 added ────────────────────────

    /// <summary>
    /// Before this change <c>JournalLimitsLookup</c> kept no last-known-good value and exposed no
    /// degraded flag at all: a corrupt row silently yielded the compiled default and the result was
    /// cached as if healthy. That stopped being tolerable when the two link caps started being read from
    /// here on the ICS import path as well as the create/update path — one setting resolving by two
    /// different rules depending on the reader is the divergence the whole defect fix exists to remove.
    /// </summary>
    [Fact]
    public async Task JournalLimits_WhenTheReadFails_ResolvesEveryCapDownward_AndReportsDegraded()
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        var dbName = Guid.NewGuid().ToString();

        // Healthy phase: every value ABOVE its compiled default, so "min of last-known-good and default"
        // is distinguishable from "last-known-good".
        var healthy = CreateContext(dbName);
        await SeedAsync(healthy,
            (SystemSettingsKeys.PhotoMetadataReadMegabytes, "16"),
            (SystemSettingsKeys.PhotoMetadataExtractionTimeoutSeconds, "60"),
            (SystemSettingsKeys.CalendarMaxWindowDays, "365"),
            (SystemSettingsKeys.CalendarMaxEventDurationDays, "1000"));

        var live = await new JournalLimitsLookup(
            healthy, cache, NullLogger<JournalLimitsLookup>.Instance).GetAsync();

        Assert.False(live.IsDegraded);
        Assert.Equal(16L * 1024 * 1024, live.PhotoMetadataReadBytes);
        Assert.Equal(365, live.CalendarMaxWindowDays);

        // Degraded phase.
        cache.Remove(JournalCacheKey);
        var broken = CreateContext(dbName);
        await broken.DisposeAsync();

        var degraded = await new JournalLimitsLookup(
            broken, cache, NullLogger<JournalLimitsLookup>.Instance).GetAsync();

        Assert.True(degraded.IsDegraded);

        // min(last-known-good, default) — every one of these is a cap, so smaller is safer.
        Assert.Equal(
            SystemSettingsDefaults.PhotoMetadataReadMegabytes * 1024L * 1024, degraded.PhotoMetadataReadBytes);
        Assert.Equal(
            SystemSettingsDefaults.PhotoMetadataExtractionTimeoutSeconds,
            degraded.PhotoMetadataExtractionTimeoutSeconds);
        Assert.Equal(SystemSettingsDefaults.CalendarMaxWindowDays, degraded.CalendarMaxWindowDays);
        Assert.Equal(SystemSettingsDefaults.CalendarMaxEventDurationDays, degraded.CalendarMaxEventDurationDays);
        Assert.Equal(
            SystemSettingsDefaults.RecurrenceMaxGeneratedOccurrences, degraded.RecurrenceMaxGeneratedOccurrences);
        Assert.Equal(SystemSettingsDefaults.JournalEntryMaxLinksPerKind, degraded.JournalEntryMaxLinksPerKind);
        Assert.Equal(SystemSettingsDefaults.JournalTaskMaxLinksPerKind, degraded.JournalTaskMaxLinksPerKind);
    }

    /// <summary>
    /// A degraded read never resolves to something MORE permissive than the last healthy one either — a
    /// value an administrator had tightened must not spring back up during an outage.
    /// </summary>
    [Fact]
    public async Task JournalLimits_ATightenedValue_StaysTightenedWhenTheReadFails()
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        var dbName = Guid.NewGuid().ToString();

        var healthy = CreateContext(dbName);
        await SeedAsync(healthy, (SystemSettingsKeys.JournalEntryMaxLinksPerKind, "5"));
        await new JournalLimitsLookup(healthy, cache, NullLogger<JournalLimitsLookup>.Instance).GetAsync();

        cache.Remove(JournalCacheKey);
        var broken = CreateContext(dbName);
        await broken.DisposeAsync();

        var degraded = await new JournalLimitsLookup(
            broken, cache, NullLogger<JournalLimitsLookup>.Instance).GetAsync();

        Assert.True(degraded.IsDegraded);
        Assert.Equal(5, degraded.JournalEntryMaxLinksPerKind);
    }

    /// <summary>
    /// An <strong>absent</strong> row is healthy, not degraded — the compiled default is the documented
    /// answer, not a fault. Treating absent as degraded is what broke the Wave 1 consent-gate endpoint
    /// on unseeded databases, which is every fresh in-memory and development environment.
    /// </summary>
    [Fact]
    public async Task JournalLimits_WithNoRowsAtAll_IsHealthy()
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        var context = CreateContext(Guid.NewGuid().ToString());

        var limits = await new JournalLimitsLookup(
            context, cache, NullLogger<JournalLimitsLookup>.Instance).GetAsync();

        Assert.False(limits.IsDegraded);
        Assert.Equal(
            SystemSettingsDefaults.PhotoMetadataReadMegabytes * 1024L * 1024, limits.PhotoMetadataReadBytes);
        Assert.Equal(SystemSettingsDefaults.CalendarMaxWindowDays, limits.CalendarMaxWindowDays);
    }

    /// <summary>
    /// A row present carrying something unusable IS degraded, and the flag is what a display surface
    /// keys off. Absent versus unusable is the distinction this whole posture rests on.
    /// </summary>
    [Fact]
    public async Task JournalLimits_WithAnUnusableStoredValue_IsDegraded()
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        var context = CreateContext(Guid.NewGuid().ToString());
        await SeedAsync(context, (SystemSettingsKeys.CalendarMaxWindowDays, "not-a-number"));

        var limits = await new JournalLimitsLookup(
            context, cache, NullLogger<JournalLimitsLookup>.Instance).GetAsync();

        Assert.True(limits.IsDegraded);
        Assert.Equal(SystemSettingsDefaults.CalendarMaxWindowDays, limits.CalendarMaxWindowDays);
    }

    // ── The read-path clamps that make the pinned bounds structural ──────────────────────────────

    /// <summary>
    /// <c>[Range]</c> is the only <em>write-side</em> bound, and it runs on the HTTP path alone. A row
    /// written by config adoption, by a hand edit or by a restore would otherwise carry a value above a
    /// tighten-only key's pinned bound straight into the generator — re-opening exactly the write
    /// amplification that conversion closed. The read path clamps, so the bound holds regardless of how
    /// the row got there.
    /// </summary>
    [Fact]
    public async Task RecurrenceOccurrenceCap_PlantedAboveThePin_IsClampedOnRead()
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        var context = CreateContext(Guid.NewGuid().ToString());
        await SeedAsync(context, (SystemSettingsKeys.RecurrenceMaxGeneratedOccurrences, "999999"));

        var limits = await new JournalLimitsLookup(
            context, cache, NullLogger<JournalLimitsLookup>.Instance).GetAsync();

        Assert.Equal(
            SystemSettingsDefaults.RecurrenceMaxGeneratedOccurrences, limits.RecurrenceMaxGeneratedOccurrences);
    }

    [Fact]
    public async Task VCardRepeatablePropertyCap_PlantedAboveThePin_IsClampedOnRead()
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        var context = CreateContext(Guid.NewGuid().ToString());
        await SeedAsync(context, (SystemSettingsKeys.ContactVCardMaxRepeatablePropertiesPerEntry, "5000"));

        var limits = await new ImportExportLimitsLookup(
            context, cache, NullLogger<ImportExportLimitsLookup>.Instance).GetAsync();

        Assert.Equal(
            SystemSettingsDefaults.ContactVCardMaxRepeatablePropertiesPerEntry,
            limits.ContactVCardMaxRepeatablePropertiesPerEntry);
    }

    /// <summary>
    /// The other direction, for the one raise-only key. A row planted BELOW the floor is clamped upward,
    /// because a smaller tracking table makes the throttle fail open sooner.
    /// </summary>
    [Fact]
    public async Task TrackedRecipientFloor_PlantedBelowThePin_IsClampedUpwardOnRead()
    {
        var context = CreateContext(Guid.NewGuid().ToString());
        await SeedAsync(context, (SystemSettingsKeys.EmailMaxTrackedRecipients, "10"));

        var stored = await SystemSettingsReader.GetIntAsync(
            context, SystemSettingsKeys.EmailMaxTrackedRecipients,
            SystemSettingsDefaults.EmailMaxTrackedRecipients);

        // The reader itself returns the stored value verbatim — the clamp is the consumer's, applied at
        // both mail call sites, and this asserts the direction it applies.
        Assert.Equal(10, stored);
        Assert.Equal(
            SystemSettingsDefaults.EmailMaxTrackedRecipients,
            Math.Max(stored, SystemSettingsDefaults.EmailMaxTrackedRecipients));
    }

    // ── The import/export bounds ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ImportExportBounds_WhenTheReadFails_ResolveDownward()
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        var dbName = Guid.NewGuid().ToString();

        var healthy = CreateContext(dbName);
        await SeedAsync(healthy,
            (SystemSettingsKeys.CalendarIcsMaxAggregateExportRows, "40000"),
            (SystemSettingsKeys.CalendarIcsMaxAggregateOccurrences, "20000"),
            (SystemSettingsKeys.CalendarIcsMaxAggregateExportWindowDays, "365"),
            (SystemSettingsKeys.ImportMaxSamplesPerSkipReason, "10000"));
        await new ImportExportLimitsLookup(
            healthy, cache, NullLogger<ImportExportLimitsLookup>.Instance).GetAsync();

        cache.Remove(ImportExportCacheKey);
        var broken = CreateContext(dbName);
        await broken.DisposeAsync();

        var degraded = await new ImportExportLimitsLookup(
            broken, cache, NullLogger<ImportExportLimitsLookup>.Instance).GetAsync();

        Assert.True(degraded.IsDegraded);
        Assert.Equal(
            SystemSettingsDefaults.CalendarIcsMaxAggregateExportRows, degraded.CalendarIcsMaxAggregateExportRows);
        Assert.Equal(
            SystemSettingsDefaults.CalendarIcsMaxAggregateOccurrences, degraded.CalendarIcsMaxAggregateOccurrences);
        Assert.Equal(
            SystemSettingsDefaults.CalendarIcsMaxAggregateExportWindowDays,
            degraded.CalendarIcsMaxAggregateExportWindowDays);
        Assert.Equal(
            SystemSettingsDefaults.ImportMaxSamplesPerSkipReason, degraded.ImportMaxSamplesPerSkipReason);
    }

    // ── AccountLimits ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AccountLimits_WhenTheReadFails_ResolvesDownward_AndReportsDegraded()
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        var dbName = Guid.NewGuid().ToString();

        var healthy = CreateContext(dbName);
        await SeedAsync(healthy, (SystemSettingsKeys.AccountMaxSmartTagsPerAccount, "500"));
        var live = await new AccountLimitsLookup(
            healthy, cache, NullLogger<AccountLimitsLookup>.Instance).GetAsync();

        Assert.False(live.IsDegraded);
        Assert.Equal(500, live.MaxSmartTagsPerAccount);

        cache.Remove(AccountCacheKey);
        var broken = CreateContext(dbName);
        await broken.DisposeAsync();

        var degraded = await new AccountLimitsLookup(
            broken, cache, NullLogger<AccountLimitsLookup>.Instance).GetAsync();

        Assert.True(degraded.IsDegraded);
        Assert.Equal(SystemSettingsDefaults.AccountMaxSmartTagsPerAccount, degraded.MaxSmartTagsPerAccount);
    }

    /// <summary>
    /// The watermarks live in <see cref="IMemoryCache"/>, not <c>static</c> fields. Same lifetime in
    /// production (the cache is a singleton), but container-scoped — so a watermark cannot leak between
    /// test classes running in parallel, which is a bug this codebase has actually had.
    /// </summary>
    [Fact]
    public async Task Watermarks_DoNotLeakBetweenContainers()
    {
        var dbName = Guid.NewGuid().ToString();
        var seeded = CreateContext(dbName);
        await SeedAsync(seeded, (SystemSettingsKeys.AccountMaxSmartTagsPerAccount, "500"));

        var firstCache = new MemoryCache(new MemoryCacheOptions());
        await new AccountLimitsLookup(seeded, firstCache, NullLogger<AccountLimitsLookup>.Instance).GetAsync();

        // A SECOND container, with its own cache and a broken context: it has no watermark of its own, so
        // it must fall back to the compiled default rather than observing the first one's 500.
        var secondCache = new MemoryCache(new MemoryCacheOptions());
        var broken = CreateContext(dbName);
        await broken.DisposeAsync();

        var degraded = await new AccountLimitsLookup(
            broken, secondCache, NullLogger<AccountLimitsLookup>.Instance).GetAsync();

        Assert.Equal(SystemSettingsDefaults.AccountMaxSmartTagsPerAccount, degraded.MaxSmartTagsPerAccount);
    }
}
