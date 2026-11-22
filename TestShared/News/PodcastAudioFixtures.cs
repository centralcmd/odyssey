using System.Buffers.Binary;
using System.Text;

namespace Odyssey.Testing.News;

/// <summary>
/// Hand-built audio containers for the podcast download tests (issue #556 §6.1): an MP3 with an ID3v2.4 head tag whose
/// <c>CHAP</c>/<c>CTOC</c> frames name chapters, an M4A with a Nero <c>chpl</c> box (its <c>moov</c> at the head or the
/// tail), and the bare magic of the other three formats. Nothing here is decodable audio; only the containers are real.
/// </summary>
public static class PodcastAudioFixtures
{
    /// <summary>One MPEG-1 Layer III frame header, 128 kbit/s at 44.1 kHz: a 417-byte frame.</summary>
    private static readonly byte[] Mp3FrameHeader = [0xFF, 0xFB, 0x90, 0x00];

    public const int Mp3FrameLength = 417;

    public sealed record Chapter(uint StartMs, uint? EndMs, string Title, string? Url = null);

    /// <summary>Raw MPEG frames: <paramref name="frames"/> valid Layer III frames back to back.</summary>
    public static byte[] Mp3Frames(int frames = 4)
    {
        var bytes = new byte[frames * Mp3FrameLength];
        for (var i = 0; i < frames; i++)
        {
            Mp3FrameHeader.CopyTo(bytes, i * Mp3FrameLength);
        }

        return bytes;
    }

    /// <summary>An MP3: an ID3v2.4 tag with the chapters (and a CTOC naming them, when asked), then MPEG frames.</summary>
    public static byte[] Mp3(IReadOnlyList<Chapter>? chapters = null, bool withToc = true, int frames = 4, int padding = 0)
    {
        var body = new MemoryStream();
        chapters ??= [];
        if (withToc && chapters.Count > 0)
        {
            var toc = new MemoryStream();
            toc.Write("toc\0"u8);
            toc.WriteByte(0x03);
            toc.WriteByte((byte)chapters.Count);
            for (var i = 0; i < chapters.Count; i++)
            {
                toc.Write(Encoding.ASCII.GetBytes($"ch{i}\0"));
            }

            WriteFrame(body, "CTOC", toc.ToArray());
        }

        for (var i = 0; i < chapters.Count; i++)
        {
            WriteFrame(body, "CHAP", Chap($"ch{i}", chapters[i]));
        }

        body.Write(new byte[padding]);
        return [.. Id3Header(4, (int)body.Length), .. body.ToArray(), .. Mp3Frames(frames)];
    }

    /// <summary>A CHAP frame body (element id, times, offsets, TIT2 and optional WXXX sub-frames).</summary>
    public static byte[] Chap(string id, Chapter chapter)
    {
        var data = new MemoryStream();
        data.Write(Encoding.ASCII.GetBytes(id + "\0"));
        Span<byte> times = stackalloc byte[16];
        BinaryPrimitives.WriteUInt32BigEndian(times, chapter.StartMs);
        BinaryPrimitives.WriteUInt32BigEndian(times[4..], chapter.EndMs ?? uint.MaxValue);
        BinaryPrimitives.WriteUInt32BigEndian(times[8..], uint.MaxValue);
        BinaryPrimitives.WriteUInt32BigEndian(times[12..], uint.MaxValue);
        data.Write(times);
        WriteFrame(data, "TIT2", [3, .. Encoding.UTF8.GetBytes(chapter.Title)]);
        if (chapter.Url is not null)
        {
            WriteFrame(data, "WXXX", [0, 0, .. Encoding.Latin1.GetBytes(chapter.Url)]);
        }

        return data.ToArray();
    }

    /// <summary>A CTOC frame body.</summary>
    public static byte[] Ctoc(string id, bool topLevel, params string[] children)
    {
        var data = new MemoryStream();
        data.Write(Encoding.ASCII.GetBytes(id + "\0"));
        data.WriteByte((byte)(topLevel ? 0x03 : 0x01));
        data.WriteByte((byte)children.Length);
        foreach (var child in children)
        {
            data.Write(Encoding.ASCII.GetBytes(child + "\0"));
        }

        return data.ToArray();
    }

    /// <summary>An ID3v2.4 tag around arbitrary frames, then MPEG frames.</summary>
    public static byte[] Mp3WithFrames(params (string Id, byte[] Data)[] frames)
    {
        var body = new MemoryStream();
        foreach (var (id, data) in frames)
        {
            WriteFrame(body, id, data);
        }

        return [.. Id3Header(4, (int)body.Length), .. body.ToArray(), .. Mp3Frames()];
    }

    public static byte[] Id3Header(byte version, int size)
    {
        var header = new byte[10];
        "ID3"u8.CopyTo(header);
        header[3] = version;
        WriteSyncSafe(header.AsSpan(6), size);
        return header;
    }

    /// <summary>An M4A: <c>ftyp</c>, <c>mdat</c> and a <c>moov</c> holding <c>udta/chpl</c>, the moov first or last.</summary>
    public static byte[] M4a(IReadOnlyList<(TimeSpan Start, string Title)> chapters, bool moovAtHead, int mdatBytes = 4096)
    {
        var ftyp = Box("ftyp", [.. "M4A "u8, 0, 0, 0, 0, .. "isom"u8]);
        var mdat = Box("mdat", new byte[mdatBytes]);
        var chpl = new MemoryStream();
        chpl.Write([1, 0, 0, 0, 0, 0, 0, 0]);
        chpl.WriteByte((byte)chapters.Count);
        var time = new byte[8];
        foreach (var (start, title) in chapters)
        {
            BinaryPrimitives.WriteInt64BigEndian(time, start.Ticks);
            chpl.Write(time);
            var text = Encoding.UTF8.GetBytes(title);
            chpl.WriteByte((byte)text.Length);
            chpl.Write(text);
        }

        var moov = Box("moov", [.. Box("mvhd", new byte[100]), .. Box("udta", Box("chpl", chpl.ToArray()))]);
        return moovAtHead ? [.. ftyp, .. moov, .. mdat] : [.. ftyp, .. mdat, .. moov];
    }

    public static byte[] Box(string type, byte[] body)
    {
        var box = new byte[8 + body.Length];
        BinaryPrimitives.WriteUInt32BigEndian(box, (uint)box.Length);
        Encoding.ASCII.GetBytes(type).CopyTo(box, 4);
        body.CopyTo(box, 8);
        return box;
    }

    public static byte[] Ogg(string codec = "OpusHead")
    {
        var page = new byte[27 + 1 + 19];
        "OggS"u8.CopyTo(page);
        page[26] = 1;
        page[27] = 19;
        Encoding.Latin1.GetBytes(codec).CopyTo(page, 28);
        return [.. page, .. new byte[256]];
    }

    public static byte[] Flac() => [.. "fLaC"u8, .. new byte[256]];

    /// <summary>Two ADTS frames of 64 bytes each.</summary>
    public static byte[] Adts()
    {
        var frame = new byte[64];
        frame[0] = 0xFF;
        frame[1] = 0xF1;
        frame[3] = 0x00;
        frame[4] = (byte)(64 >> 3);
        frame[5] = (byte)((64 & 0x07) << 5);
        return [.. frame, .. frame];
    }

    private static void WriteFrame(Stream target, string id, byte[] data)
    {
        target.Write(Encoding.ASCII.GetBytes(id));
        var size = new byte[4];
        WriteSyncSafe(size, data.Length);
        target.Write(size);
        target.Write([0, 0]);
        target.Write(data);
    }

    private static void WriteSyncSafe(Span<byte> target, int value)
    {
        target[0] = (byte)((value >> 21) & 0x7F);
        target[1] = (byte)((value >> 14) & 0x7F);
        target[2] = (byte)((value >> 7) & 0x7F);
        target[3] = (byte)(value & 0x7F);
    }
}
