using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Odyssey.Dtos.Recipes;

namespace Odyssey.Context;

/// <summary>
/// One step of a <see cref="Recipe"/>'s method, in <see cref="Position"/> order. As with
/// <see cref="RecipeIngredient"/>, the position index is deliberately not unique.
/// </summary>
public class RecipeStep
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid RecipeStepId { get; set; }

    public Guid RecipeId { get; set; }

    public int Position { get; set; }

    [StringLength(RecipeLimits.MaxSectionLength)]
    public string? Section { get; set; }

    [StringLength(RecipeLimits.MaxStepTextLength)]
    [Required]
    public required string Text { get; set; }

    public Recipe? Recipe { get; set; }
}
