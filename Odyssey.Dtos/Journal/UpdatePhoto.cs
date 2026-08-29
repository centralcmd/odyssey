using System.ComponentModel.DataAnnotations;

namespace Odyssey.Dtos.Journal;

/// <summary>
/// Update request for a library photo: replaces the editable metadata fields and the tag/person/album
/// link sets, and sets the archived state (<see cref="Archived"/> = true archives, false restores).
/// The underlying <c>FileId</c> is immutable and extraction never re-runs on update — the user's edited
/// values are authoritative.
/// </summary>
public sealed record UpdatePhoto
{
    /// <summary>Optional new name for the backing file (Files store). Ignored when blank; applying a
    /// rename requires the caller to hold <c>files.update</c>. The photo's <c>FileId</c> is immutable.</summary>
    [StringLength(256)]
    public string? FileName { get; set; }

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

    /// <summary>Desired archived state: true soft-archives the photo, false restores it.</summary>
    public bool Archived { get; set; }

    /// <summary>Desired favourite state: true marks as favourite, false clears it.</summary>
    public bool Favourite { get; set; }

    [MaxLength(PhotoLimits.MaxLinksPerKind)]
    public Guid[] TagIds { get; set; } = [];

    [MaxLength(PhotoLimits.MaxLinksPerKind)]
    public Guid[] PersonContactIds { get; set; } = [];

    [MaxLength(PhotoLimits.MaxLinksPerKind)]
    public Guid[] AlbumIds { get; set; } = [];
}
