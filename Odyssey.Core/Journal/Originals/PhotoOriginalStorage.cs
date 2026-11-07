using System.Globalization;
using System.Net;
using Odyssey.Context;
using Odyssey.Core.FileStorage;

namespace Odyssey.Core.Journal.Originals;

/// <summary>
/// The photo-original feature on the #346 store (issue #428 §3.1, §3.5):
/// <c>photo-originals/&lt;year&gt;/&lt;bucket&gt;/&lt;stem&gt;.&lt;ext&gt;</c>, original names, released after the
/// deletion grace like every production feature.
/// </summary>
public static class PhotoOriginalStorage
{
    public const string FeatureKey = "PhotoOriginals";

    public static readonly StorageFeature Feature = StorageFeature.Create<PhotoOriginalLayout>(
        FeatureKey,
        "photo-originals",
        args => [args.Year.ToString("D4", CultureInfo.InvariantCulture), args.Bucket.ToString(CultureInfo.InvariantCulture)],
        StorageReleaseMode.Graced,
        StorageFeatureReference.To<Photo>(p => p.StoredObjectId));
}

/// <summary>The upload year (UTC, of <c>Photo.CreatedAt</c>) and the numbered bucket inside it.</summary>
public sealed record PhotoOriginalLayout(int Year, int Bucket);

/// <summary>The bucket capacity, as an options class so a test can make it small.</summary>
public sealed class PhotoOriginalOptions
{
    public int BucketCapacity { get; set; } = PhotoOriginalLimits.BucketCapacity;
}

/// <summary>The constants of issue #428. None is a setting.</summary>
public static class PhotoOriginalLimits
{
    /// <summary>The store's per-folder limit (§3.3 of #346), the same capacity thumbnails use.</summary>
    public const int BucketCapacity = 10_000;

    /// <summary>
    /// Photo uploads and the backfill stop this far above the store's own 1 GB floor (§7.9), so a full library
    /// cannot starve thumbnails, which keep the store's floor.
    /// </summary>
    public const long UploadReserveBytes = 4L * 1024 * 1024 * 1024;

    /// <summary>The bucket row's lock wait, bounded per statement (§3.5).</summary>
    public const int BucketLockWaitSeconds = 5;

    /// <summary>The image types a photo may be (§6) — the image subset of the upload allow-list.</summary>
    public static readonly IReadOnlySet<string> ContentTypes = Finance.FileLookup.ImageContentTypes;

    /// <summary>The one spelling stored: <c>image/jpg</c> is accepted on the wire and stored as <c>image/jpeg</c>.</summary>
    public static string Normalize(string contentType) =>
        string.Equals(contentType, "image/jpg", StringComparison.OrdinalIgnoreCase) ? "image/jpeg" : contentType.ToLowerInvariant();
}

/// <summary>An upload whose bytes equal an existing photo's (§3.2 step 3). The controller shapes the <c>409</c>.</summary>
public sealed class DuplicatePhotoException(Guid existingPhotoId, bool existingArchived)
    : DomainException("This photo is already in the library.")
{
    public Guid ExistingPhotoId { get; } = existingPhotoId;

    public bool ExistingArchived { get; } = existingArchived;

    public override int StatusCode => (int)HttpStatusCode.Conflict;

    public override string? Code => "photo-duplicate";
}

/// <summary>A file that is not an allowed image, or whose bytes disagree with its declared type. <c>415</c>.</summary>
public sealed class PhotoUnsupportedTypeException(string message) : DomainException(message)
{
    public override int StatusCode => (int)HttpStatusCode.UnsupportedMediaType;

    public override string? Code => "photo-unsupported-type";
}

/// <summary>An upload over the effective upload cap. <c>413</c>.</summary>
public sealed class PhotoTooLargeException(string message) : DomainException(message)
{
    public override int StatusCode => (int)HttpStatusCode.RequestEntityTooLarge;

    public override string? Code => "photo-too-large";
}
