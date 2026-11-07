using System.ComponentModel.DataAnnotations;

namespace Odyssey.Dtos.Journal;

/// <summary>
/// The form fields of a photo upload, <c>POST /api/photos</c> as <c>multipart/form-data</c> (issue #428 §5.1). The
/// image itself is the separate <c>file</c> part — this type is shared with the WASM client and cannot hold an
/// <c>IFormFile</c>. Metadata is extracted from the upload and only fills the fields the caller leaves null (caller
/// values win). Links are scalar id arrays only (§6 mass-assignment guard); there is deliberately no file id: a
/// photo is never made from an existing file.
/// </summary>
public sealed record NewPhotoUpload
{
    [StringLength(PhotoLimits.MaxTitleLength)]
    public string? Title { get; set; }

    [StringLength(PhotoLimits.MaxCaptionLength)]
    public string? Caption { get; set; }

    public DateTime? TakenAt { get; set; }

    [Range(-90d, 90d)]
    public double? CapturedLatitude { get; set; }

    [Range(-180d, 180d)]
    public double? CapturedLongitude { get; set; }

    [StringLength(PhotoLimits.MaxLocationNameLength)]
    public string? LocationName { get; set; }

    [Range(1, PhotoLimits.MaxPixelDimension)]
    public int? PixelWidth { get; set; }

    [Range(1, PhotoLimits.MaxPixelDimension)]
    public int? PixelHeight { get; set; }

    /// <summary>Optionally mark the new photo as a favourite.</summary>
    public bool Favourite { get; set; }

    [MaxLength(PhotoLimits.MaxLinksPerKind)]
    public Guid[] TagIds { get; set; } = [];

    [MaxLength(PhotoLimits.MaxLinksPerKind)]
    public Guid[] PersonContactIds { get; set; } = [];

    [MaxLength(PhotoLimits.MaxLinksPerKind)]
    public Guid[] AlbumIds { get; set; } = [];
}
