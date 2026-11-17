using System.Globalization;
using System.Text;
using System.Xml;
using Odyssey.Core.Internet.News.Content;

namespace Odyssey.Core.Internet.News.Opml;

/// <summary>One exported feed, every string already sanitised (issue #508 §5.1 writer rule 4).</summary>
public sealed record OpmlExportFeed(
    string DisplayTitle,
    string Url,
    string? SiteUrl,
    string? CustomTitle,
    int? FetchIntervalMinutes,
    bool DownloadImages,
    bool Active,
    IReadOnlyList<string> TagNames);

/// <summary>The loaded export: the feeds, and the folder names in their written order.</summary>
public sealed record OpmlExportDocument(IReadOnlyList<OpmlExportFeed> Feeds, IReadOnlyList<string> Folders, DateTime CreatedAtUtc);

/// <summary>
/// Writes OPML 2.0 through an <see cref="XmlWriter"/>, never by concatenation (issue #508 §5.1): one folder per tag
/// with an exported feed, ordered as given; each tagged feed under every one of its folders, with every tag in its
/// <c>category</c>; untagged feeds at the top level after the folders. <c>CheckCharacters</c> is on as a backstop
/// behind <see cref="Sanitize"/>, and the writer escapes every attribute.
/// </summary>
public static class OpmlDocumentWriter
{
    public const string ContentType = "text/x-opml; charset=utf-8";

    public static async Task WriteAsync(OpmlExportDocument document, Stream stream, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(stream);
        var settings = new XmlWriterSettings
        {
            Async = true,
            Encoding = new UTF8Encoding(false),
            Indent = true,
            CheckCharacters = true,
            CloseOutput = false,
        };

        await using var writer = XmlWriter.Create(stream, settings);
        await writer.WriteStartDocumentAsync();
        await writer.WriteStartElementAsync(null, "opml", null);
        await writer.WriteAttributeStringAsync(null, "version", null, "2.0");
        await writer.WriteAttributeStringAsync("xmlns", "odyssey", null, OpmlDocumentReader.OdysseyNamespace);

        await writer.WriteStartElementAsync(null, "head", null);
        await writer.WriteElementStringAsync(null, "title", null, "Odyssey news feeds");
        await writer.WriteElementStringAsync(null, "dateCreated", null, document.CreatedAtUtc.ToString("r", CultureInfo.InvariantCulture));
        await writer.WriteEndElementAsync();

        await writer.WriteStartElementAsync(null, "body", null);
        var byFolder = document.Feeds
            .SelectMany(feed => feed.TagNames.Select(tag => (Tag: tag, Feed: feed)))
            .ToLookup(pair => pair.Tag, pair => pair.Feed, StringComparer.Ordinal);
        foreach (var folder in document.Folders)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await writer.WriteStartElementAsync(null, "outline", null);
            await writer.WriteAttributeStringAsync(null, "text", null, folder);
            await writer.WriteAttributeStringAsync(null, "title", null, folder);
            foreach (var feed in byFolder[folder])
            {
                await WriteFeedAsync(writer, feed);
            }

            await writer.WriteEndElementAsync();
        }

        foreach (var feed in document.Feeds.Where(f => f.TagNames.Count == 0))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await WriteFeedAsync(writer, feed);
        }

        await writer.WriteEndElementAsync();
        await writer.WriteEndElementAsync();
        await writer.WriteEndDocumentAsync();
        await writer.FlushAsync();
    }

    private static async Task WriteFeedAsync(XmlWriter writer, OpmlExportFeed feed)
    {
        await writer.WriteStartElementAsync(null, "outline", null);
        await writer.WriteAttributeStringAsync(null, "type", null, "rss");
        await writer.WriteAttributeStringAsync(null, "text", null, feed.DisplayTitle);
        await writer.WriteAttributeStringAsync(null, "title", null, feed.DisplayTitle);
        await writer.WriteAttributeStringAsync(null, "xmlUrl", null, feed.Url);
        if (!string.IsNullOrEmpty(feed.SiteUrl))
        {
            await writer.WriteAttributeStringAsync(null, "htmlUrl", null, feed.SiteUrl);
        }

        if (feed.TagNames.Count > 0)
        {
            await writer.WriteAttributeStringAsync(null, "category", null, string.Join(',', feed.TagNames.Select(t => "/" + EscapeSegment(t))));
        }

        if (feed.CustomTitle is not null)
        {
            await writer.WriteAttributeStringAsync("odyssey", "customTitle", OpmlDocumentReader.OdysseyNamespace, feed.CustomTitle);
        }

        if (feed.FetchIntervalMinutes is { } minutes)
        {
            await writer.WriteAttributeStringAsync("odyssey", "fetchIntervalMinutes", OpmlDocumentReader.OdysseyNamespace,
                minutes.ToString(CultureInfo.InvariantCulture));
        }

        await writer.WriteAttributeStringAsync("odyssey", "downloadImages", OpmlDocumentReader.OdysseyNamespace, feed.DownloadImages ? "true" : "false");
        await writer.WriteAttributeStringAsync("odyssey", "active", OpmlDocumentReader.OdysseyNamespace, feed.Active ? "true" : "false");
        await writer.WriteEndElementAsync();
    }

    /// <summary>A tag name inside a <c>category</c> path: <c>%</c>, <c>,</c> and <c>/</c> as <c>%25</c>, <c>%2C</c>, <c>%2F</c> (§5.1 rule 2).</summary>
    internal static string EscapeSegment(string name) =>
        name.Replace("%", "%25", StringComparison.Ordinal)
            .Replace(",", "%2C", StringComparison.Ordinal)
            .Replace("/", "%2F", StringComparison.Ordinal);

    /// <summary>
    /// §5.1 rule 4: <see cref="NewsText.Clean"/>, then every character XML 1.0 forbids (lone surrogates and the
    /// controls are already gone; U+FFFE and U+FFFF remain) removed.
    /// </summary>
    public static string Sanitize(string? value)
    {
        var cleaned = NewsText.Clean(value);
        if (cleaned.AsSpan().IndexOfAny('￾', '￿') < 0)
        {
            return cleaned;
        }

        return cleaned.Replace("￾", string.Empty, StringComparison.Ordinal).Replace("￿", string.Empty, StringComparison.Ordinal);
    }
}
