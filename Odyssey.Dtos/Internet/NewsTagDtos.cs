using System.ComponentModel.DataAnnotations;

namespace Odyssey.Dtos.Internet;

// News tags (issue #470 §4.12, §5.4): the same shape and guards as journal tags. Tags also do the job of favourites.

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
/// The body of <c>PUT /api/news-tags/{id}</c>: full replacement, so an omitted icon or colour clears it. Archiving has
/// its own endpoints.
/// </summary>
public sealed record UpdateNewsTag
{
    [Required]
    [StringLength(NewsLimits.MaxTagNameLength, MinimumLength = 1)]
    public string? Name { get; set; }

    [StringLength(NewsLimits.MaxTagDescriptionLength)]
    public string? Description { get; set; }

    [StringLength(TagIcons.MaxKeyLength, ErrorMessage = TagIcons.InvalidIconMessage)]
    [TagIcon]
    public string? Icon { get; set; }

    [StringLength(HexColor.Length, ErrorMessage = HexColor.InvalidMessage)]
    [HexColor]
    public string? Color { get; set; }
}

/// <summary>
/// A tag as <c>GET /api/news-tags</c> returns it: every field of the entity plus the two direct-link counts — the
/// same counts the in-use <c>409</c> reports. Articles that inherit the tag from their feed are not counted.
/// </summary>
public sealed record ExistingNewsTag
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    /// <summary>A known <see cref="TagIcons"/> key, or <c>null</c>; an unknown stored key is projected as <c>null</c>.</summary>
    public string? Icon { get; set; }

    /// <summary>Normalised <c>#RRGGBB</c>, or <c>null</c>; an invalid stored value is projected as <c>null</c>.</summary>
    public string? Color { get; set; }

    /// <summary>Derived from <c>ArchivedAtUtc</c>.</summary>
    public bool Archived { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public DateTime UpdatedAtUtc { get; set; }

    public int FeedCount { get; set; }

    public int ArticleCount { get; set; }
}
