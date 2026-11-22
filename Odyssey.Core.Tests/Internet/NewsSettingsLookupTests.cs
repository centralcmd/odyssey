using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Odyssey.Context;
using Odyssey.Core.Finance;
using Odyssey.Core.Internet.News;
using Odyssey.Dtos;
using Odyssey.Testing;
using Xunit;

namespace Odyssey.Core.Tests.Internet;

/// <summary>
/// The news settings on the read path (issue #470 §11.1, AC 38, AC 63): each setting's degraded direction resolved on
/// its own, the derived image-size ceiling as a read-side backstop, and the allow-list read live, a degraded read
/// opening nothing.
/// </summary>
public sealed class NewsSettingsLookupTests : IDisposable
{
    private readonly OdysseyContext context = new(new DbContextOptionsBuilder<OdysseyContext>()
        .UseInMemoryDatabase($"news-settings-{Guid.NewGuid():N}").Options);

    private readonly MemoryCache cache = new(new MemoryCacheOptions());

    public void Dispose()
    {
        context.Dispose();
        cache.Dispose();
    }

    // The shipped transport ceiling (10 GiB), so neither derived ceiling — the image size's or, since issue #556, the
    // podcast episode size's — clamps a shipped default. Ceiling tests pass their own value.
    private NewsSettingsLookup Lookup(long maxFileSizeBytes = 10L * 1024 * 1024 * 1024) =>
        new(context, cache, Options.Create(new FileStorageOptions { MaxFileSizeBytes = maxFileSizeBytes }), NullLogger<NewsSettingsLookup>.Instance);

    private async Task SetAsync(string key, string value)
    {
        var row = await context.SystemSettings.FirstOrDefaultAsync(r => r.Key == key);
        if (row is null)
        {
            context.SystemSettings.Add(new SystemSetting { Key = key, Value = value });
        }
        else
        {
            row.Value = value;
        }

        await context.SaveChangesAsync();
        cache.Remove(NewsSettingsLookup.CacheKey); // what a settings save evicts
    }

    [Fact]
    public async Task An_unseeded_store_resolves_to_the_shipped_defaults_and_is_healthy()
    {
        var settings = await Lookup().GetAsync();

        Assert.Equal(NewsSettings.Defaults, settings);
        Assert.Equal(20L << 30, settings.TextQuotaBytes);
    }

    [Fact]
    public async Task AC38_a_degraded_default_interval_moves_larger_and_a_degraded_cap_moves_smaller()
    {
        await SetAsync(SystemSettingsKeys.NewsDefaultFetchIntervalMinutes, "30");
        await SetAsync(SystemSettingsKeys.NewsMaxFeeds, "500");
        await SetAsync(SystemSettingsKeys.NewsTextStoreMaxGiB, "100");
        var lookup = Lookup();
        var healthy = await lookup.GetAsync();
        Assert.Equal((30, 500, 100), (healthy.DefaultFetchIntervalMinutes, healthy.MaxFeeds, healthy.TextStoreMaxGiB));

        await SetAsync(SystemSettingsKeys.NewsDefaultFetchIntervalMinutes, "not a number");
        await SetAsync(SystemSettingsKeys.NewsMaxFeeds, "not a number");
        await SetAsync(SystemSettingsKeys.NewsTextStoreMaxGiB, "not a number");
        var degraded = await lookup.GetAsync();

        Assert.True(degraded.IsDegraded);
        // Slower polling: max(last-known-good 30, default 60).
        Assert.Equal(SystemSettingsDefaults.NewsDefaultFetchIntervalMinutes, degraded.DefaultFetchIntervalMinutes);
        // A cap: min(last-known-good 500, default 200); the quota likewise min(100, 20).
        Assert.Equal(SystemSettingsDefaults.NewsMaxFeeds, degraded.MaxFeeds);
        Assert.Equal(SystemSettingsDefaults.NewsTextStoreMaxGiB, degraded.TextStoreMaxGiB);
    }

    [Fact]
    public async Task A_stored_value_outside_its_pair_is_clamped_not_degraded()
    {
        await SetAsync(SystemSettingsKeys.NewsFeedMaxArticlesPerDay, "0");

        var settings = await Lookup().GetAsync();

        Assert.False(settings.IsDegraded);
        Assert.Equal(SystemSettingsBounds.NewsFeedMaxArticlesPerDayMin, settings.FeedMaxArticlesPerDay);
    }

    [Fact]
    public async Task AC63_the_image_size_cap_is_clamped_to_the_derived_ceiling_on_the_read_path()
    {
        await SetAsync(SystemSettingsKeys.NewsImageMaxSizeKb, "5120");

        Assert.Equal(1024, (await Lookup(maxFileSizeBytes: 1024 * 1024).GetAsync()).ImageMaxSizeKb);
        cache.Remove(NewsSettingsLookup.CacheKey);
        // Below 64 KiB the ceiling is the setting's own floor.
        Assert.Equal(SystemSettingsBounds.NewsImageMaxSizeKbMin, (await Lookup(maxFileSizeBytes: 1000).GetAsync()).ImageMaxSizeKb);
    }

    [Fact]
    public async Task AC38_the_allow_list_is_read_live_and_a_value_that_does_not_parse_grants_nothing()
    {
        var lookup = Lookup();
        Assert.Empty(await lookup.GetAllowListAsync());

        await SetAsync(SystemSettingsKeys.NewsPrivateHostAllowList, "feeds.lan:8080, 10.0.0.5");
        var grants = await lookup.GetAllowListAsync();
        Assert.Equal([new NewsPrivateHostGrant("feeds.lan", 8080), new NewsPrivateHostGrant("10.0.0.5", null)], grants);

        // No cache: the next read sees the change at once.
        await SetAsync(SystemSettingsKeys.NewsPrivateHostAllowList, "this is not, a valid :: list!");
        Assert.Empty(await lookup.GetAllowListAsync());
    }
}

/// <summary>AC 62: <c>NewsTextQuota.IsReached</c> is the only comparison of the text total against its quota.</summary>
public sealed class NewsTextQuotaSourceLintTests
{
    [Fact]
    public void AC62_no_source_compares_the_text_total_itself()
    {
        var pattern = new System.Text.RegularExpressions.Regex(@"TextAndSourceBytes\s*(>=|<=|>|<)|(?<![=-])(>=|<=|>|<)\s*\w*\.?TextAndSourceBytes\b");
        var scanned = 0;
        var offenders = new List<string>();
        foreach (var project in new[] { "Odyssey.Core", "Odyssey.Api", "Odyssey.Worker" })
        {
            foreach (var file in SourceTree.Files(project))
            {
                scanned++;
                if (pattern.IsMatch(File.ReadAllText(file)))
                {
                    offenders.Add(SourceTree.Relative(file));
                }
            }
        }

        Assert.True(scanned > 100, "the lint scanned nothing");
        Assert.Empty(offenders);
    }
}
