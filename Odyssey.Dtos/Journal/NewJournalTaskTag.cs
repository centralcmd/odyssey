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
}
