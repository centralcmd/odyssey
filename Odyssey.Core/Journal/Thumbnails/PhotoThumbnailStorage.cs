using Odyssey.Context;
using Odyssey.Core.FileStorage;
using Odyssey.Dtos.Journal;

namespace Odyssey.Core.Journal.Thumbnails;

/// <summary>
/// The thumbnail feature on the #346 store (issue #362 §3.2): <c>photo-thumbnails/&lt;size&gt;/&lt;bucket&gt;/&lt;stem&gt;.webp</c>,
/// original names, released immediately (photos are personal data).
/// </summary>
public static class PhotoThumbnailStorage
{
    public const string FeatureKey = "PhotoThumbnails";

    public static readonly StorageFeature Feature = StorageFeature.Create<PhotoThumbnailLayout>(
        FeatureKey,
        "photo-thumbnails",
        args => [PhotoThumbnailRecipe.LongSide(args.Size).ToString(System.Globalization.CultureInfo.InvariantCulture),
            args.Bucket.ToString(System.Globalization.CultureInfo.InvariantCulture)],
        StorageReleaseMode.Immediate,
        StorageFeatureReference.To<PhotoThumbnail>(t => t.StoredObjectId));
}

/// <summary>A folder per size, then a numbered bucket shared by both sizes of one photo (§3.2.1).</summary>
public sealed record PhotoThumbnailLayout(ThumbnailSize Size, int Bucket);

/// <summary>The bucket capacity, as an options class so a test can make it small (§3.2.1).</summary>
public sealed class PhotoThumbnailOptions
{
    public int BucketCapacity { get; set; } = PhotoThumbnailRecipe.BucketCapacity;
}

/// <summary>
/// Every thumbnail constant (§8). None is a setting: a change means regeneration, which is a
/// <see cref="Version"/> bump in code.
/// </summary>
public static class PhotoThumbnailRecipe
{
    /// <summary>Bumping this regenerates every photo through the discovery sweep (§3.4).</summary>
    public const int Version = 1;

    public const int SmallLongSide = 200;
    public const int MediumLongSide = 400;
    public const int WebPQuality = 80;
    public const int WebPMethod = 4;

    /// <summary>The largest original the renderer reads, independent of the upload ceiling (§3.1 item 6).</summary>
    public const long MaxSourceBytes = 64L * 1024 * 1024;

    /// <summary>The largest thumbnail written (§3.3 step 4).</summary>
    public const long MaxOutputBytes = 2L * 1024 * 1024;

    public const int BucketCapacity = 10_000;

    public static int LongSide(ThumbnailSize size) => size switch
    {
        ThumbnailSize.Small => SmallLongSide,
        ThumbnailSize.Medium => MediumLongSide,
        _ => throw new ArgumentOutOfRangeException(nameof(size)),
    };
}

/// <summary>The cadences and bounds of the job (§3.3, §3.6, Amendment v7.1).</summary>
public static class PhotoThumbnailTiming
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan RunBudget = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan Lease = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan RenewEvery = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan JobDeadline = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan RenameCooldown = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan Watchdog = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan OrientationWatchdog = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan BoundedPause = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan ExitGrace = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan BreakerWindow = TimeSpan.FromHours(1);
    public static readonly TimeSpan RestartWindow = TimeSpan.FromHours(1);
    public static readonly TimeSpan HeartbeatFresh = TimeSpan.FromMinutes(3);
    public static readonly TimeSpan RowRetention = TimeSpan.FromHours(24);

    /// <summary>A storage or database outage: retried soon, since the health gate already pauses claiming.</summary>
    public static readonly TimeSpan OutageBackoff = TimeSpan.FromMinutes(1);

    public const int BreakerThreshold = 3;
    public const int MaxCountedAttempts = 6;
    public const int MaxLeaseExpiries = 2;
    public const int InteractivePerUploader = 20;
    public const int BackgroundEveryNthClaim = 4;
    public const int MaxUploadersConsidered = 200;
    public const int DiscoveryBatch = 500;
    public const int CleanupEveryNthTick = 240;
    public const int BucketAttempts = 3;
    public const int ShutdownReleaseAttempts = 3;

    /// <summary>The backoff after a counted failure: 1, 5, 30, 60 minutes, then capped at 60.</summary>
    public static TimeSpan Backoff(int countedAttempts) => countedAttempts switch
    {
        <= 1 => TimeSpan.FromMinutes(1),
        2 => TimeSpan.FromMinutes(5),
        3 => TimeSpan.FromMinutes(30),
        _ => TimeSpan.FromMinutes(60),
    };
}

/// <summary>The §9 failure codes. Only these reach a log line or the job row — never a message or a name.</summary>
public static class PhotoThumbnailFailure
{
    public const string Type = "type";
    public const string TooLarge = "too-large";
    public const string Format = "format";
    public const string Decode = "decode";
    public const string Timeout = "timeout";
    public const string Crash = "crash";
    public const string Exhausted = "failed";
}
