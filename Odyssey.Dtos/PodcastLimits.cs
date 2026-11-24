namespace Odyssey.Dtos;

/// <summary>
/// The compile-time bounds of podcasts in the news reader (issue #556). Every DTO annotation, entity length and
/// client-side check names one of these, so the server, the database and the WebAssembly client cannot drift apart.
/// Admin-editable caps are <c>SystemSettings</c> rows instead, and are never restated here.
/// </summary>
public static class PodcastLimits
{
    /// <summary>The fewest days an auto-delete rule may wait (§4.1).</summary>
    public const int MinAutoDeleteDays = 1;

    /// <summary>The most days an auto-delete rule may wait: ten years (§4.1).</summary>
    public const int MaxAutoDeleteDays = 3650;

    /// <summary>The days an auto-delete rule waits when it is first turned on (user decision).</summary>
    public const int DefaultAutoDeleteDays = 30;

    /// <summary>A plain-text episode description (§4.3).</summary>
    public const int MaxDescriptionLength = 4000;

    /// <summary>An episode's duration ceiling in seconds: one week (§4.3).</summary>
    public const int MaxDurationSeconds = 604_800;

    /// <summary>A season or episode number ceiling (§4.3).</summary>
    public const int MaxEpisodeNumber = 100_000;

    /// <summary>A chapter title (§4.5).</summary>
    public const int MaxChapterTitleLength = 256;

    /// <summary>The chapters kept per episode per source (§3.4).</summary>
    public const int MaxChapters = 500;

    /// <summary>The newest chapter start or end, in milliseconds: one week (§4.5).</summary>
    public const long MaxChapterMilliseconds = MaxDurationSeconds * 1000L;

    /// <summary>
    /// After a listen is marked finished, a position save inside this many seconds is ignored (issue #593 §5), so a late
    /// periodic or page-hide save cannot bring back a resume point on a finished episode.
    /// </summary>
    public const int PlaybackCompletionGuardSeconds = 120;

    /// <summary>The distinct artwork images one feed may hold (§3.2): 1,000 × 3 files keeps a folder under 10,000.</summary>
    public const int MaxArtworkPerFeed = 1000;

    /// <summary>A podcast author (§4.2).</summary>
    public const int MaxAuthorLength = 256;

    /// <summary>A stored audio content type: <c>audio/mpeg</c>, <c>audio/aac</c>, <c>audio/mp4</c>, <c>audio/ogg</c>, <c>audio/flac</c>.</summary>
    public const int MaxAudioContentTypeLength = 32;

    /// <summary>An enclosure's declared content type, kept as the feed carried it.</summary>
    public const int MaxDeclaredTypeLength = 64;

    /// <summary>Redirect hops one audio download may follow (§3.5); feeds keep five.</summary>
    public const int AudioMaxRedirects = 10;

    /// <summary>Downloads one run of the download job claims and awaits (§3.6).</summary>
    public const int DownloadsPerRun = 2;

    /// <summary>The share of the audio quota auto-download may fill, leaving the rest for explicit requests (§3.4).</summary>
    public const double AutoDownloadQuotaShare = 0.9;

    /// <summary>How long a user must wait to re-request audio after cancelling, clearing or removing it (§5.3 #9).</summary>
    public static readonly TimeSpan UserRequeueCooldown = TimeSpan.FromMinutes(15);
}
