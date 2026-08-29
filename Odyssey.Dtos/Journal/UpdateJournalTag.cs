using System.ComponentModel.DataAnnotations;

namespace Odyssey.Dtos.Journal;

/// <summary>Update request for a journal tag: rename/describe and toggle archive.</summary>
public sealed record UpdateJournalTag
{
    [Required]
    [StringLength(64)]
    public required string Name { get; set; }

    [StringLength(256)]
    public string? Description { get; set; }

    public bool Archived { get; set; }
}
