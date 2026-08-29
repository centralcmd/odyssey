namespace Odyssey.Core.Journal;

/// <summary>
/// The best-effort result of reading an image's embedded metadata (EXIF + IPTC/XMP). Every field is
/// optional; a missing/unreadable value stays null and <see cref="Keywords"/> stays empty. Never an error.
/// </summary>
public sealed record PhotoMetadata
{
    /// <summary>Embedded title (IPTC Object Name / XMP dc:title / EXIF ImageDescription).</summary>
    public string? Title { get; init; }

    /// <summary>Embedded caption (IPTC Caption-Abstract / XMP dc:description).</summary>
    public string? Caption { get; init; }

    /// <summary>Capture instant from EXIF DateTimeOriginal (Unspecified kind — EXIF has no timezone).</summary>
    public DateTime? TakenAt { get; init; }

    public double? Latitude { get; init; }

    public double? Longitude { get; init; }

    /// <summary>Orientation-corrected display width.</summary>
    public int? PixelWidth { get; init; }

    /// <summary>Orientation-corrected display height.</summary>
    public int? PixelHeight { get; init; }

    /// <summary>Embedded keyword/tag names (IPTC Keywords / XMP dc:subject).</summary>
    public IReadOnlyList<string> Keywords { get; init; } = [];

    public static readonly PhotoMetadata Empty = new();
}
