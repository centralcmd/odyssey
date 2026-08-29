using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Odyssey.Context;

/// <summary>Join row placing a <see cref="Photo"/> in a <see cref="PhotoAlbum"/> at a given position.
/// One photo can belong to many albums (both real FKs).</summary>
public class PhotoAlbumItem
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid PhotoAlbumItemId { get; set; }

    public Guid PhotoAlbumId { get; set; }

    public Guid PhotoId { get; set; }

    public int Position { get; set; }

    public DateTime CreatedAt { get; set; }

    public PhotoAlbum? PhotoAlbum { get; set; }

    public Photo? Photo { get; set; }
}
