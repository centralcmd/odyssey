using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;
using Odyssey.Dtos.Recipes;

namespace Odyssey.Context;

/// <summary>
/// One ingredient line of a <see cref="Recipe"/>, in <see cref="Position"/> order. <see cref="Position"/> is
/// indexed but <b>not unique</b>: a <c>PUT</c> replaces the rows wholesale in one save, and EF does not order a
/// delete before an insert, so a unique position would collide on an ordinary edit (§4).
/// </summary>
public class RecipeIngredient
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid RecipeIngredientId { get; set; }

    public Guid RecipeId { get; set; }

    public int Position { get; set; }

    /// <summary>A heading such as "Dough"; consecutive lines sharing a value form a group.</summary>
    [StringLength(RecipeLimits.MaxSectionLength)]
    public string? Section { get; set; }

    /// <summary>Null for an unmeasured line ("salt, to taste").</summary>
    [Precision(RecipeLimits.AmountPrecision, RecipeLimits.AmountScale)]
    public decimal? Amount { get; set; }

    /// <summary>The upper end of a range ("2–3 cloves"); requires <see cref="Amount"/> and exceeds it.</summary>
    [Precision(RecipeLimits.AmountPrecision, RecipeLimits.AmountScale)]
    public decimal? AmountMax { get; set; }

    public IngredientUnit Unit { get; set; }

    [StringLength(RecipeLimits.MaxIngredientNameLength)]
    [Required]
    public required string Name { get; set; }

    [StringLength(RecipeLimits.MaxPreparationLength)]
    public string? Preparation { get; set; }

    public bool Optional { get; set; }

    public Recipe? Recipe { get; set; }
}
