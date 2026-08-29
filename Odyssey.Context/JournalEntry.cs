using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Odyssey.Context;

[Index(nameof(Archived), nameof(EntryDate))]
[Index(nameof(ExternalUid), IsUnique = true)]
public class JournalEntry
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid JournalEntryId { get; set; }

    // The row's external identity anchor (issue #339), mirroring JournalTask.ExternalUid (#337) and
    // Contact.ExternalUid (#338). Required + unique — a journal entry has no recurrence concept, so
    // there is no case for one UID spanning multiple rows. Exported verbatim as the VJOURNAL UID and
    // matched case-sensitively on import; the unique index uses a binary collation (see OdysseyContext).
    [StringLength(255)]
    [Required]
    public required string ExternalUid { get; set; }

    [StringLength(200)]
    [Required]
    public required string Title { get; set; }

    [StringLength(4096)]
    [Required]
    public required string Content { get; set; }

    public DateTime EntryDate { get; set; }

    [StringLength(300)]
    public string? Location { get; set; }

    public string? CreatedByUserId { get; set; }

    public string? UpdatedByUserId { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public DateTime? Archived { get; set; }

    public ICollection<JournalEntryTag> EntryTags { get; set; } = new List<JournalEntryTag>();

    public ICollection<JournalEntryContact> Contacts { get; set; } = new List<JournalEntryContact>();

    public ICollection<JournalEntryPhoto> Photos { get; set; } = new List<JournalEntryPhoto>();

    public ICollection<JournalEntryAttachment> Attachments { get; set; } = new List<JournalEntryAttachment>();
}
