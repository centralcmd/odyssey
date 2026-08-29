using System.ComponentModel.DataAnnotations;

namespace Odyssey.Dtos.Journal;

/// <summary>
/// Update request for a journal entry: replaces fields and link/photo sets, and sets the archived
/// state (<see cref="Archived"/> = true archives, false restores) — there are no separate
/// archive/unarchive endpoints.
/// </summary>
public sealed record UpdateJournalEntry
{
    /// <summary>Optional replacement external identity. A null leaves the stored value untouched; a
    /// supplied value is stored verbatim and must not already belong to another entry (issue #339 §6).</summary>
    [StringLength(255)]
    [RegularExpression(JournalEntryExternalUidRules.Pattern, ErrorMessage = JournalEntryExternalUidRules.ErrorMessage)]
    public string? ExternalUid { get; set; }

    [Required]
    [StringLength(200)]
    public required string Title { get; set; }

    [Required]
    [StringLength(4096)]
    public required string Content { get; set; }

    [Required]
    public required DateTime EntryDate { get; set; }

    [StringLength(300)]
    public string? Location { get; set; }

    /// <summary>Desired archived state: true soft-archives the entry, false restores it.</summary>
    public bool Archived { get; set; }

    public Guid[] TagIds { get; set; } = [];

    public Guid[] ContactIds { get; set; } = [];

    public Guid[] PhotoFileIds { get; set; } = [];

    public Guid[] AttachmentFileIds { get; set; } = [];
}
