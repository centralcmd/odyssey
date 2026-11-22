using System.ComponentModel.DataAnnotations;

namespace Odyssey.Dtos.Books;

/// <summary>A contributor on a create or update. Linked by scalar contact id only.</summary>
public sealed record BookContributorRequest
{
    [Required]
    public Guid? ContactId { get; set; }

    [StringLength(BookLimits.MaxRoleLength)]
    public string? Role { get; set; }
}

/// <summary>The scalar fields shared by <see cref="NewBook"/> and <see cref="UpdateBook"/>.</summary>
public interface IBookFields
{
    string Title { get; }

    string? Publisher { get; }

    int? Year { get; }

    string? Isbn { get; }

    BookFormat? Format { get; }

    BookStatus? Status { get; }

    string? Description { get; }

    string? Notes { get; }

    Guid? MainAuthorContactId { get; }

    List<BookContributorRequest>? Contributors { get; }

    List<Guid>? PhotoIds { get; }

    Guid? CoverPhotoId { get; }

    List<Guid>? AttachmentFileIds { get; }

    List<Guid>? TagIds { get; }
}

/// <summary>
/// Create a book (issue #527 §5). Every relationship is a scalar id. A <c>null</c> array means empty; a non-empty array
/// needs its read claim.
/// </summary>
public sealed record NewBook : IBookFields
{
    [Required]
    [StringLength(BookLimits.MaxTitleLength)]
    public string Title { get; set; } = string.Empty;

    [StringLength(BookLimits.MaxPublisherLength)]
    public string? Publisher { get; set; }

    /// <summary>Negative is BC; <c>0</c> is a <c>422</c>.</summary>
    [Range(BookLimits.MinYear, BookLimits.MaxYear)]
    public int? Year { get; set; }

    /// <summary>Raw input; hyphens and spaces are allowed (§8.3).</summary>
    [StringLength(BookLimits.MaxIsbnInputLength)]
    public string? Isbn { get; set; }

    [Required]
    [EnumDataType(typeof(BookFormat))]
    public BookFormat? Format { get; set; }

    [Required]
    [EnumDataType(typeof(BookStatus))]
    public BookStatus? Status { get; set; }

    [StringLength(BookLimits.MaxDescriptionLength)]
    public string? Description { get; set; }

    [StringLength(BookLimits.MaxNotesLength)]
    public string? Notes { get; set; }

    [Required]
    public Guid? MainAuthorContactId { get; set; }

    [MaxLength(BookLimits.MaxContributors)]
    public List<BookContributorRequest>? Contributors { get; set; }

    [MaxLength(BookLimits.MaxImages)]
    public List<Guid>? PhotoIds { get; set; }

    public Guid? CoverPhotoId { get; set; }

    [MaxLength(BookLimits.MaxAttachments)]
    public List<Guid>? AttachmentFileIds { get; set; }

    [MaxLength(BookLimits.MaxTags)]
    public List<Guid>? TagIds { get; set; }
}

/// <summary>
/// Replace a book (issue #527 §5). A separate sealed record with <see cref="NewBook"/>'s fields and annotations (two
/// sealed records cannot inherit; <see cref="IBookFields"/> and a guard test keep them aligned). On every array,
/// <c>null</c> leaves the set unchanged and any array replaces it and needs that set's read claim.
/// </summary>
public sealed record UpdateBook : IBookFields
{
    /// <summary>Must equal the stored version, else <c>409 book-version-conflict</c>.</summary>
    [Required]
    public int? Version { get; set; }

    [Required]
    [StringLength(BookLimits.MaxTitleLength)]
    public string Title { get; set; } = string.Empty;

    [StringLength(BookLimits.MaxPublisherLength)]
    public string? Publisher { get; set; }

    [Range(BookLimits.MinYear, BookLimits.MaxYear)]
    public int? Year { get; set; }

    [StringLength(BookLimits.MaxIsbnInputLength)]
    public string? Isbn { get; set; }

    [Required]
    [EnumDataType(typeof(BookFormat))]
    public BookFormat? Format { get; set; }

    [Required]
    [EnumDataType(typeof(BookStatus))]
    public BookStatus? Status { get; set; }

    [StringLength(BookLimits.MaxDescriptionLength)]
    public string? Description { get; set; }

    [StringLength(BookLimits.MaxNotesLength)]
    public string? Notes { get; set; }

    [Required]
    public Guid? MainAuthorContactId { get; set; }

    [MaxLength(BookLimits.MaxContributors)]
    public List<BookContributorRequest>? Contributors { get; set; }

    [MaxLength(BookLimits.MaxImages)]
    public List<Guid>? PhotoIds { get; set; }

    /// <summary>Ignored when <see cref="PhotoIds"/> is <c>null</c>.</summary>
    public Guid? CoverPhotoId { get; set; }

    [MaxLength(BookLimits.MaxAttachments)]
    public List<Guid>? AttachmentFileIds { get; set; }

    [MaxLength(BookLimits.MaxTags)]
    public List<Guid>? TagIds { get; set; }
}

/// <summary>Sort keys for the book list.</summary>
public enum BookSortBy
{
    Title,
    Author,
    Year,
    CreatedAt,
    UpdatedAt,
}

/// <summary>The book list's query-string binding model (issue #527 §5 item 1).</summary>
public sealed class BooksQueryParams : QueryParams<BookSortBy>
{
    [MaxLength(ListDefaults.MaxFilterArrayLength)]
    public BookStatus[]? Status { get; set; }

    [MaxLength(ListDefaults.MaxFilterArrayLength)]
    public BookFormat[]? Format { get; set; }

    /// <summary>The book carries every listed tag.</summary>
    [MaxLength(ListDefaults.MaxFilterArrayLength)]
    public Guid[]? TagIds { get; set; }

    /// <summary>Main author or contributor. Needs <c>contacts.read</c>.</summary>
    public Guid? ContactId { get; set; }

    /// <summary>Needs <c>files.read</c>.</summary>
    public bool? HasAttachments { get; set; }

    /// <summary>Needs <c>photos.read</c>.</summary>
    public bool? HasCover { get; set; }
}
