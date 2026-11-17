using System.Text;
using System.Text.RegularExpressions;

namespace Odyssey.Core.Internet.News.Parsing;

/// <summary>
/// Turns a feed body into text (issue #470 §8.6): by its byte-order mark, then the XML declaration's encoding (or, for
/// a body without one, the response's <c>charset</c>), then UTF-8. The code-page provider is registered so a
/// <c>windows-1252</c> or <c>iso-8859-15</c> feed decodes. Undecodable bytes become U+FFFD rather than failing the feed.
/// </summary>
public static partial class NewsFeedDecoder
{
    static NewsFeedDecoder() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    /// <summary>The longest XML declaration read for its encoding.</summary>
    private const int DeclarationWindow = 1024;

    /// <summary>Decodes <paramref name="body"/>; <paramref name="contentTypeCharset"/> is the response's charset, if any.</summary>
    public static string Decode(ReadOnlySpan<byte> body, string? contentTypeCharset = null)
    {
        var (encoding, skip) = Detect(body, contentTypeCharset);
        var text = encoding.GetString(body[skip..]);
        return text.Length > 0 && text[0] == (char)0xFEFF ? text[1..] : text;
    }

    /// <summary>
    /// The same detection on the first bytes of a stream (issue #508 §6 item 3): the byte-order mark, then the XML
    /// declaration, then UTF-8, with no response charset. <c>false</c> when the declaration names an encoding .NET cannot
    /// decode, which an OPML import refuses rather than reading as UTF-8. <paramref name="skip"/> is the mark's length.
    /// </summary>
    public static bool TryDetectStreamEncoding(ReadOnlySpan<byte> head, out Encoding encoding, out int skip)
    {
        (encoding, skip) = Detect(head, null);
        if (skip > 0 || encoding is UnicodeEncoding or UTF32Encoding)
        {
            return true;
        }

        var declared = DeclaredEncoding(head)?.Trim();
        if (string.IsNullOrEmpty(declared))
        {
            return true;
        }

        try
        {
            _ = Encoding.GetEncoding(declared);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static (Encoding Encoding, int Skip) Detect(ReadOnlySpan<byte> body, string? charset)
    {
        if (body.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
        {
            return (new UTF8Encoding(false), 3);
        }

        if (body.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE, 0x00, 0x00]))
        {
            return (new UTF32Encoding(false, false), 4);
        }

        if (body.StartsWith((ReadOnlySpan<byte>)[0x00, 0x00, 0xFE, 0xFF]))
        {
            return (new UTF32Encoding(true, false), 4);
        }

        if (body.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE]))
        {
            return (new UnicodeEncoding(false, false), 2);
        }

        if (body.StartsWith((ReadOnlySpan<byte>)[0xFE, 0xFF]))
        {
            return (new UnicodeEncoding(true, false), 2);
        }

        // UTF-16 without a mark, recognised by "<?" (XML 1.0 Appendix F).
        if (body.StartsWith((ReadOnlySpan<byte>)[0x3C, 0x00, 0x3F, 0x00]))
        {
            return (new UnicodeEncoding(false, false), 0);
        }

        if (body.StartsWith((ReadOnlySpan<byte>)[0x00, 0x3C, 0x00, 0x3F]))
        {
            return (new UnicodeEncoding(true, false), 0);
        }

        if (DeclaredEncoding(body) is { } declared && Lookup(declared) is { } fromDeclaration)
        {
            return (fromDeclaration, 0);
        }

        if (Lookup(charset) is { } fromCharset)
        {
            return (fromCharset, 0);
        }

        return (new UTF8Encoding(false), 0);
    }

    private static string? DeclaredEncoding(ReadOnlySpan<byte> body)
    {
        var window = Encoding.Latin1.GetString(body[..Math.Min(body.Length, DeclarationWindow)]).TrimStart();
        if (!window.StartsWith("<?xml", StringComparison.Ordinal))
        {
            return null;
        }

        var end = window.IndexOf("?>", StringComparison.Ordinal);
        var declaration = end < 0 ? window : window[..end];
        var match = EncodingPattern().Match(declaration);
        return match.Success ? match.Groups["name"].Value : null;
    }

    private static Encoding? Lookup(string? name)
    {
        var trimmed = name?.Trim().Trim('"', '\'');
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        try
        {
            var encoding = Encoding.GetEncoding(trimmed);

            // A declaration of UTF-16 in an 8-bit body is wrong (the marks above caught a real one); UTF-8 reads it.
            return encoding is UnicodeEncoding or UTF32Encoding ? null : encoding;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    [GeneratedRegex("""encoding\s*=\s*["'](?<name>[A-Za-z][A-Za-z0-9._\-]{0,39})["']""", RegexOptions.CultureInvariant)]
    private static partial Regex EncodingPattern();
}
