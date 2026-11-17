using System.IO.Compression;
using Odyssey.Core.Internet.News.Parsing;
using Odyssey.Dtos;

namespace Odyssey.Core.Internet.News.Fetching;

/// <summary>The outcome of reading a response body under its cap.</summary>
public enum NewsBodyOutcome
{
    Ok = 0,

    /// <summary>Over the cap after decompression (a gzip bomb included): <c>TooLarge</c>.</summary>
    TooLarge = 1,

    /// <summary>A <c>Content-Encoding</c> the reader does not decode: <c>Unsupported</c>.</summary>
    UnsupportedEncoding = 2,

    /// <summary>The compressed stream is corrupt: <c>ParseError</c>.</summary>
    CorruptEncoding = 3,
}

public sealed record NewsBodyResult(NewsBodyOutcome Outcome, byte[] Body)
{
    public bool IsOk => Outcome == NewsBodyOutcome.Ok;
}

/// <summary>
/// Reads a body while streaming it (§3.4 step 3, §6.2): decompression is done here, not by the handler, so the cap
/// applies to the DECOMPRESSED bytes and a reader stops at the cap instead of inflating a bomb. Stacked encodings
/// (<c>gzip, br</c>) are undone in reverse order.
/// </summary>
public static class NewsBodyReader
{
    /// <summary>The decompressed feed body cap (§3.4 step 3).</summary>
    public const long MaxFeedBodyBytes = 10L * 1024 * 1024;

    public static async Task<NewsBodyResult> ReadAsync(HttpContent content, long maxBytes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (content.Headers.ContentLength is { } declared && declared > maxBytes && content.Headers.ContentEncoding.Count == 0)
        {
            return new NewsBodyResult(NewsBodyOutcome.TooLarge, []);
        }

        Stream stream = await content.ReadAsStreamAsync(cancellationToken);
        var opened = new List<Stream> { stream };
        try
        {
            foreach (var encoding in content.Headers.ContentEncoding.Reverse())
            {
                switch (encoding.Trim().ToLowerInvariant())
                {
                    case "gzip" or "x-gzip":
                        stream = new GZipStream(stream, CompressionMode.Decompress);
                        break;
                    case "deflate":
                        stream = new ZLibStream(stream, CompressionMode.Decompress);
                        break;
                    case "br":
                        stream = new BrotliStream(stream, CompressionMode.Decompress);
                        break;
                    case "identity" or "":
                        continue;
                    default:
                        return new NewsBodyResult(NewsBodyOutcome.UnsupportedEncoding, []);
                }

                opened.Add(stream);
            }

            return await ReadCappedAsync(stream, maxBytes, cancellationToken);
        }
        catch (InvalidDataException)
        {
            return new NewsBodyResult(NewsBodyOutcome.CorruptEncoding, []);
        }
        finally
        {
            for (var k = opened.Count - 1; k >= 0; k--)
            {
                await opened[k].DisposeAsync();
            }
        }
    }

    /// <summary>Reads at most <paramref name="maxBytes"/>; one byte more is <see cref="NewsBodyOutcome.TooLarge"/>.</summary>
    public static async Task<NewsBodyResult> ReadCappedAsync(Stream stream, long maxBytes, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, cancellationToken);
            if (read == 0)
            {
                return new NewsBodyResult(NewsBodyOutcome.Ok, buffer.ToArray());
            }

            if (buffer.Length + read > maxBytes)
            {
                return new NewsBodyResult(NewsBodyOutcome.TooLarge, []);
            }

            buffer.Write(chunk, 0, read);
        }
    }
}

/// <summary>
/// The HTTP validators (§8.7, AC 23): an <c>ETag</c> or <c>Last-Modified</c> containing CR/LF, a control or anything
/// outside printable ASCII is dropped — never stored and never sent back. Only spaces are trimmed, so a trailing line
/// break is a rejection, not something quietly cut off.
/// </summary>
public static class NewsValidators
{
    public static string? SanitizeETag(string? value)
    {
        var trimmed = value?.Trim(' ');
        return IsPrintableAscii(trimmed) && trimmed!.Length <= NewsLimits.MaxETagLength ? trimmed : null;
    }

    /// <summary>Kept as received when it is printable ASCII, fits its column and is a date at all.</summary>
    public static string? SanitizeLastModified(string? value)
    {
        var trimmed = value?.Trim(' ');
        return IsPrintableAscii(trimmed) && trimmed!.Length <= NewsLimits.MaxLastModifiedLength && NewsDates.Parse(trimmed) is not null
            ? trimmed
            : null;
    }

    private static bool IsPrintableAscii(string? value) =>
        !string.IsNullOrEmpty(value) && value.All(c => c is >= ' ' and <= '~');
}
