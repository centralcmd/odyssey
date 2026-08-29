using System.IO.Compression;
using System.Text;

namespace Odyssey.TestData.Generators;

/// <summary>
/// Builds tiny but standards-valid truecolour PNGs so seeded photo blobs actually render in the browser.
/// <see cref="GradientPng"/> reproduces the design system's deterministic "scene" gradients (Photos web UI
/// kit · <c>plPhotoBg</c>) — a diagonal blend across a scene palette with two soft radial highlights — so
/// the seeded library grid looks like the design, not flat colour swatches. Deterministic for given inputs.
/// </summary>
public static class DemoImages
{
    /// <summary>Square edge (px) of generated demo thumbnails; also recorded as the seeded photo dimensions.</summary>
    public const int PhotoSize = 224;

    // The design system's scene palette (Photos kit · PL_SCENES): eight {shadow, mid, highlight} triples.
    private static readonly (byte R, byte G, byte B)[][] Scenes =
    [
        [(0xF7, 0xC8, 0x73), (0xEC, 0x6F, 0x3D), (0x7C, 0x3F, 0x8C)],
        [(0x17, 0x3A, 0x5E), (0x2C, 0x7D, 0xA0), (0x9F, 0xE0, 0xDF)],
        [(0x22, 0x33, 0x1E), (0x41, 0x72, 0x2F), (0xA7, 0xC9, 0x57)],
        [(0x14, 0x13, 0x1F), (0x2C, 0x24, 0x56), (0xC4, 0x6F, 0xC0)],
        [(0xEC, 0xD9, 0xA6), (0xCB, 0x8A, 0x3B), (0x9A, 0x5A, 0x2C)],
        [(0xE2, 0xE9, 0xF0), (0xA8, 0xBC, 0xD0), (0x68, 0x80, 0x9A)],
        [(0x3A, 0x2C, 0x27), (0x7C, 0x56, 0x38), (0xDB, 0xA2, 0x68)],
        [(0x0F, 0x34, 0x3B), (0x2C, 0x6A, 0x60), (0xE5, 0xB4, 0x63)],
    ];

    /// <summary>
    /// Renders a square scene gradient for <paramref name="seed"/>, matching the design system's
    /// <c>plPhotoBg(seed)</c>: the scene, gradient angle, and highlight positions are all derived from the
    /// seed, so consecutive seeds produce a varied but reproducible set of "photos".
    /// </summary>
    public static byte[] GradientPng(int size, int seed)
    {
        var s = Math.Abs(seed);
        var scene = Scenes[s % Scenes.Length];
        var (a, b, c) = (scene[0], scene[1], scene[2]);

        var angle = (90 + s % 7 * 18) * Math.PI / 180.0;
        var dirX = Math.Sin(angle);
        var dirY = -Math.Cos(angle);
        var lineLength = Math.Abs(size * dirX) + Math.Abs(size * dirY);

        var px = (20 + s % 5 * 15) / 100.0 * size;
        var py = (15 + (s >> 2) % 5 * 14) / 100.0 * size;

        var raw = new byte[size * (1 + size * 3)];
        var cursor = 0;
        for (var y = 0; y < size; y++)
        {
            raw[cursor++] = 0; // no per-scanline filter
            for (var x = 0; x < size; x++)
            {
                // Base linear gradient: shadow → mid (0–52%) → highlight (52–100%) along the seed's angle.
                var t = Math.Clamp(((x - size / 2.0) * dirX + (y - size / 2.0) * dirY) / lineLength + 0.5, 0, 1);
                var colour = t < 0.52 ? Lerp(a, b, t / 0.52) : Lerp(b, c, (t - 0.52) / 0.48);

                // Two soft radial highlights layered over the base (as the CSS radial-gradients do).
                var shadowFall = EllipseDistance(x, y, size - px, size - py, 1.40 * size, 1.20 * size);
                colour = Over(colour, a, 1 - shadowFall / 0.60);

                var highlightFall = EllipseDistance(x, y, px, py, 1.20 * size, 0.90 * size);
                colour = Over(colour, c, 1 - highlightFall / 0.55);

                raw[cursor++] = ToByte(colour.R);
                raw[cursor++] = ToByte(colour.G);
                raw[cursor++] = ToByte(colour.B);
            }
        }

        return Encode(size, size, raw);
    }

    private static (double R, double G, double B) Lerp((byte R, byte G, byte B) from, (byte R, byte G, byte B) to, double t) =>
        (from.R + (to.R - from.R) * t, from.G + (to.G - from.G) * t, from.B + (to.B - from.B) * t);

    private static (double R, double G, double B) Over((double R, double G, double B) baseColour, (byte R, byte G, byte B) top, double alpha)
    {
        alpha = Math.Clamp(alpha, 0, 1);
        return (baseColour.R + (top.R - baseColour.R) * alpha,
                baseColour.G + (top.G - baseColour.G) * alpha,
                baseColour.B + (top.B - baseColour.B) * alpha);
    }

    private static double EllipseDistance(double x, double y, double cx, double cy, double rx, double ry)
    {
        var dx = (x - cx) / rx;
        var dy = (y - cy) / ry;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static byte ToByte(double value) => (byte)Math.Clamp(Math.Round(value), 0, 255);

    private static byte[] Encode(int width, int height, byte[] raw)
    {
        using var output = new MemoryStream();
        output.Write([137, 80, 78, 71, 13, 10, 26, 10]); // PNG signature

        var header = new byte[13];
        WriteBigEndian(header, 0, width);
        WriteBigEndian(header, 4, height);
        header[8] = 8;  // bit depth
        header[9] = 2;  // colour type: truecolour RGB
        header[10] = 0; // compression
        header[11] = 0; // filter
        header[12] = 0; // interlace
        WriteChunk(output, "IHDR", header);

        using var deflated = new MemoryStream();
        using (var zlib = new ZLibStream(deflated, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(raw);
        }
        WriteChunk(output, "IDAT", deflated.ToArray());
        WriteChunk(output, "IEND", []);

        return output.ToArray();
    }

    private static void WriteChunk(Stream stream, string type, byte[] data)
    {
        Span<byte> length = stackalloc byte[4];
        WriteBigEndian(length, data.Length);
        stream.Write(length);

        var typeBytes = Encoding.ASCII.GetBytes(type);
        stream.Write(typeBytes);
        stream.Write(data);

        var crc = Crc32(typeBytes, data);
        Span<byte> crcBytes = stackalloc byte[4];
        crcBytes[0] = (byte)((crc >> 24) & 0xFF);
        crcBytes[1] = (byte)((crc >> 16) & 0xFF);
        crcBytes[2] = (byte)((crc >> 8) & 0xFF);
        crcBytes[3] = (byte)(crc & 0xFF);
        stream.Write(crcBytes);
    }

    private static void WriteBigEndian(Span<byte> buffer, int value)
    {
        buffer[0] = (byte)((value >> 24) & 0xFF);
        buffer[1] = (byte)((value >> 16) & 0xFF);
        buffer[2] = (byte)((value >> 8) & 0xFF);
        buffer[3] = (byte)(value & 0xFF);
    }

    private static void WriteBigEndian(byte[] buffer, int offset, int value) =>
        WriteBigEndian(buffer.AsSpan(offset, 4), value);

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }

    private static uint Crc32(byte[] first, byte[] second)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var value in first)
            crc = CrcTable[(crc ^ value) & 0xFF] ^ (crc >> 8);
        foreach (var value in second)
            crc = CrcTable[(crc ^ value) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFFu;
    }
}
