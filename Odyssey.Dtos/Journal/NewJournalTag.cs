using System.ComponentModel.DataAnnotations;

namespace Odyssey.Dtos.Journal;

/// <summary>Create request for a journal tag.</summary>
public sealed record NewJournalTag
{
    [Required]
    [StringLength(64)]
    public required string Name { get; set; }

    [StringLength(256)]
    public string? Description { get; set; }

    /// <summary>
    /// A <see cref="TagIcons"/> key, or <c>null</c> for the default icon (issue #345). The default key
    /// itself is rejected — send <c>null</c>.
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
