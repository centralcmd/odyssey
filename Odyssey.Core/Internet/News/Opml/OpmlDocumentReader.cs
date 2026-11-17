using System.Globalization;
using System.Text;
using System.Xml;
using Odyssey.Core.Internet.News.Content;
using Odyssey.Core.Internet.News.Parsing;
using Odyssey.Dtos;
using Odyssey.Dtos.Internet;

namespace Odyssey.Core.Internet.News.Opml;

/// <summary>The file is not an OPML document the import can read (issue #508 §6, §9 item 3). It carries no parser text.</summary>
public sealed class OpmlUnreadableException(Exception? inner = null)
    : Exception("This file is not an OPML document.", inner);

/// <summary>
/// One feed outline (§8.1) with the §8.2 fields already interpreted. <see cref="Url"/> is the trimmed <c>xmlUrl</c>
/// exactly as submitted; <see cref="TagNames"/> are this outline's tag names (§8.3.1–2: the innermost folder and the
/// last segment of each <c>category</c> path, trimmed and cleaned, empty ones dropped), not yet de-duplicated.
/// </summary>
public sealed record OpmlFeedOutline(
    string Url,
    string? ReportTitle,
    string? CustomTitle,
    int? FetchIntervalMinutes,
    bool DownloadImages,
    bool IsActive,
    IReadOnlyList<string> TagNames);

/// <summary>
/// Reads an OPML 1.0, 1.1 or 2.0 upload as a stream (issue #508 §3, §6, §8.1–§8.3): the encoding from a byte-order
/// mark, else the XML declaration, else UTF-8; then an <see cref="XmlReader"/> over <see cref="OpmlTextReader"/> and
/// the depth cap. No DOM is built and no attribute but the mapped ones is read. Any failure — malformed XML, a root
/// other than <c>opml</c>, no <c>body</c>, nesting beyond 64, a token over the cap, an undecodable encoding — is
/// <see cref="OpmlUnreadableException"/>, and one oversized token fails the whole file (B4).
/// </summary>
public static class OpmlDocumentReader
{
    /// <summary>The namespace an Odyssey-written file declares on its root, under any prefix (§6 item 5).</summary>
    public const string OdysseyNamespace = "urn:odyssey:opml:1";

    private const string XmlnsNamespace = "http://www.w3.org/2000/xmlns/";

    private const int EncodingWindow = 1024;

    /// <summary>The reader settings (§7.5): no DTD, no resolver, comments and PIs skipped, no document-size cap.</summary>
    internal static XmlReaderSettings ReaderSettings() => new()
    {
        DtdProcessing = DtdProcessing.Ignore,
        XmlResolver = null,
        MaxCharactersFromEntities = 1024,

        // The byte cap and the token cap bound the input; the feed parser's 16M-character cap would make 64 MB unreachable.
        MaxCharactersInDocument = 0,
        CheckCharacters = false,
        IgnoreComments = true,
        IgnoreProcessingInstructions = true,
        IgnoreWhitespace = true,
        ConformanceLevel = ConformanceLevel.Document,
        CloseInput = true,
    };

    /// <summary>
    /// Reads every feed outline of <paramref name="stream"/> in document order, handing each to
    /// <paramref name="onFeed"/>. The stream is read synchronously: an uploaded form file is already buffered.
    /// </summary>
    public static void Read(Stream stream, Action<OpmlFeedOutline> onFeed, CancellationToken cancellationToken = default,
        int chunkSize = OpmlTextReader.DefaultChunkSize)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(onFeed);
        try
        {
            using var text = Decode(stream, chunkSize);
            using var reader = new DepthCappedXmlReader(XmlReader.Create(text, ReaderSettings()));
            ReadDocument(reader, onFeed, cancellationToken);
        }
        catch (Exception ex) when (ex is XmlException or OpmlTokenLimitException or DecoderFallbackException or ArgumentException)
        {
            throw new OpmlUnreadableException(ex);
        }
    }

    private static OpmlTextReader Decode(Stream stream, int chunkSize)
    {
        var head = new byte[EncodingWindow];
        var length = 0;
        int read;
        while (length < head.Length && (read = stream.Read(head, length, head.Length - length)) > 0)
        {
            length += read;
        }

        if (!NewsFeedDecoder.TryDetectStreamEncoding(head.AsSpan(0, length), out var encoding, out var skip))
        {
            throw new OpmlUnreadableException();
        }

        var source = new HeadReplayStream(head, skip, length, stream);
        return new OpmlTextReader(new StreamReader(source, encoding, detectEncodingFromByteOrderMarks: false, bufferSize: chunkSize), chunkSize);
    }

    private static void ReadDocument(XmlReader reader, Action<OpmlFeedOutline> onFeed, CancellationToken cancellationToken)
    {
        if (reader.MoveToContent() != XmlNodeType.Element || reader.LocalName != "opml")
        {
            throw new OpmlUnreadableException();
        }

        var rootNamespace = reader.NamespaceURI;
        var odysseyFile = DeclaresOdysseyNamespace(reader);
        if (reader.IsEmptyElement)
        {
            throw new OpmlUnreadableException();
        }

        var sawBody = false;
        var frames = new List<Frame>();
        var bodyDepth = -1;
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == bodyDepth)
            {
                bodyDepth = -1;
                frames.Clear();
                continue;
            }

            if (reader.NodeType != XmlNodeType.Element)
            {
                continue;
            }

            if (reader.Depth == 1)
            {
                if (reader.LocalName == "body" && reader.NamespaceURI == rootNamespace)
                {
                    sawBody = true;
                    bodyDepth = reader.IsEmptyElement ? -1 : 1;
                }

                continue;
            }

            if (bodyDepth < 0 || reader.LocalName != "outline" || reader.NamespaceURI != rootNamespace)
            {
                continue;
            }

            cancellationToken.ThrowIfCancellationRequested();
            var depth = reader.Depth;
            frames.RemoveAll(f => f.Depth >= depth);
            var url = reader.GetAttribute("xmlUrl")?.Trim();
            if (!string.IsNullOrEmpty(url))
            {
                onFeed(Outline(reader, url, odysseyFile, InnermostFolder(frames)));
                if (!reader.IsEmptyElement)
                {
                    frames.Add(new Frame(depth, IsFeed: true, Name: null));
                }
            }
            else if (!reader.IsEmptyElement)
            {
                frames.Add(new Frame(depth, IsFeed: false, Name: Name(reader.GetAttribute("text")) ?? Name(reader.GetAttribute("title"))));
            }
        }

        if (!sawBody)
        {
            throw new OpmlUnreadableException();
        }
    }

    private static bool DeclaresOdysseyNamespace(XmlReader reader)
    {
        if (!reader.MoveToFirstAttribute())
        {
            return false;
        }

        try
        {
            do
            {
                if (reader.NamespaceURI == XmlnsNamespace && reader.Value == OdysseyNamespace)
                {
                    return true;
                }
            }
            while (reader.MoveToNextAttribute());

            return false;
        }
        finally
        {
            reader.MoveToElement();
        }
    }

    private static string? InnermostFolder(List<Frame> frames)
    {
        for (var i = frames.Count - 1; i >= 0; i--)
        {
            if (!frames[i].IsFeed)
            {
                return frames[i].Name;
            }
        }

        return null;
    }

    private static OpmlFeedOutline Outline(XmlReader reader, string url, bool odysseyFile, string? folder)
    {
        var text = Name(reader.GetAttribute("text"));
        var title = Name(reader.GetAttribute("title"));
        var reportTitle = Title(text ?? title);

        // In an Odyssey file only odyssey:customTitle is a custom title, so a round trip never freezes a publisher's
        // title; in a foreign file the text is the name the person gave the feed in their old reader (§8.2).
        var customTitle = odysseyFile ? Title(Name(reader.GetAttribute("customTitle", OdysseyNamespace))) : reportTitle;

        int? interval = null;
        var downloadImages = true;
        var active = true;
        if (odysseyFile)
        {
            if (int.TryParse(reader.GetAttribute("fetchIntervalMinutes", OdysseyNamespace)?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var minutes)
                && minutes is >= NewsLimits.MinFetchIntervalMinutes and <= NewsLimits.MaxFetchIntervalMinutes)
            {
                interval = minutes;
            }

            downloadImages = !string.Equals(reader.GetAttribute("downloadImages", OdysseyNamespace)?.Trim(), "false", StringComparison.Ordinal);
            active = !string.Equals(reader.GetAttribute("active", OdysseyNamespace)?.Trim(), "false", StringComparison.Ordinal);
        }

        var tags = new List<string>();
        if (folder is not null)
        {
            tags.Add(folder);
        }

        AddCategories(reader.GetAttribute("category"), odysseyFile, tags);
        return new OpmlFeedOutline(url, reportTitle, customTitle, interval, downloadImages, active, tags);
    }

    /// <summary>
    /// The last segment of each comma-separated <c>category</c> path, at most <see cref="NewsOpmlLimits.MaxCategorySegments"/>
    /// segments read (§8.3.1). An Odyssey file's segments decode <c>%25</c>, <c>%2C</c> and <c>%2F</c>; any other file's
    /// are literal.
    /// </summary>
    internal static void AddCategories(string? category, bool odysseyFile, List<string> into)
    {
        if (string.IsNullOrEmpty(category))
        {
            return;
        }

        var start = 0;
        for (var segments = 0; segments < NewsOpmlLimits.MaxCategorySegments && start <= category.Length; segments++)
        {
            var comma = category.IndexOf(',', start);
            var end = comma < 0 ? category.Length : comma;
            var path = category.AsSpan(start, end - start).Trim().TrimEnd('/');
            var slash = path.LastIndexOf('/');
            var last = slash < 0 ? path : path[(slash + 1)..];
            if (Name(odysseyFile ? DecodeSegment(last) : last.ToString()) is { } name)
            {
                into.Add(name);
            }

            if (comma < 0)
            {
                break;
            }

            start = comma + 1;
        }
    }

    /// <summary>The writer's escape reversed, left to right: <c>%25</c>, <c>%2C</c>, <c>%2F</c>; any other <c>%</c> stays.</summary>
    internal static string DecodeSegment(ReadOnlySpan<char> segment)
    {
        if (segment.IndexOf('%') < 0)
        {
            return segment.ToString();
        }

        var builder = new StringBuilder(segment.Length);
        for (var i = 0; i < segment.Length; i++)
        {
            if (segment[i] == '%' && i + 2 < segment.Length)
            {
                var code = char.ToUpperInvariant(segment[i + 1]).ToString() + char.ToUpperInvariant(segment[i + 2]);
                var decoded = code switch
                {
                    "25" => '%',
                    "2C" => ',',
                    "2F" => '/',
                    _ => '\0',
                };
                if (decoded != '\0')
                {
                    builder.Append(decoded);
                    i += 2;
                    continue;
                }
            }

            builder.Append(segment[i]);
        }

        return builder.ToString();
    }

    /// <summary>Trimmed, <see cref="NewsText.Clean"/>ed; <c>null</c> when nothing is left.</summary>
    private static string? Name(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var cleaned = NewsText.Clean(value).Trim();
        return cleaned.Length == 0 ? null : cleaned;
    }

    private static string? Title(string? value) => value is null ? null : NewsText.Clamp(value, NewsLimits.MaxCustomTitleLength);

    private sealed record Frame(int Depth, bool IsFeed, string? Name);

    /// <summary>Replays the bytes already read for the encoding (past the byte-order mark), then the rest of the stream.</summary>
    private sealed class HeadReplayStream(byte[] head, int position, int length, Stream rest) : Stream
    {
        private int position = position;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (position < length)
            {
                var n = Math.Min(count, length - position);
                Array.Copy(head, position, buffer, offset, n);
                position += n;
                return n;
            }

            return rest.Read(buffer, offset, count);
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                rest.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
