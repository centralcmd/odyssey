using System.ComponentModel.DataAnnotations;

namespace Odyssey.Dtos.Journal;

/// <summary>List-row projection for a journal entry: scalar fields, tag ids, a content snippet, and link counts.</summary>
public sealed record JournalEntrySummary
{
    public required Guid JournalEntryId { get; set; }

    [StringLength(200)]
    public required string Title { get; set; }

    [StringLength(200)]
    public required string Snippet { get; set; }

    public required DateTime EntryDate { get; set; }

    [StringLength(300)]
    public string? Location { get; set; }

    [StringLength(255)]
    public string? CreatedByUserId { get; set; }

    /// <summary>Display name (username/email) of the author, resolved at the API edge; null if unresolved.</summary>
    [StringLength(256)]
    public string? CreatedByName { get; set; }

    public DateTime? Archived { get; set; }

    public List<Guid> TagIds { get; set; } = [];

    public required int PhotoCount { get; set; }

    public required int AttachmentCount { get; set; }

    public required int ContactCount { get; set; }
}
