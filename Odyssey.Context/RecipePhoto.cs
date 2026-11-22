using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Odyssey.Context;

/// <summary>
/// Links a library <see cref="Photo"/> to a <see cref="Recipe"/> (issue #525), the <see cref="JournalEntryPhoto"/>
/// shape. The cover is the link with the lowest <c>(Position, RecipePhotoId)</c>. Deleting the photo removes the
/// link (<c>CASCADE</c>); it never blocks the photo delete. A <c>PUT</c> diffs these rows in place, so the unique
/// <c>(RecipeId, PhotoId)</c> index cannot collide with a delete-and-reinsert of the same pair.
/// </summary>
public class RecipePhoto
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid RecipePhotoId { get; set; }

    public Guid RecipeId { get; set; }

    public Guid PhotoId { get; set; }

    public int Position { get; set; }

    public DateTime CreatedAt { get; set; }

    public Recipe? Recipe { get; set; }
}
