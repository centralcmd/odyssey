using System.Text.Json.Serialization;

namespace Odyssey.Dtos.Books;

/// <summary>
/// A contact named on a book, purpose-built so the book never carries <c>ExistingContact</c> (issue #527 §7.3).
/// <see cref="Name"/>, <see cref="Type"/> and <see cref="Archived"/> are <c>null</c> without <c>contacts.read</c>;
/// <see cref="Archived"/> is then unknown, never <c>false</c>.
/// </summary>
public sealed record BookContactReference
{
    public required Guid ContactId { get; set; }

    public string? Name { get; set; }

    public ContactType? Type { get; set; }

    public bool? Archived { get; set; }
}

/// <summary>A contributor row on a book, in its saved order.</summary>
public sealed record ExistingBookContributor
{
    public required Guid BookContributorId { get; set; }

    public int Position { get; set; }

    public string? Role { get; set; }

    public required BookContactReference Contact { get; set; }
}

/// <summary>A linked Photo Library photo. Returned only to a <c>photos.read</c> holder.</summary>
public sealed record BookImageDto
{
    public required Guid BookImageId { get; set; }

    public required Guid PhotoId { get; set; }

    public bool IsCover { get; set; }

    public string? Title { get; set; }

    public string? FileName { get; set; }

    /// <summary>
    /// The library photo's own <c>Favourited</c> timestamp (UTC; null = not a favourite), so the shared photo tile can
    /// draw its heart without a per-photo <c>GET /api/photos/{id}</c>.
    /// </summary>
    public DateTime? Favourited { get; set; }

    /// <summary>The library photo's own <c>Archived</c> timestamp (UTC; null = live), for the tile's "Archived" badge.</summary>
    public DateTime? Archived { get; set; }

    public bool ThumbnailsReady { get; set; }

    public DateTime CreatedAt { get; set; }
}

/// <summary>A linked Files-store file. Returned only to a <c>files.read</c> holder; bytes stay behind <c>/api/files</c>.</summary>
public sealed record BookAttachmentDto
{
    public required Guid BookAttachmentId { get; set; }

    public required Guid FileId { get; set; }

    public required string FileName { get; set; }

    public required string ContentType { get; set; }

    public long SizeBytes { get; set; }

    public DateTime CreatedAt { get; set; }
}

/// <summary>One book with everything the caller may see (issue #527 §5).</summary>
public sealed record ExistingBook : ITagIconRecord
{
    public required Guid BookId { get; set; }

    /// <summary>The concurrency token; echo it on <c>PUT</c>.</summary>
    public int Version { get; set; }

    public required string Title { get; set; }

    public string? Publisher { get; set; }

    public int? Year { get; set; }

    public string? Isbn { get; set; }

    public BookFormat Format { get; set; }

    public BookStatus Status { get; set; }

    public string? Description { get; set; }

    public string? Notes { get; set; }

    public required BookContactReference MainAuthor { get; set; }

    public List<ExistingBookContributor> Contributors { get; set; } = [];

    /// <summary><c>null</c> without <c>photos.read</c>.</summary>
    public List<BookImageDto>? Images { get; set; }

    /// <summary><c>null</c> without <c>photos.read</c>, or when no cover is flagged.</summary>
    public Guid? CoverPhotoId { get; set; }

    /// <summary><c>null</c> without <c>files.read</c>.</summary>
    public List<BookAttachmentDto>? Attachments { get; set; }

    public List<Guid> TagIds { get; set; } = [];

    public string DisplayIcon { get; set; } = TagIcons.Default;

    public string? DisplayColor { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    /// <summary>The creating user's display name; <c>null</c> when the attribution is gone. Never a raw user id.</summary>
    public string? CreatedByName { get; set; }

    public string? UpdatedByName { get; set; }

    /// <summary>Server-side only, for the controller's name resolution: a raw user id never reaches the wire (§7.3).</summary>
    [JsonIgnore]
    public string? CreatedByUserId { get; set; }

    /// <inheritdoc cref="CreatedByUserId"/>
    [JsonIgnore]
    public string? UpdatedByUserId { get; set; }
}

/// <summary>A list row (issue #527 §5).</summary>
public sealed record BookSummary : ITagIconRecord
{
    public required Guid BookId { get; set; }

    public required string Title { get; set; }

    public int? Year { get; set; }

    public string? Isbn { get; set; }

    public string? Publisher { get; set; }

    public BookFormat Format { get; set; }

    public BookStatus Status { get; set; }

    public required BookContactReference MainAuthor { get; set; }

    public int ContributorCount { get; set; }

    /// <summary><c>null</c> without <c>files.read</c>.</summary>
    public int? AttachmentCount { get; set; }

    /// <summary>How many images are linked; <c>null</c> without <c>photos.read</c>.</summary>
    public int? ImageCount { get; set; }

    /// <summary><c>null</c> without <c>photos.read</c>.</summary>
    public Guid? CoverPhotoId { get; set; }

    /// <summary><c>false</c> without <c>photos.read</c>.</summary>
    public bool CoverThumbnailsReady { get; set; }

    public List<Guid> TagIds { get; set; } = [];

    public string DisplayIcon { get; set; } = TagIcons.Default;

    public string? DisplayColor { get; set; }

    public DateTime UpdatedAt { get; set; }
}

/// <summary>
/// The <c>bookCredits</c> extension of a contact-delete <c>409</c> (issue #527 §5 item 12). The counts are always
/// present; <see cref="Books"/> is empty unless the caller holds <c>books.read</c>.
/// </summary>
public sealed record ContactBookCreditBlockers
{
    /// <summary>Main-author plus contributor rows naming the contact.</summary>
    public int TotalCredits { get; set; }

    public int BookCount { get; set; }

    public List<BookCreditReference> Books { get; set; } = [];
}

/// <summary>A book that blocks a contact delete, named only to a <c>books.read</c> holder.</summary>
public sealed record BookCreditReference
{
    public required Guid BookId { get; set; }

    public required string Title { get; set; }
}
