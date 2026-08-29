using Odyssey.Api.SystemSettings;
using Odyssey.Context;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Odyssey.Api.Tests;

/// <summary>
/// Direct coverage of <see cref="ImportExportLimitsLookup"/>'s §11 monotonic fail-safe (issue #343
/// AC 27/27b/27c/27f/28/28b) — a settings-store failure must never resolve to a value more permissive
/// than the last one this instance actually read, and never to <c>unlimited</c>, for any key.
/// </summary>
/// <remarks>
/// Two <see cref="ImportExportLimitsLookup"/> instances share one <see cref="IMemoryCache"/> across a
/// "healthy read" phase (which populates its internal last-known-good cache) and a "degraded read"
/// phase (a disposed/broken <see cref="OdysseyContext"/>, simulating a settings-store outage).
/// <see cref="EvictResolvedCache"/> removes only the 30s resolved-value entry between phases — using
/// the lookup's own cache-key literal, since it's <see langword="internal"/> and this test project has
/// no <c>InternalsVisibleTo</c> — so the second phase's read is forced to hit the (broken) context
/// rather than serving the first phase's healthy, cached result. If that literal ever changes, this
/// test fails loudly rather than silently passing for the wrong reason.
/// </remarks>
public class ImportExportLimitsLookupTests
{
    private const string ResolvedCacheKey = "system-settings:import-export-limits";

    private static void EvictResolvedCache(IMemoryCache cache) => cache.Remove(ResolvedCacheKey);

    private static OdysseyContext CreateContext(string dbName)
    {
        var options = new DbContextOptionsBuilder<OdysseyContext>().UseInMemoryDatabase(dbName).Options;
        return new OdysseyContext(options);
    }

    private static ImportExportLimitsLookup CreateLookup(OdysseyContext context, IMemoryCache cache) =>
        new(context, cache, NullLogger<ImportExportLimitsLookup>.Instance);

    [Fact]
    public async Task ColdStart_NothingEverRead_DegradesToTheColdFloors()
    {
        // AC 27: no healthy read has ever happened (no LKG), and the read itself fails — every count
        // and size resolves to its cold floor, never to a compiled default (which for the two contact
        // count caps would be unlimited).
        var cache = new MemoryCache(new MemoryCacheOptions());
        var dbName = Guid.NewGuid().ToString();
        var context = CreateContext(dbName);
        await context.DisposeAsync(); // simulates the settings store being unreachable

        var limits = await CreateLookup(context, cache).GetAsync();

        Assert.True(limits.IsDegraded);
        Assert.Equal(100_000, limits.ContactVCardMaxExportRows);
        Assert.Equal(100_000, limits.ContactVCardMaxImportEntries);
        Assert.Equal(5L * 1024 * 1024, limits.ContactVCardMaxImportBytes);
        Assert.Equal(5L * 1024 * 1024, limits.ContactVCardMaxExportBytes);
        Assert.Equal(2000, limits.CalendarIcsMaxExportEvents);
        Assert.Equal(2000, limits.CalendarIcsMaxImportEvents);
        Assert.Equal(5L * 1024 * 1024, limits.CalendarIcsMaxImportBytes);
        Assert.Equal(5L * 1024 * 1024, limits.CalendarIcsMaxExportBytes);
        Assert.Equal(2000, limits.TaskIcsMaxExportTasks);
        Assert.Equal(2000, limits.TaskIcsMaxImportTasks);
        Assert.Equal(5L * 1024 * 1024, limits.TaskIcsMaxImportBytes);
        Assert.Equal(5L * 1024 * 1024, limits.TaskIcsMaxExportBytes);
        Assert.Equal(2000, limits.JournalIcsMaxExportRows);
        Assert.Equal(2000, limits.JournalIcsMaxImportEntries);
        Assert.Equal(5L * 1024 * 1024, limits.JournalIcsMaxImportBytes);
        Assert.Equal(5L * 1024 * 1024, limits.JournalIcsMaxExportBytes);
    }

    [Fact]
    public async Task StockInstall_ContactCounts_DegradeToTheContactFloor_NotUnlimited()
    {
        // AC 27f: the min(∞, ∞) case — on a stock install the contact count caps' LKG (once read) AND
        // seeded default are both "unlimited"; a degraded read must still resolve to the 100,000 floor,
        // never to unlimited (the bug an earlier draft's min(LKG, seeded) alone had).
        var cache = new MemoryCache(new MemoryCacheOptions());
        var dbName = Guid.NewGuid().ToString();

        await using (var healthy = CreateContext(dbName))
        {
            await healthy.Database.EnsureCreatedAsync(); // seeds the 21 default rows, unedited
            var healthyResult = await CreateLookup(healthy, cache).GetAsync();
            Assert.False(healthyResult.IsDegraded);
            Assert.Null(healthyResult.ContactVCardMaxExportRows); // unlimited, as seeded
        }

        EvictResolvedCache(cache);
        await using var broken = CreateContext(dbName);
        await broken.DisposeAsync();

        var degraded = await CreateLookup(broken, cache).GetAsync();

        Assert.True(degraded.IsDegraded);
        Assert.Equal(100_000, degraded.ContactVCardMaxExportRows);
        Assert.Equal(100_000, degraded.ContactVCardMaxImportEntries);
    }

    [Fact]
    public async Task TightenedSize_DegradesToTheTightenedValue_NotTheLooserSeededDefault()
    {
        // AC 27b: monotonicity — a value the operator deliberately tightened below the seeded default
        // must survive a degraded read unchanged, not revert to the more permissive shipped default.
        var cache = new MemoryCache(new MemoryCacheOptions());
        var dbName = Guid.NewGuid().ToString();

        await using (var healthy = CreateContext(dbName))
        {
            await healthy.Database.EnsureCreatedAsync();
            var row = await healthy.SystemSettings.SingleAsync(s => s.Key == SystemSettingsKeys.ContactVCardMaxImportMegabytes);
            row.Value = "8"; // tightened well below the seeded default of 64
            await healthy.SaveChangesAsync();

            var healthyResult = await CreateLookup(healthy, cache).GetAsync();
            Assert.Equal(8L * 1024 * 1024, healthyResult.ContactVCardMaxImportBytes);
        }

        EvictResolvedCache(cache);
        await using var broken = CreateContext(dbName);
        await broken.DisposeAsync();

        var degraded = await CreateLookup(broken, cache).GetAsync();

        Assert.True(degraded.IsDegraded);
        Assert.Equal(8L * 1024 * 1024, degraded.ContactVCardMaxImportBytes);
    }

    [Fact]
    public async Task LoosenedSize_DegradesToTheSeededDefault_NotTheLooserConfiguredValue()
    {
        // AC 27c: the mirror image of the tightened case — a value raised ABOVE the seeded default must
        // fall back to the (tighter) seeded default on a degraded read, never stay at the looser one.
        var cache = new MemoryCache(new MemoryCacheOptions());
        var dbName = Guid.NewGuid().ToString();

        await using (var healthy = CreateContext(dbName))
        {
            await healthy.Database.EnsureCreatedAsync();
            var row = await healthy.SystemSettings.SingleAsync(s => s.Key == SystemSettingsKeys.CalendarIcsMaxImportMegabytes);
            row.Value = "100"; // raised above the seeded default of 64, but still inside [1, 1024]
            await healthy.SaveChangesAsync();

            var healthyResult = await CreateLookup(healthy, cache).GetAsync();
            Assert.Equal(100L * 1024 * 1024, healthyResult.CalendarIcsMaxImportBytes);
        }

        EvictResolvedCache(cache);
        await using var broken = CreateContext(dbName);
        await broken.DisposeAsync();

        var degraded = await CreateLookup(broken, cache).GetAsync();

        Assert.True(degraded.IsDegraded);
        Assert.Equal(64L * 1024 * 1024, degraded.CalendarIcsMaxImportBytes); // the seeded default, not 100
    }

    [Fact]
    public async Task DegradedRead_UnparseableRow_IsTreatedAsDegradedForThatKeyOnly()
    {
        // AC 28: a corrupt stored value is a per-key degraded read, not an unhandled exception — the
        // rest of the healthy read still succeeds.
        var cache = new MemoryCache(new MemoryCacheOptions());
        var dbName = Guid.NewGuid().ToString();
        await using var context = CreateContext(dbName);
        await context.Database.EnsureCreatedAsync();
        var row = await context.SystemSettings.SingleAsync(s => s.Key == SystemSettingsKeys.TaskIcsMaxImportMegabytes);
        row.Value = "not-a-number";
        await context.SaveChangesAsync();

        var result = await CreateLookup(context, cache).GetAsync();

        Assert.True(result.IsDegraded);
        Assert.Equal(5L * 1024 * 1024, result.TaskIcsMaxImportBytes); // floor, since no LKG was ever cached
        // An unrelated, well-formed key on the same read is unaffected.
        Assert.Equal(2000, result.JournalIcsMaxExportRows);
    }

    [Fact]
    public async Task DegradedRoundTripPair_ClampsExportToImport_WithoutRaisingTheHealthyImport()
    {
        // AC 28b: corrupting ONE member of a round-trip pair must satisfy both properties at once —
        // (a) the degraded result still respects export ≤ import, and (b) the healthy member (import)
        // is not itself loosened in the process. A validly-stored import of 500 must stay 500, not be
        // raised to the ICS floor (2000) just because its sibling export row is corrupt.
        var cache = new MemoryCache(new MemoryCacheOptions());
        var dbName = Guid.NewGuid().ToString();
        await using var context = CreateContext(dbName);
        await context.Database.EnsureCreatedAsync();

        var importRow = await context.SystemSettings.SingleAsync(s => s.Key == SystemSettingsKeys.CalendarIcsMaxImportEvents);
        importRow.Value = "500";
        var exportRow = await context.SystemSettings.SingleAsync(s => s.Key == SystemSettingsKeys.CalendarIcsMaxExportEvents);
        exportRow.Value = "corrupt";
        await context.SaveChangesAsync();

        var result = await CreateLookup(context, cache).GetAsync();

        Assert.True(result.IsDegraded);
        Assert.Equal(500, result.CalendarIcsMaxImportEvents); // property (b): unchanged, not raised to 2000
        Assert.NotNull(result.CalendarIcsMaxExportEvents);
        Assert.True(result.CalendarIcsMaxExportEvents <= result.CalendarIcsMaxImportEvents); // property (a)
    }

    [Fact]
    public async Task TaskExportRowCap_HealthyRead_IsClampedToTheImportCap_LikeTheOtherThreeSurfaces()
    {
        // Follow-up to #343: Tasks gained an export row cap (previously no export cap existed at all —
        // "Non-Goal 2"), so it now goes through the same round-trip clamp as the other three surfaces
        // even on a fully healthy read (not just under degradation).
        var cache = new MemoryCache(new MemoryCacheOptions());
        var dbName = Guid.NewGuid().ToString();
        await using var context = CreateContext(dbName);
        await context.Database.EnsureCreatedAsync();

        var exportRow = await context.SystemSettings.SingleAsync(s => s.Key == SystemSettingsKeys.TaskIcsMaxExportTasks);
        exportRow.Value = "unlimited";
        var importRow = await context.SystemSettings.SingleAsync(s => s.Key == SystemSettingsKeys.TaskIcsMaxImportTasks);
        importRow.Value = "300";
        await context.SaveChangesAsync();

        var result = await CreateLookup(context, cache).GetAsync();

        Assert.False(result.IsDegraded);
        Assert.Equal(300, result.TaskIcsMaxExportTasks); // clamped down to the finite import cap
        Assert.Equal(300, result.TaskIcsMaxImportTasks);
    }
}
