using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Odyssey.Context;

/// <summary>
/// A named grouping of photos (issue #321). <see cref="CoverPhotoId"/> is a real in-context FK to
/// <see cref="Photo"/> with <c>ON DELETE SET NULL</c> (configured in <see cref="OdysseyContext"/>), and is
/// additionally validated on write to be a member of the album.
/// </summary>
[Index(nameof(Name))]
[Index(nameof(Archived))]
public class PhotoAlbum
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid PhotoAlbumId { get; set; }

    [StringLength(128)]
    [Required]
    public required string Name { get; set; }

    [StringLength(1024)]
    public string? Description { get; set; }

    public Guid? CoverPhotoId { get; set; }

    public DateTime? Archived { get; set; }

    public string? CreatedByUserId { get; set; }

    public string? UpdatedByUserId { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public Photo? CoverPhoto { get; set; }

    public ICollection<PhotoAlbumItem> Items { get; set; } = new List<PhotoAlbumItem>();
}
