using System.ComponentModel.DataAnnotations;

namespace Odyssey.Dtos.Journal;

/// <summary>
/// Update request for an album: rename/describe, replace the ordered membership (<see cref="PhotoIds"/>,
/// each position = its index), set the cover, and toggle the archived state. The cover is validated
/// against the post-replace membership (§7 album PUT evaluation order).
/// </summary>
public sealed record UpdatePhotoAlbum
{
    [Required]
    [StringLength(PhotoLimits.MaxAlbumNameLength)]
    public required string Name { get; set; }

    [StringLength(PhotoLimits.MaxAlbumDescriptionLength)]
    public string? Description { get; set; }

    [MaxLength(PhotoLimits.MaxAlbumMembers)]
    public Guid[] PhotoIds { get; set; } = [];

    public Guid? CoverPhotoId { get; set; }

    /// <summary>Desired archived state: true archives, false restores.</summary>
    public bool Archived { get; set; }
}
