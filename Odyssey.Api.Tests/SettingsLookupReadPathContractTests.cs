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

                // FinanceRequestCaps carries no degraded flag; the value is the whole observable.
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
    /// The watermark carries the TTL. Inside the window a degraded read honours a value the administrator
    /// had tightened; past it the watermark is "last known", not "last known good", and the read falls back
    /// to the cold value. Six lookups stored their watermark with no expiry before the shared resolver.
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

        // Past the TTL: the watermark has expired with every other value.
        clock.UtcNow += IntSettingResolver.CacheTtl + TimeSpan.FromSeconds(1);
        cache.Remove(probe.ResultCacheKey);
        var (expired, degraded) = await probe.Read(await BrokenContextAsync(dbName), cache);

        Assert.Equal(Math.Min(probe.ColdFallback ?? probe.Default, probe.Default), expired);
        Assert.NotEqual(false, degraded);
    }

    /// <summary>
    /// The resolver is the only place a settings lookup writes a watermark, and nothing in the folder
    /// stores a cache entry with no expiry — the drift <c>new MemoryCacheEntryOptions()</c> was.
    /// </summary>
    [Fact]
    public void NoLookup_StoresACacheEntryWithoutAnExpiry()
    {
        var folder = Path.Combine(RepositoryRootPath(), "Odyssey.Api", "SystemSettings");
        var lookups = Directory.GetFiles(folder, "*Lookup.cs");
        Assert.NotEmpty(lookups);

        foreach (var file in lookups)
        {
            var source = File.ReadAllText(file);
            Assert.DoesNotContain("new MemoryCacheEntryOptions()", source, StringComparison.Ordinal);
        }

        var resolver = File.ReadAllText(Path.Combine(folder, "IntSettingResolver.cs"));
        Assert.Contains("cache.Set(WatermarkKey(spec.Key), spec.Default, CacheTtl);", resolver, StringComparison.Ordinal);
        Assert.Contains("cache.Set(WatermarkKey(spec.Key), clamped, CacheTtl);", resolver, StringComparison.Ordinal);
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
