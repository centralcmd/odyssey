using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Odyssey.Context;

/// <summary>
/// Links a <see cref="Recipe"/> to a <see cref="RecipeTag"/>. <c>CASCADE</c> from the recipe, <c>RESTRICT</c> to the
/// tag: the tag service refuses to delete a tag in use (<c>409</c>), and the key is the backstop.
/// </summary>
public class RecipeTagLink
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid RecipeTagLinkId { get; set; }

    public Guid RecipeId { get; set; }

    public Guid RecipeTagId { get; set; }

    public Recipe? Recipe { get; set; }

    public RecipeTag? RecipeTag { get; set; }
}
