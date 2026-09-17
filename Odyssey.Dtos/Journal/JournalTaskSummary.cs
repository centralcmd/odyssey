using System.ComponentModel.DataAnnotations;

namespace Odyssey.Dtos.Journal;

/// <summary>
/// List-row projection for a task. The list card is always open and IS the detail, so the row carries
/// the task's content in full rather than a truncated snippet; the board card clamps the same text to
/// two lines in CSS. Attachments stay a count — the card shows the count and nothing else.
/// </summary>
public sealed record JournalTaskSummary
{
    public required Guid JournalTaskId { get; set; }

    [StringLength(200)]
    public required string Title { get; set; }

    /// <summary>The task's note in full — the list card renders it unclamped.</summary>
    [StringLength(4096)]
    public string? Content { get; set; }

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
