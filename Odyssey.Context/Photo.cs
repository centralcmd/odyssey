using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Odyssey.Context;

/// <summary>
/// A library photo (issue #321). Since issue #428 a photo <b>owns its original</b>: the bytes are a
/// <see cref="StoredObject"/> of the <c>PhotoOriginals</c> feature, named by <see cref="StoredObjectId"/>, and the
/// photo carries its own <see cref="FileName"/>, <see cref="ContentType"/>, <see cref="SizeBytes"/> and
/// <see cref="Sha256Hash"/>. A <b>legacy</b> photo, created before #428 and not yet moved by the backfill, still
/// wraps a Files-store file through <see cref="FileId"/> (CASCADE, unique). Exactly one of the two is set
/// (<c>CK_Photos_ExactlyOneSource</c>). Extracted metadata lives only here; the original is never modified.
/// </summary>
[Index(nameof(FileId), IsUnique = true)]
[Index(nameof(StoredObjectId), IsUnique = true)]
[Index(nameof(LegacyFileId), IsUnique = true)]
[Index(nameof(Sha256Hash), nameof(SizeBytes))]
// Every list filters Archived and defaults to ordering by TakenAt (PhotoService.ListAsync), so the
// composite serves both. It supersedes the standalone Archived index, which was a strict prefix of
// it and therefore pure write cost.
[Index(nameof(Archived), nameof(TakenAt))]
[Index(nameof(TakenAt))]
[Index(nameof(Favourited))]
public class Photo
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid PhotoId { get; set; }

    /// <summary>The legacy Files-store file this photo wraps; null once the photo owns its original.</summary>
    public Guid? FileId { get; set; }

    /// <summary>The original on the object store (RESTRICT, #346 consumer rule); null for a legacy photo.</summary>
    public Guid? StoredObjectId { get; set; }

    /// <summary>
    /// The photo's file name. Set on every disk-backed photo; on a legacy photo only once it is renamed. Read it
    /// through the effective-name rule (<c>Photo.FileName ?? FileMetadata.FileName</c>), never one side alone.
    /// </summary>
    [StringLength(256)]
    public string? FileName { get; set; }

    [StringLength(256)]
    public string? ContentType { get; set; }

    public long? SizeBytes { get; set; }

    /// <summary>Lowercase hex SHA-256 of the original; the strong ETag and the duplicate-check key.</summary>
    [StringLength(64)]
    public string? Sha256Hash { get; set; }

    /// <summary>
    /// The file id a backfilled photo used to wrap. No foreign key: the file may be gone. It lets an ICS export
    /// written before the move (<c>odyssey-photo:&lt;fileId&gt;</c>) still resolve on import.
    /// </summary>
    public Guid? LegacyFileId { get; set; }

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
