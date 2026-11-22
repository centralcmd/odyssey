using Odyssey.Context;
using Odyssey.Dtos;

namespace Odyssey.Core.Internet.News;

/// <summary>
/// The derived ceiling of the <c>NewsImageMaxSizeKb</c> setting (issue #470 §11.1): the store refuses a write above
/// <c>FileStorage:MaxFileSizeBytes</c>, so a larger image cap would do nothing. The one definition, called by the
/// settings <c>PUT</c> (through <c>RequestCapCeilings</c>), the published <c>NewsImageMaxSizeKbCeiling</c> and the
/// worker's read-side clamp.
/// </summary>
public static class NewsImageSizeCeiling
{
    /// <summary>
    /// <c>max(NewsImageMaxSizeKbMin, maxFileSizeBytes / 1024)</c>. The floor is the setting's own bound, so a deployment
    /// with a very small file-size limit can still save a value.
    /// </summary>
    public static int Kb(long maxFileSizeBytes) =>
        (int)Math.Clamp(maxFileSizeBytes / 1024, SystemSettingsBounds.NewsImageMaxSizeKbMin, int.MaxValue);
}

/// <summary>
/// The derived ceiling of the <c>PodcastAudioMaxEpisodeMb</c> setting (issue #556 §8.2), the
/// <see cref="NewsImageSizeCeiling"/> recipe: the store refuses a write above <c>FileStorage:MaxFileSizeBytes</c>.
/// Called by the settings <c>PUT</c> (through <c>RequestCapCeilings</c>), the published
/// <c>PodcastAudioMaxEpisodeMbCeiling</c> and the worker's read-side clamp.
/// </summary>
public static class PodcastAudioSizeCeiling
{
    private const long BytesPerMegabyte = 1024 * 1024;

    /// <summary><c>max(PodcastAudioMaxEpisodeMbMin, maxFileSizeBytes / 1 MiB)</c>.</summary>
    public static int Mb(long maxFileSizeBytes) =>
        (int)Math.Clamp(maxFileSizeBytes / BytesPerMegabyte, SystemSettingsBounds.PodcastAudioMaxEpisodeMbMin, int.MaxValue);

    /// <summary>The effective per-episode byte cap: the setting, never above what the store accepts.</summary>
    public static long Bytes(int settingMb, long maxFileSizeBytes) =>
        Math.Min(Math.Min(settingMb, Mb(maxFileSizeBytes)) * BytesPerMegabyte, maxFileSizeBytes);
}

/// <summary>
/// The text-and-source quota predicate (§6.3). The worker's ingest skip, the quota-reason recompute and
/// <c>GET /api/news-status</c> all call it; a source lint keeps it the only comparison.
/// </summary>
public static class NewsTextQuota
{
    /// <summary>The bytes in one GiB, the setting's unit.</summary>
    public const long BytesPerGiB = 1L << 30;

    /// <summary>The quota in bytes for a resolved <c>NewsTextStoreMaxGiB</c>.</summary>
    public static long QuotaBytes(int maxGiB) => maxGiB * BytesPerGiB;

    /// <summary>True once the stored text and source bytes have reached the quota.</summary>
    public static bool IsReached(long textAndSourceBytes, long quotaBytes) => textAndSourceBytes >= quotaBytes;
}

/// <summary>
/// The per-feed daily article cap (§6.3): reached only on the SAME UTC day, so yesterday's
/// <see cref="NewsFeedFetchState.ArticlesToday"/> can never keep it set. Serves the ingest skip and the quota-reason
/// recompute.
/// </summary>
public static class NewsDailyCap
{
    public static bool IsReached(NewsFeedFetchState feed, DateOnly todayUtc, int cap)
    {
        ArgumentNullException.ThrowIfNull(feed);
        return feed.DailyQuotaDate == todayUtc && feed.ArticlesToday >= cap;
    }
}
