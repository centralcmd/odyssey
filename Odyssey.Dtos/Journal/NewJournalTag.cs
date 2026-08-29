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
}
