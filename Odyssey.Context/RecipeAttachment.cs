using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Odyssey.Context;

/// <summary>
/// Links a Files-store <see cref="FileMetadata"/> to a <see cref="Recipe"/> (issue #525 Draft v6 §4): the original recipe
/// as a PDF, a scan, a text document. The <see cref="JournalEntryAttachment"/> shape: unordered, and deleting the file
/// from the Files store removes the link (<c>CASCADE</c>); deleting the recipe removes the link only, never the file. A
/// <c>PUT</c> diffs these rows in place, so the unique <c>(RecipeId, FileId)</c> index cannot collide.
/// </summary>
public class RecipeAttachment
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid RecipeAttachmentId { get; set; }

    public Guid RecipeId { get; set; }

    public Guid FileId { get; set; }

    public DateTime CreatedAt { get; set; }

    public Recipe? Recipe { get; set; }
}
