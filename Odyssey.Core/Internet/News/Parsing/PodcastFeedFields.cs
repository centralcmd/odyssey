using System.Globalization;
using System.Text.Json;
using System.Xml.Linq;
using Odyssey.Core.Internet.News.Content;

namespace Odyssey.Core.Internet.News.Parsing;

/// <summary>
/// Reads the podcast namespaces out of an already-parsed item or channel element (issue #556 §8.3). It reads only
/// elements the depth-capped reader has already materialised, fetches nothing, and keeps every value raw except URLs,
/// which are sanitised here exactly as the news reader sanitises its own (<see cref="NewsUrl.Sanitize"/>).
/// </summary>
public static class PodcastFeedFields
{
    internal static readonly XNamespace Itunes = "http://www.itunes.com/dtds/podcast-1.0.dtd";
    internal static readonly XNamespace PodcastNs = "https://podcastindex.org/namespace/1.0";
    internal static readonly XNamespace Psc = "http://podlove.org/simple-chapters";

    /// <summary>The URL path endings an untyped enclosure is taken as audio by (§8.3), never as an image.</summary>
    private static readonly string[] AudioExtensions = [".mp3", ".m4a", ".m4b", ".aac", ".ogg", ".oga", ".opus", ".flac"];

    /// <summary>The most Podlove chapters read per item before any is normalised (twice the kept cap).</summary>
    private const int MaxRawChapters = Dtos.PodcastLimits.MaxChapters * 2;

    /// <summary>Whether an enclosure of <paramref name="type"/> at <paramref name="url"/> is audio (§8.3).</summary>
    public static bool IsAudio(string? type, string? url)
    {
        if (!string.IsNullOrWhiteSpace(type))
        {
            return type.TrimStart().StartsWith("audio/", StringComparison.OrdinalIgnoreCase);
        }

        return HasAudioExtension(url);
    }

    /// <summary>
    /// An untyped enclosure whose path ends in an audio extension (§3.4 ingest step 1): never queued as an image, on any
    /// feed — an <c>.mp3</c> cannot be one, and the image validator would download it before rejecting it.
    /// </summary>
    public static bool HasAudioExtension(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        var path = Uri.TryCreate(url.Trim(), UriKind.RelativeOrAbsolute, out var uri) && uri.IsAbsoluteUri ? uri.AbsolutePath : url.Trim();
        var query = path.IndexOfAny(['?', '#']);
        if (query >= 0)
        {
            path = path[..query];
        }

        return AudioExtensions.Any(extension => path.EndsWith(extension, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>An RSS 2.0 item's podcast fields.</summary>
    public static NewsParsedPodcastItem? FromRssItem(XElement item, Uri? itemBase)
    {
        ArgumentNullException.ThrowIfNull(item);
        NewsParsedEnclosure? audio = null;
        foreach (var enclosure in item.Elements("enclosure"))
        {
            var url = enclosure.Attribute("url")?.Value;
            if (IsAudio(enclosure.Attribute("type")?.Value, url) && NewsUrl.Sanitize(url, itemBase) is { } clean)
            {
                audio = new NewsParsedEnclosure(clean, DeclaredType(enclosure.Attribute("type")?.Value), Length(enclosure.Attribute("length")?.Value));
                break;
            }
        }

        return Common(item, itemBase, audio);
    }

    /// <summary>An Atom entry's podcast fields: the first <c>link rel="enclosure"</c> of an audio type.</summary>
    public static NewsParsedPodcastItem? FromAtomEntry(XElement entry, Uri? entryBase, Func<XElement, string> rel)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(rel);
        NewsParsedEnclosure? audio = null;
        foreach (var link in entry.Elements(NewsFeedParser.Atom + "link").Where(l => rel(l) == "enclosure"))
        {
            var url = link.Attribute("href")?.Value;
            if (IsAudio(link.Attribute("type")?.Value, url) && NewsUrl.Sanitize(url, entryBase) is { } clean)
            {
                audio = new NewsParsedEnclosure(clean, DeclaredType(link.Attribute("type")?.Value), Length(link.Attribute("length")?.Value));
                break;
            }
        }

        return Common(entry, entryBase, audio);
    }

    /// <summary>A JSON Feed item's podcast fields: the first attachment with an audio <c>mime_type</c>.</summary>
    public static NewsParsedPodcastItem? FromJsonItem(JsonElement item, Uri? itemBase)
    {
        if (!item.TryGetProperty("attachments", out var attachments) || attachments.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var attachment in attachments.EnumerateArray())
        {
            if (attachment.ValueKind != JsonValueKind.Object
                || JsonString(attachment, "mime_type") is not { } mime
                || !mime.TrimStart().StartsWith("audio/", StringComparison.OrdinalIgnoreCase)
                || NewsUrl.Sanitize(JsonString(attachment, "url"), itemBase) is not { } url)
            {
                continue;
            }

            long? size = attachment.TryGetProperty("size_in_bytes", out var bytes) && bytes.ValueKind == JsonValueKind.Number
                && bytes.TryGetInt64(out var length) && length >= 0 ? length : null;
            double? duration = attachment.TryGetProperty("duration_in_seconds", out var seconds) && seconds.ValueKind == JsonValueKind.Number
                && seconds.TryGetDouble(out var value) ? value : null;
            return new NewsParsedPodcastItem
            {
                Audio = new NewsParsedEnclosure(url, DeclaredType(mime), size),
                DurationSeconds = duration,
            };
        }

        return null;
    }

    /// <summary>
    /// Folds one RSS channel child into the channel's podcast fields (§8.3): the iTunes author, image, explicit flag and
    /// type, and RSS <c>image/url</c> as the image when no iTunes image is given.
    /// </summary>
    public static NewsParsedPodcastFeed? FoldChannel(NewsParsedPodcastFeed? current, XElement element, Uri? feedBase)
    {
        ArgumentNullException.ThrowIfNull(element);
        var feed = current ?? new NewsParsedPodcastFeed();
        if (element.Name == Itunes + "author")
        {
            return feed with { Author = feed.Author ?? Text(element) };
        }

        if (element.Name == Itunes + "image")
        {
            // An iTunes image wins over RSS image/url whichever comes first.
            return NewsUrl.Sanitize(element.Attribute("href")?.Value, feedBase) is { } href ? feed with { ImageUrl = href } : current;
        }

        if (element.Name == Itunes + "explicit")
        {
            return feed with { Explicit = feed.Explicit ?? Text(element) };
        }

        if (element.Name == Itunes + "type")
        {
            return feed with { Type = feed.Type ?? Text(element) };
        }

        if (element.Name == XName.Get("image") && current?.ImageUrl is null
            && NewsUrl.Sanitize(Text(element.Element("url")), feedBase) is { } url)
        {
            return feed with { ImageUrl = url };
        }

        return current;
    }

    private static NewsParsedPodcastItem? Common(XElement item, Uri? itemBase, NewsParsedEnclosure? audio)
    {
        var subtitle = Text(item.Element(Itunes + "subtitle"));
        var summary = Text(item.Element(Itunes + "summary"));
        var chaptersElement = item.Element(PodcastNs + "chapters");
        var parsed = new NewsParsedPodcastItem
        {
            Audio = audio,
            Duration = Text(item.Element(Itunes + "duration")),
            Season = Text(item.Element(Itunes + "season")),
            Episode = Text(item.Element(Itunes + "episode")),
            EpisodeType = Text(item.Element(Itunes + "episodeType")),
            Explicit = Text(item.Element(Itunes + "explicit")),
            ImageUrl = NewsUrl.Sanitize(item.Element(Itunes + "image")?.Attribute("href")?.Value, itemBase),
            // HTML-kind: publishers put markup in both, so both go through the pre-scan and the walk (§8.3).
            Subtitle = subtitle is null ? null : new NewsTextField(subtitle, NewsTextKind.Html),
            Summary = summary is null ? null : new NewsTextField(summary, NewsTextKind.Html),
            Chapters = [.. (item.Element(Psc + "chapters")?.Elements(Psc + "chapter") ?? [])
                .Take(MaxRawChapters)
                .Select(c => new NewsParsedChapter(c.Attribute("start")?.Value, c.Attribute("title")?.Value, c.Attribute("href")?.Value))],
            ChaptersUrl = NewsUrl.Sanitize(chaptersElement?.Attribute("url")?.Value, itemBase),
        };
        return parsed.IsEmpty ? null : parsed;
    }

    private static string? DeclaredType(string? type)
    {
        var clean = NewsText.CleanLine(type);
        return clean.Length == 0 ? null : NewsText.Clamp(clean, Dtos.PodcastLimits.MaxDeclaredTypeLength);
    }

    private static long? Length(string? value) =>
        long.TryParse(value?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var length) && length > 0 ? length : null;

    private static string? Text(XElement? element) =>
        element?.Value is { } value && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;

    private static string? JsonString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            && value.GetString() is { } text && !string.IsNullOrWhiteSpace(text)
            ? text.Trim()
            : null;
}
