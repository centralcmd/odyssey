namespace Odyssey.Core.Imaging;

/// <summary>Why a container walk produced no usable output.</summary>
public enum ImageWalkOutcome
{
    /// <summary>The container was walked end to end and rebuilt.</summary>
    Ok,

    /// <summary>The bytes are not a well-formed container of the declared type — truncated, corrupt,
    /// or carrying a structure the walk cannot account for. For an identity image this is a defect, not a
    /// degraded read, so it is rejected rather than stored.</summary>
    Undecodable,

    /// <summary>An animated container (WebP <c>ANIM</c>/<c>ANMF</c>, PNG <c>acTL</c>). Rejected rather
    /// than silently de-animated — flattening is a different outcome than the user uploaded.</summary>
    Animated,
}

/// <summary>
/// What one walk of a container produced (issue #86 §5.3). The walk is the <b>only</b> parser on the
/// untrusted-input path: the stripped bytes, the dimensions and the animation determination all fall
/// out of a single linear pass that has to happen anyway to decide what to keep.
/// </summary>
/// <param name="Outcome">Whether the walk succeeded, and if not, why.</param>
/// <param name="Bytes">The rebuilt container, metadata removed and any trailer discarded.</param>
/// <param name="Width">Pixel width read from the container's own header.</param>
/// <param name="Height">Pixel height read from the container's own header.</param>
/// <param name="HasMetadata">
/// Whether the <i>input</i> carried an EXIF, IPTC, XMP or ICC payload. On a walk of the <i>output</i>
/// this must be false — that is what makes the metadata guarantee a property of the stored artifact
/// rather than of the code that produced it.
/// </param>
/// <param name="ConsumedLength">
/// How many input bytes the container itself occupied. A walk of the output must consume all of it:
/// anything past the terminator is a trailer (a ZIP polyglot, a motion-photo MP4 payload).
/// </param>
public sealed record ImageWalkResult(
    ImageWalkOutcome Outcome,
    byte[] Bytes,
    int Width,
    int Height,
    bool HasMetadata,
    int ConsumedLength)
{
    public static ImageWalkResult Failed(ImageWalkOutcome outcome) =>
        new(outcome, [], 0, 0, HasMetadata: false, ConsumedLength: 0);

    public bool IsOk => Outcome == ImageWalkOutcome.Ok;
}

/// <summary>
/// Dependency-free removal of metadata from a PNG / JPEG / WebP container <b>without touching pixel
/// data</b>, by <b>allow-list</b> — a deny-list cannot enumerate what it has not heard of (issue #86
/// §5.3).
///
/// <para>
/// This ships no raster-graphics dependency on purpose (<c>Directory.Packages.props</c> records the
/// choice of <c>MetadataExtractor</c> over ImageSharp/Magick/SkiaSharp), so nothing here decodes,
/// resizes or re-encodes: the stored bytes are the uploaded bytes minus metadata segments. The two
/// accepted consequences are recorded in §11 — EXIF <c>Orientation</c> is lost (invisible on the normal
/// path, where the client's canvas re-encode has already applied it) and all ICC profiles are dropped,
/// so a wide-gamut source renders as untagged sRGB.
/// </para>
///
/// <para>
/// <b><c>MetadataExtractor</c> is deliberately absent from this path.</b> <c>ImageMetadataReader</c> is a
/// full EXIF/IPTC/ICC/XMP parse, so using it here would put a large parser on the untrusted-input path
/// to learn two integers — and a bounded prefix would fail closed on a legitimate photo whose frame
/// header sits behind large EXIF/MPF/ICC segments. It appears only in tests, where it is the
/// <i>independent</i> oracle that makes strip-then-verify meaningful: the runtime re-scan is a second
/// pass of this same code and on its own is only a self-check.
/// </para>
/// </summary>
public static class ImageContainerWalk
{
    /// <summary>Walks a container of the given (already magic-byte-verified) content type.</summary>
    public static ImageWalkResult Walk(ReadOnlySpan<byte> source, string contentType) => contentType switch
    {
        "image/jpeg" => WalkJpeg(source),
        "image/png" => WalkPng(source),
        "image/webp" => WalkWebp(source),
        _ => ImageWalkResult.Failed(ImageWalkOutcome.Undecodable),
    };

    // ── JPEG ──────────────────────────────────────────────────────────────────────────────────────
    //
    // Keep ONLY SOI, DQT, DHT, DRI, the frame headers and SOS. Every APPn is dropped, APP0 INCLUDED:
    // JFIF APP0 carries Xthumbnail/Ythumbnail and JFXX embeds its own thumbnail, so the one marker a
    // keep-list is tempted to retain is the one that can still smuggle a second image — and a JPEG
    // decodes correctly without it. COM is dropped.
    //
    // Progressive JPEG is CLEANED, not rejected: after each SOS the walk skips the entropy-coded
    // segment (honouring byte-stuffing, so FF 00 and RSTn are data, not markers) and resumes the marker
    // chain, dropping any APPn interleaved between scans exactly as it drops one before the first scan.

    private const byte Marker = 0xFF;
    private const byte Soi = 0xD8;
    private const byte Eoi = 0xD9;
    private const byte Sos = 0xDA;
    private const byte Dqt = 0xDB;
    private const byte Dht = 0xC4;
    private const byte Dri = 0xDD;

    /// <summary>
    /// The frame headers, <b>enumerated rather than a range</b>. SOF0–SOF15 is not contiguous: 0xC4 is
    /// DHT, 0xC8 is JPG and 0xCC is DAC, all of which sit inside 0xC0–0xCF.
    /// </summary>
    private static bool IsFrameHeader(byte marker) =>
        marker is >= 0xC0 and <= 0xC3
            or >= 0xC5 and <= 0xC7
            or >= 0xC9 and <= 0xCB
            or >= 0xCD and <= 0xCF;

    private static bool IsKeptJpegMarker(byte marker) =>
        marker is Dqt or Dht or Dri or Sos || IsFrameHeader(marker);

    /// <summary>APPn (0xE0–0xEF) and COM (0xFE) are where EXIF, IPTC, XMP, ICC, MPF and JFIF thumbnails live.</summary>
    private static bool IsMetadataJpegMarker(byte marker) => marker is >= 0xE0 and <= 0xEF or 0xFE;

    private static ImageWalkResult WalkJpeg(ReadOnlySpan<byte> source)
    {
        if (source.Length < 4 || source[0] != Marker || source[1] != Soi)
        {
            return ImageWalkResult.Failed(ImageWalkOutcome.Undecodable);
        }

        var output = new System.IO.MemoryStream(source.Length);
        output.WriteByte(Marker);
        output.WriteByte(Soi);

        var hasMetadata = false;
        var width = 0;
        var height = 0;
        var position = 2;

        while (true)
        {
            // Marker chain. A fill byte (0xFF) may legally repeat before the marker code itself.
            if (position + 1 >= source.Length)
            {
                return ImageWalkResult.Failed(ImageWalkOutcome.Undecodable);
            }

            if (source[position] != Marker)
            {
                return ImageWalkResult.Failed(ImageWalkOutcome.Undecodable);
            }

            position++;
            while (position < source.Length && source[position] == Marker)
            {
                position++;
            }

            if (position >= source.Length)
            {
                return ImageWalkResult.Failed(ImageWalkOutcome.Undecodable);
            }

            var code = source[position];
            position++;

            if (code == Eoi)
            {
                // Truncate here: anything after EOI is a trailer, not part of the image.
                output.WriteByte(Marker);
                output.WriteByte(Eoi);
                return new ImageWalkResult(
                    ImageWalkOutcome.Ok, output.ToArray(), width, height, hasMetadata, position);
            }

            // Standalone markers carry no length payload. TEM (0x01) and RSTn (0xD0–0xD7) are the only
            // ones that can appear in the chain here; both are dropped by the allow-list.
            if (code is 0x01 or >= 0xD0 and <= 0xD7)
            {
                continue;
            }

            if (position + 1 >= source.Length)
            {
                return ImageWalkResult.Failed(ImageWalkOutcome.Undecodable);
            }

            var segmentLength = (source[position] << 8) | source[position + 1];
            if (segmentLength < 2 || position + segmentLength > source.Length)
            {
                return ImageWalkResult.Failed(ImageWalkOutcome.Undecodable);
            }

            var segment = source.Slice(position, segmentLength);

            if (IsMetadataJpegMarker(code))
            {
                hasMetadata = true;
            }

            if (IsFrameHeader(code))
            {
                // length(2) precision(1) height(2) width(2)
                if (segment.Length < 7)
                {
                    return ImageWalkResult.Failed(ImageWalkOutcome.Undecodable);
                }

                height = (segment[3] << 8) | segment[4];
                width = (segment[5] << 8) | segment[6];
            }

            if (IsKeptJpegMarker(code))
            {
                output.WriteByte(Marker);
                output.WriteByte(code);
                output.Write(segment);
            }

            position += segmentLength;

            if (code != Sos)
            {
                continue;
            }

            // Entropy-coded data follows the scan header. Copy it verbatim only if the scan header was
            // kept, then resume the marker chain at the next real marker. Byte-stuffing means an 0xFF
            // inside the data is followed by 0x00, and RSTn are restart markers that belong to the data.
            var scanStart = position;
            while (position < source.Length)
            {
                if (source[position] != Marker)
                {
                    position++;
                    continue;
                }

                var next = position + 1 < source.Length ? source[position + 1] : (byte)0x00;
                if (next == 0x00 || next is >= 0xD0 and <= 0xD7 || next == Marker)
                {
                    position += next == Marker ? 1 : 2;
                    continue;
                }

                break;
            }

            if (position >= source.Length)
            {
                // An image whose last scan runs to the end of the buffer with no EOI is truncated.
                return ImageWalkResult.Failed(ImageWalkOutcome.Undecodable);
            }

            output.Write(source[scanStart..position]);
        }
    }

    // ── PNG ───────────────────────────────────────────────────────────────────────────────────────
    //
    // Keep only IHDR, PLTE, IDAT, IEND, tRNS, gAMA, cHRM and sRGB. iCCP is dropped along with the text
    // chunks: an ICC profile carries `desc` and `cprt` TEXT, and colour-profile fidelity is not worth a
    // text channel on an avatar. Chunk bytes (CRC included) are copied verbatim, so nothing is recomputed.

    private static ReadOnlySpan<byte> PngSignature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private static readonly HashSet<string> KeptPngChunks =
        ["IHDR", "PLTE", "IDAT", "IEND", "tRNS", "gAMA", "cHRM", "sRGB"];

    private static readonly HashSet<string> MetadataPngChunks =
        ["eXIf", "tEXt", "zTXt", "iTXt", "iCCP"];

    private static ImageWalkResult WalkPng(ReadOnlySpan<byte> source)
    {
        if (source.Length < 8 || !source[..8].SequenceEqual(PngSignature))
        {
            return ImageWalkResult.Failed(ImageWalkOutcome.Undecodable);
        }

        var output = new System.IO.MemoryStream(source.Length);
        output.Write(PngSignature);

        var hasMetadata = false;
        var width = 0;
        var height = 0;
        var sawHeader = false;
        var position = 8;

        while (position + 8 <= source.Length)
        {
            var dataLength = ReadUInt32BigEndian(source[position..]);
            // A chunk length above int.MaxValue cannot be indexed and is not a legitimate PNG.
            if (dataLength > int.MaxValue - 12)
            {
                return ImageWalkResult.Failed(ImageWalkOutcome.Undecodable);
            }

            var chunkLength = 12 + (int)dataLength; // length(4) + type(4) + data + crc(4)
            if (position + chunkLength > source.Length)
            {
                return ImageWalkResult.Failed(ImageWalkOutcome.Undecodable);
            }

            var type = System.Text.Encoding.ASCII.GetString(source.Slice(position + 4, 4));

            if (type == "acTL")
            {
                // Animated PNG. Rejected, never flattened to its first frame.
                return ImageWalkResult.Failed(ImageWalkOutcome.Animated);
            }

            if (MetadataPngChunks.Contains(type))
            {
                hasMetadata = true;
            }

            if (type == "IHDR")
            {
                if (dataLength < 8)
                {
                    return ImageWalkResult.Failed(ImageWalkOutcome.Undecodable);
                }

                width = (int)ReadUInt32BigEndian(source[(position + 8)..]);
                height = (int)ReadUInt32BigEndian(source[(position + 12)..]);
                sawHeader = true;
            }

            if (KeptPngChunks.Contains(type))
            {
                output.Write(source.Slice(position, chunkLength));
            }

            position += chunkLength;

            if (type != "IEND")
            {
                continue;
            }

            // Truncate at IEND — anything after it is a trailer.
            return sawHeader && width > 0 && height > 0
                ? new ImageWalkResult(ImageWalkOutcome.Ok, output.ToArray(), width, height, hasMetadata, position)
                : ImageWalkResult.Failed(ImageWalkOutcome.Undecodable);
        }

        // Ran out of bytes without an IEND.
        return ImageWalkResult.Failed(ImageWalkOutcome.Undecodable);
    }

    // ── WebP ──────────────────────────────────────────────────────────────────────────────────────
    //
    // Reject on ANIM/ANMF. Otherwise rebuild the RIFF container without EXIF, XMP and ICCP (symmetric
    // with PNG's iCCP), CLEAR the corresponding VP8X flag bits, and recompute the RIFF size.

    /// <summary>VP8X flag bits. Bit 0 is the most significant: <c>Rsv Rsv ICC Alpha EXIF XMP Anim Rsv</c>.</summary>
    private const byte Vp8xIccFlag = 0x20;
    private const byte Vp8xExifFlag = 0x08;
    private const byte Vp8xXmpFlag = 0x04;
    private const byte Vp8xAnimationFlag = 0x02;

    private static readonly HashSet<string> DroppedWebpChunks = ["EXIF", "XMP ", "ICCP"];

    private static ImageWalkResult WalkWebp(ReadOnlySpan<byte> source)
    {
        if (source.Length < 12
            || !source[..4].SequenceEqual("RIFF"u8)
            || !source.Slice(8, 4).SequenceEqual("WEBP"u8))
        {
            return ImageWalkResult.Failed(ImageWalkOutcome.Undecodable);
        }

        var declaredSize = ReadUInt32LittleEndian(source[4..]);
        // The RIFF size counts everything after the size field itself.
        var containerLength = declaredSize > int.MaxValue - 8 ? source.Length : 8 + (int)declaredSize;
        if (containerLength > source.Length)
        {
            return ImageWalkResult.Failed(ImageWalkOutcome.Undecodable);
        }

        var body = new System.IO.MemoryStream(containerLength);
        body.Write("WEBP"u8);

        var hasMetadata = false;
        var width = 0;
        var height = 0;
        var position = 12;

        while (position + 8 <= containerLength)
        {
            var fourCc = System.Text.Encoding.ASCII.GetString(source.Slice(position, 4));
            var dataLength = ReadUInt32LittleEndian(source[(position + 4)..]);
            if (dataLength > int.MaxValue - 9)
            {
                return ImageWalkResult.Failed(ImageWalkOutcome.Undecodable);
            }

            // Odd-sized chunk payloads are followed by a single pad byte.
            var padded = (int)dataLength + ((dataLength & 1) == 1 ? 1 : 0);
            if (position + 8 + padded > containerLength)
            {
                return ImageWalkResult.Failed(ImageWalkOutcome.Undecodable);
            }

            var data = source.Slice(position + 8, (int)dataLength);

            if (fourCc is "ANIM" or "ANMF")
            {
                return ImageWalkResult.Failed(ImageWalkOutcome.Animated);
            }

            if (DroppedWebpChunks.Contains(fourCc))
            {
                hasMetadata = true;
                position += 8 + padded;
                continue;
            }

            if (fourCc == "VP8X")
            {
                if (data.Length < 10)
                {
                    return ImageWalkResult.Failed(ImageWalkOutcome.Undecodable);
                }

                var flags = data[0];
                if ((flags & Vp8xAnimationFlag) != 0)
                {
                    return ImageWalkResult.Failed(ImageWalkOutcome.Animated);
                }

                if ((flags & (Vp8xIccFlag | Vp8xExifFlag | Vp8xXmpFlag)) != 0)
                {
                    hasMetadata = true;
                }

                width = ReadUInt24LittleEndian(data[4..]) + 1;
                height = ReadUInt24LittleEndian(data[7..]) + 1;

                // Rewrite the header with the three flag bits cleared, so the stored container does not
                // advertise payloads it no longer carries.
                var rewritten = data.ToArray();
                rewritten[0] = (byte)(flags & ~(Vp8xIccFlag | Vp8xExifFlag | Vp8xXmpFlag));
                WriteWebpChunk(body, fourCc, rewritten);
                position += 8 + padded;
                continue;
            }

            if (fourCc == "VP8 " && width == 0)
            {
                // Lossy bitstream: 3-byte frame tag, the 3-byte start code 9D 01 2A, then 14-bit
                // little-endian width and height.
                if (data.Length < 10 || data[3] != 0x9D || data[4] != 0x01 || data[5] != 0x2A)
                {
                    return ImageWalkResult.Failed(ImageWalkOutcome.Undecodable);
                }

                width = (data[6] | (data[7] << 8)) & 0x3FFF;
                height = (data[8] | (data[9] << 8)) & 0x3FFF;
            }
            else if (fourCc == "VP8L" && width == 0)
            {
                // Lossless bitstream: the 0x2F signature, then 14 bits of (width - 1) and 14 of (height - 1).
                if (data.Length < 5 || data[0] != 0x2F)
                {
                    return ImageWalkResult.Failed(ImageWalkOutcome.Undecodable);
                }

                var bits = (uint)(data[1] | (data[2] << 8) | (data[3] << 16) | (data[4] << 24));
                width = (int)(bits & 0x3FFF) + 1;
                height = (int)((bits >> 14) & 0x3FFF) + 1;
            }

            WriteWebpChunk(body, fourCc, data);
            position += 8 + padded;
        }

        if (width <= 0 || height <= 0)
        {
            return ImageWalkResult.Failed(ImageWalkOutcome.Undecodable);
        }

        var payload = body.ToArray();
        var output = new byte[8 + payload.Length];
        "RIFF"u8.CopyTo(output);
        WriteUInt32LittleEndian(output.AsSpan(4), (uint)payload.Length);
        payload.CopyTo(output, 8);

        return new ImageWalkResult(ImageWalkOutcome.Ok, output, width, height, hasMetadata, containerLength);
    }

    private static void WriteWebpChunk(System.IO.MemoryStream body, string fourCc, ReadOnlySpan<byte> data)
    {
        Span<byte> header = stackalloc byte[8];
        System.Text.Encoding.ASCII.GetBytes(fourCc, header);
        WriteUInt32LittleEndian(header[4..], (uint)data.Length);
        body.Write(header);
        body.Write(data);
        if ((data.Length & 1) == 1)
        {
            body.WriteByte(0x00);
        }
    }

    private static uint ReadUInt32BigEndian(ReadOnlySpan<byte> bytes) =>
        ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];

    private static uint ReadUInt32LittleEndian(ReadOnlySpan<byte> bytes) =>
        bytes[0] | ((uint)bytes[1] << 8) | ((uint)bytes[2] << 16) | ((uint)bytes[3] << 24);

    private static int ReadUInt24LittleEndian(ReadOnlySpan<byte> bytes) =>
        bytes[0] | (bytes[1] << 8) | (bytes[2] << 16);

    private static void WriteUInt32LittleEndian(Span<byte> target, uint value)
    {
        target[0] = (byte)value;
        target[1] = (byte)(value >> 8);
        target[2] = (byte)(value >> 16);
        target[3] = (byte)(value >> 24);
    }
}
