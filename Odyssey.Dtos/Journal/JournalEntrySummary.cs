using System.ComponentModel.DataAnnotations;

namespace Odyssey.Dtos.Journal;

/// <summary>
/// List-row projection for a journal entry. The entry card is always open and IS the detail, so the
/// row carries the full content, the contact links and the photo links rather than a snippet plus
/// counts — one request per batch instead of a detail fetch per rendered card. Attachments stay a
/// count: they sit behind the card's file disclosure and are hydrated only when it is opened.
/// </summary>
public sealed record JournalEntrySummary
{
    public required Guid JournalEntryId { get; set; }

    [StringLength(200)]
    public required string Title { get; set; }

    /// <summary>The entry text in full — the card renders it unclamped.</summary>
    [StringLength(4096)]
    public required string Content { get; set; }

    public required DateTime EntryDate { get; set; }

    [StringLength(300)]
    public string? Location { get; set; }

    [StringLength(255)]
    public string? CreatedByUserId { get; set; }

    /// <summary>Display name (username/email) of the author, resolved at the API edge; null if unresolved.</summary>
    [StringLength(256)]
    public string? CreatedByName { get; set; }

    [StringLength(255)]
    public string? UpdatedByUserId { get; set; }

    /// <summary>Display name of the last editor, resolved at the API edge; null if unresolved.</summary>
    [StringLength(256)]
    public string? UpdatedByName { get; set; }

    public required DateTime CreatedAt { get; set; }

    public required DateTime UpdatedAt { get; set; }

    public DateTime? Archived { get; set; }

    public List<Guid> TagIds { get; set; } = [];

    public List<Guid> ContactIds { get; set; } = [];

    public List<JournalEntryPhotoDto> Photos { get; set; } = [];

    /// <summary>
    /// Attachments are a count, not a list: the card's footer disclosure is what unfolds them, and
    /// the count is the only thing the closed state needs. Photos and contacts carry their links
    /// instead, so nothing on this row counts one thing while listing another.
    /// </summary>
    public required int AttachmentCount { get; set; }
}
