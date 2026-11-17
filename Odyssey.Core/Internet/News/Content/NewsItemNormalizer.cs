using Odyssey.Context;
using Odyssey.Core.Internet.News.Html;
using Odyssey.Core.Internet.News.Identity;
using Odyssey.Core.Internet.News.Parsing;
using Odyssey.Dtos;
using Odyssey.Dtos.Internet;

namespace Odyssey.Core.Internet.News.Content;

/// <summary>Turns one feed text field into a single clean line (an HTML field through the pre-scan and the walk).</summary>
public static class NewsFieldText
{
    /// <summary>The field as one line; a refused HTML fragment yields an empty string.</summary>
    public static string ToLine(NewsTextField field, Uri? baseUrl = null) =>
        field.Kind == NewsTextKind.Html
            ? NewsHtmlText.ToLine(field.Value, baseUrl) is { IsRefused: false } line ? line.Text : string.Empty
            : NewsText.CleanLine(field.Value);
}

/// <summary>An image reference of an article, in §6.2 order, ready for a <c>NewsArticleImages</c> row.</summary>
public sealed record NewsNormalizedImage(NewsArticleImageOrigin Origin, string SourceUrl, string? AltText, int? Width, int? Height);

/// <summary>
/// One item after §8.3, §8.7 and §8.8: every field cleaned and clamped to its column, the body as plain text (or the
/// reason it was omitted), the excerpt, the image list, both hashes and the identity candidates. Nothing here has
/// touched the database; the ingestor resolves identity and writes the rows and files.
/// </summary>
public sealed record NewsNormalizedItem
{
    /// <summary>Clamped to <see cref="NewsLimits.MaxArticleTitleLength"/>; empty when missing or refused.</summary>
    public required string Title { get; init; }

    /// <summary>Why the title was refused (§7.10), or <see cref="NewsHtmlRefusal.None"/>.</summary>
    public NewsHtmlRefusal TitleRefusal { get; init; }

    /// <summary>Clamped to <see cref="NewsLimits.MaxAuthorLength"/>; null when missing or refused.</summary>
    public string? Author { get; init; }

    public NewsHtmlRefusal AuthorRefusal { get; init; }

    /// <summary>An absolute http(s) URL with no user info, or null (§8.7). <c>javascript:</c> and <c>data:</c> are null.</summary>
    public string? Link { get; init; }

    /// <summary><see cref="NewsUrlKey.LinkKey"/> of <see cref="Link"/>.</summary>
    public string? LinkKey { get; init; }

    public byte[]? LinkKeyHash { get; init; }

    /// <summary>The cleaned GUID as the feed carried it (any length), or null.</summary>
    public string? Guid { get; init; }

    /// <summary>
    /// The plain-text file's content (§8.3): UTF-8, LF line ends, at most <see cref="NewsLimits.MaxBodyLength"/> code
    /// points. Null exactly when <see cref="BodyOmittedReason"/> is set, in which case no PlainText file is written.
    /// </summary>
    public string? Body { get; init; }

    public NewsBodyOmittedReason? BodyOmittedReason { get; init; }

    /// <summary>Code points in <see cref="Body"/>; 0 for an omitted body.</summary>
    public int BodyLength { get; init; }

    /// <summary>At most <see cref="NewsLimits.MaxExcerptLength"/> code points, cut at a word boundary.</summary>
    public required string Excerpt { get; init; }

    public DateTime? PublishedAtUtc { get; init; }

    public DateTime? UpdatedAtUtc { get; init; }

    /// <summary>Enclosures, <c>media:content</c>, <c>media:thumbnail</c>, JSON images, JSON attachments, inline images.</summary>
    public IReadOnlyList<NewsNormalizedImage> Images { get; init; } = [];

    /// <summary>The change detector (§8.8).</summary>
    public required byte[] ContentHash { get; init; }

    /// <summary>The identity hash used in <c>h:</c> (§8.8).</summary>
    public required byte[] IdentityHash { get; init; }

    /// <summary>The identity candidates (<c>g:</c>/<c>g#</c>, <c>l:</c>, <c>h:</c>) before the per-fetch marking.</summary>
    public required NewsItemKeys Keys { get; init; }

    /// <summary>The source file's content, or null when the item was over the source cap (<see cref="SourceTooLarge"/>).</summary>
    public byte[]? Source { get; init; }

    public string SourceContentType { get; init; } = NewsFeedParser.XmlSourceContentType;

    public bool SourceTooLarge { get; init; }

    /// <summary>The body was cut (at the length cap, the walk's node cap or its deadline).</summary>
    public bool BodyTruncated { get; init; }
}

/// <summary>
/// The per-item content pipeline (issue #470 §3.4 step 5.1, §7.10, §8.3, §8.7, §8.8). Pure: the same parsed item always
/// normalises to the same result, on either host.
/// </summary>
public static class NewsItemNormalizer
{
    /// <summary>A GUID longer than this becomes <c>g#&lt;sha256&gt;</c> (§8.7).</summary>
    public const int MaxGuidLength = 2000;

    /// <summary>How much of the body <see cref="NewsNormalizedItem.IdentityHash"/> covers (§8.8).</summary>
    public const int IdentityBodyCodePoints = 4096;

    /// <summary>The inline image references kept per item: one past the last storable position (§4.11).</summary>
    public const int MaxImageReferences = NewsLimits.MaxImagePosition + 1;

    public static NewsNormalizedItem Normalize(NewsParsedItem item, TimeSpan? walkDeadline = null)
    {
        ArgumentNullException.ThrowIfNull(item);
        var baseUrl = item.BaseUrl;

        // Title: text, or an HTML fragment through the pre-scan. A refused one is stored empty (§7.10).
        var (fullTitle, titleRefusal) = Line(item.Title, baseUrl, walkDeadline);
        var (fullAuthor, authorRefusal) = Line(item.Author is null ? null : new NewsTextField(item.Author, NewsTextKind.Text), baseUrl, walkDeadline);

        // Body: content first, then the summary field (§8.3 step 9). The summary is the other field, if any.
        var bodyField = item.Content ?? item.Summary;
        var summaryField = item.Content is null ? null : item.Summary;

        var body = Text(bodyField, baseUrl, walkDeadline, collectImages: true);
        var summary = Text(summaryField, baseUrl, walkDeadline, collectImages: false);

        var excerptSource = summary is { Refusal: NewsHtmlRefusal.None, Text.Length: > 0 } ? summary.Text
            : body.Refusal == NewsHtmlRefusal.None ? body.Text
            : summary is { Refusal: NewsHtmlRefusal.None } ? summary.Text
            : string.Empty;

        var link = NewsUrl.Sanitize(item.Link, baseUrl);
        var linkKey = NewsUrlKey.LinkKey(link);
        var guid = NewsText.Clean(item.Guid).Trim() is { Length: > 0 } g ? g : null;

        var omitted = body.Refusal switch
        {
            NewsHtmlRefusal.TooLarge => NewsBodyOmittedReason.TooLarge,
            NewsHtmlRefusal.TooComplex => NewsBodyOmittedReason.TooComplex,
            _ => (NewsBodyOmittedReason?)null,
        };

        var (storedBody, truncated) = omitted is null ? NewsHtmlText.Truncate(body.Text, NewsLimits.MaxBodyLength) : (null, false);
        var title = NewsText.Clamp(fullTitle, NewsLimits.MaxArticleTitleLength);
        var author = fullAuthor.Length == 0 ? null : NewsText.Clamp(fullAuthor, NewsLimits.MaxAuthorLength);

        byte[] contentHash;
        using (var hash = new NewsHashing.Composite())
        {
            if (titleRefusal != NewsHtmlRefusal.None)
            {
                hash.Raw(item.Title!.Value);
            }
            else
            {
                hash.Text(fullTitle);
            }

            if (authorRefusal != NewsHtmlRefusal.None)
            {
                hash.Raw(item.Author!);
            }
            else
            {
                hash.Text(fullAuthor.Length == 0 ? null : fullAuthor);
            }

            hash.Text(link);
            if (omitted is not null)
            {
                hash.Raw(bodyField!.Value);
            }
            else
            {
                hash.Text(body.Text);
            }

            contentHash = hash.Finish();
        }

        var identityBody = omitted is null ? NewsText.TakeCodePoints(body.Text, IdentityBodyCodePoints) : string.Empty;
        byte[] identityHash;
        using (var hash = new NewsHashing.Composite())
        {
            identityHash = hash.Text(fullTitle).Text(identityBody).Finish();
        }

        return new NewsNormalizedItem
        {
            Title = title,
            TitleRefusal = titleRefusal,
            Author = author,
            AuthorRefusal = authorRefusal,
            Link = link,
            LinkKey = linkKey,
            LinkKeyHash = linkKey is null ? null : NewsUrlKey.Hash(linkKey),
            Guid = guid,
            Body = storedBody,
            BodyOmittedReason = omitted,
            BodyLength = storedBody is null ? 0 : NewsText.CodePointLength(storedBody),
            BodyTruncated = truncated || body.Truncated,
            Excerpt = NewsText.Excerpt(excerptSource, NewsLimits.MaxExcerptLength),
            PublishedAtUtc = item.PublishedAt?.UtcDateTime,
            UpdatedAtUtc = item.UpdatedAt?.UtcDateTime,
            Images = Images(item, body.Images),
            ContentHash = contentHash,
            IdentityHash = identityHash,
            Keys = NewsItemKeys.For(guid, linkKey, identityHash, NewsText.CodePointLength(fullTitle) + NewsText.CodePointLength(body.Text)),
            Source = item.Source,
            SourceContentType = item.SourceContentType,
            SourceTooLarge = item.SourceTooLarge,
        };
    }

    private static (string Text, NewsHtmlRefusal Refusal) Line(NewsTextField? field, Uri? baseUrl, TimeSpan? deadline)
    {
        if (field is null)
        {
            return (string.Empty, NewsHtmlRefusal.None);
        }

        if (field.Kind == NewsTextKind.Text)
        {
            return (NewsText.CleanLine(field.Value), NewsHtmlRefusal.None);
        }

        var line = NewsHtmlText.ToLine(field.Value, baseUrl, deadline);
        return (line.Text, line.Refusal);
    }

    private static NewsHtmlTextResult Text(NewsTextField? field, Uri? baseUrl, TimeSpan? deadline, bool collectImages)
    {
        if (field is null)
        {
            return new NewsHtmlTextResult(NewsHtmlRefusal.None, string.Empty, [], false);
        }

        if (field.Kind == NewsTextKind.Html)
        {
            var html = NewsHtmlText.ToPlainText(field.Value, baseUrl, deadline, truncate: false);
            return collectImages ? html : html with { Images = [] };
        }

        // Plain-text sources skip the pre-scan (§8.3 step 8).
        return new NewsHtmlTextResult(NewsHtmlRefusal.None, NewsHtmlText.NormalizeParagraphs(NewsText.Clean(field.Value)), [], false);
    }

    private static IReadOnlyList<NewsNormalizedImage> Images(NewsParsedItem item, IReadOnlyList<NewsHtmlImage> inline)
    {
        var images = new List<NewsNormalizedImage>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var image in item.Images.OrderBy(i => (byte)i.Origin))
        {
            if (images.Count < MaxImageReferences && seen.Add(image.SourceUrl))
            {
                images.Add(new NewsNormalizedImage(image.Origin, image.SourceUrl, image.AltText, image.Width, image.Height));
            }
        }

        foreach (var image in inline)
        {
            if (images.Count < MaxImageReferences && seen.Add(image.SourceUrl))
            {
                images.Add(new NewsNormalizedImage(NewsArticleImageOrigin.InlineImg, image.SourceUrl, image.AltText, image.Width, image.Height));
            }
        }

        return images;
    }
}
