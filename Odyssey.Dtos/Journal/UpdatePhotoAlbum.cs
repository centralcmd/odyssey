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

    /// <summary>
    /// A <see cref="TagIcons"/> key, or <c>null</c> for the album default (issue #404). The default keys are
    /// rejected — send <c>null</c>. <c>PUT</c> is full replacement, so an omitted icon clears it.
    /// </summary>
    [StringLength(TagIcons.MaxKeyLength, ErrorMessage = TagIcons.InvalidIconMessage)]
    [TagIcon]
    public string? Icon { get; set; }

    /// <summary>
    /// An optional <c>#RRGGBB</c> colour, or <c>null</c> for none (issue #404). Any valid hex is accepted and
    /// stored upper-case; an empty string is rejected — send <c>null</c>. <c>PUT</c> is full replacement, so
    /// an omitted colour clears it.
    /// </summary>
    [StringLength(HexColor.Length, ErrorMessage = HexColor.InvalidMessage)]
    [HexColor]
    public string? Color { get; set; }

    /// <summary>Desired archived state: true archives, false restores.</summary>
    public bool Archived { get; set; }
}
