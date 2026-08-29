using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Odyssey.Context;

/// <summary>
/// A photo tag (mirrors JournalTag / TransactionTags). <see cref="Name"/> carries a <b>unique</b> index;
/// its case-insensitivity relies on the column's <b>default</b> collation (MariaDB's <c>utf8mb4_*_ci</c>),
/// not an explicitly-set one, so keyword-driven find-or-create (§10.6) cannot create case-variant
/// duplicates. The service-layer name check plus the duplicate-key (1062) re-fetch are the belt-and-braces
/// if that collation assumption ever changes. Uniqueness is global across active and archived tags.
/// </summary>
[Index(nameof(Name), IsUnique = true)]
[Index(nameof(Archived))]
public class PhotoTag
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid PhotoTagId { get; set; }

    [StringLength(64)]
    [Required]
    public required string Name { get; set; }

    [StringLength(256)]
    public string? Description { get; set; }

    public DateTime? Archived { get; set; }

    public ICollection<PhotoTagLink> PhotoTags { get; set; } = new List<PhotoTagLink>();
}
