using System.Text;
using Odyssey.Core.Journal;
using Xunit;

namespace Odyssey.Core.Tests;

/// <summary>
/// Direct unit tests for <see cref="PhotoMetadataExtractor"/>. Every case feeds a real, minimal image
/// buffer built in-code (PNG with an IHDR + optional XMP iTXt chunk; a hand-assembled little-endian
/// TIFF/EXIF for the EXIF-only branches) so the actual parse paths run — not just the catch→Empty
/// fallback the rest of the suite exercises.
/// </summary>
public class PhotoMetadataExtractorTests
{
    private static readonly PhotoMetadataExtractor Extractor = new();

    // ── Degrade-to-empty (the only path the wider suite covered) ──

    [Theory]
    [InlineData(new byte[0])]
    [InlineData(new byte[] { 1, 2, 3, 4 })]
    public void Extract_UnreadableBuffer_ReturnsEmpty(byte[] content)
    {
        var md = Extractor.Extract(content);

        Assert.Null(md.Title);
        Assert.Null(md.Caption);
        Assert.Null(md.TakenAt);
        Assert.Null(md.Latitude);
        Assert.Null(md.Longitude);
        Assert.Null(md.PixelWidth);
        Assert.Null(md.PixelHeight);
        Assert.Empty(md.Keywords);
    }

    // ── Dimensions (PNG IHDR source; no EXIF ⇒ no orientation transpose) ──

    [Fact]
    public void Extract_Png_ReadsPixelDimensions()
    {
        var png = BuildPng(width: 640, height: 480);

        var md = Extractor.Extract(png);

        Assert.Equal(640, md.PixelWidth);
        Assert.Equal(480, md.PixelHeight);
    }

    // ── XMP title / caption / keywords ──

    [Fact]
    public void Extract_Xmp_ReadsTitleCaptionAndKeywords()
    {
        var xmp = BuildXmp(title: "Sunset over the bay", description: "A warm evening", keywords: ["beach", "sunset"]);
        var png = BuildPng(320, 200, xmp);

        var md = Extractor.Extract(png);

        Assert.Equal("Sunset over the bay", md.Title);
        Assert.Equal("A warm evening", md.Caption);
        Assert.Contains("beach", md.Keywords);
        Assert.Contains("sunset", md.Keywords);
    }

    [Fact]
    public void Extract_Xmp_DeduplicatesNothingButMergesKeywords()
    {
        var xmp = BuildXmp(title: null, description: null, keywords: ["one", "two", "three"]);
        var png = BuildPng(10, 10, xmp);

        var md = Extractor.Extract(png);

        Assert.Equal(3, md.Keywords.Count);
        Assert.Equal(["one", "two", "three"], md.Keywords);
    }

    // ── EXIF orientation transpose (§16.4) ──

    [Theory]
    [InlineData(1, 100, 60, 100, 60)]  // normal orientation → dimensions as-stored
    [InlineData(2, 100, 60, 100, 60)]  // mirror horizontal → not a 90° rotation, no swap
    [InlineData(3, 100, 60, 100, 60)]  // rotate 180° → no swap
    [InlineData(4, 100, 60, 100, 60)]  // mirror vertical → no swap
    [InlineData(5, 100, 60, 60, 100)]  // transpose → swapped
    [InlineData(6, 100, 60, 60, 100)]  // rotate 90° CW → width/height swapped
    [InlineData(7, 100, 60, 60, 100)]  // transverse → swapped
    [InlineData(8, 100, 60, 60, 100)]  // rotate 90° CCW → swapped
    public void Extract_Exif_TransposesDimensionsForRotatedOrientation(
        int orientation, int storedW, int storedH, int expectW, int expectH)
    {
        var tiff = BuildTiff(
            ifd0: [Short(TagOrientation, (ushort)orientation)],
            subIfd: [Short(TagExifImageWidth, (ushort)storedW), Short(TagExifImageHeight, (ushort)storedH)]);

        var md = Extractor.Extract(tiff);

        Assert.Equal(expectW, md.PixelWidth);
        Assert.Equal(expectH, md.PixelHeight);
    }

    // ── EXIF capture time: DateTimeOriginal, then Digitized fallback ──

    [Fact]
    public void Extract_Exif_ReadsDateTimeOriginal()
    {
        var tiff = BuildTiff(ifd0: [], subIfd: [Ascii(TagDateTimeOriginal, "2021:06:15 09:30:00")]);

        var md = Extractor.Extract(tiff);

        Assert.Equal(new DateTime(2021, 6, 15, 9, 30, 0), md.TakenAt);
        Assert.Equal(DateTimeKind.Unspecified, md.TakenAt!.Value.Kind);
    }

    [Fact]
    public void Extract_Exif_FallsBackToDateTimeDigitized()
    {
        var tiff = BuildTiff(ifd0: [], subIfd: [Ascii(TagDateTimeDigitized, "2019:01:02 03:04:05")]);

        var md = Extractor.Extract(tiff);

        Assert.Equal(new DateTime(2019, 1, 2, 3, 4, 5), md.TakenAt);
    }

    // ── GPS: real fix vs the 0,0 "no location" sentinel ──

    [Fact]
    public void Extract_Exif_ReadsGpsLocation()
    {
        var tiff = BuildTiff(ifd0: [], gps: GpsEntries("N", [(40, 1), (0, 1), (0, 1)], "E", [(74, 1), (0, 1), (0, 1)]));

        var md = Extractor.Extract(tiff);

        Assert.NotNull(md.Latitude);
        Assert.NotNull(md.Longitude);
        Assert.Equal(40d, md.Latitude!.Value, 3);
        Assert.Equal(74d, md.Longitude!.Value, 3);
    }

    [Fact]
    public void Extract_Exif_TreatsZeroZeroGpsAsAbsent()
    {
        var tiff = BuildTiff(ifd0: [], gps: GpsEntries("N", [(0, 1), (0, 1), (0, 1)], "E", [(0, 1), (0, 1), (0, 1)]));

        var md = Extractor.Extract(tiff);

        Assert.Null(md.Latitude);
        Assert.Null(md.Longitude);
    }

    // ── Title / caption source precedence: IPTC → XMP → EXIF ImageDescription ──

    [Fact]
    public void Extract_Title_PrefersIptcOverXmpAndExif()
    {
        var tiff = BuildTiff(ifd0:
        [
            Ascii(TagImageDescription, "EXIF Desc"),
            Undefined(TagXmp, Utf8(BuildXmp(title: "XMP Title", description: null, keywords: []))),
            Undefined(TagIptc, BuildIptc(objectName: "IPTC Title", caption: null, keywords: [])),
        ]);

        Assert.Equal("IPTC Title", Extractor.Extract(tiff).Title);
    }

    [Fact]
    public void Extract_Title_PrefersXmpOverExif_WhenNoIptc()
    {
        var tiff = BuildTiff(ifd0:
        [
            Ascii(TagImageDescription, "EXIF Desc"),
            Undefined(TagXmp, Utf8(BuildXmp(title: "XMP Title", description: null, keywords: []))),
        ]);

        Assert.Equal("XMP Title", Extractor.Extract(tiff).Title);
    }

    [Fact]
    public void Extract_Title_FallsBackToExifImageDescription()
    {
        var tiff = BuildTiff(ifd0: [Ascii(TagImageDescription, "EXIF Desc")]);

        Assert.Equal("EXIF Desc", Extractor.Extract(tiff).Title);
    }

    [Fact]
    public void Extract_Caption_PrefersIptcOverXmp()
    {
        var tiff = BuildTiff(ifd0:
        [
            Undefined(TagXmp, Utf8(BuildXmp(title: null, description: "XMP Caption", keywords: []))),
            Undefined(TagIptc, BuildIptc(objectName: null, caption: "IPTC Caption", keywords: [])),
        ]);

        Assert.Equal("IPTC Caption", Extractor.Extract(tiff).Caption);
    }

    [Fact]
    public void Extract_Keywords_MergeIptcAndXmp()
    {
        var tiff = BuildTiff(ifd0:
        [
            Undefined(TagXmp, Utf8(BuildXmp(title: null, description: null, keywords: ["xmpword"]))),
            Undefined(TagIptc, BuildIptc(objectName: null, caption: null, keywords: ["iptcword"])),
        ]);

        var keywords = Extractor.Extract(tiff).Keywords;
        Assert.Contains("iptcword", keywords);
        Assert.Contains("xmpword", keywords);
    }

    // ─────────────────────────── PNG builder ───────────────────────────

    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private static byte[] BuildPng(int width, int height, string? xmp = null)
    {
        using var ms = new MemoryStream();
        ms.Write(PngSignature);

        var ihdr = new byte[13];
        WriteBe32(ihdr, 0, (uint)width);
        WriteBe32(ihdr, 4, (uint)height);
        ihdr[8] = 8;   // bit depth
        ihdr[9] = 2;   // colour type: truecolour
        WriteChunk(ms, "IHDR", ihdr);

        if (xmp is not null)
        {
            using var data = new MemoryStream();
            data.Write(Encoding.Latin1.GetBytes("XML:com.adobe.xmp"));
            data.WriteByte(0); // keyword null terminator
            data.WriteByte(0); // compression flag (uncompressed)
            data.WriteByte(0); // compression method
            data.WriteByte(0); // language tag (empty) + null
            data.WriteByte(0); // translated keyword (empty) + null
            data.Write(Encoding.UTF8.GetBytes(xmp));
            WriteChunk(ms, "iTXt", data.ToArray());
        }

        WriteChunk(ms, "IEND", []);
        return ms.ToArray();
    }

    private static void WriteChunk(Stream s, string type, byte[] data)
    {
        var len = new byte[4];
        WriteBe32(len, 0, (uint)data.Length);
        s.Write(len);

        var typeBytes = Encoding.ASCII.GetBytes(type);
        s.Write(typeBytes);
        s.Write(data);

        var crcInput = new byte[typeBytes.Length + data.Length];
        typeBytes.CopyTo(crcInput, 0);
        data.CopyTo(crcInput, typeBytes.Length);
        var crc = new byte[4];
        WriteBe32(crc, 0, Crc32(crcInput));
        s.Write(crc);
    }

    private static void WriteBe32(byte[] buf, int offset, uint value)
    {
        buf[offset] = (byte)(value >> 24);
        buf[offset + 1] = (byte)(value >> 16);
        buf[offset + 2] = (byte)(value >> 8);
        buf[offset + 3] = (byte)value;
    }

    private static uint Crc32(byte[] data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (var b in data)
        {
            crc ^= b;
            for (var i = 0; i < 8; i++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1;
            }
        }

        return crc ^ 0xFFFFFFFF;
    }

    private static string BuildXmp(string? title, string? description, string[] keywords)
    {
        var sb = new StringBuilder();
        sb.Append("<?xpacket begin=\"﻿\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?>");
        sb.Append("<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">");
        sb.Append("<rdf:Description rdf:about=\"\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\">");
        if (title is not null)
        {
            sb.Append($"<dc:title><rdf:Alt><rdf:li xml:lang=\"x-default\">{title}</rdf:li></rdf:Alt></dc:title>");
        }

        if (description is not null)
        {
            sb.Append($"<dc:description><rdf:Alt><rdf:li xml:lang=\"x-default\">{description}</rdf:li></rdf:Alt></dc:description>");
        }

        if (keywords.Length > 0)
        {
            sb.Append("<dc:subject><rdf:Bag>");
            foreach (var k in keywords)
            {
                sb.Append($"<rdf:li>{k}</rdf:li>");
            }

            sb.Append("</rdf:Bag></dc:subject>");
        }

        sb.Append("</rdf:Description></rdf:RDF></x:xmpmeta><?xpacket end=\"w\"?>");
        return sb.ToString();
    }

    // ─────────────────────────── TIFF / EXIF builder (little-endian) ───────────────────────────

    private const ushort TagImageDescription = 0x010E;
    private const ushort TagOrientation = 0x0112;
    private const ushort TagXmp = 0x02BC;            // XMP packet (read as XmpDirectory)
    private const ushort TagIptc = 0x83BB;           // IPTC-NAA IIM datastream (read as IptcDirectory)
    private const ushort TagDateTimeOriginal = 0x9003;
    private const ushort TagDateTimeDigitized = 0x9004;
    private const ushort TagExifImageWidth = 0xA002;
    private const ushort TagExifImageHeight = 0xA003;
    private const ushort TagExifSubIfdPointer = 0x8769;
    private const ushort TagGpsInfoPointer = 0x8825;

    private const ushort TypeAscii = 2;
    private const ushort TypeShort = 3;
    private const ushort TypeLong = 4;
    private const ushort TypeRational = 5;
    private const ushort TypeUndefined = 7;

    private static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);

    private static TiffEntry Undefined(ushort tag, byte[] data) => new(tag, TypeUndefined, (uint)data.Length, data);

    // A minimal IPTC IIM datastream: 0x1C marker, record#, dataset#, 2-byte big-endian length, data.
    private static byte[] BuildIptc(string? objectName, string? caption, string[] keywords)
    {
        var bytes = new List<byte>();
        void DataSet(byte record, byte dataset, string value)
        {
            var data = Encoding.ASCII.GetBytes(value);
            bytes.Add(0x1C);
            bytes.Add(record);
            bytes.Add(dataset);
            bytes.Add((byte)(data.Length >> 8));
            bytes.Add((byte)(data.Length & 0xFF));
            bytes.AddRange(data);
        }

        if (objectName is not null) DataSet(0x02, 0x05, objectName);       // 2:05 Object Name (title)
        if (caption is not null) DataSet(0x02, 0x78, caption);             // 2:120 Caption/Abstract
        foreach (var k in keywords) DataSet(0x02, 0x19, k);                // 2:25 Keywords (repeatable)
        return [.. bytes];
    }

    private sealed record TiffEntry(ushort Tag, ushort Type, uint Count, byte[] Value);

    private static TiffEntry Short(ushort tag, ushort value)
    {
        var v = new byte[2];
        v[0] = (byte)value;
        v[1] = (byte)(value >> 8);
        return new TiffEntry(tag, TypeShort, 1, v);
    }

    private static TiffEntry Ascii(ushort tag, string value)
    {
        var bytes = Encoding.ASCII.GetBytes(value + "\0");
        return new TiffEntry(tag, TypeAscii, (uint)bytes.Length, bytes);
    }

    private static TiffEntry Rationals(ushort tag, (uint Num, uint Den)[] values)
    {
        var v = new byte[8 * values.Length];
        for (var i = 0; i < values.Length; i++)
        {
            WriteLe32(v, i * 8, values[i].Num);
            WriteLe32(v, i * 8 + 4, values[i].Den);
        }

        return new TiffEntry(tag, TypeRational, (uint)values.Length, v);
    }

    private static List<TiffEntry> GpsEntries(string latRef, (uint, uint)[] lat, string lngRef, (uint, uint)[] lng) =>
    [
        Ascii(0x0001, latRef),
        Rationals(0x0002, lat),
        Ascii(0x0003, lngRef),
        Rationals(0x0004, lng),
    ];

    // Assemble a bare little-endian TIFF: header → IFD0 → optional Exif SubIFD → optional GPS IFD →
    // out-of-line data pool. Pointer entries (0x8769 / 0x8825) are appended to IFD0 and back-patched.
    private static byte[] BuildTiff(List<TiffEntry> ifd0, List<TiffEntry>? subIfd = null, List<TiffEntry>? gps = null)
    {
        var ifd0Entries = new List<TiffEntry>(ifd0);

        const int headerSize = 8;
        var n0 = ifd0.Count + (subIfd is not null ? 1 : 0) + (gps is not null ? 1 : 0);
        var ifd0Size = 2 + 12 * n0 + 4;
        var subSize = subIfd is not null ? 2 + 12 * subIfd.Count + 4 : 0;
        var gpsSize = gps is not null ? 2 + 12 * gps.Count + 4 : 0;

        var ifd0Off = headerSize;
        var subOff = ifd0Off + ifd0Size;
        var gpsOff = subOff + subSize;
        var dataOff = gpsOff + gpsSize;

        if (subIfd is not null)
        {
            ifd0Entries.Add(new TiffEntry(TagExifSubIfdPointer, TypeLong, 1, Le32((uint)subOff)));
        }

        if (gps is not null)
        {
            ifd0Entries.Add(new TiffEntry(TagGpsInfoPointer, TypeLong, 1, Le32((uint)gpsOff)));
        }

        var pool = new List<byte>();

        using var ms = new MemoryStream();
        ms.Write([0x49, 0x49]);          // "II" little-endian
        ms.Write([0x2A, 0x00]);          // magic 42
        ms.Write(Le32((uint)ifd0Off));   // offset to IFD0

        WriteIfd(ms, ifd0Entries, dataOff, pool);
        if (subIfd is not null)
        {
            WriteIfd(ms, subIfd, dataOff, pool);
        }

        if (gps is not null)
        {
            WriteIfd(ms, gps, dataOff, pool);
        }

        ms.Write(pool.ToArray());
        return ms.ToArray();
    }

    private static void WriteIfd(Stream s, List<TiffEntry> entries, int dataOff, List<byte> pool)
    {
        var count = new byte[2];
        count[0] = (byte)entries.Count;
        count[1] = (byte)(entries.Count >> 8);
        s.Write(count);

        foreach (var e in entries)
        {
            var tag = new byte[2];
            tag[0] = (byte)e.Tag;
            tag[1] = (byte)(e.Tag >> 8);
            s.Write(tag);

            var type = new byte[2];
            type[0] = (byte)e.Type;
            type[1] = (byte)(e.Type >> 8);
            s.Write(type);

            s.Write(Le32(e.Count));

            if (e.Value.Length <= 4)
            {
                var inline = new byte[4];
                e.Value.CopyTo(inline, 0);
                s.Write(inline);
            }
            else
            {
                s.Write(Le32((uint)(dataOff + pool.Count)));
                pool.AddRange(e.Value);
                if (pool.Count % 2 != 0)
                {
                    pool.Add(0); // keep the data pool word-aligned
                }
            }
        }

        s.Write(Le32(0)); // next-IFD offset
    }

    private static byte[] Le32(uint value)
    {
        var v = new byte[4];
        WriteLe32(v, 0, value);
        return v;
    }

    private static void WriteLe32(byte[] buf, int offset, uint value)
    {
        buf[offset] = (byte)value;
        buf[offset + 1] = (byte)(value >> 8);
        buf[offset + 2] = (byte)(value >> 16);
        buf[offset + 3] = (byte)(value >> 24);
    }
}
