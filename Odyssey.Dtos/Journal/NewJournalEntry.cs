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

    /// <summary>Optional detail text beside the linked place (issue #380 §4), e.g. "room 3".</summary>
    [StringLength(300)]
    public string? Location { get; set; }

    /// <summary>
    /// The linked place, by id only (issue #380 §5.2). A changed link needs <c>locations.read</c>; re-sending
    /// the stored id needs nothing. This is a full replacement, so an omitted id clears the link.
    /// </summary>
    public Guid? LocationId { get; set; }

    public Guid[] TagIds { get; set; } = [];

    public Guid[] ContactIds { get; set; } = [];

    /// <summary>Library photo ids, in gallery order (issue #428: photos are uploaded through the photo API first).</summary>
    public Guid[] PhotoIds { get; set; } = [];

    public Guid[] AttachmentFileIds { get; set; } = [];
}
