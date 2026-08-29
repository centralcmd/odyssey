using System.ComponentModel.DataAnnotations;

namespace Odyssey.Dtos.Journal;

/// <summary>Create request for a to-do item. Links are accepted as scalar id arrays only (§6 mass-assignment guard).</summary>
public sealed record NewJournalTask
{
    /// <summary>Optional stable external identity (VTODO UID / vCard-style urn:uuid). When omitted the
    /// service generates a <c>urn:uuid:{Guid}</c>; when supplied it is stored verbatim and must not
    /// already belong to another task (issue #337 §6).</summary>
    [StringLength(255)]
    public string? ExternalUid { get; set; }

    [Required]
    [StringLength(200)]
    public required string Title { get; set; }

    [StringLength(4096)]
    public string? Content { get; set; }

    public DateOnly? Deadline { get; set; }

    [EnumDataType(typeof(JournalTaskStatus))]
    public JournalTaskStatus Status { get; set; }

    public Guid[] TagIds { get; set; } = [];

    public Guid[] AttachmentFileIds { get; set; } = [];
}
