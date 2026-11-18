using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using Odyssey.Context;
using Odyssey.Core.Internet.News.Content;
using Odyssey.Dtos;
using Odyssey.Dtos.Internet;

namespace Odyssey.Core.Internet.News.Parsing;

/// <summary>
/// The feed parser (issue #470 §3.1 item 5, §8.6): detects the format from the body — never from
/// <c>Content-Type</c> — and reads RSS 2.0/0.9x, RSS 1.0/0.90 (RDF), Atom 1.0 or JSON Feed 1.0/1.1 into one item model.
///
/// <para>
/// <strong>Hardening.</strong> XML is read through <see cref="DepthCappedXmlReader"/> (depth 64) with
/// <c>DtdProcessing.Ignore</c>, no resolver and character caps, after <see cref="NewsEntityPrepass"/> has made every
/// non-XML entity reference literal; no URL in the document (a DTD, an <c>rdf:resource</c>, an <c>xml:base</c>) is
/// ever fetched. JSON is read with <c>MaxDepth = 32</c>. Items are streamed: at most <see cref="MaxItemsRead"/> are
/// read, and the newest <see cref="MaxItemsKept"/> by date are returned.
/// </para>
/// </summary>
public static class NewsFeedParser
{
    public const int MaxItemsRead = 500;

    public const int MaxItemsKept = 200;

    /// <summary>A source file's cap (§6.1): an item above it keeps no source file.</summary>
    public const int MaxSourceBytes = 1024 * 1024;

    /// <summary>The XML reader's character cap: the 10 MiB body cap, plus room for the entity pre-pass.</summary>
    public const long MaxDocumentCharacters = 16L * 1024 * 1024;

    public const int MaxJsonDepth = 32;

    public const string XmlSourceContentType = "application/xml";

    public const string JsonSourceContentType = "application/json";

    internal static readonly XNamespace Atom = "http://www.w3.org/2005/Atom";
    internal static readonly XNamespace ContentNs = "http://purl.org/rss/1.0/modules/content/";
    internal static readonly XNamespace Dc = "http://purl.org/dc/elements/1.1/";
    internal static readonly XNamespace Media = "http://search.yahoo.com/mrss/";
    internal static readonly XNamespace Sy = "http://purl.org/rss/1.0/modules/syndication/";
    internal static readonly XNamespace Rdf = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";
    internal static readonly XNamespace Rss10 = "http://purl.org/rss/1.0/";
    internal static readonly XNamespace Rss090 = "http://my.netscape.com/rdf/simple/0.9/";
    internal static readonly XNamespace Xhtml = "http://www.w3.org/1999/xhtml";
    private static readonly XName XmlBase = XNamespace.Xml + "base";

    /// <summary>Parses a feed body.</summary>
    /// <param name="body">The decompressed body (at most 10 MiB by the fetcher's cap).</param>
    /// <param name="documentUrl">The URL the body came from (after redirects): the last resort for relative URLs.</param>
    /// <param name="contentTypeCharset">The response's <c>charset</c> parameter, used only to decode.</param>
    public static NewsFeedParseResult Parse(ReadOnlySpan<byte> body, Uri? documentUrl, string? contentTypeCharset = null)
    {
        var text = NewsFeedDecoder.Decode(body, contentTypeCharset);
        var start = 0;
        while (start < text.Length && char.IsWhiteSpace(text[start]))
        {
            start++;
        }

        if (start >= text.Length)
        {
            return NewsFeedParseResult.NotAFeed;
        }

        try
        {
            return text[start] switch
            {
                '{' => ParseJson(text[start..], documentUrl),
                '<' => ParseXml(text[start..], documentUrl),
                _ => NewsFeedParseResult.NotAFeed,
            };
        }
        catch (Exception ex) when (ex is XmlException or JsonException or DecoderFallbackException)
        {
            return NewsFeedParseResult.ParseError;
        }
    }

    // ── XML ─────────────────────────────────────────────────────────────────────────────────────────────

    internal static XmlReaderSettings ReaderSettings() => new()
    {
        DtdProcessing = DtdProcessing.Ignore,
        XmlResolver = null,
        MaxCharactersFromEntities = 1024,
        MaxCharactersInDocument = MaxDocumentCharacters,
        CheckCharacters = false,
        IgnoreComments = true,
        IgnoreProcessingInstructions = true,
        ConformanceLevel = ConformanceLevel.Document,
        CloseInput = true,
    };

    private static NewsFeedParseResult ParseXml(string text, Uri? documentUrl)
    {
        using var reader = new DepthCappedXmlReader(XmlReader.Create(new StringReader(NewsEntityPrepass.Apply(text)), ReaderSettings()));
        if (reader.MoveToContent() != XmlNodeType.Element)
        {
            return NewsFeedParseResult.NotAFeed;
        }

        var rootName = XName.Get(reader.LocalName, reader.NamespaceURI);
        if (rootName == XName.Get("rss"))
        {
            return Finish(new XmlFeedReader(reader, NewsFeedFormat.Rss, documentUrl).ReadRss());
        }

        if (rootName == Atom + "feed")
        {
            return Finish(new XmlFeedReader(reader, NewsFeedFormat.Atom, documentUrl).ReadAtom());
        }

        if (rootName == Rdf + "RDF")
        {
            var namespaces = reader.GetNamespacesInScope(XmlNamespaceScope.All).Values;
            var itemNs = namespaces.Contains(Rss10.NamespaceName) ? Rss10
                : namespaces.Contains(Rss090.NamespaceName) ? Rss090
                : null;
            return itemNs is null
                ? NewsFeedParseResult.NotAFeed
                : Finish(new XmlFeedReader(reader, NewsFeedFormat.RssRdf, documentUrl).ReadRdf(itemNs));
        }

        return NewsFeedParseResult.NotAFeed;
    }

    private static NewsFeedParseResult Finish(NewsParsedFeed feed) => new(NewsFeedParseOutcome.Ok, feed);

    /// <summary>
    /// Streams one XML feed: the container's children are read one at a time with <see cref="XElement.ReadFrom"/>
    /// (through the depth-capped reader), so only one item is ever materialised.
    /// </summary>
    private sealed class XmlFeedReader(DepthCappedXmlReader reader, NewsFeedFormat format, Uri? documentUri)
    {
        private readonly Uri? documentUrl = documentUri;
        private readonly List<NewsParsedItem> items = [];
        private bool limitReached;
        private string? title;
        private string? siteLink;
        private string? description;
        private string? language;
        private int? ttl;
        private Uri? feedBase = documentUri;
        private string? feedAuthor;
        private XElement? pendingSy;

        public NewsParsedFeed ReadRss()
        {
            feedBase = Rebase(feedBase, reader.GetAttribute("xml:base"));
            ForEachChild(name => name == XName.Get("channel"), (element, depth) =>
            {
                if (element.Name == XName.Get("item"))
                {
                    AddItem(element, RssItem);
                }
                else if (depth == 1)
                {
                    RssChannelField(element);
                }
            });

            return Feed();
        }

        public NewsParsedFeed ReadRdf(XNamespace itemNs)
        {
            feedBase = Rebase(feedBase, reader.GetAttribute("xml:base"));
            ForEachChild(_ => false, (element, _) =>
            {
                if (element.Name == itemNs + "item")
                {
                    AddItem(element, e => RdfItem(e, itemNs));
                }
                else if (element.Name == itemNs + "channel")
                {
                    title = Text(element.Element(itemNs + "title"));
                    siteLink = Text(element.Element(itemNs + "link"));
                    description = Text(element.Element(itemNs + "description"));
                    language = Text(element.Element(Dc + "language"));
                    ttl = SyndicationTtl(element);
                }
            });

            return Feed();
        }

        public NewsParsedFeed ReadAtom()
        {
            feedBase = Rebase(feedBase, reader.GetAttribute("xml:base"));
            language = reader.GetAttribute("xml:lang");
            ForEachChild(_ => false, (element, _) =>
            {
                if (element.Name == Atom + "entry")
                {
                    AddItem(element, AtomEntry);
                    return;
                }

                if (element.Name == Atom + "title")
                {
                    title = AtomText(element) is { } t ? NewsFieldText.ToLine(t) : null;
                }
                else if (element.Name == Atom + "subtitle")
                {
                    description = AtomText(element) is { } t ? NewsFieldText.ToLine(t) : null;
                }
                else if (element.Name == Atom + "link" && AtomRel(element) == "alternate" && siteLink is null)
                {
                    siteLink = NewsUrl.Sanitize(element.Attribute("href")?.Value, Rebase(feedBase, element));
                }
                else if (element.Name == Atom + "author")
                {
                    feedAuthor ??= Text(element.Element(Atom + "name"));
                }
            });

            return Feed();
        }

        /// <summary>
        /// Visits the root's element children one at a time — and, for a container <paramref name="descend"/> names (the
        /// RSS channel), that container's children. The visitor's level is 0 for a root child, 1 inside the container.
        /// </summary>
        private void ForEachChild(Func<XName, bool> descend, Action<XElement, int> visit) =>
            Walk(reader.Depth, 0, descend, visit);

        private void Walk(int containerDepth, int level, Func<XName, bool> descend, Action<XElement, int> visit)
        {
            if (reader.IsEmptyElement)
            {
                reader.Read();
                return;
            }

            reader.Read();
            while (!limitReached && !reader.EOF && !(reader.NodeType == XmlNodeType.EndElement && reader.Depth == containerDepth))
            {
                if (reader.NodeType != XmlNodeType.Element)
                {
                    reader.Read();
                    continue;
                }

                if (level == 0 && descend(XName.Get(reader.LocalName, reader.NamespaceURI)))
                {
                    feedBase = Rebase(feedBase, reader.GetAttribute("xml:base"));
                    Walk(reader.Depth, level + 1, descend, visit);
                    continue;
                }

                visit(ReadElement(), level);
            }

            if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == containerDepth)
            {
                reader.Read();
            }
        }

        /// <summary>
        /// Reads the current element with every namespace in scope declared on it, so its re-serialised form re-parses
        /// on its own (§6.1, AC 31).
        /// </summary>
        private XElement ReadElement()
        {
            var inScope = reader.GetNamespacesInScope(XmlNamespaceScope.ExcludeXml);
            var element = (XElement)XNode.ReadFrom(reader);
            foreach (var (prefix, uri) in inScope)
            {
                var attributeName = prefix.Length == 0 ? XName.Get("xmlns") : XNamespace.Xmlns + prefix;
                if (element.Attribute(attributeName) is null)
                {
                    element.SetAttributeValue(attributeName, uri);
                }
            }

            return element;
        }

        private void AddItem(XElement element, Func<XElement, NewsParsedItem> map)
        {
            if (items.Count >= MaxItemsRead)
            {
                limitReached = true;
                return;
            }

            var item = map(element);
            var source = Encoding.UTF8.GetBytes(element.ToString(SaveOptions.DisableFormatting | SaveOptions.OmitDuplicateNamespaces));
            items.Add(item with
            {
                DocumentIndex = items.Count,
                Source = source.Length <= MaxSourceBytes ? source : null,
                SourceTooLarge = source.Length > MaxSourceBytes,
                SourceContentType = XmlSourceContentType,
            });
        }

        private NewsParsedFeed Feed() => new()
        {
            Format = format,
            Title = FeedTitle(title),
            SiteUrl = NewsUrl.Sanitize(siteLink, feedBase),
            Description = FeedDescription(description),
            Language = FeedLanguage(language),
            TtlMinutes = ttl,
            Items = SelectNewest(items),
            ItemsRead = items.Count,
            ItemLimitReached = limitReached,
        };

        private void RssChannelField(XElement element)
        {
            if (element.Name.Namespace != XNamespace.None)
            {
                if (element.Name == Sy + "updatePeriod" || element.Name == Sy + "updateFrequency")
                {
                    pendingSy ??= new XElement("sy");
                    pendingSy.Add(element);
                    ttl ??= SyndicationTtl(pendingSy);
                }
                else if (element.Name == Dc + "language")
                {
                    language ??= Text(element);
                }

                return;
            }

            switch (element.Name.LocalName)
            {
                case "title":
                    title ??= Text(element);
                    break;
                case "link":
                    siteLink ??= Text(element);
                    break;
                case "description":
                    description ??= Text(element);
                    break;
                case "language":
                    language ??= Text(element);
                    break;
                case "ttl":
                    if (int.TryParse(Text(element), NumberStyles.None, CultureInfo.InvariantCulture, out var minutes) && minutes > 0)
                    {
                        ttl = minutes;
                    }

                    break;
            }
        }

        private NewsParsedItem RssItem(XElement item)
        {
            var itemBase = Rebase(feedBase, item);
            var link = Text(item.Element("link"));
            var guidElement = item.Element("guid");
            var guid = Text(guidElement);

            // RSS 2.0: a guid that is a permalink (the default) is the item's URL when it has no link.
            if (link is null && guid is not null
                && !string.Equals(guidElement?.Attribute("isPermaLink")?.Value?.Trim(), "false", StringComparison.OrdinalIgnoreCase)
                && NewsUrl.Sanitize(guid) is not null)
            {
                link = guid;
            }

            var content = Text(item.Element(ContentNs + "encoded"));
            var summary = Text(item.Element("description"));
            return new NewsParsedItem
            {
                Guid = guid,
                Link = link,
                Title = Text(item.Element("title")) is { } t ? new NewsTextField(DecodeTitle(t), NewsTextKind.Text) : null,
                Author = Text(item.Element("author")) ?? Text(item.Element(Dc + "creator")),
                PublishedAt = NewsDates.Parse(Text(item.Element("pubDate"))) ?? NewsDates.Parse(Text(item.Element(Dc + "date"))),
                Content = content is null ? null : new NewsTextField(content, NewsTextKind.Html),
                Summary = summary is null ? null : new NewsTextField(summary, NewsTextKind.Html),
                BaseUrl = ItemBase(item, itemBase, link),
                Images = RssImages(item, itemBase),
            };
        }

        private NewsParsedItem RdfItem(XElement item, XNamespace itemNs)
        {
            var itemBase = Rebase(feedBase, item);
            var link = Text(item.Element(itemNs + "link"));
            var content = Text(item.Element(ContentNs + "encoded"));
            var summary = Text(item.Element(itemNs + "description"));
            return new NewsParsedItem
            {
                Guid = item.Attribute(Rdf + "about")?.Value is { Length: > 0 } about ? about : null,
                Link = link,
                Title = Text(item.Element(itemNs + "title")) is { } t ? new NewsTextField(DecodeTitle(t), NewsTextKind.Text) : null,
                Author = Text(item.Element(Dc + "creator")),
                PublishedAt = NewsDates.Parse(Text(item.Element(Dc + "date"))),
                Content = content is null ? null : new NewsTextField(content, NewsTextKind.Html),
                Summary = summary is null ? null : new NewsTextField(summary, NewsTextKind.Html),
                BaseUrl = ItemBase(item, itemBase, link),
                Images = RssImages(item, itemBase),
            };
        }

        private NewsParsedItem AtomEntry(XElement entry)
        {
            var entryBase = Rebase(feedBase, entry);
            var links = entry.Elements(Atom + "link").ToList();
            var alternate = links.FirstOrDefault(l => AtomRel(l) == "alternate") ?? links.FirstOrDefault();
            var link = alternate?.Attribute("href")?.Value;
            var linkBase = alternate is null ? entryBase : Rebase(entryBase, alternate);
            var resolvedLink = NewsUrl.Sanitize(link, linkBase) ?? link;

            var contentElement = entry.Element(Atom + "content");
            var contentBase = contentElement is null ? entryBase : Rebase(entryBase, contentElement);
            var content = contentElement is not null && contentElement.Attribute("src") is null ? AtomText(contentElement) : null;
            var summary = entry.Element(Atom + "summary") is { } summaryElement ? AtomText(summaryElement) : null;

            var images = new List<NewsParsedImage>();
            foreach (var enclosure in links.Where(l => AtomRel(l) == "enclosure" && IsImageOrUntyped(l.Attribute("type")?.Value)))
            {
                AddImage(images, NewsArticleImageOrigin.Enclosure, enclosure.Attribute("href")?.Value, Rebase(entryBase, enclosure), null, null, null);
            }

            AddMediaImages(images, entry, entryBase);

            var published = NewsDates.Parse(Text(entry.Element(Atom + "published")));
            var updated = NewsDates.Parse(Text(entry.Element(Atom + "updated")));
            return new NewsParsedItem
            {
                Guid = Text(entry.Element(Atom + "id")),
                Link = resolvedLink,
                Title = entry.Element(Atom + "title") is { } titleElement ? AtomText(titleElement) : null,
                Author = Text(entry.Element(Atom + "author")?.Element(Atom + "name")) ?? feedAuthor,
                PublishedAt = published ?? updated,
                UpdatedAt = updated,
                Content = content,
                Summary = summary,
                BaseUrl = HasXmlBase(entry, contentElement) ? contentBase : ItemBase(entry, contentBase, resolvedLink),
                Images = images,
            };
        }

        private static bool HasXmlBase(XElement entry, XElement? content) =>
            entry.Attribute(XmlBase) is not null || content?.Attribute(XmlBase) is not null;

        /// <summary>§8.6 "Base URL": <c>xml:base</c>, else the item link, else the feed's own base.</summary>
        private Uri? ItemBase(XElement item, Uri? rebased, string? link)
        {
            if (item.Attribute(XmlBase) is not null || feedBase != documentUrl)
            {
                return rebased;
            }

            return NewsUrl.TryResolve(link, rebased, out var linkUri) ? linkUri : rebased;
        }

        private IReadOnlyList<NewsParsedImage> RssImages(XElement item, Uri? itemBase)
        {
            var images = new List<NewsParsedImage>();
            foreach (var enclosure in item.Elements("enclosure").Where(e => IsImageOrUntyped(e.Attribute("type")?.Value)))
            {
                AddImage(images, NewsArticleImageOrigin.Enclosure, enclosure.Attribute("url")?.Value, itemBase, null, null, null);
            }

            AddMediaImages(images, item, itemBase);
            return images;
        }

        private static void AddMediaImages(List<NewsParsedImage> images, XElement item, Uri? itemBase)
        {
            var scopes = new[] { item }.Concat(item.Elements(Media + "group")).ToList();
            foreach (var media in scopes.SelectMany(s => s.Elements(Media + "content")))
            {
                var medium = media.Attribute("medium")?.Value;
                var type = media.Attribute("type")?.Value;
                var isImage = medium is null ? IsImageOrUntyped(type) : string.Equals(medium, "image", StringComparison.OrdinalIgnoreCase);
                if (isImage)
                {
                    AddImage(images, NewsArticleImageOrigin.MediaContent, media.Attribute("url")?.Value, itemBase,
                        Text(media.Element(Media + "description")), Dimension(media.Attribute("width")), Dimension(media.Attribute("height")));
                }
            }

            foreach (var thumbnail in scopes.SelectMany(s => s.Elements(Media + "thumbnail")))
            {
                AddImage(images, NewsArticleImageOrigin.MediaThumbnail, thumbnail.Attribute("url")?.Value, itemBase,
                    null, Dimension(thumbnail.Attribute("width")), Dimension(thumbnail.Attribute("height")));
            }
        }

        private static NewsTextField? AtomText(XElement element)
        {
            var type = element.Attribute("type")?.Value.Trim().ToLowerInvariant() ?? "text";
            switch (type)
            {
                case "html" or "text/html":
                    return new NewsTextField(element.Value, NewsTextKind.Html);
                case "xhtml" or "application/xhtml+xml":
                    var div = element.Element(Xhtml + "div");
                    var nodes = div is null ? element.Nodes() : div.Nodes();
                    return new NewsTextField(string.Concat(nodes.Select(n => n.ToString(SaveOptions.DisableFormatting))), NewsTextKind.Html);
                case "text":
                    return new NewsTextField(element.Value, NewsTextKind.Text);
                default:
                    // Another media type: text and XML are readable as text; anything else is base64 and is skipped.
                    return type.StartsWith("text/", StringComparison.Ordinal) || type.EndsWith("xml", StringComparison.Ordinal)
                        ? new NewsTextField(element.Value, NewsTextKind.Text)
                        : null;
            }
        }

        private static string AtomRel(XElement link) =>
            link.Attribute("rel")?.Value.Trim().ToLowerInvariant() is { Length: > 0 } rel ? rel : "alternate";

        private static int? SyndicationTtl(XElement container)
        {
            var period = Text(container.Element(Sy + "updatePeriod"))?.ToLowerInvariant();
            if (period is null)
            {
                return null;
            }

            var minutes = period switch
            {
                "hourly" => 60,
                "daily" => 1440,
                "weekly" => 10080,
                "monthly" => 43200,
                "yearly" => 525600,
                _ => 0,
            };
            if (minutes == 0)
            {
                return null;
            }

            var frequency = int.TryParse(Text(container.Element(Sy + "updateFrequency")), NumberStyles.None, CultureInfo.InvariantCulture, out var f) && f > 0 ? f : 1;
            return Math.Max(1, minutes / frequency);
        }

        private static Uri? Rebase(Uri? current, XElement element) => Rebase(current, element.Attribute(XmlBase)?.Value);

        private static Uri? Rebase(Uri? current, string? xmlBase)
        {
            if (string.IsNullOrWhiteSpace(xmlBase))
            {
                return current;
            }

            return NewsUrl.TryResolve(xmlBase, current, out var resolved) ? resolved : current;
        }
    }

    /// <summary>An element's trimmed text, or null when it is missing or blank.</summary>
    private static string? Text(XElement? element) =>
        element?.Value is { } value && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;

    /// <summary>RSS titles are text, but are often HTML-escaped twice ("&amp;amp;#8217;"): one more decode is applied.</summary>
    private static string DecodeTitle(string title) => title.Contains('&', StringComparison.Ordinal) ? WebUtility.HtmlDecode(title) : title;

    private static bool IsImageOrUntyped(string? type) =>
        string.IsNullOrWhiteSpace(type) || type.TrimStart().StartsWith("image/", StringComparison.OrdinalIgnoreCase);

    private static int? Dimension(XAttribute? attribute) =>
        int.TryParse(attribute?.Value.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number is > 0 and <= 100_000
            ? number
            : null;

    private static void AddImage(List<NewsParsedImage> images, NewsArticleImageOrigin origin, string? url, Uri? baseUrl, string? alt, int? width, int? height)
    {
        if (NewsUrl.Sanitize(url, baseUrl) is not { } source || images.Exists(i => i.SourceUrl == source))
        {
            return;
        }

        var cleanAlt = alt is null ? null : NewsText.CleanLine(alt);
        images.Add(new NewsParsedImage(
            origin,
            source,
            string.IsNullOrEmpty(cleanAlt) ? null : NewsText.Clamp(cleanAlt, NewsLimits.MaxImageAltTextLength),
            width,
            height));
    }

    // ── Channel-level fields ────────────────────────────────────────────────────────────────────────────

    private static string? FeedTitle(string? title) =>
        NewsText.CleanLine(title) is { Length: > 0 } clean ? NewsText.Clamp(clean, NewsLimits.MaxFeedTitleLength) : null;

    private static string? FeedDescription(string? description) =>
        description is null ? null
        : NewsFieldText.ToLine(new NewsTextField(description, NewsTextKind.Html)) is { Length: > 0 } clean
            ? NewsText.Clamp(clean, NewsLimits.MaxFeedDescriptionLength)
            : null;

    /// <summary>A language tag is kept only in BCP 47's alphabet (letters, digits, hyphens) and within its column.</summary>
    private static string? FeedLanguage(string? language)
    {
        var clean = NewsText.CleanLine(language).Replace('_', '-');
        return clean.Length is > 0 and <= NewsLimits.MaxLanguageLength && clean.All(c => char.IsAsciiLetterOrDigit(c) || c == '-')
            ? clean
            : null;
    }

    /// <summary>
    /// The items kept (§3.4 step 4): the newest <see cref="MaxItemsKept"/> by date (published, else updated), dated items
    /// first, an undated one ranked by its place in the document. The sort is stable.
    /// </summary>
    internal static IReadOnlyList<NewsParsedItem> SelectNewest(IReadOnlyList<NewsParsedItem> items) =>
        items
            .OrderBy(i => (i.PublishedAt ?? i.UpdatedAt) is null ? 1 : 0)
            .ThenByDescending(i => i.PublishedAt ?? i.UpdatedAt)
            .ThenBy(i => i.DocumentIndex)
            .Take(MaxItemsKept)
            .ToList();

    // ── JSON Feed ───────────────────────────────────────────────────────────────────────────────────────

    private static NewsFeedParseResult ParseJson(string text, Uri? documentUrl)
    {
        using var document = JsonDocument.Parse(text, new JsonDocumentOptions
        {
            MaxDepth = MaxJsonDepth,
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip,
        });

        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || String(root, "version") is not { } version
            || !version.StartsWith("https://jsonfeed.org/version/1", StringComparison.Ordinal))
        {
            return NewsFeedParseResult.NotAFeed;
        }

        var homePage = NewsUrl.Sanitize(String(root, "home_page_url"), documentUrl);
        var feedBase = homePage is not null ? new Uri(homePage) : documentUrl;
        var feedAuthor = JsonAuthor(root);

        var items = new List<NewsParsedItem>();
        var limitReached = false;
        if (root.TryGetProperty("items", out var array) && array.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in array.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (items.Count >= MaxItemsRead)
                {
                    limitReached = true;
                    break;
                }

                items.Add(JsonItem(item, items.Count, feedBase, feedAuthor));
            }
        }

        return Finish(new NewsParsedFeed
        {
            Format = NewsFeedFormat.JsonFeed,
            Title = FeedTitle(String(root, "title")),
            SiteUrl = homePage,
            Description = FeedDescription(String(root, "description")),
            Language = FeedLanguage(String(root, "language")),
            Items = SelectNewest(items),
            ItemsRead = items.Count,
            ItemLimitReached = limitReached,
        });
    }

    private static NewsParsedItem JsonItem(JsonElement item, int index, Uri? feedBase, string? feedAuthor)
    {
        var url = String(item, "url");
        var link = url ?? String(item, "external_url");
        var itemBase = NewsUrl.TryResolve(url, feedBase, out var urlUri) ? urlUri : feedBase;

        var contentHtml = String(item, "content_html");
        var contentText = String(item, "content_text");
        var content = contentHtml is not null
            ? new NewsTextField(contentHtml, NewsTextKind.Html)
            : contentText is not null ? new NewsTextField(contentText, NewsTextKind.Text) : null;
        var summary = String(item, "summary");

        var images = new List<NewsParsedImage>();
        AddImage(images, NewsArticleImageOrigin.JsonImage, String(item, "image"), itemBase, null, null, null);
        AddImage(images, NewsArticleImageOrigin.JsonImage, String(item, "banner_image"), itemBase, null, null, null);
        if (item.TryGetProperty("attachments", out var attachments) && attachments.ValueKind == JsonValueKind.Array)
        {
            foreach (var attachment in attachments.EnumerateArray())
            {
                if (attachment.ValueKind == JsonValueKind.Object
                    && String(attachment, "mime_type") is { } mime
                    && mime.TrimStart().StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                {
                    AddImage(images, NewsArticleImageOrigin.JsonAttachment, String(attachment, "url"), itemBase, String(attachment, "title"), null, null);
                }
            }
        }

        var source = SerializeJson(item);
        return new NewsParsedItem
        {
            DocumentIndex = index,
            Guid = JsonId(item),
            Link = link,
            Title = new NewsTextField(String(item, "title") ?? string.Empty, NewsTextKind.Text),
            Author = JsonAuthor(item) ?? feedAuthor,
            PublishedAt = NewsDates.Parse(String(item, "date_published")),
            UpdatedAt = NewsDates.Parse(String(item, "date_modified")),
            Content = content,
            Summary = summary is null ? null : new NewsTextField(summary, NewsTextKind.Text),
            BaseUrl = itemBase,
            Images = images,
            Source = source.Length <= MaxSourceBytes ? source : null,
            SourceTooLarge = source.Length > MaxSourceBytes,
            SourceContentType = JsonSourceContentType,
        };
    }

    /// <summary>The item's <c>id</c>: a string as given, a number as its JSON text (§8.6), anything else none.</summary>
    private static string? JsonId(JsonElement item) =>
        item.TryGetProperty("id", out var id)
            ? id.ValueKind switch
            {
                JsonValueKind.String => id.GetString() is { } s && !string.IsNullOrWhiteSpace(s) ? s.Trim() : null,
                JsonValueKind.Number => id.GetRawText(),
                _ => null,
            }
            : null;

    private static string? JsonAuthor(JsonElement element)
    {
        if (element.TryGetProperty("authors", out var authors) && authors.ValueKind == JsonValueKind.Array)
        {
            foreach (var author in authors.EnumerateArray())
            {
                if (author.ValueKind == JsonValueKind.Object && String(author, "name") is { } name)
                {
                    return name;
                }
            }
        }

        return element.TryGetProperty("author", out var single) && single.ValueKind == JsonValueKind.Object
            ? String(single, "name")
            : null;
    }

    private static string? String(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            && value.GetString() is { } text && !string.IsNullOrWhiteSpace(text)
            ? text.Trim()
            : null;

    private static byte[] SerializeJson(JsonElement item)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = false, SkipValidation = false }))
        {
            item.WriteTo(writer);
        }

        return buffer.ToArray();
    }
}
