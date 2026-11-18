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

    /// <summary>
    /// The album's <see cref="TagIcons"/> key, or <c>null</c> for the album default (issue #404). A stored key
    /// the catalogue no longer has is projected as <c>null</c>, never echoed. Draw it with
    /// <see cref="PhotoAlbumIcons.Glyph(string?)"/>.
    /// </summary>
    public string? Icon { get; set; }

    /// <summary>
    /// The album's colour as normalised <c>#RRGGBB</c>, or <c>null</c> (issue #404). A stored value that is
    /// not a valid hex is projected as <c>null</c>, never echoed.
    /// </summary>
    public string? Color { get; set; }

    /// <summary>Both of the cover photo's thumbnails exist (issue #362 §4.4); false without a cover.</summary>
    public bool CoverThumbnailsReady { get; set; }

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
