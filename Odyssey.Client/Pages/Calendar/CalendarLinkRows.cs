using Odyssey.Client.Pages.Journal;
using Odyssey.Dtos.Finance;
using Odyssey.Dtos.Journal;

namespace Odyssey.Client.Pages.Calendar;

/// <summary>
/// The hydrated calendar links (issue #521 §5.3) as the journal's row types (issue #522 v3 §E), so the event dialog shows
/// them through the same <see cref="JournalFileRows"/> and photo tiles the journal uses, with no per-file metadata call.
/// </summary>
internal static class CalendarLinkRows
{
    /// <summary>The name a photo is shown and announced by: its title, else its file name.</summary>
    public const string UntitledPhoto = "Untitled photo";

    /// <summary>
    /// A file row: <see cref="CalendarLinkFileDto.FileId"/> becomes the id, the rest map field for field. Rows are drawn
    /// only under <c>files.read</c>, where nothing is null; an unexpected null renders as "Unknown" (v4 "Defensive
    /// mapping") — <see cref="JournalFileRows"/> reads a negative size and the default date that way. It never throws.
    /// </summary>
    public static FileMetadataResponse File(CalendarLinkFileDto f) => new(
        f.FileId,
        string.IsNullOrWhiteSpace(f.FileName) ? JournalFileRows.Unknown : f.FileName,
        f.ContentType ?? string.Empty,
        f.SizeBytes ?? JournalFileRows.UnknownSize,
        string.Empty,
        f.UploadedAtUtc ?? default,
        null);

    public static List<FileMetadataResponse> Files(IEnumerable<CalendarLinkFileDto>? files) =>
        files is null ? [] : [.. files.Select(File)];

    /// <summary>The photos in gallery order, named by title, else file name.</summary>
    public static List<JournalDraftPhoto> Photos(IEnumerable<CalendarLinkPhotoDto>? photos) =>
        photos is null
            ? []
            : [.. photos.OrderBy(p => p.Position).Select(p => new JournalDraftPhoto(
                p.PhotoId, PhotoName(p.Title, p.FileName), p.ThumbnailsReady, p.Favourited, p.Archived))];

    public static string PhotoName(string? title, string? fileName) =>
        !string.IsNullOrWhiteSpace(title) ? title : !string.IsNullOrWhiteSpace(fileName) ? fileName : UntitledPhoto;
}
