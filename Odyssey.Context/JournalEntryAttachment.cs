using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Odyssey.Context;

public class JournalEntryAttachment
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid JournalEntryAttachmentId { get; set; }

    public Guid JournalEntryId { get; set; }

    public Guid FileId { get; set; }

    public DateTime CreatedAt { get; set; }

    public JournalEntry? JournalEntry { get; set; }
}
