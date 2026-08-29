using System.ComponentModel.DataAnnotations;

namespace Odyssey.Dtos.Journal;

/// <summary>
/// Update request for a task's fields, lifecycle and ordering. <see cref="Status"/> and
/// <see cref="Position"/> are optional: when supplied they move the task (status → timestamp
/// derivation; position → gap-free reorder within the column); when null the current state is kept,
/// so a plain field edit does not disturb the board.
/// </summary>
public sealed record UpdateJournalTask
{
    /// <summary>Optional external identity. When supplied it replaces the stored value and must not
    /// already belong to a different task; when null the current value is kept unchanged (issue #337 §6).</summary>
    [StringLength(255)]
    public string? ExternalUid { get; set; }

    [Required]
    [StringLength(200)]
    public required string Title { get; set; }

    [StringLength(4096)]
    public string? Content { get; set; }

    public DateOnly? Deadline { get; set; }

    [EnumDataType(typeof(JournalTaskStatus))]
    public JournalTaskStatus? Status { get; set; }

    [Range(0, int.MaxValue)]
    public int? Position { get; set; }

    public Guid[] TagIds { get; set; } = [];

    public Guid[] AttachmentFileIds { get; set; } = [];
}
