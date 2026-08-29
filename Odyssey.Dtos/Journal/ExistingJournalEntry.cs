using System.ComponentModel.DataAnnotations;

namespace Odyssey.Dtos.Journal;

/// <summary>Full journal-entry read model. Cross-context links are returned as ids only (§10.2); the client hydrates names.</summary>
public sealed record ExistingJournalEntry
{
    public required Guid JournalEntryId { get; set; }

    /// <summary>Stable external identity, exported verbatim as the VJOURNAL UID (issue #339 §6).</summary>
    [StringLength(255)]
    public string ExternalUid { get; set; } = string.Empty;

    [StringLength(200)]
    public required string Title { get; set; }

    [StringLength(4096)]
    public required string Content { get; set; }

    public required DateTime EntryDate { get; set; }

    [StringLength(300)]
    public string? Location { get; set; }

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

    public DateTime? Archived { get; set; }

    public List<Guid> TagIds { get; set; } = [];

    public List<Guid> ContactIds { get; set; } = [];

    public List<JournalEntryPhotoDto> Photos { get; set; } = [];

    public List<JournalEntryAttachmentDto> Attachments { get; set; } = [];
}
