using System.ComponentModel.DataAnnotations;

namespace Odyssey.Dtos.Journal;

/// <summary>List-row projection for a task: scalar fields, tag ids, and attachment count.</summary>
public sealed record JournalTaskSummary
{
    public required Guid JournalTaskId { get; set; }

    [StringLength(200)]
    public required string Title { get; set; }

    /// <summary>A short plain-text preview of the task's content (truncated), for the card body.</summary>
    [StringLength(200)]
    public string? Snippet { get; set; }

    public DateOnly? Deadline { get; set; }

    public required JournalTaskStatus Status { get; set; }

    public required int Position { get; set; }

    [StringLength(255)]
    public string? CreatedByUserId { get; set; }

    /// <summary>Display name (username/email) of the author, resolved at the API edge; null if unresolved.</summary>
    [StringLength(256)]
    public string? CreatedByName { get; set; }

    public DateTime? StartedAt { get; set; }

    public DateTime? CompletedAt { get; set; }

    public DateTime? Archived { get; set; }

    public List<Guid> TagIds { get; set; } = [];

    public required int AttachmentCount { get; set; }
}
