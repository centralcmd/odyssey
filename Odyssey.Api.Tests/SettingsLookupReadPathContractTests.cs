using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Internal;
using Microsoft.Extensions.Logging.Abstractions;
using Odyssey.Api.SystemSettings;
using Odyssey.Context;
using Odyssey.Dtos;
using Xunit;

namespace Odyssey.Api.Tests;

/// <summary>
/// One read-path contract, asserted against every settings lookup that resolves an int setting (issue
/// #287 H1). Before <see cref="IntSettingResolver"/> each lookup carried its own copy and the copies had
/// drifted: <c>ContractLimitsLookup</c> and <c>UploadLimitsLookup</c> served a row above the ceiling
/// verbatim, <c>"0"</c> was degraded in one lookup and clamped in another, and six stored the
/// last-known-good watermark with no expiry. A row per lookup here means a future copy that drifts fails
/// by name.
/// </summary>
public class SettingsLookupReadPathContractTests
{
    /// <summary>One lookup, reduced to the one key it is probed through.</summary>
    public sealed record Probe(
        string Name,
        string Key,
        int Default,
        int Min,
        int Max,
        string ResultCacheKey,
        Func<OdysseyContext, IMemoryCache, Task<(int Value, bool? IsDegraded)>> Read,
        int? ColdFallback = null)
    {
        public override string ToString() => Name;
    }

    private const long Megabyte = 1024 * 1024;

    private static readonly Probe[] Probes =
    [
        new("account", SystemSettingsKeys.AccountMaxSmartTagsPerAccount,
            SystemSettingsDefaults.AccountMaxSmartTagsPerAccount,
            SystemSettingsBounds.AccountMaxSmartTagsPerAccountMin, SystemSettingsBounds.AccountMaxSmartTagsPerAccountMax,
            "system-settings:account-limits",
            async (context, cache) =>
            {
                var limits = await new AccountLimitsLookup(context, cache, NullLogger<AccountLimitsLookup>.Instance).GetAsync();
                return (limits.MaxSmartTagsPerAccount, limits.IsDegraded);
            }),
        new("contract", SystemSettingsKeys.ContractMaxSmartTagsPerContract,
            SystemSettingsDefaults.ContractMaxSmartTagsPerContract,
            SystemSettingsBounds.ContractMaxSmartTagsPerContractMin, SystemSettingsBounds.ContractMaxSmartTagsPerContractMax,
            "system-settings:contract-limits",
            async (context, cache) =>
            {
                var limits = await new ContractLimitsLookup(context, cache, NullLogger<ContractLimitsLookup>.Instance).GetAsync();
                return (limits.MaxSmartTagsPerContract, limits.IsDegraded);
            }),
        new("property", SystemSettingsKeys.PropertyMaxSmartTagsPerProperty,
            SystemSettingsDefaults.PropertyMaxSmartTagsPerProperty,
            SystemSettingsBounds.PropertyMaxSmartTagsPerPropertyMin, SystemSettingsBounds.PropertyMaxSmartTagsPerPropertyMax,
            "system-settings:property-limits",
            async (context, cache) =>
            {
                var limits = await new PropertyLimitsLookup(context, cache, NullLogger<PropertyLimitsLookup>.Instance).GetAsync();
                return (limits.MaxSmartTagsPerProperty, limits.IsDegraded);
            }),
        new("upload", SystemSettingsKeys.FileStorageMaxUploadMegabytes,
            SystemSettingsDefaults.FileStorageMaxUploadMegabytes,
            SystemSettingsBounds.FileStorageMaxUploadMegabytesMin, SystemSettingsBounds.FileStorageMaxUploadMegabytesMax,
            "system-settings:upload-limits",
            async (context, cache) =>
            {
                var limits = await new UploadLimitsLookup(context, cache, NullLogger<UploadLimitsLookup>.Instance).GetAsync();
                Assert.Equal(limits.MaxUploadMegabytes * Megabyte, limits.MaxUploadBytes);
                return (limits.MaxUploadMegabytes, limits.IsDegraded);
            }),
        new("journal", SystemSettingsKeys.PhotoMaxLinksPerKind,
            SystemSettingsDefaults.PhotoMaxLinksPerKind,
            SystemSettingsBounds.PhotoMaxLinksPerKindMin, SystemSettingsBounds.PhotoMaxLinksPerKindMax,
            "system-settings:journal-request-caps",
            async (context, cache) =>
            {
                var limits = await new JournalLimitsLookup(context, cache, NullLogger<JournalLimitsLookup>.Instance).GetAsync();
                return (limits.PhotoMaxLinksPerKind, limits.IsDegraded);
            }),
        new("import-export bound", SystemSettingsKeys.CalendarIcsMaxAggregateExportRows,
            SystemSettingsDefaults.CalendarIcsMaxAggregateExportRows,
            SystemSettingsBounds.CalendarIcsMaxAggregateExportRowsMin, SystemSettingsBounds.CalendarIcsMaxAggregateExportRowsMax,
            "system-settings:import-export-limits",
            async (context, cache) =>
            {
                var limits = await new ImportExportLimitsLookup(context, cache, NullLogger<ImportExportLimitsLookup>.Instance).GetAsync();
                return (limits.CalendarIcsMaxAggregateExportRows, limits.IsDegraded);
            }),
        new("import-export size", SystemSettingsKeys.CalendarIcsMaxImportMegabytes,
            SystemSettingsDefaults.CalendarIcsMaxImportMegabytes,
            SystemSettingsBounds.CalendarIcsMaxImportMegabytesMin, SystemSettingsBounds.CalendarIcsMaxImportMegabytesMax,
            "system-settings:import-export-limits",
            async (context, cache) =>
            {
                var limits = await new ImportExportLimitsLookup(context, cache, NullLogger<ImportExportLimitsLookup>.Instance).GetAsync();
                return ((int)(limits.CalendarIcsMaxImportBytes / Megabyte), limits.IsDegraded);
            },
            // §11's cold floor: a size with no watermark degrades to 5 MB, not to the shipped default.
            ColdFallback: 5),
        new("system settings", SystemSettingsKeys.ContractMaxPartiesPerContract,
            SystemSettingsDefaults.ContractMaxPartiesPerContract,
            SystemSettingsBounds.ContractMaxPartiesPerContractMin, SystemSettingsBounds.ContractMaxPartiesPerContractMax,
            "system-settings:finance-request-caps",
            async (context, cache) =>
            {
                var caps = await new SystemSettingsLookup(context, cache, NullLogger<SystemSettingsLookup>.Instance).GetRequestCapsAsync();

                // FinanceRequestCaps carries no degraded flag, so the value is the whole observable here and
                // the degraded assertions are vacuous for this row. The outcome itself is pinned once, at
                // the resolver, by IntSettingResolverTests.
                return (caps.MaxPartiesPerContract, null);
            }),
    ];

    public static TheoryData<Probe> AllProbes()
    {
        var data = new TheoryData<Probe>();
        foreach (var probe in Probes)
        {
            data.Add(probe);
        }

        return data;
    }

    private static OdysseyContext CreateContext(string dbName) =>
        new(new DbContextOptionsBuilder<OdysseyContext>().UseInMemoryDatabase(dbName).Options);

    private static async Task SeedAsync(string dbName, string key, string value)
    {
        await using var context = CreateContext(dbName);
        context.SystemSettings.Add(new SystemSetting { Key = key, Value = value, UpdatedAt = DateTime.UtcNow });
        await context.SaveChangesAsync();
    }

    private static async Task<(int Value, bool? IsDegraded)> ReadStoredAsync(Probe probe, string stored)
    {
        var dbName = Guid.NewGuid().ToString();
        await SeedAsync(dbName, probe.Key, stored);
        await using var context = CreateContext(dbName);
        return await probe.Read(context, new MemoryCache(new MemoryCacheOptions()));
    }

    private static async Task<OdysseyContext> BrokenContextAsync(string dbName)
    {
        var broken = CreateContext(dbName);
        await broken.DisposeAsync();
        return broken;
    }

    /// <summary>
    /// A row above the ceiling — a hand edit, a restore, a save made before the ceiling narrowed —
    /// resolves to the ceiling, and is clamped rather than degraded because it parsed. Contract and
    /// upload served such a row verbatim before the shared resolver.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllProbes))]
    public async Task AboveTheCeiling_ResolvesToTheCeiling_NotDegraded(Probe probe)
    {
        foreach (var stored in new[] { (probe.Max + 1).ToString(CultureInfo.InvariantCulture), int.MaxValue.ToString(CultureInfo.InvariantCulture) })
        {
            var (value, degraded) = await ReadStoredAsync(probe, stored);

            Assert.Equal(probe.Max, value);
            Assert.NotEqual(true, degraded);
        }
    }

    /// <summary>
    /// <c>"0"</c> parses: it is the below-floor case, not the unparseable one, so it resolves to the floor.
    /// Account and journal sent it to the degraded fallback instead.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllProbes))]
    public async Task ZeroAndNegative_ResolveToTheFloor_NotDegraded(Probe probe)
    {
        foreach (var stored in new[] { "0", "-3" })
        {
            var (value, degraded) = await ReadStoredAsync(probe, stored);

            Assert.Equal(probe.Min, value);
            Assert.NotEqual(true, degraded);
        }
    }

    [Theory]
    [MemberData(nameof(AllProbes))]
    public async Task InsideThePair_IsHonoured(Probe probe)
    {
        var (value, degraded) = await ReadStoredAsync(probe, probe.Min.ToString(CultureInfo.InvariantCulture));

        Assert.Equal(probe.Min, value);
        Assert.NotEqual(true, degraded);
    }

    [Theory]
    [MemberData(nameof(AllProbes))]
    public async Task Unparseable_IsDegraded_AndResolvesConservatively(Probe probe)
    {
        var (value, degraded) = await ReadStoredAsync(probe, "not-a-number");

        Assert.Equal(Math.Min(probe.ColdFallback ?? probe.Default, probe.Default), value);
        Assert.NotEqual(false, degraded);
    }

    /// <summary>
    /// The watermark carries the TTL. After an explicit eviction inside the window a degraded read honours
    /// a value the administrator had tightened; once the window passes naturally the watermark is "last
    /// known", not "last known good", and the read falls back to the cold value. Six lookups stored their
    /// watermark with no expiry before the shared resolver.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllProbes))]
    public async Task TheWatermark_ExpiresWithTheTtl(Probe probe)
    {
        var clock = new ManualClock();
        var cache = new MemoryCache(new MemoryCacheOptions { Clock = clock });
        var dbName = Guid.NewGuid().ToString();
        var tightened = probe.Min;
        Assert.True(tightened < Math.Min(probe.ColdFallback ?? probe.Default, probe.Default),
            "the probe needs a tightened value distinguishable from the cold fallback");

        await SeedAsync(dbName, probe.Key, tightened.ToString(CultureInfo.InvariantCulture));
        await using (var healthy = CreateContext(dbName))
        {
            Assert.Equal(tightened, (await probe.Read(healthy, cache)).Value);
        }

        // Inside the TTL: the tightened watermark holds through an outage.
        cache.Remove(probe.ResultCacheKey);
        Assert.Equal(tightened, (await probe.Read(await BrokenContextAsync(dbName), cache)).Value);

        // Past the TTL, with no eviction: the resolved value and the watermark expire together, so a
        // fault on the next read finds no live watermark and resolves to the cold value. This is the
        // documented price of the TTL rule (IntSettingResolver, CLAUDE.md), pinned so it changes only
        // deliberately.
        clock.UtcNow += IntSettingResolver.CacheTtl + TimeSpan.FromSeconds(1);
        var (expired, degraded) = await probe.Read(await BrokenContextAsync(dbName), cache);

        Assert.Equal(Math.Min(probe.ColdFallback ?? probe.Default, probe.Default), expired);
        Assert.NotEqual(false, degraded);
    }

    /// <summary>
    /// Nothing in the settings folder stores a cache entry with no expiry — the drift
    /// <c>new MemoryCacheEntryOptions()</c> and a two-argument <c>cache.Set</c> both were. Asserted as a
    /// property of every call rather than as literal source lines, so a rename does not trip it.
    /// </summary>
    [Fact]
    public void NoSettingsCacheEntry_IsStoredWithoutAnExpiry()
    {
        var folder = Path.Combine(RepositoryRootPath(), "Odyssey.Api", "SystemSettings");
        var files = Directory.GetFiles(folder, "*.cs");
        var calls = 0;

        foreach (var file in files)
        {
            var source = File.ReadAllText(file);
            Assert.DoesNotContain("new MemoryCacheEntryOptions()", source, StringComparison.Ordinal);

            foreach (var arguments in CacheSetArguments(source))
            {
                calls++;
                Assert.True(arguments >= 3,
                    $"{Path.GetFileName(file)} calls cache.Set with {arguments} argument(s); every settings cache entry needs an expiry.");
            }
        }

        Assert.True(calls > 0, "the scan found no cache.Set calls, so it is no longer looking at the lookups");
    }

    /// <summary>Counts the top-level arguments of each <c>cache.Set(...)</c> call in a source file.</summary>
    private static IEnumerable<int> CacheSetArguments(string source)
    {
        const string marker = "cache.Set(";
        for (var start = source.IndexOf(marker, StringComparison.Ordinal);
             start >= 0;
             start = source.IndexOf(marker, start + marker.Length, StringComparison.Ordinal))
        {
            var depth = 0;
            var arguments = 1;
            for (var i = start + marker.Length; i < source.Length; i++)
            {
                var c = source[i];
                if (c is '(' or '[' or '{') depth++;
                else if (c is ']' or '}') depth--;
                else if (c == ')')
                {
                    if (depth == 0) break;
                    depth--;
                }
                else if (c == ',' && depth == 0) arguments++;
            }

            yield return arguments;
        }
    }

    private static string RepositoryRootPath()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "Odyssey.sln")))
        {
            dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
        }

        Assert.NotNull(dir);
        return dir!;
    }

    private sealed class ManualClock : ISystemClock
    {
        public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.UtcNow;
    }
}
