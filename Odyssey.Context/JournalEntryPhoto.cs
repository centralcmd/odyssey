using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Odyssey.Context;

public class JournalEntryPhoto
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid JournalEntryPhotoId { get; set; }

    public Guid JournalEntryId { get; set; }

    // Photo Library unification (issue #321 v4): a journal entry photo links a library Photo by PhotoId
    // rather than owning a raw FileId. The FileId a renderer needs is resolved from the library Photo at
    // read time via IPhotoLookup.
    public Guid PhotoId { get; set; }

    public int Position { get; set; }

    public DateTime CreatedAt { get; set; }

    public JournalEntry? JournalEntry { get; set; }
}
