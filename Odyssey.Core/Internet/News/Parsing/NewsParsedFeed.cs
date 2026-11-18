using Odyssey.Context;
using Odyssey.Dtos.Internet;

namespace Odyssey.Core.Internet.News.Parsing;

/// <summary>What <see cref="NewsFeedParser.Parse"/> concluded about a body.</summary>
public enum NewsFeedParseOutcome
{
    Ok = 0,

    /// <summary>Not one of the four formats (§8.6): maps to <see cref="NewsFeedErrorKind.NotAFeed"/>.</summary>
    NotAFeed = 1,

    /// <summary>A supported format that does not parse (malformed, too deep, too long): <see cref="NewsFeedErrorKind.ParseError"/>.</summary>
    ParseError = 2,
}

/// <summary>Whether a feed string is plain text or an HTML fragment (which must pass the pre-scan before it is parsed).</summary>
public enum NewsTextKind
{
    Text = 0,
    Html = 1,
}

/// <summary>A raw feed string and how to read it. <see cref="Value"/> is exactly what the feed carried.</summary>
public sealed record NewsTextField(string Value, NewsTextKind Kind);

/// <summary>An image the feed declares outside the body (§6.2): an enclosure, <c>media:*</c> or a JSON image.</summary>
/// <param name="SourceUrl">Already sanitised: an absolute http(s) URL with no user info.</param>
public sealed record NewsParsedImage(NewsArticleImageOrigin Origin, string SourceUrl, string? AltText, int? Width, int? Height);

/// <summary>
/// One item, as the feed carried it: every field raw (not yet cleaned, clamped or converted to text), so the
/// normaliser (<see cref="Content.NewsItemNormalizer"/>) is the one place the §8.7 rules are applied.
/// </summary>
public sealed record NewsParsedItem
{
    /// <summary>The item's position in the document, from 0.</summary>
    public int DocumentIndex { get; init; }

    public string? Guid { get; init; }

    /// <summary>The item link as written, resolved later against <see cref="BaseUrl"/>.</summary>
    public string? Link { get; init; }

    public NewsTextField? Title { get; init; }

    public string? Author { get; init; }

    public DateTimeOffset? PublishedAt { get; init; }

    public DateTimeOffset? UpdatedAt { get; init; }

    /// <summary>The preferred body (§8.3 step 9, before <see cref="Summary"/>): Atom content, content:encoded, JSON content.</summary>
    public NewsTextField? Content { get; init; }

    /// <summary>The summary field (<c>description</c>, Atom or JSON <c>summary</c>); the body when there is no content.</summary>
    public NewsTextField? Summary { get; init; }

    /// <summary>What relative URLs in this item resolve against (§8.6, "Base URL").</summary>
    public Uri? BaseUrl { get; init; }

    /// <summary>Feed-declared images in §6.2 order; inline <c>&lt;img&gt;</c> references are found by the normaliser.</summary>
    public IReadOnlyList<NewsParsedImage> Images { get; init; } = [];

    /// <summary>The re-serialised item (UTF-8), or <c>null</c> when it is over <see cref="NewsFeedParser.MaxSourceBytes"/>.</summary>
    public byte[]? Source { get; init; }

    /// <summary><c>application/xml</c> or <c>application/json</c> (§6.1).</summary>
    public string SourceContentType { get; init; } = NewsFeedParser.XmlSourceContentType;

    /// <summary>The item was over the source cap, so no source file is kept (§6.1: logged by the caller).</summary>
    public bool SourceTooLarge { get; init; }
}

/// <summary>A parsed feed: its channel-level fields (cleaned and clamped) and up to <see cref="NewsFeedParser.MaxItemsKept"/> items.</summary>
public sealed record NewsParsedFeed
{
    public required NewsFeedFormat Format { get; init; }

    public string? Title { get; init; }

    public string? SiteUrl { get; init; }

    public string? Description { get; init; }

    public string? Language { get; init; }

    /// <summary>The publisher's suggested polling interval in minutes (RSS <c>ttl</c>, RDF <c>sy:*</c>), when given.</summary>
    public int? TtlMinutes { get; init; }

    /// <summary>The newest items by date, at most <see cref="NewsFeedParser.MaxItemsKept"/>, newest first.</summary>
    public IReadOnlyList<NewsParsedItem> Items { get; init; } = [];

    /// <summary>How many items were read (at most <see cref="NewsFeedParser.MaxItemsRead"/>).</summary>
    public int ItemsRead { get; init; }

    /// <summary>The document had more than <see cref="NewsFeedParser.MaxItemsRead"/> items; the rest were not read.</summary>
    public bool ItemLimitReached { get; init; }
}

/// <summary>The parse result. <see cref="Feed"/> is set exactly when <see cref="Outcome"/> is <see cref="NewsFeedParseOutcome.Ok"/>.</summary>
public sealed record NewsFeedParseResult(NewsFeedParseOutcome Outcome, NewsParsedFeed? Feed)
{
    public static NewsFeedParseResult NotAFeed { get; } = new(NewsFeedParseOutcome.NotAFeed, null);

    public static NewsFeedParseResult ParseError { get; } = new(NewsFeedParseOutcome.ParseError, null);

    /// <summary>The fetch-state error kind for a failed outcome; <see cref="NewsFeedErrorKind.None"/> for a success.</summary>
    public NewsFeedErrorKind ErrorKind => Outcome switch
    {
        NewsFeedParseOutcome.Ok => NewsFeedErrorKind.None,
        NewsFeedParseOutcome.NotAFeed => NewsFeedErrorKind.NotAFeed,
        _ => NewsFeedErrorKind.ParseError,
    };
}
