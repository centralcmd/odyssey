using System.ComponentModel.DataAnnotations;

namespace Odyssey.Dtos.Journal;

/// <summary>Create request for a task tag.</summary>
public sealed record NewJournalTaskTag
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
}
