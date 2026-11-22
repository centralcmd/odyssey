using System.Buffers.Binary;

namespace Odyssey.Core.Internet.News.Podcasts.Audio;

/// <summary>A detected audio container (issue #556 §6.1).</summary>
public sealed record PodcastAudioType(string ContentType, string Extension);

/// <summary>
/// Audio detection from the first <see cref="HeadBytes"/> of a body (issue #556 §6.1). The declared type and the URL
/// extension are ignored: only magic bytes decide, and anything that is not one of the five containers is <c>null</c>.
/// Nothing is decoded.
/// </summary>
public static class PodcastAudioFormat
{
    public const int HeadBytes = 64 * 1024;

    public static readonly PodcastAudioType Mpeg = new("audio/mpeg", ".mp3");
    public static readonly PodcastAudioType Aac = new("audio/aac", ".aac");
    public static readonly PodcastAudioType Mp4 = new("audio/mp4", ".m4a");
    public static readonly PodcastAudioType Ogg = new("audio/ogg", ".ogg");
    public static readonly PodcastAudioType Flac = new("audio/flac", ".flac");

    private static readonly string[] Mp4Brands = ["M4A ", "M4B ", "mp42", "isom"];

    private static readonly int[][] Mpeg1Bitrates =
    [
        [0, 32, 64, 96, 128, 160, 192, 224, 256, 288, 320, 352, 384, 416, 448],
        [0, 32, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 384],
        [0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320],
    ];

    private static readonly int[][] Mpeg2Bitrates =
    [
        [0, 32, 48, 56, 64, 80, 96, 112, 128, 144, 160, 176, 192, 224, 256],
        [0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160],
        [0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160],
    ];

    private static readonly int[][] SampleRates =
    [
        [11025, 12000, 8000],
        [0, 0, 0],
        [22050, 24000, 16000],
        [44100, 48000, 32000],
    ];

    public static PodcastAudioType? Detect(ReadOnlySpan<byte> head)
    {
        if (head.Length < 4)
        {
            return null;
        }

        if (head[..4].SequenceEqual("fLaC"u8))
        {
            return Flac;
        }

        if (head[..4].SequenceEqual("OggS"u8))
        {
            return IsOggAudio(head) ? Ogg : null;
        }

        if (head.Length >= 12 && head.Slice(4, 4).SequenceEqual("ftyp"u8))
        {
            return IsMp4Audio(head) ? Mp4 : null;
        }

        if (head.Length >= 10 && head[..3].SequenceEqual("ID3"u8))
        {
            return AfterId3(head);
        }

        if (MpegFrameLength(head, 0, layerIIIOnly: true) is { } first && MpegFrameLength(head, first, layerIIIOnly: true) is not null)
        {
            return Mpeg;
        }

        return AdtsFrameLength(head, 0) is { } adts && AdtsFrameLength(head, adts) is not null ? Aac : null;
    }

    /// <summary>The ID3v2 tag's total length (header, body and footer), or <c>null</c> when it is not one.</summary>
    public static long? Id3Length(ReadOnlySpan<byte> head)
    {
        if (head.Length < 10 || !head[..3].SequenceEqual("ID3"u8) || head[3] is < 2 or > 4)
        {
            return null;
        }

        if (SyncSafe(head.Slice(6, 4)) is not { } size)
        {
            return null;
        }

        return 10L + size + ((head[5] & 0x10) != 0 ? 10 : 0);
    }

    internal static int? SyncSafe(ReadOnlySpan<byte> bytes)
    {
        var value = 0;
        foreach (var b in bytes[..4])
        {
            if ((b & 0x80) != 0)
            {
                return null;
            }

            value = (value << 7) | b;
        }

        return value;
    }

    private static PodcastAudioType? AfterId3(ReadOnlySpan<byte> head)
    {
        if (Id3Length(head) is not { } length)
        {
            return null;
        }

        // A tag longer than the window (cover art routinely is): the frame after it cannot be seen, so the tag decides.
        if (length >= head.Length - 4)
        {
            return Mpeg;
        }

        var offset = (int)length;
        // Padding some encoders leave past the declared size.
        while (offset < head.Length - 4 && head[offset] == 0)
        {
            offset++;
        }

        if (MpegFrameLength(head, offset, layerIIIOnly: false) is not null)
        {
            return Mpeg;
        }

        if (AdtsFrameLength(head, offset) is not null)
        {
            return Aac;
        }

        return offset + 4 <= head.Length && head.Slice(offset, 4).SequenceEqual("fLaC"u8) ? Flac : null;
    }

    /// <summary>The end offset of the MPEG audio frame at <paramref name="offset"/> (where the next one starts), or <c>null</c>.</summary>
    internal static int? MpegFrameLength(ReadOnlySpan<byte> data, int offset, bool layerIIIOnly)
    {
        if (offset < 0 || offset > data.Length - 4)
        {
            return null;
        }

        var b1 = data[offset + 1];
        var b2 = data[offset + 2];
        if (data[offset] != 0xFF || (b1 & 0xE0) != 0xE0)
        {
            return null;
        }

        var version = (b1 >> 3) & 0x3;
        var layer = (b1 >> 1) & 0x3;
        var bitrateIndex = b2 >> 4;
        var rateIndex = (b2 >> 2) & 0x3;
        if (version == 1 || layer == 0 || (layerIIIOnly && layer != 1) || bitrateIndex is 0 or 15 || rateIndex == 3)
        {
            return null;
        }

        var layerRow = 3 - layer; // 0 = I, 1 = II, 2 = III
        var kbps = (version == 3 ? Mpeg1Bitrates : Mpeg2Bitrates)[layerRow][bitrateIndex];
        var rate = SampleRates[version][rateIndex];
        var padding = (b2 >> 1) & 0x1;
        var length = layerRow switch
        {
            0 => ((12 * kbps * 1000 / rate) + padding) * 4,
            2 when version != 3 => (72 * kbps * 1000 / rate) + padding,
            _ => (144 * kbps * 1000 / rate) + padding,
        };
        return length >= 4 ? length + offset : null;
    }

    /// <summary>The end offset of the ADTS frame at <paramref name="offset"/>, or <c>null</c>.</summary>
    internal static int? AdtsFrameLength(ReadOnlySpan<byte> data, int offset)
    {
        if (offset < 0 || offset > data.Length - 7)
        {
            return null;
        }

        if (data[offset] != 0xFF || (data[offset + 1] & 0xF6) != 0xF0)
        {
            return null;
        }

        var length = ((data[offset + 3] & 0x03) << 11) | (data[offset + 4] << 3) | (data[offset + 5] >> 5);
        return length >= 7 ? offset + length : null;
    }

    private static bool IsMp4Audio(ReadOnlySpan<byte> head)
    {
        var size = BinaryPrimitives.ReadUInt32BigEndian(head);
        var end = (int)Math.Min(head.Length, size is >= 16 ? size : head.Length);
        if (Matches(head.Slice(8, 4)))
        {
            return true;
        }

        for (var at = 16; at + 4 <= end; at += 4)
        {
            if (Matches(head.Slice(at, 4)))
            {
                return true;
            }
        }

        return false;

        static bool Matches(ReadOnlySpan<byte> brand)
        {
            foreach (var candidate in Mp4Brands)
            {
                if (brand.SequenceEqual(System.Text.Encoding.ASCII.GetBytes(candidate)))
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>The first Ogg page's payload begins with an Opus or a Vorbis identification header.</summary>
    private static bool IsOggAudio(ReadOnlySpan<byte> head)
    {
        if (head.Length < 27)
        {
            return false;
        }

        var segments = head[26];
        var payload = 27 + segments;
        if (payload > head.Length)
        {
            return false;
        }

        var rest = head[payload..];
        return rest.StartsWith("OpusHead"u8) || rest.StartsWith("\u0001vorbis"u8);
    }
}
