using System.ComponentModel.DataAnnotations;

namespace Odyssey.Dtos.Journal;

/// <summary>Update request for a photo tag: rename/describe and toggle the archived state.</summary>
public sealed record UpdatePhotoTag
{
    [Required]
    [StringLength(PhotoLimits.MaxTagNameLength)]
    public required string Name { get; set; }

    [StringLength(PhotoLimits.MaxTagDescriptionLength)]
    public string? Description { get; set; }

    /// <summary>Desired archived state: true archives, false restores.</summary>
    public bool Archived { get; set; }
}
