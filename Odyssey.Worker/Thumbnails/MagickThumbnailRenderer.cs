using ImageMagick;
using Odyssey.Core.Journal.Thumbnails;

namespace Odyssey.Worker.Thumbnails;

/// <summary>
/// The Magick.NET renderer (issue #362 §3.5): format pinned, frame 0 only, the JPEG DCT-scaling hint, a ping against
/// the size limits, the post-read format assertion, then Medium first and Small derived from it, oriented, sRGB,
/// stripped, WebP at quality 80. Exceptions propagate: the job maps them to <c>decode</c> and logs the type only.
/// </summary>
public sealed class MagickThumbnailRenderer : IPhotoThumbnailRenderer
{
    public ThumbnailRenderResult Render(ThumbnailRenderRequest request)
    {
        MagickRuntime.EnsureConfigured();
        var pinned = Pin(request.Format);
        var settings = new MagickReadSettings { Format = pinned, FrameIndex = 0, FrameCount = 1 };
        if (request.Format == ThumbnailSourceFormat.Jpeg)
        {
            // Mandatory: libjpeg DCT-scales while decoding, so a large baseline JPEG never materialises at full size.
            settings.SetDefine(MagickFormat.Jpeg, "size", "800x800");
        }

        using (var ping = new MagickImage())
        {
            ping.Ping(request.Bytes, settings);
            // The walk measured JPEG, still PNG and still WebP already; this covers GIF and animated input, and is a
            // second check for the rest. A JPEG pings at its DCT-scaled size, which only ever passes.
            if (!PhotoThumbnailLimits.Fits(request.Format, ping.Width, ping.Height, progressive: false))
            {
                return ThumbnailRenderResult.Failed(PhotoThumbnailFailure.TooLarge);
            }
        }

        using var image = new MagickImage(request.Bytes, settings);
        if (image.Format != pinned)
        {
            // An alarm, raised after the coder has parsed the bytes: the gates are the pin, the policy and the walk.
            return ThumbnailRenderResult.Failed(PhotoThumbnailFailure.Format);
        }

        // Resizing first gives the same output — the long-side fit does not depend on orientation — and the
        // full-size pixel cache is never copied.
        image.Thumbnail(new MagickGeometry(PhotoThumbnailRecipe.MediumLongSide, PhotoThumbnailRecipe.MediumLongSide) { Greater = true });
        Orient(image, request.Orientation);
        image.ColorSpace = ColorSpace.sRGB;
        var medium = Encode(image);

        image.Thumbnail(new MagickGeometry(PhotoThumbnailRecipe.SmallLongSide, PhotoThumbnailRecipe.SmallLongSide) { Greater = true });
        var small = Encode(image);
        return ThumbnailRenderResult.Rendered(medium, small);
    }

    private static ThumbnailRendition Encode(MagickImage image)
    {
        image.Strip();
        image.Quality = PhotoThumbnailRecipe.WebPQuality;
        image.Settings.SetDefine(MagickFormat.WebP, "method", PhotoThumbnailRecipe.WebPMethod.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return new ThumbnailRendition(image.ToByteArray(MagickFormat.WebP), (int)image.Width, (int)image.Height);
    }

    /// <summary>EXIF orientation 1–8 as explicit rotate and flip calls, never ImageMagick's own auto-orient.</summary>
    internal static void Orient(MagickImage image, int orientation)
    {
        switch (orientation)
        {
            case 2:
                image.Flop();
                break;
            case 3:
                image.Rotate(180);
                break;
            case 4:
                image.Flip();
                break;
            case 5:
                image.Transpose();
                break;
            case 6:
                image.Rotate(90);
                break;
            case 7:
                image.Transverse();
                break;
            case 8:
                image.Rotate(270);
                break;
        }

        image.Orientation = Orientation.TopLeft;
    }

    internal static MagickFormat Pin(ThumbnailSourceFormat format) => format switch
    {
        ThumbnailSourceFormat.Jpeg => MagickFormat.Jpeg,
        ThumbnailSourceFormat.Png => MagickFormat.Png,
        ThumbnailSourceFormat.Gif => MagickFormat.Gif,
        _ => MagickFormat.WebP,
    };
}
