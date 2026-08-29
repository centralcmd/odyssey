using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Odyssey.Context;

public class JournalEntryContact
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid JournalEntryContactId { get; set; }

    public Guid JournalEntryId { get; set; }

    public Guid ContactId { get; set; }

    public JournalEntry? JournalEntry { get; set; }
}
