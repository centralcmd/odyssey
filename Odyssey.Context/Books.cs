using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;
using Odyssey.Dtos.Books;

namespace Odyssey.Context;

/// <summary>
/// A book in the household catalogue (issue #527). Shared household data like journal entries: no owner column, access
/// by the <c>books.*</c> claims. There is no archive column; <see cref="BookStatus.SoldOrGivenAway"/> is how a book
/// leaves the shelf.
/// </summary>
[Index(nameof(Title))]
[Index(nameof(Isbn))]
[Index(nameof(Status))]
[Index(nameof(MainAuthorContactId))]
public class Book
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid BookId { get; set; }

    [StringLength(500)]
    public required string Title { get; set; }

    [StringLength(200)]
    public string? Publisher { get; set; }

    /// <summary>Publication year of this edition; negative is BC, never 0, no clock-derived bound.</summary>
    public int? Year { get; set; }

    /// <summary>Normalised ISBN-10 or ISBN-13: digits plus an optional trailing <c>X</c>. Not unique.</summary>
    [StringLength(13)]
    public string? Isbn { get; set; }

    public BookFormat Format { get; set; }

    public BookStatus Status { get; set; }

    [StringLength(4000)]
    public string? Description { get; set; }

    [StringLength(8000)]
    public string? Notes { get; set; }

    /// <summary>The required main author. RESTRICT: a credited contact cannot be deleted (§7.10).</summary>
    public Guid MainAuthorContactId { get; set; }

    /// <summary>Optimistic-concurrency token, incremented on every successful update.</summary>
    [ConcurrencyCheck]
    public int Version { get; set; } = 1;

    public string? CreatedByUserId { get; set; }

    public string? UpdatedByUserId { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public ICollection<BookContributor> Contributors { get; set; } = new List<BookContributor>();

    public ICollection<BookImage> Images { get; set; } = new List<BookImage>();

    public ICollection<BookAttachment> Attachments { get; set; } = new List<BookAttachment>();

    public ICollection<BookTagLink> TagLinks { get; set; } = new List<BookTagLink>();
}

/// <summary>A co-author, translator or other contributor. One row per contact per book; RESTRICT to the contact.</summary>
[Index(nameof(ContactId))]
public class BookContributor
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid BookContributorId { get; set; }

    public Guid BookId { get; set; }

    public Guid ContactId { get; set; }

    [StringLength(64)]
    public string? Role { get; set; }

    /// <summary>0-based order from the request array; not unique, ties break on the id.</summary>
    public int Position { get; set; }

    public Book? Book { get; set; }
}

/// <summary>
/// A Photo Library photo linked to a book. At most one row per book has <see cref="IsCover"/>; the service is the only
/// writer and the book's <see cref="Book.Version"/> serialises writers. A unique database guard was rejected because
/// moving the cover emits two updates in an order EF does not guarantee (#527 §4).
/// </summary>
public class BookImage
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid BookImageId { get; set; }

    public Guid BookId { get; set; }

    public Guid PhotoId { get; set; }

    public bool IsCover { get; set; }

    public DateTime CreatedAt { get; set; }

    public Book? Book { get; set; }
}

/// <summary>A Files-store file attached to a book. Deleting the book leaves the file in the Files store.</summary>
public class BookAttachment
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid BookAttachmentId { get; set; }

    public Guid BookId { get; set; }

    public Guid FileId { get; set; }

    public DateTime CreatedAt { get; set; }

    public Book? Book { get; set; }
}

/// <summary>A book tag, the same shape and lifecycle as <see cref="PhotoTag"/>.</summary>
[Index(nameof(Name), IsUnique = true)]
[Index(nameof(Archived))]
public class BookTag
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid BookTagId { get; set; }

    [StringLength(64)]
    [Required]
    public required string Name { get; set; }

    [StringLength(256)]
    public string? Description { get; set; }

    public DateTime? Archived { get; set; }

    /// <summary>A <c>TagIcons</c> catalogue key, or <c>NULL</c> for the default icon.</summary>
    [StringLength(64)]
    public string? Icon { get; set; }

    /// <summary>An optional <c>#RRGGBB</c> colour stored upper-case.</summary>
    [StringLength(7)]
    public string? Color { get; set; }

    public ICollection<BookTagLink> Links { get; set; } = new List<BookTagLink>();
}

/// <summary>Join row linking a <see cref="Book"/> to a <see cref="BookTag"/>; RESTRICT to the tag.</summary>
public class BookTagLink
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid BookTagLinkId { get; set; }

    public Guid BookId { get; set; }

    public Guid BookTagId { get; set; }

    public Book? Book { get; set; }

    public BookTag? BookTag { get; set; }
}
