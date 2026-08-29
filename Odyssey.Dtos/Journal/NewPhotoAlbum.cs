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
}
