using System.ComponentModel.DataAnnotations;

namespace Odyssey.Dtos.Journal;

/// <summary>Album detail read model: the ordered member photo ids and the optional cover photo id.</summary>
public sealed record ExistingPhotoAlbum
{
    public required Guid PhotoAlbumId { get; set; }

    [StringLength(PhotoLimits.MaxAlbumNameLength)]
    public required string Name { get; set; }

    [StringLength(PhotoLimits.MaxAlbumDescriptionLength)]
    public string? Description { get; set; }

    public Guid? CoverPhotoId { get; set; }

    public DateTime? Archived { get; set; }

    [StringLength(255)]
    public string? CreatedByUserId { get; set; }

    [StringLength(256)]
    public string? CreatedByName { get; set; }

    [StringLength(255)]
    public string? UpdatedByUserId { get; set; }

    [StringLength(256)]
    public string? UpdatedByName { get; set; }

    public required DateTime CreatedAt { get; set; }

    public required DateTime UpdatedAt { get; set; }

    /// <summary>Member photo ids in album order (by <c>Position</c>).</summary>
    public List<Guid> PhotoIds { get; set; } = [];
}
