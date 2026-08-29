using System.ComponentModel.DataAnnotations;

namespace Odyssey.Dtos.Journal;

/// <summary>Create request for a journal entry. Links are accepted as scalar id arrays only (§6 mass-assignment guard).</summary>
public sealed record NewJournalEntry
{
    /// <summary>Optional stable external identity (VJOURNAL UID / urn:uuid). When omitted the service
    /// generates a <c>urn:uuid:{Guid}</c>; when supplied it is stored verbatim and must not already
    /// belong to another entry (issue #339 §6).</summary>
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

    public Guid[] TagIds { get; set; } = [];

    public Guid[] ContactIds { get; set; } = [];

    public Guid[] PhotoFileIds { get; set; } = [];

    public Guid[] AttachmentFileIds { get; set; } = [];
}
