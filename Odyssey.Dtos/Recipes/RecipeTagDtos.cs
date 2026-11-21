using System.ComponentModel.DataAnnotations;
using Odyssey.Dtos.Journal;

namespace Odyssey.Dtos.Recipes;

/// <summary>Read model for a recipe tag; the photo-tag shape plus <see cref="RecipeCount"/>.</summary>
public sealed record ExistingRecipeTag
{
    public required Guid RecipeTagId { get; set; }

    [StringLength(PhotoLimits.MaxTagNameLength)]
    public required string Name { get; set; }

    [StringLength(PhotoLimits.MaxTagDescriptionLength)]
    public string? Description { get; set; }

    public DateTime? Archived { get; set; }

    /// <summary>A <see cref="TagIcons"/> key, or <c>null</c>; an unknown stored key reads as <c>null</c>.</summary>
    public string? Icon { get; set; }

    /// <summary>Normalised <c>#RRGGBB</c>, or <c>null</c>; an invalid stored value reads as <c>null</c>.</summary>
    public string? Color { get; set; }

    /// <summary>How many recipes carry the tag.</summary>
    public int RecipeCount { get; set; }
}

/// <summary>Create request for a recipe tag.</summary>
public sealed record NewRecipeTag
{
    [Required]
    [StringLength(PhotoLimits.MaxTagNameLength)]
    public required string Name { get; set; }

    [StringLength(PhotoLimits.MaxTagDescriptionLength)]
    public string? Description { get; set; }

    /// <summary>A <see cref="TagIcons"/> key, or <c>null</c> for the default; the default key itself is rejected.</summary>
    [StringLength(TagIcons.MaxKeyLength, ErrorMessage = TagIcons.InvalidIconMessage)]
    [TagIcon]
    public string? Icon { get; set; }

    /// <summary>An optional <c>#RRGGBB</c>, stored upper-case; an empty string is rejected — send <c>null</c>.</summary>
    [StringLength(HexColor.Length, ErrorMessage = HexColor.InvalidMessage)]
    [HexColor]
    public string? Color { get; set; }
}

/// <summary>Full-replacement update for a recipe tag, including its archived state.</summary>
public sealed record UpdateRecipeTag
{
    [Required]
    [StringLength(PhotoLimits.MaxTagNameLength)]
    public required string Name { get; set; }

    [StringLength(PhotoLimits.MaxTagDescriptionLength)]
    public string? Description { get; set; }

    /// <summary>Desired archived state: true archives, false restores.</summary>
    public bool Archived { get; set; }

    [StringLength(TagIcons.MaxKeyLength, ErrorMessage = TagIcons.InvalidIconMessage)]
    [TagIcon]
    public string? Icon { get; set; }

    [StringLength(HexColor.Length, ErrorMessage = HexColor.InvalidMessage)]
    [HexColor]
    public string? Color { get; set; }
}
