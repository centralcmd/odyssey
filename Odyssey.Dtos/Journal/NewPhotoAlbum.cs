using System.ComponentModel.DataAnnotations;

namespace Odyssey.Dtos.Journal;

/// <summary>
/// Create request for an album. The optional <see cref="PhotoIds"/> seed the initial membership in the
/// given order (each position = its index); <see cref="CoverPhotoId"/>, if set, must be one of them.
/// Members are referenced by scalar id only (§6 mass-assignment guard).
/// </summary>
public sealed record NewPhotoAlbum
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
}
