namespace Odyssey.Dtos.Journal;

/// <summary>
/// A journal entry's photo link (issue #321 v4): a reference to a library <c>Photo</c> by
/// <see cref="PhotoId"/> plus its gallery order. The read enriches each link with the library photo's
/// <see cref="FileId"/> so the existing gallery/lightbox (which builds the content URL from the file id)
/// keeps working unchanged. A link whose <see cref="PhotoId"/> no longer resolves is omitted from the
/// read entirely, so <see cref="FileId"/> is never empty on a returned link.
/// </summary>
public sealed record JournalEntryPhotoDto
{
    public required Guid JournalEntryPhotoId { get; set; }

    public required Guid PhotoId { get; set; }

    public required Guid FileId { get; set; }

    public required int Position { get; set; }

    public required DateTime CreatedAt { get; set; }
}
