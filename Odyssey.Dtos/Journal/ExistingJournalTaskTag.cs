using System.ComponentModel.DataAnnotations;

namespace Odyssey.Dtos.Journal;

/// <summary>Read model for a task tag.</summary>
public sealed record ExistingJournalTaskTag
{
    public required Guid JournalTaskTagId { get; set; }

    [StringLength(64)]
    public required string Name { get; set; }

    [StringLength(256)]
    public string? Description { get; set; }

    public DateTime? Archived { get; set; }
}
