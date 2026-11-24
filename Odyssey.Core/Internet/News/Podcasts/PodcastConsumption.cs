using Odyssey.Context;

namespace Odyssey.Core.Internet.News.Podcasts;

/// <summary>An episode's audio row with its episode and the time its current stored copy was consumed.</summary>
public sealed class PodcastConsumedAudio
{
    public PodcastEpisodeAudio Audio { get; init; } = null!;

    public PodcastEpisode Episode { get; init; } = null!;

    /// <summary>The later of the device-download and listened stamps that are at or after <c>StoredAtUtc</c>; else <c>null</c>.</summary>
    public DateTime? ConsumedAtUtc { get; init; }
}

/// <summary>
/// When the stored copy of an episode's audio was consumed (issue #593 §4), the one rule auto-delete counts from:
/// the later of <see cref="PodcastEpisode.LastPlayedAtUtc"/> (saved to a device, proven by a whole transfer) and
/// <see cref="PodcastEpisode.LastListenedAtUtc"/> (an in-browser listen reached the end), each counted only when it is
/// at or after <see cref="PodcastEpisodeAudio.StoredAtUtc"/>. A stamp from before the current download never counts.
/// </summary>
/// <remarks>
/// Two forms of one rule: <see cref="Rows"/> for the queries (the retention job, the auto-download backlog) and
/// <see cref="ConsumedAt"/> in memory (the summary's <c>AutoDeleteAtUtc</c>). <c>PodcastConsumptionTests</c> runs both
/// over every stamp combination. The query form is a conditional (SQL <c>CASE</c>), never <c>GREATEST</c>: MariaDB's
/// <c>GREATEST</c> is <c>NULL</c> when either argument is, which would make every single-stamp episode look unconsumed.
/// </remarks>
public static class PodcastConsumption
{
    /// <summary>Each audio row joined to its episode, with <see cref="PodcastConsumedAudio.ConsumedAtUtc"/> computed in the query.</summary>
    public static IQueryable<PodcastConsumedAudio> Rows(IQueryable<PodcastEpisodeAudio> audios, IQueryable<PodcastEpisode> episodes) =>
        from audio in audios
        join episode in episodes on audio.NewsArticleId equals episode.NewsArticleId
        select new PodcastConsumedAudio
        {
            Audio = audio,
            Episode = episode,
            ConsumedAtUtc = episode.LastListenedAtUtc != null && audio.StoredAtUtc != null && episode.LastListenedAtUtc >= audio.StoredAtUtc
                    && (episode.LastPlayedAtUtc == null || episode.LastPlayedAtUtc < audio.StoredAtUtc || episode.LastListenedAtUtc > episode.LastPlayedAtUtc)
                ? episode.LastListenedAtUtc
                : episode.LastPlayedAtUtc != null && audio.StoredAtUtc != null && episode.LastPlayedAtUtc >= audio.StoredAtUtc
                    ? episode.LastPlayedAtUtc
                    : null,
        };

    /// <summary>The same rule in memory.</summary>
    public static DateTime? ConsumedAt(DateTime? lastPlayedAtUtc, DateTime? lastListenedAtUtc, DateTime? storedAtUtc)
    {
        if (storedAtUtc is not { } stored)
        {
            return null;
        }

        DateTime? played = lastPlayedAtUtc is { } p && p >= stored ? p : null;
        DateTime? listened = lastListenedAtUtc is { } l && l >= stored ? l : null;
        return (played, listened) switch
        {
            ({ } a, { } b) => a >= b ? a : b,
            ({ } a, null) => a,
            (null, { } b) => b,
            _ => null,
        };
    }
}
