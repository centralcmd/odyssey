using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Odyssey.Context;

/// <summary>
/// A canonical library record wrapping one Files-store image (issue #321). <see cref="FileId"/> is a real
/// FK to <see cref="FileMetadata"/> with <c>ON DELETE CASCADE</c> and a unique index — one library record
/// per image, and deleting the file takes the record with it. Extracted metadata
/// (date/coords/dimensions/title/caption) lives only here; the original file is never modified.
/// </summary>
[Index(nameof(FileId), IsUnique = true)]
[Index(nameof(Archived))]
[Index(nameof(TakenAt))]
[Index(nameof(Favourited))]
public class Photo
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid PhotoId { get; set; }

    public Guid FileId { get; set; }

    [StringLength(200)]
    public string? Title { get; set; }

    [StringLength(2000)]
    public string? Caption { get; set; }

    /// <summary>Capture instant from EXIF <c>DateTimeOriginal</c>. Stored as the camera's local
    /// wall-clock with <see cref="DateTimeKind.Unspecified"/> — EXIF carries no timezone.</summary>
    public DateTime? TakenAt { get; set; }

    public double? CapturedLatitude { get; set; }

    public double? CapturedLongitude { get; set; }

    [StringLength(256)]
    public string? LocationName { get; set; }

    public int? PixelWidth { get; set; }

    public int? PixelHeight { get; set; }

    /// <summary>Soft-archive timestamp (UTC); null = active.</summary>
    public DateTime? Archived { get; set; }

    /// <summary>Favourited timestamp (UTC); null = not a favourite. Toggled from the library UI.</summary>
    public DateTime? Favourited { get; set; }

    public string? CreatedByUserId { get; set; }

    public string? UpdatedByUserId { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public ICollection<PhotoTagLink> Tags { get; set; } = new List<PhotoTagLink>();

    public ICollection<PhotoPerson> People { get; set; } = new List<PhotoPerson>();

    public ICollection<PhotoAlbumItem> Albums { get; set; } = new List<PhotoAlbumItem>();
}
