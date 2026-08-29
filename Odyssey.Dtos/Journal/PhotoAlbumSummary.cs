using System.ComponentModel.DataAnnotations;

namespace Odyssey.Dtos.Journal;

/// <summary>List-row projection for an album: scalar fields, cover photo id, and its member count.</summary>
public sealed record PhotoAlbumSummary
{
    public required Guid PhotoAlbumId { get; set; }

    [StringLength(PhotoLimits.MaxAlbumNameLength)]
    public required string Name { get; set; }

    [StringLength(PhotoLimits.MaxAlbumDescriptionLength)]
    public string? Description { get; set; }

    public Guid? CoverPhotoId { get; set; }

    /// <summary>The cover photo's backing file id, projected so a list row can render its thumbnail
    /// without a separate per-photo fetch. Null when there is no cover (or it resolved to nothing).</summary>
    public Guid? CoverFileId { get; set; }

    public DateTime? Archived { get; set; }

    [StringLength(255)]
    public string? CreatedByUserId { get; set; }

    [StringLength(256)]
    public string? CreatedByName { get; set; }

    public required int PhotoCount { get; set; }
}
