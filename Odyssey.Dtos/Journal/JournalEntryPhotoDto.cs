namespace Odyssey.Dtos.Journal;

/// <summary>
/// A journal entry's photo link (issue #321 v4): a reference to a library <c>Photo</c> by
/// <see cref="PhotoId"/> plus its gallery order. Since issue #428 the client reaches the original through
/// <c>GET /api/photos/{PhotoId}/content</c>; the read carries the photo's <see cref="Title"/> and effective
/// <see cref="FileName"/> so the gallery can label it without the Files API. A link whose <see cref="PhotoId"/>
/// no longer resolves is omitted from the read entirely.
/// </summary>
public sealed record JournalEntryPhotoDto
{
    public required Guid JournalEntryPhotoId { get; set; }

    public required Guid PhotoId { get; set; }

    public string? Title { get; set; }

    /// <summary>The effective file name (<c>Photo.FileName ?? FileMetadata.FileName</c>, issue #428 §5.4).</summary>
    public string? FileName { get; set; }

    /// <summary>
    /// The library photo's own <c>Favourited</c> timestamp (UTC; null = not a favourite), so the shared photo tile can
    /// draw its heart without a per-photo <c>GET /api/photos/{id}</c>. Null for a caller without
    /// <c>photos.read</c>, by the same controller helper as <see cref="ThumbnailsReady"/>.
    /// </summary>
    public DateTime? Favourited { get; set; }

    /// <summary>The library photo's own <c>Archived</c> timestamp (UTC; null = live), for the tile's "Archived" badge. Null without <c>photos.read</c>.</summary>
    public DateTime? Archived { get; set; }

    public required int Position { get; set; }

    public required DateTime CreatedAt { get; set; }

    /// <summary>
    /// Both thumbnails exist (issue #362 §4.4). Forced to false for a caller without <c>photos.read</c>, by one
    /// helper in <c>JournalEntriesController</c> on every action that returns this DTO (§7.3).
    /// </summary>
    public bool ThumbnailsReady { get; set; }
}
