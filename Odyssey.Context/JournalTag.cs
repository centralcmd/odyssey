using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Odyssey.Context;

[Index(nameof(Name))]
[Index(nameof(Archived))]
public class JournalTag
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid JournalTagId { get; set; }

    [StringLength(64)]
    [Required]
    public required string Name { get; set; }

    [StringLength(256)]
    public string? Description { get; set; }

    public DateTime? Archived { get; set; }

    /// <summary>
    /// A <c>TagIcons</c> catalogue key, or <c>NULL</c> for the default icon (issue #345, after #279). Never
    /// markup or a URL, and never the default key itself. A key the catalogue no longer has is projected
    /// as <c>null</c> on read, so removing one needs no data migration.
    /// </summary>
    [StringLength(64)]
    public string? Icon { get; set; }

    public ICollection<JournalEntryTag> EntryTags { get; set; } = new List<JournalEntryTag>();
}
