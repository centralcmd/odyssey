using System.ComponentModel.DataAnnotations;

namespace Odyssey.Dtos.Recipes;

/// <summary>Sortable keys for the recipes list. <see cref="TotalMinutes"/> puts nulls last in both directions.</summary>
public enum RecipeSortBy
{
    Title,
    UpdatedAt,
    CreatedAt,
    TotalMinutes,
}

/// <summary>Sortable keys for the recipe-tags list.</summary>
public enum RecipeTagSortBy
{
    Name,
    Status,
    RecipeCount,
}

/// <summary>Recipes list query (issue #525 §5 item 1).</summary>
public sealed class RecipesQueryParams : QueryParams<RecipeSortBy>
{
    /// <summary>A recipe must carry every listed tag (AND).</summary>
    [MaxLength(ListDefaults.MaxFilterArrayLength)]
    public Guid[]? TagIds { get; set; }

    /// <summary>Prep + cook at most this; a recipe with no times is excluded when set.</summary>
    [Range(1, RecipeLimits.MaxTotalMinutes)]
    public int? MaxTotalMinutes { get; set; }

    /// <summary><c>403</c> without <c>photos.read</c>: the filter alone would reveal what the read withholds.</summary>
    public bool? HasPhotos { get; set; }

    /// <summary><c>403</c> without <c>files.read</c>, for the same reason as <see cref="HasPhotos"/>.</summary>
    public bool? HasAttachments { get; set; }

    /// <summary>Archived recipes are hidden unless <see cref="ArchivalStatus.Archived"/> is asked for.</summary>
    public ArchivalStatus? Status { get; set; }
}

/// <summary>Single-recipe read query: the optional servings to scale to.</summary>
public sealed class RecipeReadQuery
{
    [Range(1, RecipeLimits.MaxScaleServings)]
    public int? Servings { get; set; }
}

/// <summary>Recipe-tags list query. An omitted <see cref="Status"/> applies no filter: active and archived both.</summary>
public sealed class RecipeTagsQueryParams : QueryParams<RecipeTagSortBy>
{
    public ArchivalStatus? Status { get; set; }
}

/// <summary>Problem-details <c>code</c> values the Recipes module emits.</summary>
public static class RecipeErrorCodes
{
    /// <summary>The <c>PUT</c> carried a <c>version</c> that is no longer the stored one.</summary>
    public const string VersionConflict = "recipe-version-conflict";
}
