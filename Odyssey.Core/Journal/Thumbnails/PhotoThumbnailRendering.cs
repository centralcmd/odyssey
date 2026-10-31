using Odyssey.Core.Imaging;

namespace Odyssey.Core.Journal.Thumbnails;

/// <summary>The four decodable formats, pinned from the stored content type before any native code runs (§3.5).</summary>
public enum ThumbnailSourceFormat
{
    Jpeg,
    Png,
    Gif,
    WebP,
}

/// <summary>
/// One render: the bytes ImageMagick may see (pre-stripped where the walk allows), the pinned format and the
/// orientation read from the full original.
/// </summary>
public sealed record ThumbnailRenderRequest(byte[] Bytes, ThumbnailSourceFormat Format, int Orientation);

public sealed record ThumbnailRendition(byte[] WebP, int Width, int Height);

/// <summary>Either both renditions, or a §9 failure code.</summary>
public sealed record ThumbnailRenderResult(ThumbnailRendition? Medium, ThumbnailRendition? Small, string? FailureCode)
{
    public static ThumbnailRenderResult Rendered(ThumbnailRendition medium, ThumbnailRendition small) => new(medium, small, null);

    public static ThumbnailRenderResult Failed(string code) => new(null, null, code);
}

/// <summary>
/// The decoder (§3.1 item 1). The interface lives here; the Magick.NET implementation lives in
/// <c>Odyssey.Worker/Thumbnails/</c> only, so native ImageMagick never loads in the API. Synchronous on purpose: the
/// job runs it on a dedicated background thread under a wall-clock watchdog, because native code cannot be cancelled.
/// </summary>
public interface IPhotoThumbnailRenderer
{
    ThumbnailRenderResult Render(ThumbnailRenderRequest request);
}

/// <summary>The outcome of the decoder's security self-test (§6): null when it passed, else the failing probe.</summary>
public sealed record ThumbnailSelfTestResult(string? FailedProbe)
{
    public static readonly ThumbnailSelfTestResult Passed = new((string?)null);

    public bool Ok => FailedProbe is null;
}

/// <summary>
/// The startup self-test behind an interface (§6 item 4), so tests fake a missing or over-denying policy without
/// touching the process-wide one. The job calls it once, on its first run, inside its catch-all.
/// </summary>
public interface IThumbnailDecoderSelfTest
{
    ThumbnailSelfTestResult Run();
}

/// <summary>
/// The pre-native size gate (§3.5 step 4, §6): the walk's header dimensions — and, for JPEG, whether it is
/// progressive — checked before any native code runs. The same bounds are applied again to the ping, for the
/// inputs the walk does not measure (GIF, animated PNG and WebP).
/// </summary>
public static class PhotoThumbnailLimits
{
    public const long BaselineJpegMaxPixels = 200L * 1000 * 1000;
    public const long ProgressiveJpegMaxPixels = 40L * 1000 * 1000;
    public const int JpegMaxSide = 30_000;
    public const long OtherMaxPixels = 60L * 1000 * 1000;
    public const int OtherMaxSide = 16_384;

    /// <summary>The ImageMagick memory limit (§6), which also bounds the pixel cache; disk 0 so it never spills.</summary>
    public const ulong MagickMemoryBytes = 320UL * 1024 * 1024;

    public static bool Fits(ThumbnailSourceFormat format, long width, long height, bool progressive)
    {
        if (width <= 0 || height <= 0)
        {
            return false;
        }

        var (maxPixels, maxSide) = format == ThumbnailSourceFormat.Jpeg
            ? (progressive ? ProgressiveJpegMaxPixels : BaselineJpegMaxPixels, JpegMaxSide)
            : (OtherMaxPixels, OtherMaxSide);
        return width <= maxSide && height <= maxSide && width * height <= maxPixels;
    }

    /// <summary>The stored content type normalised and pinned; null for anything outside the four (§6).</summary>
    public static ThumbnailSourceFormat? Pin(string? contentType) => NormaliseContentType(contentType) switch
    {
        "image/jpeg" => ThumbnailSourceFormat.Jpeg,
        "image/png" => ThumbnailSourceFormat.Png,
        "image/gif" => ThumbnailSourceFormat.Gif,
        "image/webp" => ThumbnailSourceFormat.WebP,
        _ => null,
    };

    /// <summary><c>image/jpg</c> is <c>image/jpeg</c> (§3.5 step 1): the walk matches only the exact strings.</summary>
    public static string? NormaliseContentType(string? contentType)
    {
        var lower = contentType?.Trim().ToLowerInvariant();
        return lower == "image/jpg" ? "image/jpeg" : lower;
    }
}

/// <summary>
/// The pre-strip step (§3.5 step 3), mapped exactly: <c>Ok</c> sends on the stripped bytes, <c>Undecodable</c> is
/// terminal and never falls through to ImageMagick, <c>Animated</c> sends the unstripped bytes for frame 0. GIF is
/// not covered by the walk and goes in unstripped.
/// </summary>
public static class PhotoThumbnailPreStrip
{
    public sealed record Prepared(byte[] Bytes, string? FailureCode);

    public static Prepared Prepare(byte[] original, ThumbnailSourceFormat format)
    {
        if (format == ThumbnailSourceFormat.Gif)
        {
            return new Prepared(original, null);
        }

        var contentType = format switch
        {
            ThumbnailSourceFormat.Jpeg => "image/jpeg",
            ThumbnailSourceFormat.Png => "image/png",
            _ => "image/webp",
        };
        var walk = ImageContainerWalk.Walk(original, contentType);
        return walk.Outcome switch
        {
            ImageWalkOutcome.Ok => PhotoThumbnailLimits.Fits(format, walk.Width, walk.Height, walk.IsProgressive)
                ? new Prepared(walk.Bytes, null)
                : new Prepared([], PhotoThumbnailFailure.TooLarge),
            ImageWalkOutcome.Animated => new Prepared(original, null),
            _ => new Prepared([], PhotoThumbnailFailure.Decode),
        };
    }
}
