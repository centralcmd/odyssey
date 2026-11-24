using System.ComponentModel.DataAnnotations;

namespace Odyssey.Dtos.Recipes;

/// <summary>
/// The fields <see cref="NewRecipe"/> and <see cref="UpdateRecipe"/> share (issue #525 §5). Designed as a base type,
/// which is why it is the one recipe DTO that is not <c>sealed</c>. Links are scalar id arrays only; ingredients and
/// steps are value rows with no ids, ordered by array position.
/// </summary>
public abstract record RecipeRequest
{
    [Required]
    [StringLength(RecipeLimits.MaxTitleLength)]
    public string Title { get; set; } = string.Empty;

    [StringLength(RecipeLimits.MaxDescriptionLength)]
    public string? Description { get; set; }

    [StringLength(RecipeLimits.MaxNotesLength)]
    public string? Notes { get; set; }

    [StringLength(RecipeLimits.MaxSourceLength)]
    public string? Source { get; set; }

    /// <summary>Nullable so an omitted value is a <c>400</c> rather than a silent <c>0</c>.</summary>
    [Required]
    [Range(RecipeLimits.MinServings, RecipeLimits.MaxServings)]
    public int? Servings { get; set; }

    [StringLength(RecipeLimits.MaxServingsLabelLength)]
    public string? ServingsLabel { get; set; }

    [Range(0, RecipeLimits.MaxMinutes)]
    public int? PrepMinutes { get; set; }

    [Range(0, RecipeLimits.MaxMinutes)]
    public int? CookMinutes { get; set; }

    [MaxLength(RecipeLimits.MaxIngredients)]
    public RecipeIngredientRequest[] Ingredients { get; set; } = [];

    [MaxLength(RecipeLimits.MaxSteps)]
    public RecipeStepRequest[] Steps { get; set; } = [];

    /// <summary>Recipe tag ids; a full replacement on <c>PUT</c>. Adding one needs <c>recipes.tags.read</c>.</summary>
    [MaxLength(RecipeLimits.MaxTags)]
    public Guid[] TagIds { get; set; } = [];
}

/// <summary>Create request for a recipe.</summary>
public sealed record NewRecipe : RecipeRequest
{
    /// <summary>Library photo ids in gallery order; the first is the cover. Non-empty needs <c>photos.read</c>.</summary>
    [MaxLength(RecipeLimits.MaxPhotos)]
    public Guid[] PhotoIds { get; set; } = [];

    /// <summary>Files-store file ids to link (unordered). Non-empty needs <c>files.read</c>.</summary>
    [MaxLength(RecipeLimits.MaxAttachments)]
    public Guid[] AttachmentFileIds { get; set; } = [];
}

/// <summary>Full-replacement update for a recipe, guarded by <see cref="Version"/>.</summary>
public sealed record UpdateRecipe : RecipeRequest
{
    /// <summary>The <c>version</c> the caller read; a mismatch is <c>409 recipe-version-conflict</c>.</summary>
    [Required]
    public int? Version { get; set; }

    /// <summary>Desired archived state: true archives (keeping an earlier timestamp), false restores.</summary>
    public bool Archived { get; set; }

    /// <summary>
    /// <c>null</c> leaves the photo links unchanged; an array replaces them (<c>[]</c> removes all). A caller without
    /// <c>photos.read</c> cannot see the current set, so it must send <c>null</c> — any array is a <c>403</c>.
    /// </summary>
    [MaxLength(RecipeLimits.MaxPhotos)]
    public Guid[]? PhotoIds { get; set; }

    /// <summary>
    /// <c>null</c> leaves the file links unchanged; an array replaces them (<c>[]</c> removes all). A caller without
    /// <c>files.read</c> cannot see the current set, so it must send <c>null</c> — any array is a <c>403</c>.
    /// </summary>
    [MaxLength(RecipeLimits.MaxAttachments)]
    public Guid[]? AttachmentFileIds { get; set; }
}

/// <summary>One ingredient line of a recipe request.</summary>
public sealed record RecipeIngredientRequest
{
    [StringLength(RecipeLimits.MaxSectionLength)]
    public string? Section { get; set; }

    [Range(typeof(decimal), RecipeLimits.MinAmount, RecipeLimits.MaxAmount, ParseLimitsInInvariantCulture = true, ConvertValueInInvariantCulture = true)]
    public decimal? Amount { get; set; }

    /// <summary>Nullable so an omitted unit is a <c>400</c>, never a silent <see cref="IngredientUnit.None"/>.</summary>
    [Required]
    [EnumDataType(typeof(IngredientUnit))]
    public IngredientUnit? Unit { get; set; }

    [Required]
    [StringLength(RecipeLimits.MaxIngredientNameLength)]
    public string Name { get; set; } = string.Empty;

    public bool Optional { get; set; }
}

/// <summary>One step of a recipe request's method.</summary>
public sealed record RecipeStepRequest
{
    [StringLength(RecipeLimits.MaxSectionLength)]
    public string? Section { get; set; }

    [Required]
    [StringLength(RecipeLimits.MaxStepTextLength)]
    public string Text { get; set; } = string.Empty;
}
