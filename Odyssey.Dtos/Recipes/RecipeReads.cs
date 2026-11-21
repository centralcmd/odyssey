namespace Odyssey.Dtos.Recipes;

/// <summary>
/// A recipe as <c>GET /api/recipes/{id}</c> returns it (issue #525 §5). Author names only, never raw user ids, and
/// <see cref="Photos"/> is <c>null</c> for a caller without <c>photos.read</c> — absent, not hidden.
/// </summary>
public sealed record ExistingRecipe : ITagIconRecord
{
    public required Guid RecipeId { get; set; }

    /// <summary>The concurrency token; echo it on <c>PUT</c>.</summary>
    public required int Version { get; set; }

    public required string Title { get; set; }

    public string? Description { get; set; }

    public string? Notes { get; set; }

    /// <summary>Plain text; never rendered as markup.</summary>
    public string? Source { get; set; }

    /// <summary>The recipe's stored servings.</summary>
    public required int Servings { get; set; }

    public string? ServingsLabel { get; set; }

    /// <summary><see cref="Servings"/>, or the requested <c>?servings</c> when the response is scaled.</summary>
    public required int DisplayedServings { get; set; }

    public bool Scaled { get; set; }

    public int? PrepMinutes { get; set; }

    public int? CookMinutes { get; set; }

    /// <summary>Prep + cook, a null counting as 0; null when both are null.</summary>
    public int? TotalMinutes { get; set; }

    public List<RecipeIngredientDto> Ingredients { get; set; } = [];

    public List<RecipeStepDto> Steps { get; set; } = [];

    /// <summary>Null without <c>photos.read</c>.</summary>
    public List<RecipePhotoDto>? Photos { get; set; }

    /// <summary>In tag-name order (issue #345).</summary>
    public List<Guid> TagIds { get; set; } = [];

    public string DisplayIcon { get; set; } = TagIcons.Default;

    public string? DisplayColor { get; set; }

    public required DateTime CreatedAt { get; set; }

    public required DateTime UpdatedAt { get; set; }

    /// <summary>The author's display name, or <c>null</c> when the attribution was nulled (author deleted).</summary>
    public string? CreatedByName { get; set; }

    public string? UpdatedByName { get; set; }

    public DateTime? Archived { get; set; }
}

/// <summary>One ingredient line of a recipe read.</summary>
public sealed record RecipeIngredientDto
{
    public required Guid RecipeIngredientId { get; set; }

    public required int Position { get; set; }

    public string? Section { get; set; }

    /// <summary>Scaled when the read was scaled and the unit is scalable.</summary>
    public decimal? Amount { get; set; }

    public decimal? AmountMax { get; set; }

    public required IngredientUnit Unit { get; set; }

    public required string Name { get; set; }

    public string? Preparation { get; set; }

    public bool Optional { get; set; }
}

/// <summary>One step of a recipe read.</summary>
public sealed record RecipeStepDto
{
    public required Guid RecipeStepId { get; set; }

    public required int Position { get; set; }

    public string? Section { get; set; }

    public required string Text { get; set; }
}

/// <summary>A recipe's photo link — only ever returned to a <c>photos.read</c> holder.</summary>
public sealed record RecipePhotoDto
{
    public required Guid RecipePhotoId { get; set; }

    public required Guid PhotoId { get; set; }

    /// <summary>The lowest position is the cover.</summary>
    public required int Position { get; set; }

    public string? Title { get; set; }

    public string? FileName { get; set; }

    public bool ThumbnailsReady { get; set; }
}

/// <summary>A row of <c>GET /api/recipes</c>: no ingredients or steps, only their counts.</summary>
public sealed record RecipeSummary : ITagIconRecord
{
    public required Guid RecipeId { get; set; }

    public required string Title { get; set; }

    public string? Description { get; set; }

    public required int Servings { get; set; }

    public string? ServingsLabel { get; set; }

    public int? PrepMinutes { get; set; }

    public int? CookMinutes { get; set; }

    public int? TotalMinutes { get; set; }

    public int IngredientCount { get; set; }

    public int StepCount { get; set; }

    /// <summary>Null without <c>photos.read</c>.</summary>
    public Guid? CoverPhotoId { get; set; }

    /// <summary>False without <c>photos.read</c>.</summary>
    public bool CoverThumbnailsReady { get; set; }

    public List<Guid> TagIds { get; set; } = [];

    public string DisplayIcon { get; set; } = TagIcons.Default;

    public string? DisplayColor { get; set; }

    public required DateTime UpdatedAt { get; set; }

    public DateTime? Archived { get; set; }
}
