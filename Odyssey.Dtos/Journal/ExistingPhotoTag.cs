using System.ComponentModel.DataAnnotations;

namespace Odyssey.Dtos.Journal;

/// <summary>Read model for a photo tag.</summary>
public sealed record ExistingPhotoTag
{
    public required Guid PhotoTagId { get; set; }

    [StringLength(PhotoLimits.MaxTagNameLength)]
    public required string Name { get; set; }

    [StringLength(PhotoLimits.MaxTagDescriptionLength)]
    public string? Description { get; set; }

    public DateTime? Archived { get; set; }
}
