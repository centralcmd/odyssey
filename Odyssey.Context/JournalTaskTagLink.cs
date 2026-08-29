using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Odyssey.Context;

public class JournalTaskTagLink
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid JournalTaskTagLinkId { get; set; }

    public Guid JournalTaskId { get; set; }

    public Guid JournalTaskTagId { get; set; }

    public JournalTask? JournalTask { get; set; }

    public JournalTaskTag? JournalTaskTag { get; set; }
}
