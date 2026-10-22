using System.ComponentModel.DataAnnotations;

namespace Odyssey.Dtos.Journal;

/// <summary>Create request for a photo tag.</summary>
public sealed record NewPhotoTag
{
    [Required]
    [StringLength(PhotoLimits.MaxTagNameLength)]
    public required string Name { get; set; }

    [StringLength(PhotoLimits.MaxTagDescriptionLength)]
    public string? Description { get; set; }

    /// <summary>
    /// A <see cref="TagIcons"/> key, or <c>null</c> for the default icon (issue #345). The default key
    /// itself is rejected — send <c>null</c>.
    /// </summary>
    [StringLength(TagIcons.MaxKeyLength, ErrorMessage = TagIcons.InvalidIconMessage)]
    [TagIcon]
    public string? Icon { get; set; }
}
