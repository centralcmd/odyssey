using System.Buffers.Binary;
using System.Text;
using Odyssey.Dtos;

namespace Odyssey.Core.Internet.News.Podcasts.Audio;

/// <summary>
/// The chapter-marker reader for stored audio (issue #556 §6.1, §7.7): <c>ID3v2</c> <c>CHAP</c>/<c>CTOC</c> frames in the
/// head tag (≤ 16 MiB) and the MP4 Nero <c>chpl</c> box in <c>moov</c> (≤ 4 MiB read, wherever <c>moov</c> sits).
/// Managed and iterative over a bounded window: at most <see cref="MaxNodes"/> frames or boxes, every length checked
/// against what remains with checked arithmetic, a visited set on <c>CTOC</c> children, and the caller's deadline
/// polled in every loop. A malformed file yields no chapters, never an exception (cancellation aside).
/// </summary>
public static class PodcastAudioChapters
{
    public const int MaxId3Bytes = 16 * 1024 * 1024;
    public const int MaxMoovBytes = 4 * 1024 * 1024;
    public const int MaxNodes = 10_000;

    /// <summary>The deadline the job gives one file (§7.7).</summary>
    public static readonly TimeSpan Deadline = TimeSpan.FromSeconds(2);

    /// <summary>Reads chapters from a stored file; <paramref name="contentType"/> picks the reader.</summary>
    public static async Task<IReadOnlyList<PodcastRawChapter>> ReadAsync(Stream content, string contentType, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        try
        {
            return contentType switch
            {
                "audio/mpeg" or "audio/aac" => await ReadId3Async(content, cancellationToken),
                "audio/mp4" => await ReadMp4Async(content, cancellationToken),
                _ => [],
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return [];
        }
    }

    private static async Task<IReadOnlyList<PodcastRawChapter>> ReadId3Async(Stream content, CancellationToken cancellationToken)
    {
        var header = new byte[10];
        if (await content.ReadAtLeastAsync(header, header.Length, throwOnEndOfStream: false, cancellationToken) < header.Length
            || PodcastAudioFormat.Id3Length(header) is not { } length || length - 10 > MaxId3Bytes)
        {
            return [];
        }

        var body = new byte[(int)Math.Min(length - 10, MaxId3Bytes)];
        var read = await content.ReadAtLeastAsync(body, body.Length, throwOnEndOfStream: false, cancellationToken);
        return ParseId3(header, body.AsSpan(0, read), cancellationToken);
    }

    /// <summary>The tag body after its 10-byte header. Version 2.3 and 2.4 only; an unsynchronised tag yields none.</summary>
    public static IReadOnlyList<PodcastRawChapter> ParseId3(ReadOnlySpan<byte> header, ReadOnlySpan<byte> body, CancellationToken cancellationToken)
    {
        var version = header[3];
        var flags = header[5];
        if (version is not (3 or 4) || (flags & 0x80) != 0)
        {
            return [];
        }

        var at = 0;
        if ((flags & 0x40) != 0)
        {
            if (body.Length < 4)
            {
                return [];
            }

            int? extended = version == 4 ? PodcastAudioFormat.SyncSafe(body) : checked((int)BinaryPrimitives.ReadUInt32BigEndian(body) + 4);
            if (extended is not { } skip || skip < 4 || skip > body.Length)
            {
                return [];
            }

            at = skip;
        }

        var chapters = new Dictionary<string, PodcastRawChapter>(StringComparer.Ordinal);
        var order = new List<string>();
        var tocs = new Dictionary<string, (bool TopLevel, List<string> Children)>(StringComparer.Ordinal);
        var nodes = 0;
        foreach (var frame in Frames(body[at..], version, cancellationToken, ref nodes))
        {
            var data = body.Slice(at + frame.Offset, frame.Length);
            if (frame.Id == "CHAP" && Chap(data, version, cancellationToken, ref nodes) is { } chapter && !chapters.ContainsKey(chapter.Id))
            {
                chapters[chapter.Id] = chapter.Chapter;
                order.Add(chapter.Id);
            }
            else if (frame.Id == "CTOC" && Ctoc(data) is { } toc && !tocs.ContainsKey(toc.Id))
            {
                tocs[toc.Id] = (toc.TopLevel, toc.Children);
            }
        }

        if (tocs.Count == 0)
        {
            return [.. order.Select(id => chapters[id])];
        }

        // The tables of contents, walked from the top level with a visited set, so a CTOC cycle ends.
        var reached = new List<PodcastRawChapter>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var stack = new Stack<string>(tocs.Where(t => t.Value.TopLevel).Select(t => t.Key).Reverse());
        while (stack.Count > 0 && reached.Count <= PodcastLimits.MaxChapters)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++nodes > MaxNodes)
            {
                break;
            }

            var id = stack.Pop();
            if (!visited.Add(id))
            {
                continue;
            }

            if (chapters.TryGetValue(id, out var chapter))
            {
                reached.Add(chapter);
            }
            else if (tocs.TryGetValue(id, out var toc))
            {
                for (var i = toc.Children.Count - 1; i >= 0; i--)
                {
                    stack.Push(toc.Children[i]);
                }
            }
        }

        return reached;
    }

    private readonly record struct Frame(string Id, int Offset, int Length);

    private static List<Frame> Frames(ReadOnlySpan<byte> data, byte version, CancellationToken cancellationToken, ref int nodes)
    {
        var frames = new List<Frame>();
        var at = 0;
        while (at + 10 <= data.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++nodes > MaxNodes || data[at] == 0)
            {
                break;
            }

            var id = Encoding.ASCII.GetString(data.Slice(at, 4));
            int? size = version == 4 ? PodcastAudioFormat.SyncSafe(data.Slice(at + 4, 4)) : (int?)Math.Min(int.MaxValue, BinaryPrimitives.ReadUInt32BigEndian(data.Slice(at + 4, 4)));
            if (size is not { } length || length < 0 || length > data.Length - at - 10)
            {
                break;
            }

            frames.Add(new Frame(id, at + 10, length));
            at = checked(at + 10 + length);
        }

        return frames;
    }

    private static (string Id, PodcastRawChapter Chapter)? Chap(ReadOnlySpan<byte> data, byte version, CancellationToken cancellationToken, ref int nodes)
    {
        var end = data.IndexOf((byte)0);
        if (end <= 0 || end + 1 + 16 > data.Length)
        {
            return null;
        }

        var id = Encoding.Latin1.GetString(data[..end]);
        var times = data.Slice(end + 1, 16);
        var start = BinaryPrimitives.ReadUInt32BigEndian(times);
        var stop = BinaryPrimitives.ReadUInt32BigEndian(times[4..]);
        string? title = null;
        string? url = null;
        var sub = data[(end + 1 + 16)..];
        foreach (var frame in Frames(sub, version, cancellationToken, ref nodes))
        {
            var value = sub.Slice(frame.Offset, frame.Length);
            if (frame.Id == "TIT2" && title is null)
            {
                title = Text(value);
            }
            else if (frame.Id == "WXXX" && url is null)
            {
                url = UserUrl(value);
            }
        }

        return (id, new PodcastRawChapter(start == uint.MaxValue ? null : start, stop == uint.MaxValue ? null : stop, title, url));
    }

    private static (string Id, bool TopLevel, List<string> Children)? Ctoc(ReadOnlySpan<byte> data)
    {
        var end = data.IndexOf((byte)0);
        if (end <= 0 || end + 3 > data.Length)
        {
            return null;
        }

        var id = Encoding.Latin1.GetString(data[..end]);
        var flags = data[end + 1];
        int count = data[end + 2];
        var at = end + 3;
        var children = new List<string>(count);
        for (var i = 0; i < count && at < data.Length; i++)
        {
            var rest = data[at..];
            var stop = rest.IndexOf((byte)0);
            if (stop <= 0)
            {
                break;
            }

            children.Add(Encoding.Latin1.GetString(rest[..stop]));
            at += stop + 1;
        }

        return (id, (flags & 0x02) != 0, children);
    }

    private static string? Text(ReadOnlySpan<byte> value)
    {
        if (value.Length < 1)
        {
            return null;
        }

        return Decode(value[0], value[1..]).TrimEnd('\0');
    }

    private static string? UserUrl(ReadOnlySpan<byte> value)
    {
        if (value.Length < 2)
        {
            return null;
        }

        var encoding = value[0];
        var rest = value[1..];
        var wide = encoding is 1 or 2;
        var skip = Terminator(rest, wide);
        return skip < 0 ? null : Encoding.Latin1.GetString(rest[skip..]).TrimEnd('\0');
    }

    /// <summary>The offset after the description's terminator, or −1.</summary>
    private static int Terminator(ReadOnlySpan<byte> data, bool wide)
    {
        if (!wide)
        {
            var end = data.IndexOf((byte)0);
            return end < 0 ? -1 : end + 1;
        }

        for (var i = 0; i + 1 < data.Length; i += 2)
        {
            if (data[i] == 0 && data[i + 1] == 0)
            {
                return i + 2;
            }
        }

        return -1;
    }

    private static string Decode(byte encoding, ReadOnlySpan<byte> bytes) => encoding switch
    {
        0 => Encoding.Latin1.GetString(bytes),
        1 when bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE => Encoding.Unicode.GetString(bytes[2..]),
        1 when bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF => Encoding.BigEndianUnicode.GetString(bytes[2..]),
        1 or 2 => Encoding.BigEndianUnicode.GetString(bytes),
        3 => Encoding.UTF8.GetString(bytes),
        _ => string.Empty,
    };

    private static async Task<IReadOnlyList<PodcastRawChapter>> ReadMp4Async(Stream content, CancellationToken cancellationToken)
    {
        var header = new byte[16];
        long position = 0;
        long? length = content.CanSeek ? content.Length : null;
        for (var nodes = 0; nodes < MaxNodes; nodes++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = await content.ReadAtLeastAsync(header.AsMemory(0, 8), 8, throwOnEndOfStream: false, cancellationToken);
            if (read < 8)
            {
                return [];
            }

            long size = BinaryPrimitives.ReadUInt32BigEndian(header);
            var type = Encoding.ASCII.GetString(header, 4, 4);
            var headerLength = 8;
            if (size == 1)
            {
                if (await content.ReadAtLeastAsync(header.AsMemory(8, 8), 8, throwOnEndOfStream: false, cancellationToken) < 8)
                {
                    return [];
                }

                size = BinaryPrimitives.ReadInt64BigEndian(header.AsSpan(8));
                headerLength = 16;
            }
            else if (size == 0)
            {
                size = length is { } total ? total - position : long.MaxValue;
            }

            if (size < headerLength)
            {
                return [];
            }

            var bodyLength = size - headerLength;
            if (type == "moov")
            {
                var buffer = new byte[(int)Math.Min(bodyLength, MaxMoovBytes)];
                var got = await content.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, cancellationToken);
                return ParseMoov(buffer.AsSpan(0, got), cancellationToken);
            }

            position = checked(position + size);
            if (!await SkipAsync(content, bodyLength, cancellationToken))
            {
                return [];
            }
        }

        return [];
    }

    private static async Task<bool> SkipAsync(Stream content, long count, CancellationToken cancellationToken)
    {
        if (content.CanSeek)
        {
            var target = checked(content.Position + count);
            if (target > content.Length)
            {
                return false;
            }

            content.Position = target;
            return true;
        }

        var scratch = new byte[81920];
        while (count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = await content.ReadAsync(scratch.AsMemory(0, (int)Math.Min(scratch.Length, count)), cancellationToken);
            if (read == 0)
            {
                return false;
            }

            count -= read;
        }

        return true;
    }

    /// <summary>The <c>moov</c> body: finds <c>udta/chpl</c> and reads the Nero chapter list (start times in 100 ns).</summary>
    public static IReadOnlyList<PodcastRawChapter> ParseMoov(ReadOnlySpan<byte> moov, CancellationToken cancellationToken)
    {
        var nodes = 0;
        if (!TryChild(moov, "udta", cancellationToken, ref nodes, out var udta) || !TryChild(udta, "chpl", cancellationToken, ref nodes, out var chpl))
        {
            return [];
        }

        if (chpl.Length < 5)
        {
            return [];
        }

        var at = 4 + (chpl[0] != 0 ? 4 : 0);
        if (at >= chpl.Length)
        {
            return [];
        }

        int count = chpl[at++];
        var starts = new List<(long Start, string Title)>(count);
        for (var i = 0; i < count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (at + 9 > chpl.Length)
            {
                break;
            }

            var start = BinaryPrimitives.ReadInt64BigEndian(chpl[at..]);
            int titleLength = chpl[at + 8];
            at += 9;
            if (titleLength > chpl.Length - at)
            {
                break;
            }

            starts.Add((start, Encoding.UTF8.GetString(chpl.Slice(at, titleLength))));
            at += titleLength;
        }

        return [.. starts.Select((c, index) => new PodcastRawChapter(
            c.Start < 0 ? null : c.Start / 10_000,
            index + 1 < starts.Count && starts[index + 1].Start >= 0 ? starts[index + 1].Start / 10_000 : null,
            c.Title,
            null))];
    }

    /// <summary>The body of the first child box of <paramref name="type"/>, by a bounded walk of the siblings.</summary>
    private static bool TryChild(ReadOnlySpan<byte> parent, string type, CancellationToken cancellationToken, ref int nodes, out ReadOnlySpan<byte> body)
    {
        var wanted = Encoding.ASCII.GetBytes(type);
        var at = 0;
        while (at + 8 <= parent.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++nodes > MaxNodes)
            {
                break;
            }

            long size = BinaryPrimitives.ReadUInt32BigEndian(parent[at..]);
            var headerLength = 8;
            if (size == 1)
            {
                if (at + 16 > parent.Length)
                {
                    break;
                }

                size = BinaryPrimitives.ReadInt64BigEndian(parent[(at + 8)..]);
                headerLength = 16;
            }
            else if (size == 0)
            {
                size = parent.Length - at;
            }

            if (size < headerLength || size > parent.Length - at)
            {
                break;
            }

            if (parent.Slice(at + 4, 4).SequenceEqual(wanted))
            {
                body = parent.Slice(at + headerLength, (int)size - headerLength);
                return true;
            }

            at += (int)size;
        }

        body = default;
        return false;
    }
}
