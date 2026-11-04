using System.ComponentModel.DataAnnotations;

namespace Odyssey.Dtos.Journal;

/// <summary>Read model for a journal tag.</summary>
public sealed record ExistingJournalTag
{
    public required Guid JournalTagId { get; set; }

    [StringLength(64)]
    public required string Name { get; set; }

    [StringLength(256)]
    public string? Description { get; set; }

    public DateTime? Archived { get; set; }

    /// <summary>
    /// The tag's <see cref="TagIcons"/> key, or <c>null</c> for the default (issue #345). A stored key the
    /// catalogue no longer has is projected as <c>null</c>, never echoed.
    /// </summary>
    public string? Icon { get; set; }

    /// <summary>
    /// The tag's colour as normalised <c>#RRGGBB</c>, or <c>null</c> (issue #392). A stored value that is
    /// not a valid hex is projected as <c>null</c>, never echoed.
    /// </summary>
    public string? Color { get; set; }
}
