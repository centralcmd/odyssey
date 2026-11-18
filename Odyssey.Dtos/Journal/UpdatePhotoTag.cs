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

    /// <summary>
    /// A <see cref="TagIcons"/> key, or <c>null</c> for the default icon (issue #345). The default key
    /// itself is rejected — send <c>null</c>. <c>PUT</c> is full replacement, so an omitted icon resets
    /// the tag to the default.
    /// </summary>
    [StringLength(TagIcons.MaxKeyLength, ErrorMessage = TagIcons.InvalidIconMessage)]
    [TagIcon]
    public string? Icon { get; set; }

    /// <summary>
    /// An optional <c>#RRGGBB</c> colour, or <c>null</c> for none (issue #392). Any valid hex is accepted
    /// and stored upper-case; an empty string is rejected — send <c>null</c>. <c>PUT</c> is full
    /// replacement, so an omitted colour clears it.
    /// </summary>
    [StringLength(HexColor.Length, ErrorMessage = HexColor.InvalidMessage)]
    [HexColor]
    public string? Color { get; set; }
}
