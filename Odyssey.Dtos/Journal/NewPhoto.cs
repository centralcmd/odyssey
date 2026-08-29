using System.ComponentModel.DataAnnotations;

namespace Odyssey.Dtos.Journal;

/// <summary>
/// Create request for a library photo (issue #321). The photo wraps one existing Files-store image
/// referenced by <see cref="FileId"/>; metadata is extracted from the file on add and only fills the
/// fields the caller leaves null (caller values win). Links are accepted as scalar id arrays only
/// (§6 mass-assignment guard) — never nested entity objects.
/// </summary>
public sealed record NewPhoto
{
    [Required]
    public required Guid FileId { get; set; }

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
