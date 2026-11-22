using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;
using Odyssey.Dtos.Recipes;

namespace Odyssey.Context;

/// <summary>
/// A household recipe (issue #525): shared data with no per-user owner, gated by the <c>recipes.*</c> claims.
/// Total time is not stored; it is <see cref="PrepMinutes"/> + <see cref="CookMinutes"/> at read time.
/// </summary>
[Index(nameof(Title))]
[Index(nameof(Archived))]
public class Recipe
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid RecipeId { get; set; }

    [StringLength(RecipeLimits.MaxTitleLength)]
    [Required]
    public required string Title { get; set; }

    [StringLength(RecipeLimits.MaxDescriptionLength)]
    public string? Description { get; set; }

    [StringLength(RecipeLimits.MaxNotesLength)]
    public string? Notes { get; set; }

    /// <summary>Free-text attribution ("Grandma" or a URL), stored as given and never fetched.</summary>
    [StringLength(RecipeLimits.MaxSourceLength)]
    public string? Source { get; set; }

    public int Servings { get; set; }

    [StringLength(RecipeLimits.MaxServingsLabelLength)]
    public string? ServingsLabel { get; set; }

    public int? PrepMinutes { get; set; }

    public int? CookMinutes { get; set; }

    /// <summary>
    /// The optimistic-concurrency token (§4): 1 on create, incremented by every successful <c>PUT</c>, which must
    /// carry the value it read. Configured as a concurrency token, so a racing second write fails its save.
    /// </summary>
    public int Version { get; set; }

    /// <summary>User attribution, <c>SET NULL</c> to <c>AspNetUsers</c> (declared in <c>OdysseyContext</c>).</summary>
    public string? CreatedByUserId { get; set; }

    public string? UpdatedByUserId { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public DateTime? Archived { get; set; }

    public ICollection<RecipeIngredient> Ingredients { get; set; } = new List<RecipeIngredient>();

    public ICollection<RecipeStep> Steps { get; set; } = new List<RecipeStep>();

    public ICollection<RecipePhoto> Photos { get; set; } = new List<RecipePhoto>();

    public ICollection<RecipeAttachment> Attachments { get; set; } = new List<RecipeAttachment>();

    public ICollection<RecipeTagLink> Tags { get; set; } = new List<RecipeTagLink>();
}
