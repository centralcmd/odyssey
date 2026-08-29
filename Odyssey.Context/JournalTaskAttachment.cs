using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Odyssey.Context;

public class JournalTaskAttachment
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid JournalTaskAttachmentId { get; set; }

    public Guid JournalTaskId { get; set; }

    public Guid FileId { get; set; }

    public DateTime CreatedAt { get; set; }

    public JournalTask? JournalTask { get; set; }
}
