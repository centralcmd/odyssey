using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Odyssey.Context;

/// <summary>Join row linking a <see cref="Photo"/> to a <see cref="PhotoTag"/> (both real FKs).</summary>
public class PhotoTagLink
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid PhotoTagLinkId { get; set; }

    public Guid PhotoId { get; set; }

    public Guid PhotoTagId { get; set; }

    public Photo? Photo { get; set; }

    public PhotoTag? PhotoTag { get; set; }
}
