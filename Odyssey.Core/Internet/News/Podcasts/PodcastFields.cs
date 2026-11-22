using System.Globalization;
using Odyssey.Core.Internet.News.Content;
using Odyssey.Core.Internet.News.Parsing;
using Odyssey.Dtos;
using Odyssey.Dtos.Internet;

namespace Odyssey.Core.Internet.News.Podcasts;

/// <summary>One chapter after §8.6: start and end in milliseconds, a cleaned title, a sanitised link or none.</summary>
public sealed record PodcastNormalizedChapter(long StartMs, long? EndMs, string Title, string? Url);

/// <summary>A chapter from any source before §8.6: times already converted to milliseconds by the reader (checked).</summary>
public sealed record PodcastRawChapter(long? StartMs, long? EndMs, string? Title, string? Url);

/// <summary>An episode's fields after §8.3 and §8.6, ready for a <c>PodcastEpisodes</c> row and its chapters.</summary>
public sealed record PodcastNormalizedEpisode
{
    public required string EnclosureUrl { get; init; }

    public string? EnclosureDeclaredType { get; init; }

    public long? EnclosureDeclaredLength { get; init; }

    public string? Description { get; init; }

    public int? DurationSeconds { get; init; }

    public int? SeasonNumber { get; init; }

    public int? EpisodeNumber { get; init; }

    public PodcastEpisodeType EpisodeType { get; init; }

    public bool? Explicit { get; init; }

    public string? ImageUrl { get; init; }

    public IReadOnlyList<PodcastNormalizedChapter> FeedChapters { get; init; } = [];

    public string? ChaptersUrl { get; init; }

    /// <summary>The §8.5 change detector over every field above.</summary>
    public required byte[] EpisodeHash { get; init; }
}

/// <summary>A podcast's channel fields after §8.3.</summary>
public sealed record PodcastNormalizedFeed(string? Author, string? ImageUrl, bool? Explicit, PodcastType? PodcastType);

/// <summary>
/// The podcast field rules (issue #556 §8.3, §8.5, §8.6), pure: the same input always gives the same result. Every
/// chapter source — Podlove, Podcasting 2.0 JSON, ID3 and MP4 — goes through <see cref="NormalizeChapters"/>, so one
/// set of cleaning, URL and range rules covers them all.
/// </summary>
public static class PodcastFields
{
    /// <summary>An episode, or <c>null</c> when the item has no audio enclosure (§2: video and plain items get no episode).</summary>
    public static PodcastNormalizedEpisode? NormalizeEpisode(NewsParsedPodcastItem? item, Uri? baseUrl)
    {
        if (item?.Audio is not { } audio)
        {
            return null;
        }

        var description = Description(item.Subtitle, baseUrl) ?? Description(item.Summary, baseUrl);
        var chapters = NormalizeChapters(item.Chapters.Select(c => new PodcastRawChapter(
            ParseNormalPlayTime(c.Start), null, c.Title, NewsUrl.Sanitize(c.Href, baseUrl))));
        var episode = new PodcastNormalizedEpisode
        {
            EnclosureUrl = audio.Url,
            EnclosureDeclaredType = audio.DeclaredType,
            EnclosureDeclaredLength = audio.DeclaredLength,
            Description = description,
            DurationSeconds = ParseDuration(item.Duration) ?? Seconds(item.DurationSeconds),
            SeasonNumber = ParseNumber(item.Season),
            EpisodeNumber = ParseNumber(item.Episode),
            EpisodeType = ParseEpisodeType(item.EpisodeType),
            Explicit = ParseExplicit(item.Explicit),
            ImageUrl = item.ImageUrl,
            FeedChapters = chapters,
            ChaptersUrl = item.ChaptersUrl,
            EpisodeHash = [],
        };
        return episode with { EpisodeHash = Hash(episode) };
    }

    public static PodcastNormalizedFeed? NormalizeFeed(NewsParsedPodcastFeed? feed)
    {
        if (feed is null)
        {
            return null;
        }

        var author = NewsText.CleanLine(feed.Author);
        return new PodcastNormalizedFeed(
            author.Length == 0 ? null : NewsText.Clamp(author, PodcastLimits.MaxAuthorLength),
            feed.ImageUrl,
            ParseExplicit(feed.Explicit),
            NewsText.CleanLine(feed.Type).ToLowerInvariant() switch
            {
                "episodic" => PodcastType.Episodic,
                "serial" => PodcastType.Serial,
                _ => null,
            });
    }

    /// <summary>
    /// <c>itunes:duration</c> (§8.3): whole seconds, <c>M:S</c> or <c>H:M:S</c>, fractional seconds dropped, checked
    /// arithmetic; anything else, or beyond <see cref="PodcastLimits.MaxDurationSeconds"/>, is <c>null</c>.
    /// </summary>
    public static int? ParseDuration(string? value)
    {
        var text = NewsText.CleanLine(value);
        if (text.Length is 0 or > 32)
        {
            return null;
        }

        var parts = text.Split(':');
        if (parts.Length > 3)
        {
            return null;
        }

        var last = parts[^1];
        var dot = last.IndexOf('.', StringComparison.Ordinal);
        if (dot >= 0)
        {
            if (!last[(dot + 1)..].All(char.IsAsciiDigit))
            {
                return null;
            }

            parts[^1] = last[..dot];
        }

        long total = 0;
        foreach (var part in parts)
        {
            if (part.Length is 0 or > 9 || !part.All(char.IsAsciiDigit))
            {
                return null;
            }

            total = checked(total * 60 + long.Parse(part, NumberStyles.None, CultureInfo.InvariantCulture));
        }

        return total <= PodcastLimits.MaxDurationSeconds ? (int)total : null;
    }

    /// <summary><c>itunes:season</c> / <c>itunes:episode</c>: a whole number from 0 to <see cref="PodcastLimits.MaxEpisodeNumber"/>.</summary>
    public static int? ParseNumber(string? value) =>
        int.TryParse(NewsText.CleanLine(value), NumberStyles.None, CultureInfo.InvariantCulture, out var number)
            && number <= PodcastLimits.MaxEpisodeNumber ? number : null;

    public static PodcastEpisodeType ParseEpisodeType(string? value) => NewsText.CleanLine(value).ToLowerInvariant() switch
    {
        "trailer" => PodcastEpisodeType.Trailer,
        "bonus" => PodcastEpisodeType.Bonus,
        _ => PodcastEpisodeType.Full,
    };

    public static bool? ParseExplicit(string? value) => NewsText.CleanLine(value).ToLowerInvariant() switch
    {
        "true" or "yes" or "explicit" => true,
        "false" or "no" or "clean" => false,
        _ => null,
    };

    /// <summary>
    /// A Podlove start (§8.6): normal play time — <c>HH:MM:SS(.mmm)</c>, <c>MM:SS(.mmm)</c> or bare seconds — in
    /// milliseconds, checked; anything else is <c>null</c>.
    /// </summary>
    public static long? ParseNormalPlayTime(string? value)
    {
        var text = NewsText.CleanLine(value);
        if (text.Length is 0 or > 32)
        {
            return null;
        }

        var parts = text.Split(':');
        if (parts.Length > 3)
        {
            return null;
        }

        var fraction = 0L;
        var last = parts[^1];
        var dot = last.IndexOf('.', StringComparison.Ordinal);
        if (dot >= 0)
        {
            var digits = last[(dot + 1)..];
            if (digits.Length is 0 or > 9 || !digits.All(char.IsAsciiDigit))
            {
                return null;
            }

            // Milliseconds: the first three digits, right-padded ("5" is 500 ms).
            fraction = long.Parse(digits.PadRight(3, '0')[..3], NumberStyles.None, CultureInfo.InvariantCulture);
            parts[^1] = last[..dot];
        }

        long seconds = 0;
        foreach (var part in parts)
        {
            if (part.Length is 0 or > 9 || !part.All(char.IsAsciiDigit))
            {
                return null;
            }

            seconds = checked(seconds * 60 + long.Parse(part, NumberStyles.None, CultureInfo.InvariantCulture));
        }

        return checked(seconds * 1000 + fraction);
    }

    /// <summary>
    /// Seconds from a JSON number (§8.6) in milliseconds: NaN, infinite, negative or beyond the one-week ceiling is
    /// <c>null</c>.
    /// </summary>
    public static long? MillisecondsFromSeconds(double? seconds) =>
        seconds is { } value && double.IsFinite(value) && value >= 0 && value * 1000 <= PodcastLimits.MaxChapterMilliseconds
            ? (long)Math.Floor(value * 1000)
            : null;

    /// <summary>
    /// The §8.6 rules every chapter source shares: a missing or out-of-range start skips the chapter; an end before the
    /// start (or out of range) becomes <c>null</c>; the title is cleaned, clamped, and <c>Chapter N</c> when empty; the
    /// link passes the news URL rule or is <c>null</c>. Sorted by start, the first of a duplicated start kept, at most
    /// <see cref="PodcastLimits.MaxChapters"/>.
    /// </summary>
    public static IReadOnlyList<PodcastNormalizedChapter> NormalizeChapters(IEnumerable<PodcastRawChapter> chapters)
    {
        ArgumentNullException.ThrowIfNull(chapters);
        var kept = new List<(long Start, long? End, string? Title, string? Url)>();
        foreach (var chapter in chapters)
        {
            if (chapter.StartMs is not { } start || start < 0 || start > PodcastLimits.MaxChapterMilliseconds)
            {
                continue;
            }

            long? end = chapter.EndMs is { } e && e >= start && e <= PodcastLimits.MaxChapterMilliseconds ? e : null;
            kept.Add((start, end, chapter.Title, NewsUrl.Sanitize(chapter.Url)));
        }

        var ordered = kept
            .Select((c, index) => (c, index))
            .OrderBy(x => x.c.Start).ThenBy(x => x.index)
            .GroupBy(x => x.c.Start).Select(g => g.First().c)
            .Take(PodcastLimits.MaxChapters)
            .ToList();
        return [.. ordered.Select((c, index) =>
        {
            var title = NewsText.CleanLine(c.Title);
            return new PodcastNormalizedChapter(
                c.Start,
                c.End,
                title.Length == 0
                    ? string.Create(CultureInfo.InvariantCulture, $"Chapter {index + 1}")
                    : NewsText.Clamp(title, PodcastLimits.MaxChapterTitleLength),
                c.Url);
        })];
    }

    /// <summary>The §8.5 hash: every episode-only field, framed, so an unchanged episode is never rewritten.</summary>
    public static byte[] Hash(PodcastNormalizedEpisode episode)
    {
        ArgumentNullException.ThrowIfNull(episode);
        using var hash = new NewsHashing.Composite();
        hash.Text(NewsUrlKey.LinkKey(episode.EnclosureUrl) ?? episode.EnclosureUrl)
            .Text(episode.EnclosureDeclaredType)
            .Text(episode.EnclosureDeclaredLength?.ToString(CultureInfo.InvariantCulture))
            .Text(episode.Description)
            .Text(episode.DurationSeconds?.ToString(CultureInfo.InvariantCulture))
            .Text(episode.SeasonNumber?.ToString(CultureInfo.InvariantCulture))
            .Text(episode.EpisodeNumber?.ToString(CultureInfo.InvariantCulture))
            .Text(((byte)episode.EpisodeType).ToString(CultureInfo.InvariantCulture))
            .Text(episode.Explicit?.ToString())
            .Text(episode.ImageUrl is null ? null : NewsUrlKey.LinkKey(episode.ImageUrl) ?? episode.ImageUrl)
            .Text(episode.ChaptersUrl is null ? null : NewsUrlKey.LinkKey(episode.ChaptersUrl) ?? episode.ChaptersUrl);
        foreach (var chapter in episode.FeedChapters)
        {
            hash.Text(chapter.StartMs.ToString(CultureInfo.InvariantCulture)).Text(chapter.Title).Text(chapter.Url);
        }

        return hash.Finish();
    }

    /// <summary>The URL key's hash used for de-duplication (artwork, chapter files): the news link key, else the URL.</summary>
    public static byte[] UrlKeyHash(string url) => NewsUrlKey.Hash(NewsUrlKey.LinkKey(url) ?? url);

    private static string? Description(NewsTextField? field, Uri? baseUrl)
    {
        if (field is null)
        {
            return null;
        }

        var line = NewsFieldText.ToLine(field, baseUrl);
        return line.Length == 0 ? null : NewsText.Excerpt(line, PodcastLimits.MaxDescriptionLength);
    }

    private static int? Seconds(double? seconds) =>
        seconds is { } value && double.IsFinite(value) && value >= 0 && value <= PodcastLimits.MaxDurationSeconds ? (int)value : null;
}
