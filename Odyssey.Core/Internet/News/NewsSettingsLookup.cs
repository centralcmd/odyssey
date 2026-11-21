using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Odyssey.Context;
using Odyssey.Core.Finance;
using Odyssey.Core.SystemSettings;
using Odyssey.Dtos;

namespace Odyssey.Core.Internet.News;

/// <summary>
/// The resolved int settings of the news reader (issue #470 §11.1), with the OPML import's entry cap (issue #508 §8.5).
/// <see cref="IsDegraded"/> says at least one was resolved conservatively; the values are still usable.
/// </summary>
public sealed record NewsSettings(
    int DefaultFetchIntervalMinutes,
    int MaxFeeds,
    int ImageMaxPerArticle,
    int ImageMaxSizeKb,
    int ImageStoreMaxGiB,
    int TextStoreMaxGiB,
    int FeedMaxArticlesPerDay,
    int FeedMaxImageMbPerDay,
    int OpmlMaxImportEntries,
    int PodcastAudioStoreMaxGiB = SystemSettingsDefaults.PodcastAudioStoreMaxGiB,
    int PodcastAudioMaxEpisodeMb = SystemSettingsDefaults.PodcastAudioMaxEpisodeMb,
    int PodcastAutoDownloadsPerFeedPerDay = SystemSettingsDefaults.PodcastAutoDownloadsPerFeedPerDay,
    int PodcastAutoDownloadMaxUnfetchedPerFeed = SystemSettingsDefaults.PodcastAutoDownloadMaxUnfetchedPerFeed,
    bool IsDegraded = false)
{
    /// <summary>The shipped defaults: what an unseeded store resolves to.</summary>
    public static NewsSettings Defaults { get; } = new(
        SystemSettingsDefaults.NewsDefaultFetchIntervalMinutes,
        SystemSettingsDefaults.NewsMaxFeeds,
        SystemSettingsDefaults.NewsImageMaxPerArticle,
        SystemSettingsDefaults.NewsImageMaxSizeKb,
        SystemSettingsDefaults.NewsImageStoreMaxGiB,
        SystemSettingsDefaults.NewsTextStoreMaxGiB,
        SystemSettingsDefaults.NewsFeedMaxArticlesPerDay,
        SystemSettingsDefaults.NewsFeedMaxImageMbPerDay,
        SystemSettingsDefaults.NewsOpmlMaxImportEntries);

    public long ImageMaxBytes => ImageMaxSizeKb * 1024L;

    public long ImageStoreQuotaBytes => ImageStoreMaxGiB * NewsTextQuota.BytesPerGiB;

    /// <summary>The resolved text-and-source quota in bytes: what <see cref="NewsTextQuota.IsReached"/> compares against.</summary>
    public long TextQuotaBytes => NewsTextQuota.QuotaBytes(TextStoreMaxGiB);

    public long FeedMaxImageBytesPerDay => FeedMaxImageMbPerDay * 1024L * 1024L;

    /// <summary>The resolved audio quota in bytes (issue #556 §6.4).</summary>
    public long PodcastAudioQuotaBytes => PodcastAudioStoreMaxGiB * NewsTextQuota.BytesPerGiB;
}

/// <summary>
/// The news reader's settings on the read path (issue #470 §11.1, §7.9). Both hosts resolve through it: the worker for
/// its caps and quotas, the API for <c>GET /api/news-status</c> and the feed limit. The allow-list is read UNCACHED on
/// every call, so a revoked grant binds on the next fetch.
/// </summary>
public interface INewsSettingsLookup
{
    /// <summary>The int settings, on the shared 30 s TTL.</summary>
    Task<NewsSettings> GetAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// <c>NewsPrivateHostAllowList</c>, read live. A failed read or a stored value that does not parse resolves to
    /// <em>no grant at all</em> — a degraded read must never open an address (§7.9, AC 38).
    /// </summary>
    Task<IReadOnlyList<NewsPrivateHostGrant>> GetAllowListAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Backs <see cref="INewsSettingsLookup"/> through <see cref="IntSettingResolver"/>. Each setting's degraded direction
/// is resolved on its own (CLAUDE.md: never a shared <c>Math.Min</c>): the default polling interval is a floor on how
/// often publishers are contacted, so a degraded read moves it LARGER (slower polling); every other key is a cap or a
/// quota, so it moves SMALLER. <c>NewsImageMaxSizeKb</c> is also clamped to its derived ceiling,
/// <see cref="NewsImageSizeCeiling.Kb"/>, as a backstop to the write-side check. A degraded resolution is not cached, so
/// recovery is immediate on the next good read — the weather lookup's posture.
/// </summary>
public sealed class NewsSettingsLookup(
    OdysseyContext context,
    IMemoryCache cache,
    IOptions<FileStorageOptions> fileStorageOptions,
    ILogger<NewsSettingsLookup> logger) : INewsSettingsLookup
{
    public const string CacheKey = "system-settings:news";

    /// <summary>The keys this entry serves; <c>SystemSettingsCacheEvictionTests</c> reads it. The allow-list is not one.</summary>
    private static readonly string[] Keys =
    [
        SystemSettingsKeys.NewsDefaultFetchIntervalMinutes,
        SystemSettingsKeys.NewsMaxFeeds,
        SystemSettingsKeys.NewsImageMaxPerArticle,
        SystemSettingsKeys.NewsImageMaxSizeKb,
        SystemSettingsKeys.NewsImageStoreMaxGiB,
        SystemSettingsKeys.NewsTextStoreMaxGiB,
        SystemSettingsKeys.NewsFeedMaxArticlesPerDay,
        SystemSettingsKeys.NewsFeedMaxImageMbPerDay,
        SystemSettingsKeys.NewsOpmlMaxImportEntries,
        SystemSettingsKeys.PodcastAudioStoreMaxGiB,
        SystemSettingsKeys.PodcastAudioMaxEpisodeMb,
        SystemSettingsKeys.PodcastAutoDownloadsPerFeedPerDay,
        SystemSettingsKeys.PodcastAutoDownloadMaxUnfetchedPerFeed,
    ];

    private static readonly IntSettingSpec DefaultInterval = new(
        SystemSettingsKeys.NewsDefaultFetchIntervalMinutes,
        SystemSettingsDefaults.NewsDefaultFetchIntervalMinutes,
        SystemSettingsBounds.NewsDefaultFetchIntervalMinutesMin,
        SystemSettingsBounds.NewsDefaultFetchIntervalMinutesMax,
        ConservativeDirection.Larger);

    private static readonly IntSettingSpec MaxFeeds = new(
        SystemSettingsKeys.NewsMaxFeeds, SystemSettingsDefaults.NewsMaxFeeds,
        SystemSettingsBounds.NewsMaxFeedsMin, SystemSettingsBounds.NewsMaxFeedsMax);

    private static readonly IntSettingSpec ImageMaxPerArticle = new(
        SystemSettingsKeys.NewsImageMaxPerArticle, SystemSettingsDefaults.NewsImageMaxPerArticle,
        SystemSettingsBounds.NewsImageMaxPerArticleMin, SystemSettingsBounds.NewsImageMaxPerArticleMax);

    private static readonly IntSettingSpec ImageMaxSizeKb = new(
        SystemSettingsKeys.NewsImageMaxSizeKb, SystemSettingsDefaults.NewsImageMaxSizeKb,
        SystemSettingsBounds.NewsImageMaxSizeKbMin, SystemSettingsBounds.NewsImageMaxSizeKbMax);

    private static readonly IntSettingSpec ImageStoreMaxGiB = new(
        SystemSettingsKeys.NewsImageStoreMaxGiB, SystemSettingsDefaults.NewsImageStoreMaxGiB,
        SystemSettingsBounds.NewsImageStoreMaxGiBMin, SystemSettingsBounds.NewsImageStoreMaxGiBMax);

    private static readonly IntSettingSpec TextStoreMaxGiB = new(
        SystemSettingsKeys.NewsTextStoreMaxGiB, SystemSettingsDefaults.NewsTextStoreMaxGiB,
        SystemSettingsBounds.NewsTextStoreMaxGiBMin, SystemSettingsBounds.NewsTextStoreMaxGiBMax);

    private static readonly IntSettingSpec FeedMaxArticlesPerDay = new(
        SystemSettingsKeys.NewsFeedMaxArticlesPerDay, SystemSettingsDefaults.NewsFeedMaxArticlesPerDay,
        SystemSettingsBounds.NewsFeedMaxArticlesPerDayMin, SystemSettingsBounds.NewsFeedMaxArticlesPerDayMax);

    private static readonly IntSettingSpec FeedMaxImageMbPerDay = new(
        SystemSettingsKeys.NewsFeedMaxImageMbPerDay, SystemSettingsDefaults.NewsFeedMaxImageMbPerDay,
        SystemSettingsBounds.NewsFeedMaxImageMbPerDayMin, SystemSettingsBounds.NewsFeedMaxImageMbPerDayMax);

    private static readonly IntSettingSpec OpmlMaxImportEntries = new(
        SystemSettingsKeys.NewsOpmlMaxImportEntries, SystemSettingsDefaults.NewsOpmlMaxImportEntries,
        SystemSettingsBounds.NewsOpmlMaxImportEntriesMin, SystemSettingsBounds.NewsOpmlMaxImportEntriesMax);

    private static readonly IntSettingSpec AudioStoreMaxGiB = new(
        SystemSettingsKeys.PodcastAudioStoreMaxGiB, SystemSettingsDefaults.PodcastAudioStoreMaxGiB,
        SystemSettingsBounds.PodcastAudioStoreMaxGiBMin, SystemSettingsBounds.PodcastAudioStoreMaxGiBMax);

    private static readonly IntSettingSpec AudioMaxEpisodeMb = new(
        SystemSettingsKeys.PodcastAudioMaxEpisodeMb, SystemSettingsDefaults.PodcastAudioMaxEpisodeMb,
        SystemSettingsBounds.PodcastAudioMaxEpisodeMbMin, SystemSettingsBounds.PodcastAudioMaxEpisodeMbMax);

    private static readonly IntSettingSpec AutoDownloadsPerDay = new(
        SystemSettingsKeys.PodcastAutoDownloadsPerFeedPerDay, SystemSettingsDefaults.PodcastAutoDownloadsPerFeedPerDay,
        SystemSettingsBounds.PodcastAutoDownloadsPerFeedPerDayMin, SystemSettingsBounds.PodcastAutoDownloadsPerFeedPerDayMax);

    private static readonly IntSettingSpec AutoDownloadMaxUnfetched = new(
        SystemSettingsKeys.PodcastAutoDownloadMaxUnfetchedPerFeed, SystemSettingsDefaults.PodcastAutoDownloadMaxUnfetchedPerFeed,
        SystemSettingsBounds.PodcastAutoDownloadMaxUnfetchedPerFeedMin, SystemSettingsBounds.PodcastAutoDownloadMaxUnfetchedPerFeedMax);

    private readonly IntSettingResolver resolver = new(cache, logger, CacheKey);

    public async Task<NewsSettings> GetAsync(CancellationToken cancellationToken = default)
    {
        if (cache.TryGetValue(CacheKey, out NewsSettings? cached) && cached is not null)
        {
            return cached;
        }

        var (values, readFailed) = await resolver.ReadAsync(context, Keys, "news settings", cancellationToken);
        var interval = resolver.Resolve(DefaultInterval, values, readFailed);
        var maxFeeds = resolver.Resolve(MaxFeeds, values, readFailed);
        var perArticle = resolver.Resolve(ImageMaxPerArticle, values, readFailed);
        var sizeKb = resolver.Resolve(ImageMaxSizeKb, values, readFailed);
        var imageStore = resolver.Resolve(ImageStoreMaxGiB, values, readFailed);
        var textStore = resolver.Resolve(TextStoreMaxGiB, values, readFailed);
        var articlesPerDay = resolver.Resolve(FeedMaxArticlesPerDay, values, readFailed);
        var imageMbPerDay = resolver.Resolve(FeedMaxImageMbPerDay, values, readFailed);
        var opmlEntries = resolver.Resolve(OpmlMaxImportEntries, values, readFailed);
        var audioStore = resolver.Resolve(AudioStoreMaxGiB, values, readFailed);
        var episodeMb = resolver.Resolve(AudioMaxEpisodeMb, values, readFailed);
        var autoPerDay = resolver.Resolve(AutoDownloadsPerDay, values, readFailed);
        var autoUnfetched = resolver.Resolve(AutoDownloadMaxUnfetched, values, readFailed);

        var degraded = readFailed || new[]
            {
                interval, maxFeeds, perArticle, sizeKb, imageStore, textStore, articlesPerDay, imageMbPerDay, opmlEntries,
                audioStore, episodeMb, autoPerDay, autoUnfetched,
            }
            .Any(setting => setting.IsDegraded);

        // The read-side backstop of the derived ceiling (§11.1): the same helper the settings PUT validates with.
        var ceiling = NewsImageSizeCeiling.Kb(fileStorageOptions.Value.MaxFileSizeBytes);
        var settings = new NewsSettings(
            interval.Value,
            maxFeeds.Value,
            perArticle.Value,
            Math.Min(sizeKb.Value, ceiling),
            imageStore.Value,
            textStore.Value,
            articlesPerDay.Value,
            imageMbPerDay.Value,
            opmlEntries.Value,
            audioStore.Value,
            // The read-side backstop of the episode cap's derived ceiling (issue #556 §8.2).
            Math.Min(episodeMb.Value, PodcastAudioSizeCeiling.Mb(fileStorageOptions.Value.MaxFileSizeBytes)),
            autoPerDay.Value,
            autoUnfetched.Value,
            degraded);
        if (!degraded)
        {
            cache.Set(CacheKey, settings, IntSettingResolver.CacheTtl);
        }

        return settings;
    }

    public async Task<IReadOnlyList<NewsPrivateHostGrant>> GetAllowListAsync(CancellationToken cancellationToken = default)
    {
        string? stored;
        try
        {
            stored = await context.SystemSettings.AsNoTracking()
                .Where(row => row.Key == SystemSettingsKeys.NewsPrivateHostAllowList)
                .Select(row => row.Value)
                .FirstOrDefaultAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "Reading the news private-host allow-list failed; every private address is blocked until it reads again.");
            return [];
        }

        if (string.IsNullOrEmpty(stored))
        {
            return [];
        }

        var grants = NewsPrivateHostAllowListRule.Parse(stored);
        if (grants.Count == 0)
        {
            // Never the value: the key is enough for an operator to find the row.
            logger.LogError("The stored {Key} does not parse; every private address is blocked until it is fixed.", SystemSettingsKeys.NewsPrivateHostAllowList);
        }

        return grants;
    }
}
