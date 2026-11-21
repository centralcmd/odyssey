using System.ComponentModel.DataAnnotations;

namespace Odyssey.Dtos.Books;

/// <summary>Read model for a book tag, the photo-tag shape plus a usage count on list rows.</summary>
public sealed record ExistingBookTag
{
    public required Guid BookTagId { get; set; }

    [StringLength(BookLimits.MaxTagNameLength)]
    public required string Name { get; set; }

    [StringLength(BookLimits.MaxTagDescriptionLength)]
    public string? Description { get; set; }

    public DateTime? Archived { get; set; }

    /// <summary>A <see cref="TagIcons"/> key, or <c>null</c> for the default; an unknown stored key projects as <c>null</c>.</summary>
    public string? Icon { get; set; }

    /// <summary>Normalised <c>#RRGGBB</c>, or <c>null</c>.</summary>
    public string? Color { get; set; }

    /// <summary>How many books carry the tag.</summary>
    public int BookCount { get; set; }
}

/// <summary>Create request for a book tag.</summary>
public sealed record NewBookTag
{
    [Required]
    [StringLength(BookLimits.MaxTagNameLength)]
    public required string Name { get; set; }

    [StringLength(BookLimits.MaxTagDescriptionLength)]
    public string? Description { get; set; }

    [StringLength(TagIcons.MaxKeyLength, ErrorMessage = TagIcons.InvalidIconMessage)]
    [TagIcon]
    public string? Icon { get; set; }

    [StringLength(HexColor.Length, ErrorMessage = HexColor.InvalidMessage)]
    [HexColor]
    public string? Color { get; set; }
}

/// <summary>Update request for a book tag: full replacement plus the archived state.</summary>
public sealed record UpdateBookTag
{
    [Required]
    [StringLength(BookLimits.MaxTagNameLength)]
    public required string Name { get; set; }

    [StringLength(BookLimits.MaxTagDescriptionLength)]
    public string? Description { get; set; }

    public bool Archived { get; set; }

    [StringLength(TagIcons.MaxKeyLength, ErrorMessage = TagIcons.InvalidIconMessage)]
    [TagIcon]
    public string? Icon { get; set; }

    [StringLength(HexColor.Length, ErrorMessage = HexColor.InvalidMessage)]
    [HexColor]
    public string? Color { get; set; }
}

/// <summary>Sort keys for the book-tag list.</summary>
public enum BookTagSortBy
{
    Name,
    Status,
    BookCount,
}

/// <summary>The book-tag list's query-string binding model.</summary>
public sealed class BookTagsQueryParams : QueryParams<BookTagSortBy>
{
    public ArchivalStatus? Status { get; set; }
}
