using System.Buffers.Binary;

namespace Odyssey.Api.Map;

/// <summary>
/// The fixed 127-byte header of a PMTiles version 3 archive (issue #384), read for the start-up check and
/// nothing else: the API never parses tiles or directories. Layout per the PMTiles v3 specification.
/// </summary>
public sealed record PmtilesHeader
{
    public const int Length = 127;
    public const byte VectorTileType = 1;

    private static ReadOnlySpan<byte> Magic => "PMTiles"u8;

    public required byte Version { get; init; }
    public required ulong RootDirectoryOffset { get; init; }
    public required ulong RootDirectoryLength { get; init; }
    public required ulong MetadataOffset { get; init; }
    public required ulong MetadataLength { get; init; }
    public required ulong LeafDirectoriesOffset { get; init; }
    public required ulong LeafDirectoriesLength { get; init; }
    public required ulong TileDataOffset { get; init; }
    public required ulong TileDataLength { get; init; }
    public required byte InternalCompression { get; init; }
    public required byte TileCompression { get; init; }
    public required byte TileType { get; init; }
    public required byte MinZoom { get; init; }
    public required byte MaxZoom { get; init; }
    public required double MinLongitude { get; init; }
    public required double MinLatitude { get; init; }
    public required double MaxLongitude { get; init; }
    public required double MaxLatitude { get; init; }

    /// <summary>Whether <paramref name="header"/> starts with the PMTiles magic. Callers check this first.</summary>
    public static bool HasMagic(ReadOnlySpan<byte> header) => header.Length >= Magic.Length && header[..Magic.Length].SequenceEqual(Magic);

    public static PmtilesHeader Parse(ReadOnlySpan<byte> h)
    {
        if (h.Length < Length)
            throw new ArgumentException("A PMTiles header is 127 bytes.", nameof(h));

        static double E7(ReadOnlySpan<byte> b) => BinaryPrimitives.ReadInt32LittleEndian(b) / 10_000_000d;
        static ulong U64(ReadOnlySpan<byte> b) => BinaryPrimitives.ReadUInt64LittleEndian(b);

        return new PmtilesHeader
        {
            Version = h[7],
            RootDirectoryOffset = U64(h[8..]),
            RootDirectoryLength = U64(h[16..]),
            MetadataOffset = U64(h[24..]),
            MetadataLength = U64(h[32..]),
            LeafDirectoriesOffset = U64(h[40..]),
            LeafDirectoriesLength = U64(h[48..]),
            TileDataOffset = U64(h[56..]),
            TileDataLength = U64(h[64..]),
            InternalCompression = h[97],
            TileCompression = h[98],
            TileType = h[99],
            MinZoom = h[100],
            MaxZoom = h[101],
            MinLongitude = E7(h[102..]),
            MinLatitude = E7(h[106..]),
            MaxLongitude = E7(h[110..]),
            MaxLatitude = E7(h[114..]),
        };
    }

    /// <summary>
    /// Whether every declared section lies inside a file of <paramref name="fileLength"/> bytes. The sums are
    /// <c>checked</c>, so a corrupt or hostile <c>uint64</c> field fails rather than wrapping (#384 V6).
    /// </summary>
    public bool SectionsFit(ulong fileLength)
    {
        try
        {
            return Fits(RootDirectoryOffset, RootDirectoryLength) && Fits(MetadataOffset, MetadataLength)
                && Fits(LeafDirectoriesOffset, LeafDirectoriesLength) && Fits(TileDataOffset, TileDataLength);
        }
        catch (OverflowException)
        {
            return false;
        }

        bool Fits(ulong offset, ulong length) => checked(offset + length) <= fileLength;
    }

    /// <summary>None (1) or gzip (2): the only codecs the browser library's default decompressor handles.</summary>
    public static bool IsDecodable(byte compression) => compression is 1 or 2;
}
