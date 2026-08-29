using System.ComponentModel.DataAnnotations;

namespace Odyssey.Dtos.Journal;

/// <summary>Full task read model. Attachments are returned as ids/metadata only; the client hydrates file names.</summary>
public sealed record ExistingJournalTask
{
    public required Guid JournalTaskId { get; set; }

    /// <summary>Stable external identity used as the VTODO UID on .ics export (issue #337 §6).</summary>
    [StringLength(255)]
    public required string ExternalUid { get; set; }

    [StringLength(200)]
    public required string Title { get; set; }

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

    [StringLength(255)]
    public string? UpdatedByUserId { get; set; }

    /// <summary>Display name of the last editor, resolved at the API edge; null if unresolved.</summary>
    [StringLength(256)]
    public string? UpdatedByName { get; set; }

    public required DateTime CreatedAt { get; set; }

    public required DateTime UpdatedAt { get; set; }

    /// <summary>When the item moved to Doing (source of the derived Doing state); null if never started.</summary>
    public DateTime? StartedAt { get; set; }

    public DateTime? CompletedAt { get; set; }

    /// <summary>Soft-archive timestamp; non-null means the item is Archived.</summary>
    public DateTime? Archived { get; set; }

    public List<Guid> TagIds { get; set; } = [];

    public List<JournalTaskAttachmentDto> Attachments { get; set; } = [];
}
