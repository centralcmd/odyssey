namespace Odyssey.Dtos.Journal;

/// <summary>
/// A file attached to a calendar event or series (issue #521 §5.3). The id and the link date are calendar data under
/// <c>calendar.read</c>; the name, type, size and upload time are <see langword="null"/> without <c>files.read</c>.
/// </summary>
public sealed record CalendarLinkFileDto
{
    public required Guid FileId { get; set; }

    public required DateTime CreatedAt { get; set; }

    public string? FileName { get; set; }

    public string? ContentType { get; set; }

    public long? SizeBytes { get; set; }

    public DateTime? UploadedAtUtc { get; set; }
}

/// <summary>
/// A library photo linked to a calendar event or series (issue #521 §5.3). Id, gallery position and link date are
/// calendar data; the title and file name are <see langword="null"/>, and <see cref="ThumbnailsReady"/> is false, without
/// <c>photos.read</c> (SF3).
/// </summary>
public sealed record CalendarLinkPhotoDto
{
    public required Guid PhotoId { get; set; }

    public required int Position { get; set; }

    public required DateTime CreatedAt { get; set; }

    public string? Title { get; set; }

    public string? FileName { get; set; }

    public bool ThumbnailsReady { get; set; }
}

/// <summary>Whether a calendar <c>.ics</c> export carries its attached files and photos (issue #521 §5.4).</summary>
public enum IcsAttachmentsMode
{
    /// <summary>Every linked file and photo travels inline (or as a reference to content already written).</summary>
    Include = 0,

    /// <summary>No <c>ATTACH</c> is written, and neither <c>files.read</c> nor <c>photos.read</c> is needed.</summary>
    Omit = 1,
}
