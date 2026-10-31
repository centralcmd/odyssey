using System.Buffers.Binary;
using System.Text;

namespace Odyssey.Core.Tests.Thumbnails;

/// <summary>
/// Minimal containers the walk accepts, built byte by byte: the job tests use a fake renderer, so these only have to
/// be well-formed, not decodable. The real-decoder tests in <c>Odyssey.Worker.Tests</c> use real images.
/// </summary>
internal static class ThumbnailFixtures
{
    /// <summary>A JPEG with the given header size, an EXIF APP1 (orientation, and a fake GPS tag) unless omitted.</summary>
    public static byte[] Jpeg(int width, int height, bool progressive = false, int? orientation = null, int exifPadding = 0)
    {
        using var ms = new MemoryStream();
        ms.Write([0xFF, 0xD8]);
        if (orientation is { } value)
        {
            Segment(ms, 0xE1, ExifApp1(value, exifPadding));
        }

        Segment(ms, 0xDB, [0x00, .. Enumerable.Repeat((byte)1, 64)]);
        var frame = new byte[15];
        frame[0] = 8;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(1), (ushort)height);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(3), (ushort)width);
        frame[5] = 3;
        for (var c = 0; c < 3; c++)
        {
            frame[6 + (c * 3)] = (byte)(c + 1);
            frame[7 + (c * 3)] = 0x11;
        }

        Segment(ms, progressive ? (byte)0xC2 : (byte)0xC0, frame);
        Segment(ms, 0xDA, [0x03, 0x01, 0x00, 0x02, 0x11, 0x03, 0x11, 0x00, 0x3F, 0x00]);
        ms.Write([0x12, 0x34, 0x56, 0x78]);
        ms.Write([0xFF, 0xD9]);
        return ms.ToArray();
    }

    /// <summary>A still PNG with the given header size.</summary>
    public static byte[] Png(int width, int height)
    {
        using var ms = new MemoryStream();
        ms.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var ihdr = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(ihdr, (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4), (uint)height);
        ihdr[8] = 8;
        ihdr[9] = 2;
        Chunk(ms, "IHDR", ihdr);
        Chunk(ms, "IDAT", [0x78, 0x9C, 0x01, 0x00]);
        Chunk(ms, "IEND", []);
        return ms.ToArray();
    }

    /// <summary>GIF is not walked; any bytes do.</summary>
    public static byte[] Gif() => Encoding.ASCII.GetBytes("GIF89a....");

    private static void Segment(MemoryStream ms, byte marker, byte[] payload)
    {
        ms.WriteByte(0xFF);
        ms.WriteByte(marker);
        var length = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(length, (ushort)(payload.Length + 2));
        ms.Write(length);
        ms.Write(payload);
    }

    private static void Chunk(MemoryStream ms, string type, byte[] data)
    {
        var length = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, (uint)data.Length);
        ms.Write(length);
        var typeBytes = Encoding.ASCII.GetBytes(type);
        ms.Write(typeBytes);
        ms.Write(data);
        var crc = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crc, Crc([.. typeBytes, .. data]));
        ms.Write(crc);
    }

    private static uint Crc(byte[] bytes)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in bytes)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            }
        }

        return ~crc;
    }

    /// <summary>"Exif\0\0" + a little-endian TIFF with IFD0 holding Orientation (0x0112), and padding after it.</summary>
    private static byte[] ExifApp1(int orientation, int padding)
    {
        using var tiff = new MemoryStream();
        tiff.Write("II"u8);
        tiff.Write([0x2A, 0x00, 0x08, 0x00, 0x00, 0x00]);
        tiff.Write([0x01, 0x00]);
        tiff.Write([0x12, 0x01, 0x03, 0x00, 0x01, 0x00, 0x00, 0x00, (byte)orientation, 0x00, 0x00, 0x00]);
        tiff.Write([0x00, 0x00, 0x00, 0x00]);
        tiff.Write(new byte[padding]);
        return [.. "Exif\0\0"u8, .. tiff.ToArray()];
    }
}
