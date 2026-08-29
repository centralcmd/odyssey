using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Odyssey.Context;

// The kanban status is NOT stored — it is derived from the timestamps below (issue #311 review):
// Archived != null → Archived; else CompletedAt != null → Done; else StartedAt != null → Doing;
// else Backlog. Position is meaningful only within the Backlog/Doing columns, hence the composite index.
[Index(nameof(Archived), nameof(Position))]
[Index(nameof(ExternalUid), IsUnique = true)]
public class JournalTask
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid JournalTaskId { get; set; }

    // Stable external identity for VTODO/.ics interop (issue #337 §6). Every task gets one at creation
    // (auto-generated urn:uuid when the caller doesn't supply it); imported VTODOs store their UID here
    // verbatim, and export emits it as the VTODO UID — so the internal PK never leaks into exported files.
    // Required + unique (diverging from Calendar's nullable/non-unique ExternalUid, which shares a UID
    // across recurrence exceptions — Journal Tasks has no such case since recurring VTODOs are rejected).
    [StringLength(255)]
    [Required]
    public required string ExternalUid { get; set; }

    [StringLength(200)]
    [Required]
    public required string Title { get; set; }

    [StringLength(4096)]
    public string? Content { get; set; }

    public DateOnly? Deadline { get; set; }

    public int Position { get; set; }

    public string? CreatedByUserId { get; set; }

    public string? UpdatedByUserId { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    /// <summary>Set when the item first moves to Doing; non-null ⇒ Doing (unless later Done/Archived).</summary>
    public DateTime? StartedAt { get; set; }

    /// <summary>Set when the item moves to Done; non-null ⇒ Done (unless Archived).</summary>
    public DateTime? CompletedAt { get; set; }

    /// <summary>Soft-archive timestamp; non-null ⇒ Archived (supersedes the other derived states).</summary>
    public DateTime? Archived { get; set; }

    public ICollection<JournalTaskTagLink> ItemTags { get; set; } = new List<JournalTaskTagLink>();

    public ICollection<JournalTaskAttachment> Attachments { get; set; } = new List<JournalTaskAttachment>();
}
