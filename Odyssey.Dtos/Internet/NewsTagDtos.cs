using System.ComponentModel.DataAnnotations;

namespace Odyssey.Dtos.Internet;

// News tags (issue #470 §4.12, §5.4): the same contract as journal and photo tags — a paged list with search, status and
// sort; create; a full-replacement PUT that also carries the archival state; and a delete that refuses a tag in use
// (409, archive it instead). Tags also do the job of favourites.

/// <summary>The body of <c>POST /api/news-tags</c>.</summary>
public sealed record NewNewsTag
{
    [Required]
    [StringLength(NewsLimits.MaxTagNameLength, MinimumLength = 1)]
    public string? Name { get; set; }

    [StringLength(NewsLimits.MaxTagDescriptionLength)]
    public string? Description { get; set; }

    /// <summary>A <see cref="TagIcons"/> key, or <c>null</c> for the default icon. The default key itself is rejected.</summary>
    [StringLength(TagIcons.MaxKeyLength, ErrorMessage = TagIcons.InvalidIconMessage)]
    [TagIcon]
    public string? Icon { get; set; }

    /// <summary>An optional <c>#RRGGBB</c> colour, stored upper-case; an empty string is rejected — send <c>null</c>.</summary>
    [StringLength(HexColor.Length, ErrorMessage = HexColor.InvalidMessage)]
    [HexColor]
    public string? Color { get; set; }
}

/// <summary>
/// The body of <c>PUT /api/news-tags/{id}</c>: full replacement, so an omitted icon or colour clears it, and
/// <see cref="Archived"/> archives or restores the tag — as on every other tag resource.
/// </summary>
public sealed record UpdateNewsTag
{
    [Required]
    [StringLength(NewsLimits.MaxTagNameLength, MinimumLength = 1)]
    public string? Name { get; set; }

    [StringLength(NewsLimits.MaxTagDescriptionLength)]
    public string? Description { get; set; }

    /// <summary><c>true</c> archives the tag (its links stay, and it still filters); <c>false</c> restores it.</summary>
    public bool Archived { get; set; }

    [StringLength(TagIcons.MaxKeyLength, ErrorMessage = TagIcons.InvalidIconMessage)]
    [TagIcon]
    public string? Icon { get; set; }

    [StringLength(HexColor.Length, ErrorMessage = HexColor.InvalidMessage)]
    [HexColor]
    public string? Color { get; set; }
}

/// <summary>A tag as <c>GET /api/news-tags</c> returns it — the read shape every tag resource shares.</summary>
public sealed record ExistingNewsTag
{
    public Guid NewsTagId { get; set; }

    [StringLength(NewsLimits.MaxTagNameLength)]
    public string Name { get; set; } = string.Empty;

    [StringLength(NewsLimits.MaxTagDescriptionLength)]
    public string? Description { get; set; }

    /// <summary>When the tag was archived, or <c>null</c> while it is active.</summary>
    public DateTime? Archived { get; set; }

    /// <summary>A known <see cref="TagIcons"/> key, or <c>null</c>; an unknown stored key is projected as <c>null</c>.</summary>
    public string? Icon { get; set; }

    /// <summary>Normalised <c>#RRGGBB</c>, or <c>null</c>; an invalid stored value is projected as <c>null</c>.</summary>
    public string? Color { get; set; }
}
