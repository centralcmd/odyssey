namespace Odyssey.Core.Internet.News.Podcasts;

/// <summary>The podcast jobs' fixed timings and limits (issue #556 §3.4–§3.7), in one place.</summary>
public static class PodcastTiming
{
    /// <summary>A feed's artwork incident count is cleared this long after its last incident (§3.4 artwork step 3).</summary>
    public static readonly TimeSpan ArtworkIncidentReset = TimeSpan.FromDays(30);

    /// <summary>An artwork or chapter-file row lease.</summary>
    public static readonly TimeSpan PhaseLease = TimeSpan.FromMinutes(3);

    /// <summary>Artwork rows fetched per tick per process.</summary>
    public const int ArtworkPerTick = 20;

    /// <summary>Chapter files fetched per tick per process.</summary>
    public const int ChaptersPerTick = 20;

    /// <summary>The artwork retry schedule; past its end the row is <c>Failed</c>.</summary>
    public static readonly TimeSpan[] ArtworkBackoff =
        [TimeSpan.FromHours(1), TimeSpan.FromHours(6), TimeSpan.FromHours(24), TimeSpan.FromHours(72)];

    /// <summary>The chapter-file and audio retry schedule; past its end the row is <c>Failed</c>.</summary>
    public static readonly TimeSpan[] Backoff = [TimeSpan.FromHours(1), TimeSpan.FromHours(6), TimeSpan.FromHours(24)];

    /// <summary>A deferred audio or artwork row is retried hourly without spending an attempt.</summary>
    public static readonly TimeSpan DeferredRetry = TimeSpan.FromHours(1);

    /// <summary>The largest chapter file read, decompressed (§6.3).</summary>
    public const int MaxChapterFileBytes = 1024 * 1024;

    /// <summary>The artwork breaker: this many incidents in <see cref="ArtworkBreakerWindow"/> pause the artwork phase.</summary>
    public const int ArtworkBreakerThreshold = 3;

    public static readonly TimeSpan ArtworkBreakerWindow = TimeSpan.FromHours(1);

    /// <summary>The audio download lease; renewed every <see cref="AudioLeaseRenewal"/> while bytes flow (§3.6).</summary>
    public static readonly TimeSpan AudioLease = TimeSpan.FromMinutes(3);

    public static readonly TimeSpan AudioLeaseRenewal = TimeSpan.FromSeconds(60);

    /// <summary>The download job's tick, and its stale window (the longest download plus five minutes).</summary>
    public static readonly TimeSpan DownloadTick = TimeSpan.FromSeconds(30);

    public static readonly TimeSpan AudioTotalDeadlineMax = TimeSpan.FromMinutes(60);

    public static readonly TimeSpan DownloadStaleAfter = AudioTotalDeadlineMax + TimeSpan.FromMinutes(5);

    /// <summary>The retention job's tick (§3.7) and its stale window.</summary>
    public static readonly TimeSpan RetentionTick = TimeSpan.FromHours(24);

    public static readonly TimeSpan RetentionStaleAfter = TimeSpan.FromHours(1);

    /// <summary>Rows the purge loop's cleanup and the retention job release per save (§3.7, §3.8).</summary>
    public const int ReleaseChunk = 100;
}
