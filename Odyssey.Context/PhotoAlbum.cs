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

    /// <summary>
    /// A <c>TagIcons</c> catalogue key, or <c>NULL</c> for the album default (<c>PhotoAlbumIcons.Default</c>,
    /// issue #404). Never markup or a URL, and never a default key itself. A key the catalogue no longer has
    /// is projected as <c>null</c> on read, so retiring one needs no data migration.
    /// </summary>
    [StringLength(64)]
    public string? Icon { get; set; }

    /// <summary>
    /// An optional <c>#RRGGBB</c> colour stored upper-case, or <c>NULL</c> for none (issue #404). Every read
    /// path projects it through <c>HexColor.Normalize</c>, so a hand-edited value reads as <c>null</c>.
    /// </summary>
    [StringLength(7)]
    public string? Color { get; set; }

    public DateTime? Archived { get; set; }

    public string? CreatedByUserId { get; set; }

    public string? UpdatedByUserId { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public Photo? CoverPhoto { get; set; }

    public ICollection<PhotoAlbumItem> Items { get; set; } = new List<PhotoAlbumItem>();
}
