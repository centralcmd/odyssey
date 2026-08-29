using MetadataExtractor;
using MetadataExtractor.Formats.Exif;
using MetadataExtractor.Formats.Gif;
using MetadataExtractor.Formats.Iptc;
using MetadataExtractor.Formats.Jpeg;
using MetadataExtractor.Formats.Png;
using MetadataExtractor.Formats.WebP;
using MetadataExtractor.Formats.Xmp;

namespace Odyssey.Core.Journal;

/// <summary>
/// EXIF/IPTC/XMP reader built on the pinned, pure-managed <c>MetadataExtractor</c> library. Best-effort:
/// every read is guarded so a truncated/corrupt/hostile buffer degrades to null fields, never an
/// exception (§5.4, §11).
/// </summary>
public sealed class PhotoMetadataExtractor : IPhotoMetadataExtractor
{
    public PhotoMetadata Extract(byte[] content)
    {
        try
        {
            using var stream = new MemoryStream(content, writable: false);
            var directories = ImageMetadataReader.ReadMetadata(stream);

            var (width, height) = ReadOrientedDimensions(directories);

            return new PhotoMetadata
            {
                Title = ReadTitle(directories),
                Caption = ReadCaption(directories),
                TakenAt = ReadTakenAt(directories),
                Latitude = ReadGeo(directories)?.Latitude,
                Longitude = ReadGeo(directories)?.Longitude,
                PixelWidth = width,
                PixelHeight = height,
                Keywords = ReadKeywords(directories),
            };
        }
        catch
        {
            // Any parse failure (unsupported/corrupt/truncated/hostile) → no metadata.
            return PhotoMetadata.Empty;
        }
    }

    private static DateTime? ReadTakenAt(IReadOnlyList<MetadataExtractor.Directory> directories)
    {
        var subIfd = directories.OfType<ExifSubIfdDirectory>().FirstOrDefault();
        if (subIfd is null)
        {
            return null;
        }

        // EXIF stores wall-clock with no timezone; keep it Unspecified (the API never claims UTC, §9).
        if (subIfd.TryGetDateTime(ExifDirectoryBase.TagDateTimeOriginal, out var original))
        {
            return DateTime.SpecifyKind(original, DateTimeKind.Unspecified);
        }

        if (subIfd.TryGetDateTime(ExifDirectoryBase.TagDateTimeDigitized, out var digitized))
        {
            return DateTime.SpecifyKind(digitized, DateTimeKind.Unspecified);
        }

        return null;
    }

    private static GeoLocation? ReadGeo(IReadOnlyList<MetadataExtractor.Directory> directories)
    {
        var geo = directories.OfType<GpsDirectory>().FirstOrDefault()?.GetGeoLocation();
        if (geo is not { } location)
        {
            return null;
        }

        // A 0,0 fix is the "no location" sentinel many cameras emit; treat it as absent.
        return location.Latitude == 0d && location.Longitude == 0d ? null : location;
    }

    // Raw stored dimensions from whichever format directory reports them, then transpose for an
    // orientation tag that rotates the image 90°/270° so the reported size is display-correct (§16.4).
    private static (int? Width, int? Height) ReadOrientedDimensions(IReadOnlyList<MetadataExtractor.Directory> directories)
    {
        int? width = null;
        int? height = null;

        foreach (var (dir, widthTag, heightTag) in DimensionSources(directories))
        {
            if (dir.TryGetInt32(widthTag, out var w) && dir.TryGetInt32(heightTag, out var h) && w > 0 && h > 0)
            {
                width = w;
                height = h;
                break;
            }
        }

        if (width is null || height is null)
        {
            return (width, height);
        }

        var orientation = directories.OfType<ExifIfd0Directory>().FirstOrDefault();
        if (orientation is not null
            && orientation.TryGetInt32(ExifDirectoryBase.TagOrientation, out var value)
            && value is >= 5 and <= 8)
        {
            (width, height) = (height, width);
        }

        return (width, height);
    }

    private static IEnumerable<(MetadataExtractor.Directory Dir, int WidthTag, int HeightTag)> DimensionSources(
        IReadOnlyList<MetadataExtractor.Directory> directories)
    {
        if (directories.OfType<JpegDirectory>().FirstOrDefault() is { } jpeg)
        {
            yield return (jpeg, JpegDirectory.TagImageWidth, JpegDirectory.TagImageHeight);
        }

        if (directories.OfType<PngDirectory>().FirstOrDefault() is { } png)
        {
            yield return (png, PngDirectory.TagImageWidth, PngDirectory.TagImageHeight);
        }

        if (directories.OfType<WebPDirectory>().FirstOrDefault() is { } webp)
        {
            yield return (webp, WebPDirectory.TagImageWidth, WebPDirectory.TagImageHeight);
        }

        if (directories.OfType<GifImageDirectory>().FirstOrDefault() is { } gif)
        {
            yield return (gif, GifImageDirectory.TagWidth, GifImageDirectory.TagHeight);
        }

        if (directories.OfType<ExifSubIfdDirectory>().FirstOrDefault() is { } subIfd)
        {
            yield return (subIfd, ExifDirectoryBase.TagExifImageWidth, ExifDirectoryBase.TagExifImageHeight);
        }
    }

    private static string? ReadTitle(IReadOnlyList<MetadataExtractor.Directory> directories)
    {
        var iptc = directories.OfType<IptcDirectory>().FirstOrDefault()?.GetDescription(IptcDirectory.TagObjectName);
        if (!string.IsNullOrWhiteSpace(iptc))
        {
            return iptc.Trim();
        }

        var xmp = ReadXmpProperty(directories, "dc:title[1]");
        if (!string.IsNullOrWhiteSpace(xmp))
        {
            return xmp.Trim();
        }

        var exif = directories.OfType<ExifIfd0Directory>().FirstOrDefault()?.GetDescription(ExifDirectoryBase.TagImageDescription);
        return string.IsNullOrWhiteSpace(exif) ? null : exif.Trim();
    }

    private static string? ReadCaption(IReadOnlyList<MetadataExtractor.Directory> directories)
    {
        var iptc = directories.OfType<IptcDirectory>().FirstOrDefault()?.GetDescription(IptcDirectory.TagCaption);
        if (!string.IsNullOrWhiteSpace(iptc))
        {
            return iptc.Trim();
        }

        var xmp = ReadXmpProperty(directories, "dc:description[1]");
        return string.IsNullOrWhiteSpace(xmp) ? null : xmp.Trim();
    }

    private static List<string> ReadKeywords(IReadOnlyList<MetadataExtractor.Directory> directories)
    {
        var keywords = new List<string>();

        var iptcKeywords = directories.OfType<IptcDirectory>().FirstOrDefault()?.GetStringArray(IptcDirectory.TagKeywords);
        if (iptcKeywords is not null)
        {
            keywords.AddRange(iptcKeywords);
        }

        // XMP dc:subject is a bag serialised as dc:subject[1], dc:subject[2], … in the flat property map.
        var xmp = directories.OfType<XmpDirectory>().FirstOrDefault()?.GetXmpProperties();
        if (xmp is not null)
        {
            keywords.AddRange(xmp
                .Where(kv => kv.Key.StartsWith("dc:subject[", StringComparison.Ordinal))
                .Select(kv => kv.Value));
        }

        return keywords;
    }

    private static string? ReadXmpProperty(IReadOnlyList<MetadataExtractor.Directory> directories, string key)
    {
        var xmp = directories.OfType<XmpDirectory>().FirstOrDefault()?.GetXmpProperties();
        return xmp is not null && xmp.TryGetValue(key, out var value) ? value : null;
    }
}
