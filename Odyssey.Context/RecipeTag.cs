using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;
using Odyssey.Dtos;
using Odyssey.Dtos.Journal;

namespace Odyssey.Context;

/// <summary>
/// A recipe tag (issue #525), the <see cref="PhotoTag"/> shape: a <b>unique</b> <see cref="Name"/> across active
/// and archived tags, case-insensitive through the column's default <c>utf8mb4_*_ci</c> collation, with the
/// service's in-memory check and the duplicate-key re-check in front of it. The lengths reuse the photo-tag
/// constants.
/// </summary>
[Index(nameof(Name), IsUnique = true)]
[Index(nameof(Archived))]
public class RecipeTag
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid RecipeTagId { get; set; }

    [StringLength(PhotoLimits.MaxTagNameLength)]
    [Required]
    public required string Name { get; set; }

    [StringLength(PhotoLimits.MaxTagDescriptionLength)]
    public string? Description { get; set; }

    public DateTime? Archived { get; set; }

    /// <summary>A <c>TagIcons</c> catalogue key, or <c>NULL</c> for the default icon; an unknown key reads as null.</summary>
    [StringLength(TagIcons.MaxKeyLength)]
    public string? Icon { get; set; }

    /// <summary>An optional <c>#RRGGBB</c> colour stored upper-case; every read normalises it.</summary>
    [StringLength(HexColor.Length)]
    public string? Color { get; set; }

    public ICollection<RecipeTagLink> Recipes { get; set; } = new List<RecipeTagLink>();
}
