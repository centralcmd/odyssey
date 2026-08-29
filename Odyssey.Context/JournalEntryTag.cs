using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Odyssey.Context;

public class JournalEntryTag
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid JournalEntryTagId { get; set; }

    public Guid JournalEntryId { get; set; }

    public Guid JournalTagId { get; set; }

    public JournalEntry? JournalEntry { get; set; }

    public JournalTag? JournalTag { get; set; }
}
